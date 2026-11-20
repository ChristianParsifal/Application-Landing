using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Landing {
    internal enum Mode { Msi, Inno, Nsis, Portable }

    internal sealed class PackageInfo {
        public string Name, Property;
    }

    internal static class Engine {
        private const long ZipLimit = 30L * 1024 * 1024 * 1024;
        private static readonly Regex Reserved = new Regex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsName(string s) {
            return !String.IsNullOrWhiteSpace(s) && s != "." && s != ".." && s.Length <= 180 && s.TrimEnd(' ', '.') == s && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Reserved.IsMatch(s);
        }
        public static string SuggestName(string source) {
            string name = Directory.Exists(source) ? new DirectoryInfo(source).Name : Path.GetFileNameWithoutExtension(source);
            return IsName(name) ? name : "我的应用";
        }
        public static string Destination(string root, string name) {
            if (!IsName(name)) throw new IOException("请输入有效的应用文件夹名称。");
            if (String.IsNullOrWhiteSpace(root) || !Regex.IsMatch(root, @"^[A-Za-z]:[\\/]")) throw new IOException("请选择本机磁盘上的完整目录，例如 D:\\Apps。");
            if (root.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || root.IndexOf(':', 2) >= 0) throw new IOException("安装位置包含无效字符。");
            return Path.Combine(Path.GetFullPath(root), name);
        }
        public static void CheckVacant(string target) {
            try { File.GetAttributes(target); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            throw new IOException("目标已存在。请更换位置或文件夹名称，以免覆盖现有应用。");
        }
        private static void NoLink(string path) {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("便携版源文件夹里有链接或联接点，请移除后重试：" + Path.GetFileName(path));
        }
        private static string WithSlash(string path) { return path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
        public static void DeployPortable(string source, string target) {
            CheckVacant(target);
            bool folder = Directory.Exists(source);
            NoLink(source);
            string parent = Path.GetDirectoryName(target);
            if (folder && WithSlash(Path.GetFullPath(parent)).StartsWith(WithSlash(Path.GetFullPath(source)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("收纳位置须在源文件夹之外。");
            if (folder) {
                // Resolve drive aliases and links for the closest existing ancestor before staging.
                string ancestor = parent;
                while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
                if (WithSlash(RealDirectory(ancestor)).StartsWith(WithSlash(RealDirectory(source)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("收纳位置指向源文件夹内部，请选择其他位置。");
            }
            Directory.CreateDirectory(parent);
            string stage = Path.Combine(parent, ".landing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try {
                if (folder) CopyTree(source, stage, 0);
                else if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase)) ExtractZip(source, stage, Path.GetFileName(target));
                else File.Copy(source, Path.Combine(stage, Path.GetFileName(source)));
                Directory.Move(stage, target);
            } finally {
                if (Directory.Exists(stage)) {
                    try {
                        foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(stage, true);
                    } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        private static void CopyTree(string source, string dest, int depth) {
            if (depth > 100) throw new IOException("源文件夹的层级过深。");
            foreach (string entry in Directory.EnumerateFileSystemEntries(source)) {
                NoLink(entry);
                string name = Path.GetFileName(entry);
                if (!IsName(name)) throw new IOException("源文件夹包含 Windows 路径不兼容的名称。");
                string output = Path.Combine(dest, name);
                if (Directory.Exists(entry)) {
                    Directory.CreateDirectory(output);
                    CopyTree(entry, output, depth + 1);
                } else File.Copy(entry, output);
            }
        }
        private sealed class ZipItem {
            public ZipArchiveEntry Entry;
            public string[] Parts;
            public bool Folder;
        }
        private static void ExtractZip(string source, string stage, string appName) {
            byte[] zone = ReadZone(source);
            using (var archive = ZipFile.OpenRead(source)) {
                if (archive.Entries.Count == 0) throw new IOException("ZIP 中没有文件。");
                if (archive.Entries.Count > 100000) throw new IOException("ZIP 的项目数超过 100,000。");
                var items = new List<ZipItem>();
                long total = 0;
                foreach (var entry in archive.Entries) {
                    string raw = entry.FullName.Replace('\\', '/');
                    bool folder = raw.EndsWith("/", StringComparison.Ordinal);
                    string[] parts = (folder ? raw.Substring(0, raw.Length - 1) : raw).Split('/');
                    if (parts.Length > 100 || parts.Any(p => !IsName(p)) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new IOException("ZIP 中包含无效路径或链接，请检查压缩包。");
                    if (entry.Length > ZipLimit - total) throw new IOException("ZIP 解压大小超过 30 GiB。");
                    total += entry.Length;
                    items.Add(new ZipItem { Entry = entry, Parts = parts, Folder = folder });
                }
                if (!items.Any(i => !i.Folder)) throw new IOException("ZIP 中没有文件。");
                string top = items[0].Parts[0];
                bool unwrap = String.Equals(top, appName, StringComparison.OrdinalIgnoreCase) && items.All(i =>
                    String.Equals(i.Parts[0], top, StringComparison.OrdinalIgnoreCase) && (i.Folder || i.Parts.Length > 1));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byte[] buffer = new byte[81920];
                foreach (var item in items) {
                    string output = stage;
                    for (int i = unwrap ? 1 : 0; i < item.Parts.Length; i++) output = Path.Combine(output, item.Parts[i]);
                    if (!seen.Add(output)) throw new IOException("ZIP 中包含重名项目。");
                    if (item.Folder) { Directory.CreateDirectory(output); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (var input = item.Entry.Open())
                    using (var file = new FileStream(output, FileMode.CreateNew)) {
                        long remaining = item.Entry.Length;
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) != 0) {
                            if (n > remaining) throw new IOException("ZIP 中记录的文件大小不一致。");
                            file.Write(buffer, 0, n);
                            remaining -= n;
                        }
                        if (remaining != 0) throw new IOException("ZIP 内容不完整。");
                    }
                    if (zone != null) WriteZone(output, zone);
                }
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, System.Text.StringBuilder path, uint size, uint flags);
        private static string RealDirectory(string path) {
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                var text = new System.Text.StringBuilder(32768);
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
                uint n = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
                if (n == 0 || n >= text.Capacity) throw new System.ComponentModel.Win32Exception();
                return text.ToString();
            }
        }
        private static byte[] ReadZone(string path) {
            using (var handle = CreateFile(path + ":Zone.Identifier", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                if (handle.IsInvalid) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2 || error == 3 || error == 123 || error == 1 || error == 50) return null;
                    throw new System.ComponentModel.Win32Exception(error);
                }
                using (var stream = new FileStream(handle, FileAccess.Read)) {
                    if (stream.Length > 65536) throw new IOException("ZIP 的来源标记异常。");
                    using (var data = new MemoryStream()) { stream.CopyTo(data); return data.ToArray(); }
                }
            }
        }
        private static void WriteZone(string path, byte[] data) {
            using (var handle = CreateFile(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new IOException("目标磁盘无法保留下载来源标记，请选择 NTFS 文件夹。");
                using (var stream = new FileStream(handle, FileAccess.Write)) stream.Write(data, 0, data.Length);
            }
        }

        // Run only on the user's explicit click. Installer owns elevation and the commit.
        public static int RunInstaller(string source, string target, Mode mode, string property) {
            CheckVacant(target);
            string command = source;
            string arguments;
            switch (mode) {
                case Mode.Msi:
                    if (!Regex.IsMatch(property ?? "", @"^[A-Z][A-Z0-9_]{0,71}$")) throw new IOException("缺少有效的 MSI 安装目录属性。");
                    command = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                    arguments = "/i \"" + source + "\" /norestart " + property + "=\"" + target + "\"";
                    break;
                case Mode.Inno: arguments = "/NORESTART /DIR=\"" + target + "\""; break;
                case Mode.Nsis: arguments = "/D=" + target; break; // /D must be last and unquoted, even with spaces.
                default: throw new InvalidOperationException();
            }
            using (var process = Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(source) })) {
                if (process == null) throw new IOException("安装程序未启动。");
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint db);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(uint db, string query, out uint view);
        [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
        [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetString(uint record, uint index, System.Text.StringBuilder output, ref uint size);
        [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);

        private static string Value(uint record, uint index) {
            uint size = 0;
            uint result = MsiRecordGetString(record, index, null, ref size);
            if (result != 0 && result != 234) return "";
            var buffer = new System.Text.StringBuilder((int)size + 1);
            size++;
            return MsiRecordGetString(record, index, buffer, ref size) == 0 ? buffer.ToString() : "";
        }
        private static Dictionary<string, string> Query(uint database, string sql) {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            uint view, record;
            if (MsiDatabaseOpenView(database, sql, out view) != 0) return rows;
            try {
                if (MsiViewExecute(view, 0) == 0) while (MsiViewFetch(view, out record) == 0) {
                    try { rows[Value(record, 1)] = Value(record, 2); }
                    finally { MsiCloseHandle(record); }
                }
            } finally { MsiCloseHandle(view); }
            return rows;
        }
        public static PackageInfo ReadMsi(string source) {
            uint database;
            if (MsiOpenDatabase(source, IntPtr.Zero, out database) != 0) throw new IOException("Windows Installer 无法读取此 MSI 文件。");
            try {
                var properties = Query(database, "SELECT `Property`, `Value` FROM `Property`");
                var dirs = Query(database, "SELECT `Directory`, `DefaultDir` FROM `Directory`");
                string property = null, name = null;
                properties.TryGetValue("WIXUI_INSTALLDIR", out property);
                if (property == null || !Regex.IsMatch(property, @"^[A-Z][A-Z0-9_]{0,71}$") || !dirs.ContainsKey(property) || property == "TARGETDIR") {
                    property = new[] { "INSTALLDIR", "INSTALLFOLDER", "APPLICATIONFOLDER", "APPDIR", "APPINSTALLDIR", "INSTALLLOCATION", "INSTALLPATH" }.FirstOrDefault(dirs.ContainsKey);
                }
                if (property != null && dirs.TryGetValue(property, out name)) {
                    name = name.Split(':')[0].Split('|').Last();
                }
                if (!IsName(name)) properties.TryGetValue("ProductName", out name);
                return new PackageInfo { Name = IsName(name) ? name : SuggestName(source), Property = property };
            } finally { MsiCloseHandle(database); }
        }
    }
}
