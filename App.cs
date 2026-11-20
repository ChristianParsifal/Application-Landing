using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Rectangle = System.Windows.Shapes.Rectangle;

[assembly: AssemblyTitle("应用落点")]
[assembly: AssemblyProduct("应用落点")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Landing {
    internal sealed class App {
        private readonly Window window;
        private readonly Grid drop, modeRow, propertyRow;
        private readonly Rectangle surface;
        private readonly Border tile;
        private readonly TextBlock title, hint, glyph, preview, status;
        private readonly Button pickFile, pickFolder, browse, commit, open;
        private readonly TextBox root, folder, msiProperty;
        private readonly ComboBox modes;
        private readonly StackPanel options;
        private readonly ProgressBar pulse;
        private string source, result, baseName, portableName;
        private bool busy, filling;
        private static readonly Brush InkMuted = BrushFrom("#586677"), Blue = BrushFrom("#0866E8"), Red = BrushFrom("#A52934"), Green = BrushFrom("#087657");
        private static Brush BrushFrom(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }

        [STAThread]
        public static void Main(string[] args) {
            bool test = args.Length == 2 && args[0] == "--ui-test";
            try {
                var wpf = new Application();
                var app = new App();
                if (test) {
                    System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                    app.UiTest(args[1]);
                } else wpf.Run(app.window);
            } catch (Exception ex) {
                if (!test) { MessageBox.Show(ex.ToString(), "应用落点启动失败"); Environment.ExitCode = 1; return; }
                File.WriteAllText(Path.Combine(args[1], "ui-fail.txt"), ex.ToString());
                Environment.ExitCode = 1;
            }
        }

        private T Control<T>(string name) where T : class { return (T)window.FindName(name); }
        private App() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) window = (Window)XamlReader.Load(stream);
            using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.ico")) {
                window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
            drop = Control<Grid>("DropArea"); modeRow = Control<Grid>("ModeRow"); propertyRow = Control<Grid>("PropertyRow");
            surface = Control<Rectangle>("DropSurface"); tile = Control<Border>("IconTile");
            title = Control<TextBlock>("DropTitle"); hint = Control<TextBlock>("DropHint"); glyph = Control<TextBlock>("DropIcon");
            preview = Control<TextBlock>("Preview"); status = Control<TextBlock>("Status");
            pickFile = Control<Button>("PickFile"); pickFolder = Control<Button>("PickSourceFolder"); browse = Control<Button>("BrowseTarget");
            commit = Control<Button>("Commit"); open = Control<Button>("OpenTarget");
            root = Control<TextBox>("TargetRoot"); folder = Control<TextBox>("FolderName"); msiProperty = Control<TextBox>("MsiProperty");
            modes = Control<ComboBox>("ModeChoice"); options = Control<StackPanel>("Options"); pulse = Control<ProgressBar>("Pulse");
            root.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            root.TextChanged += (s, e) => RefreshAfterEdit();
            folder.TextChanged += (s, e) => RefreshAfterEdit();
            msiProperty.TextChanged += (s, e) => RefreshAfterEdit();
            modes.SelectionChanged += (s, e) => {
                if (filling) return;
                filling = true;
                if (modeRow.Visibility == Visibility.Visible) {
                    if (String.Equals(folder.Text, baseName, StringComparison.Ordinal) || String.Equals(folder.Text, portableName, StringComparison.Ordinal))
                        folder.Text = CurrentMode() == Mode.Portable ? portableName : baseName;
                }
                filling = false;
                RefreshAfterEdit();
            };
            pickFile.Click += (s, e) => ChooseFile();
            pickFolder.Click += (s, e) => ChooseFolder(true);
            browse.Click += (s, e) => ChooseFolder(false);
            commit.Click += async (s, e) => await Deploy();
            open.Click += (s, e) => {
                try {
                    if (result == null || !Directory.Exists(result)) throw new IOException("目标文件夹已被移动或删除。");
                    Process.Start(new ProcessStartInfo(result) { UseShellExecute = true, Verb = "open" });
                } catch (Exception ex) { ShowStatus(ex.Message, Red); }
            };
            drop.PreviewDragEnter += PreviewDrag;
            drop.PreviewDragOver += PreviewDrag;
            drop.DragLeave += (s, e) => {
                Point point = e.GetPosition(drop);
                if (point.X < 0 || point.Y < 0 || point.X >= drop.ActualWidth || point.Y >= drop.ActualHeight) PaintDrop(false);
            };
            drop.PreviewDrop += (s, e) => {
                PaintDrop(false);
                if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) {
                    var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (paths != null && paths.Length == 1) SelectSource(paths[0]);
                    else ShowStatus("一次请选择一个文件或文件夹。", Red);
                }
                e.Handled = true;
            };
            window.Closing += (s, e) => { if (busy) { e.Cancel = true; ShowStatus("正在处理，请等待操作结束。", Blue); } };
        }
        private void ChooseFile() {
            var dialog = new OpenFileDialog { CheckFileExists = true, Title = "选择安装包或便携版", Filter = "应用文件 (*.msi;*.exe;*.zip)|*.msi;*.exe;*.zip" };
            if (dialog.ShowDialog(window) == true) SelectSource(dialog.FileName);
        }
        private sealed class Owner : Forms.IWin32Window {
            public IntPtr Handle { get; private set; }
            public Owner(Window window) { Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; }
        }
        private void ChooseFolder(bool from) {
            using (var dialog = new Forms.FolderBrowserDialog { Description = from ? "选择便携版源文件夹" : "选择目标位置", ShowNewFolderButton = !from }) {
                string initial = from ? (Directory.Exists(source) ? source : null) : root.Text;
                if (Directory.Exists(initial)) dialog.SelectedPath = initial;
                if (dialog.ShowDialog(new Owner(window)) != Forms.DialogResult.OK) return;
                if (from) SelectSource(dialog.SelectedPath); else root.Text = dialog.SelectedPath;
            }
        }
        private void PreviewDrag(object sender, DragEventArgs e) {
            bool accept = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) && (((string[])e.Data.GetData(DataFormats.FileDrop)) ?? new string[0]).Length == 1;
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            PaintDrop(accept);
            e.Handled = true;
        }
        private void PaintDrop(bool active) {
            surface.Fill = BrushFrom(active ? "#EAF3FF" : "#FAFCFF");
            surface.Stroke = active ? Blue : BrushFrom("#9DAFC7");
            tile.Background = BrushFrom(active ? "#D6E8FF" : "#E6F0FF");
            title.Text = active ? "松开以添加应用" : (source == null ? "将应用拖到这里" : Path.GetFileName(source));
        }
        private void AddMode(string label, Mode mode) { modes.Items.Add(new ComboBoxItem { Content = label, Tag = mode }); }
        private Mode? CurrentMode() { ComboBoxItem item = modes.SelectedItem as ComboBoxItem; return item != null && item.Tag is Mode ? (Mode?)item.Tag : null; }
        private void SelectSource(string value) {
            if (busy) return;
            try {
                string path = Path.GetFullPath(value.TrimEnd('\\', '/'));
                bool directory = Directory.Exists(path);
                string ext = directory ? "" : Path.GetExtension(path).ToLowerInvariant();
                if ((!directory && !File.Exists(path)) || (!directory && ext != ".zip" && ext != ".msi" && ext != ".exe"))
                    throw new IOException("请选择 MSI、EXE、ZIP 或便携版文件夹。");
                var info = ext == ".msi" ? Engine.ReadMsi(path) : new PackageInfo { Name = Engine.SuggestName(path) };
                portableName = Engine.SuggestName(path);
                if (ext == ".exe") {
                    try { string product = FileVersionInfo.GetVersionInfo(path).ProductName; if (Engine.IsName(product)) info.Name = product; }
                    catch (Win32Exception) { }
                }
                source = path;
                baseName = info.Name;
                filling = true;
                modeRow.Visibility = ext == ".exe" ? Visibility.Visible : Visibility.Collapsed;
                modes.Items.Clear();
                if (ext == ".exe") {
                    modes.Items.Add(new ComboBoxItem { Content = "请选择 EXE 的处理方式" });
                    AddMode("Inno Setup 安装包", Mode.Inno);
                    AddMode("NSIS 安装包", Mode.Nsis);
                    AddMode("单文件便携版（只复制）", Mode.Portable);
                } else if (ext == ".msi") AddMode("Windows Installer", Mode.Msi);
                else AddMode("便携版", Mode.Portable);
                modes.SelectedIndex = 0;
                folder.Text = info.Name;
                msiProperty.Text = info.Property ?? "";
                hint.Text = directory ? "便携版文件夹" : (ext == ".zip" ? "ZIP 压缩包" : (ext == ".msi" ? "MSI 安装包" : "EXE 文件 · 请确认类型"));
                glyph.Text = directory ? "\uE8B7" : "\uE8A5";
                PaintDrop(false);
                filling = false;
                RefreshAfterEdit();
            } catch (Exception ex) { ShowStatus(ex.Message, Red); }
        }
        private void ShowStatus(string text, Brush brush) { status.Text = text; status.Foreground = brush; }
        private void RefreshAfterEdit() {
            if (filling || busy) return;
            result = null;
            open.Visibility = Visibility.Collapsed;
            RefreshForm();
        }
        private void RefreshForm() {
            commit.IsEnabled = false;
            if (source == null) return;
            options.Visibility = Visibility.Visible;
            Mode? mode = CurrentMode();
            propertyRow.Visibility = mode == Mode.Msi ? Visibility.Visible : Visibility.Collapsed;
            commit.Content = mode == Mode.Portable ? "开始收纳" : "开始安装";
            string target;
            try { target = Engine.Destination(root.Text.Trim(), folder.Text); }
            catch (Exception ex) { preview.Text = "请检查目标路径。"; ShowStatus(ex.Message, Red); return; }
            preview.Text = "完整路径  " + target;
            if (mode == null) { ShowStatus("请确认 EXE 是安装包还是单文件便携版。", InkMuted); return; }
            if (mode == Mode.Msi && !Regex.IsMatch(msiProperty.Text.Trim(), @"^[A-Z][A-Z0-9_]{0,71}$")) {
                ShowStatus("未识别目录属性。请查阅厂商文档并填入属性名。", InkMuted);
                return;
            }
            if (result == null) {
                try { Engine.CheckVacant(target); }
                catch (Exception ex) { ShowStatus(ex.Message, Red); return; }
                ShowStatus(mode == Mode.Portable ? "将复制或解压到新文件夹。" : "请在原安装向导中确认目标路径。", InkMuted);
                commit.IsEnabled = !busy;
            }
        }
        private void Busy(bool value) {
            busy = value;
            pickFile.IsEnabled = pickFolder.IsEnabled = browse.IsEnabled = root.IsEnabled = options.IsEnabled = !value;
            pulse.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value) commit.IsEnabled = false;
        }
        private async Task Deploy() {
            if (!commit.IsEnabled || busy || source == null || !CurrentMode().HasValue) return;
            string input = source, target = Engine.Destination(root.Text.Trim(), folder.Text), property = msiProperty.Text.Trim();
            Mode mode = CurrentMode().Value;
            string message;
            Brush color;
            Busy(true);
            ShowStatus(mode == Mode.Portable ? "正在收纳文件…" : "正在等待安装向导结束…", Blue);
            try {
                int code = await Task.Run(() => {
                    if (mode != Mode.Portable) return Engine.RunInstaller(input, target, mode, property);
                    Engine.DeployPortable(input, target);
                    return 0;
                });
                bool restart = mode == Mode.Msi && (code == 3010 || code == 1641);
                if (code == 0 || restart) {
                    if (Directory.Exists(target)) {
                        result = target;
                        open.Visibility = Visibility.Visible;
                        message = mode == Mode.Portable ? "收纳完成。" : (restart ? "安装程序报告成功，需重启。" : "安装程序已结束，目标目录已找到。");
                        color = Green;
                    } else {
                        message = restart ? "安装程序要求重启，尚未发现目标目录。" : "安装程序已结束，但未发现目标目录。请检查安装向导的实际路径。";
                        color = InkMuted;
                    }
                } else {
                    message = mode == Mode.Msi && code == 1602 ? "安装已取消。" : "安装程序退出。代码 " + code + "，请检查安装向导提示。";
                    color = Red;
                }
            } catch (Win32Exception ex) { message = ex.NativeErrorCode == 1223 ? "操作已取消。" : ex.Message; color = Red; }
            catch (Exception ex) { message = ex.Message; color = Red; }
            Busy(false);
            RefreshForm();
            ShowStatus(message, color);
        }
        private void UiTest(string directory) {
            string file = Path.Combine(directory, "示例工具.exe");
            root.Text = directory;
            SelectSource(file);
            if (commit.IsEnabled || CurrentMode().HasValue) throw new Exception("EXE mode must be explicitly selected.");
            modes.SelectedIndex = 3;
            if (!commit.IsEnabled || !preview.Text.EndsWith("示例工具", StringComparison.Ordinal)) throw new Exception("Incorrect preview.");
            var frame = new System.Windows.Threading.DispatcherFrame();
            var task = Deploy();
            task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            if (result != Path.Combine(directory, "示例工具") || !File.Exists(Path.Combine(result, "示例工具.exe"))) throw new Exception(status.Text);
            File.WriteAllText(Path.Combine(directory, "ui-pass.txt"), status.Text);
        }
    }
}
