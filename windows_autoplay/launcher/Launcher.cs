using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace A2BAutoplay
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var instance = new Mutex(true, "Local\\A2BAutoplay", out created))
            {
                if (!created) { MessageBox.Show("程序已经运行，请使用已打开的窗口。", "A=B 自动验证"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LauncherForm(args));
            }
        }
    }

    public sealed class LauncherForm : Form
    {
        private readonly string root = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string dataRoot;
        private readonly string[] arguments;
        private readonly TextBox pathBox = new TextBox();
        private readonly ComboBox startMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        private readonly Label status = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly TextBox detail = new TextBox();
        private readonly Button start = new Button { Text = "开始自动验证" };
        private readonly Button pause = new Button { Text = "暂停", Enabled = false };
        private readonly Button stop = new Button { Text = "停止", Enabled = false };
        private readonly Dictionary<string, int> rowIndices = new Dictionary<string, int>();
        private CancellationTokenSource cancellation;
        private AutomationRunner runner;
        private volatile bool busy;
        private bool paused;
        private string runLog;
        private readonly object logLock = new object();
        private readonly BridgeClient bridge = new BridgeClient();

        public LauncherForm(string[] args)
        {
            arguments = args;
            var configuredData = Environment.GetEnvironmentVariable("A2B_AUTOPLAY_DATA_DIRECTORY");
            dataRoot = String.IsNullOrWhiteSpace(configuredData) ? root : Path.GetFullPath(configuredData);
            Text = "A=B 自动验证";
            ClientSize = new Size(920, 660);
            MinimumSize = new Size(760, 480);
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            pathRow.Controls.Add(new Label { Text = "游戏目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            pathBox.Dock = DockStyle.Fill;
            pathRow.Controls.Add(pathBox, 1, 0);
            var browse = new Button { Text = "选择目录", Dock = DockStyle.Fill };
            browse.Click += delegate
            {
                if (busy) return;
                using (var dialog = new FolderBrowserDialog { Description = "选择包含 A2B.exe 的游戏目录" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
            };
            pathRow.Controls.Add(browse, 2, 0);
            layout.Controls.Add(pathRow, 0, 0);
            var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
            modeRow.Controls.Add(new Label { Text = "起始关卡", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
            startMode.Items.AddRange(new object[] { "从第一关开始", "从当前关开始", "从首个未通过关卡开始" });
            startMode.SelectedIndex = 2;
            modeRow.Controls.Add(startMode);
            modeRow.Controls.Add(new Label { Text = "从当前关开始：请先在游戏中打开该关卡。", AutoSize = true, Margin = new Padding(12, 6, 0, 0) });
            layout.Controls.Add(modeRow, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
            foreach (var button in new[] { start, pause, stop }) { button.Size = new Size(140, 32); buttons.Controls.Add(button); }
            var openData = new Button { Text = "打开备份和日志", Size = new Size(140, 32) };
            openData.Click += delegate
            {
                Directory.CreateDirectory(dataRoot);
                Process.Start(new ProcessStartInfo(dataRoot) { UseShellExecute = true });
            };
            buttons.Controls.Add(openData);
            start.Click += delegate { StartWork(); };
            pause.Click += delegate { paused = !paused; if (runner != null) runner.Pause(paused); pause.Text = paused ? "继续" : "暂停"; };
            stop.Click += delegate { if (cancellation != null) cancellation.Cancel(); status.Text = "正在停止，并返回关卡界面…"; };
            layout.Controls.Add(buttons, 0, 2);
            status.Text = "首次运行会部署游戏插件；自动运行前备份游戏存档。";
            status.Dock = DockStyle.Fill;
            layout.Controls.Add(status, 0, 3);
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add("id", "题目"); grid.Columns.Add("state", "状态"); grid.Columns.Add("message", "进度 / 结果");
            grid.Columns[0].FillWeight = 35; grid.Columns[1].FillWeight = 20; grid.Columns[2].FillWeight = 75;
            layout.Controls.Add(grid, 0, 4);
            detail.Dock = DockStyle.Fill;
            detail.Multiline = true; detail.ReadOnly = true; detail.ScrollBars = ScrollBars.Vertical;
            layout.Controls.Add(detail, 0, 5);
            Controls.Add(layout);
            Shown += delegate
            {
                pathBox.Text = FindGame();
                for (int index = 0; index < arguments.Length - 1; index++)
                    if (arguments[index] == "--start-mode")
                        startMode.SelectedIndex = arguments[index + 1] == "first" ? 0 : arguments[index + 1] == "current" ? 1 : 2;
                if (arguments.Contains("--auto-start") || arguments.Contains("--connect-only")) StartWork();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!busy) return;
                e.Cancel = true;
                cancellation.Cancel();
                status.Text = "正在停止。完成后可关闭窗口。";
            };
        }

        private void Ui(Action action)
        {
            if (!IsDisposed && IsHandleCreated)
            {
                try { BeginInvoke(action); } catch (InvalidOperationException) { }
            }
        }
        private void Report(string id, string state, string message)
        {
            var record = new Dictionary<string, object> { { "time", DateTime.Now.ToString("o") }, { "id", id }, { "state", state }, { "message", message } };
            if (state != "验证中" || !message.StartsWith("用例 "))
            {
                lock (logLock) if (runLog != null) File.AppendAllText(runLog, new JavaScriptSerializer().Serialize(record) + "\n", new UTF8Encoding(false));
            }
            Ui(delegate
            {
                status.Text = (id == "" ? "" : id + "：") + state + "  " + message.Split('\n')[0];
                if (id != "")
                {
                    int index;
                    if (!rowIndices.TryGetValue(id, out index)) { index = grid.Rows.Add(id, state, message.Split('\n')[0]); rowIndices[id] = index; }
                    else { grid.Rows[index].Cells[1].Value = state; grid.Rows[index].Cells[2].Value = message.Split('\n')[0]; }
                    if (state == "通过") grid.Rows[index].DefaultCellStyle.BackColor = Color.Honeydew;
                    if (state == "失败") grid.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
                }
                if (state != "验证中" || !message.StartsWith("用例 ")) detail.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + id + " " + state + "\r\n" + message.Replace("\n", "\r\n") + "\r\n\r\n");
            });
        }
        private void StartWork()
        {
            if (busy) return;
            var game = pathBox.Text.Trim();
            var mode = (StartMode)startMode.SelectedIndex;
            if (!File.Exists(Path.Combine(game, "A2B.exe"))) { status.Text = "未找到游戏，请选择包含 A2B.exe 的目录。"; return; }
            busy = true; paused = false;
            start.Enabled = false; pause.Enabled = false; stop.Enabled = true; pathBox.Enabled = false; startMode.Enabled = false;
            rowIndices.Clear(); grid.Rows.Clear(); detail.Clear();
            cancellation = new CancellationTokenSource();
            Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
            runLog = Path.Combine(dataRoot, "logs", "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jsonl");
            File.WriteAllText(Path.Combine(dataRoot, "settings.json"), new JavaScriptSerializer().Serialize(new { game_path = game, start_mode = (int)mode }), Encoding.UTF8);
            Task.Factory.StartNew(delegate
            {
                try
                {
                    EnsureConnected(game, cancellation.Token);
                    if (arguments.Contains("--connect-only")) { Report("", "连接成功", "插件已加载；本次不运行关卡。"); return; }
                    Backup(game);
                    runner = new AutomationRunner(bridge, delegate(PuzzleItem puzzle, CancellationToken token) { return Obtain(game, puzzle, token); }, Report, Wait);
                    runner.StartFrom = mode;
                    for (int index = 0; index < arguments.Length - 1; index++)
                        if (arguments[index] == "--limit") runner.Limit = Int32.Parse(arguments[index + 1]);
                    Ui(delegate { pause.Enabled = true; });
                    runner.Run(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    try { bridge.Call("menu", null); } catch (BridgeException) { }
                    Report("", "已停止", "当前自动流程已停止。再次开始会重新读取游戏进度。");
                }
                catch (Exception error) { Report("", "自动调度停止", error.Message); }
                finally
                {
                    runner = null; busy = false;
                    Ui(delegate { start.Enabled = true; pause.Enabled = false; stop.Enabled = false; pathBox.Enabled = true; startMode.Enabled = true; pause.Text = "暂停"; });
                }
            });
        }
        private static void Wait(int milliseconds, CancellationToken token)
        { if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested(); }

        private bool Connected()
        {
            try { return Data.Bool(bridge.Call("ping", null), "ready"); }
            catch (BridgeException) { return false; }
        }
        private void EnsureConnected(string game, CancellationToken token)
        {
            if (Connected()) return;
            if (Process.GetProcessesByName("A2B").Length > 0)
                throw new InvalidOperationException("游戏已运行但插件尚未连接。请退出游戏，再点击开始。");
            Report("", "准备", "正在部署游戏桥接插件…");
            Deploy(game);
            token.ThrowIfCancellationRequested();
            Report("", "启动", "正在启动 A=B，等待插件连接…");
            Process.Start(new ProcessStartInfo(Path.Combine(game, "A2B.exe")) { WorkingDirectory = game, UseShellExecute = true });
            for (int attempt = 0; attempt < 60; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (Connected()) return;
                Wait(1000, token);
            }
            throw new InvalidOperationException("插件连接超时。请查看游戏目录 BepInEx/LogOutput.log。");
        }
        private void Deploy(string game)
        {
            var loader = Path.Combine(root, "payload", "loader");
            var dll = Path.Combine(root, "payload", "A2B.Autoplay.Bridge.dll");
            if (!Directory.Exists(loader) || !File.Exists(dll)) throw new FileNotFoundException("程序资源不完整，请重新下载发布程序。");
            var installedLoader = Path.Combine(game, "BepInEx", "core", "BepInEx.dll");
            if (!File.Exists(installedLoader))
            {
                if (File.Exists(Path.Combine(game, "winhttp.dll"))) throw new InvalidOperationException("游戏目录已有其他加载器，请先确认其兼容性。");
                CopyDirectory(loader, game, false);
            }
            var pluginDirectory = Path.Combine(game, "BepInEx", "plugins", "A2BAutoplay");
            Directory.CreateDirectory(pluginDirectory);
            File.Copy(dll, Path.Combine(pluginDirectory, Path.GetFileName(dll)), true);
        }
        private void Backup(string game)
        {
            var source = Path.Combine(game, "A2B_Data", "save");
            var destination = Path.Combine(dataRoot, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            if (Directory.Exists(source)) CopyDirectory(source, destination, false);
            Report("", "备份完成", destination);
        }
        private static void CopyDirectory(string source, string destination, bool overwrite)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
            foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite);
        }
        private Candidate Obtain(string game, PuzzleItem puzzle, CancellationToken token)
        {
            var python = Path.Combine(root, "runtime", "python.exe");
            var helper = Path.Combine(root, "solver", "tools", "autoplay_candidate.py");
            var puzzleDirectory = Path.Combine(game, "A2B_Data", "task");
            var info = new ProcessStartInfo(python, Quote(helper) + " " + Quote(puzzle.Id) + " --puzzle-directory " + Quote(puzzleDirectory))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.Combine(root, "solver") };
            var stdout = new StringBuilder(); var stderr = new StringBuilder();
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                try { while (!process.WaitForExit(200)) token.ThrowIfCancellationRequested(); process.WaitForExit(); token.ThrowIfCancellationRequested(); }
                catch { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } throw; }
                var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(stdout.ToString());
                if (process.ExitCode != 0) throw new InvalidOperationException(Data.Text(result, "error") + " " + stderr.ToString());
                return new Candidate { Code = Data.Text(result, "code"), Source = Data.Text(result, "source") };
            }
        }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        private string FindGame()
        {
            var settings = Path.Combine(dataRoot, "settings.json");
            if (File.Exists(settings))
            {
                try
                {
                    var saved = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settings));
                    if (saved.ContainsKey("start_mode"))
                    {
                        int mode = Data.Number(saved, "start_mode");
                        if (mode >= 0 && mode <= 2) startMode.SelectedIndex = mode;
                    }
                    var path = Data.Text(saved, "game_path");
                    if (File.Exists(Path.Combine(path, "A2B.exe"))) return path;
                }
                catch (Exception) { }
            }
            var steam = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", ""));
            var libraries = new List<string>();
            if (steam != "") libraries.Add(steam);
            var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(folders))
                foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            foreach (var library in libraries)
            {
                var path = Path.Combine(library, "steamapps", "common", "A2B");
                if (File.Exists(Path.Combine(path, "A2B.exe"))) return path;
            }
            return "";
        }
    }
}
