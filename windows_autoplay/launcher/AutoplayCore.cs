using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace A2BAutoplay
{
    public sealed class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }
    public interface IBridge
    {
        Dictionary<string, object> Call(string command, Dictionary<string, object> arguments);
    }
    public sealed class BridgeClient : IBridge
    {
        public const int Port = 17850;
        private readonly int port;
        public BridgeClient(int port = Port) { this.port = port; }
        public Dictionary<string, object> Call(string command, Dictionary<string, object> arguments)
        {
            var body = arguments == null ? new Dictionary<string, object>() : new Dictionary<string, object>(arguments);
            body["command"] = command;
            try
            {
                using (var client = new TcpClient())
                {
                    var pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    using (pending.AsyncWaitHandle)
                        if (!pending.AsyncWaitHandle.WaitOne(2000)) throw new IOException("游戏连接超时。");
                    client.EndConnect(pending);
                    client.ReceiveTimeout = 20000;
                    client.SendTimeout = 20000;
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, true))
                    {
                        var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                        writer.WriteLine(json.Serialize(body));
                        var line = reader.ReadLine();
                        if (line == null) throw new IOException("游戏关闭了连接。");
                        var result = json.Deserialize<Dictionary<string, object>>(line);
                        if (!Data.Bool(result, "ok")) throw new BridgeException(Data.Text(result, "error"));
                        return result;
                    }
                }
            }
            catch (BridgeException) { throw; }
            catch (Exception error) { throw new BridgeException("无法连接游戏插件：" + error.Message); }
        }
    }
    public static class Data
    {
        public static string Text(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
        public static bool Bool(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) && value is bool && (bool)value; }
        public static int Number(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? Convert.ToInt32(value) : 0; }
    }
    public enum StartMode { First, Current, FirstUnsolved }
    public sealed class PuzzleItem
    {
        public string Id;
        public string Title;
        public bool Solved;
        public static List<PuzzleItem> Select(Dictionary<string, object> response, StartMode mode, string currentId)
        {
            var items = new List<PuzzleItem>();
            foreach (var value in (System.Collections.IEnumerable)response["puzzles"])
            {
                var raw = (Dictionary<string, object>)value;
                var id = Data.Text(raw, "id");
                if (!Regex.IsMatch(id, @"^c[1-6]_\d+_.+$")) continue;
                items.Add(new PuzzleItem { Id = id, Title = Data.Text(raw, "title"), Solved = Data.Bool(raw, "solved") });
            }
            items = items.OrderBy(item => Coordinate(item.Id, 1)).ThenBy(item => Coordinate(item.Id, 2)).ToList();
            if (mode == StartMode.FirstUnsolved) return items.Where(item => !item.Solved).ToList();
            if (mode == StartMode.Current)
            {
                int index = items.FindIndex(item => item.Id == currentId);
                if (index < 0) throw new InvalidOperationException("请先在游戏中打开要作为起点的关卡，再点击开始。");
                return items.Skip(index).ToList();
            }
            return items;
        }
        private static int Coordinate(string id, int group)
        { return Int32.Parse(Regex.Match(id, @"^c(\d+)_(\d+)_").Groups[group].Value); }
    }
    public sealed class Candidate
    {
        public string Code;
        public string Source;
    }
    public sealed class AutomationRunner
    {
        private readonly IBridge bridge;
        private readonly Func<PuzzleItem, CancellationToken, Candidate> obtain;
        private readonly Action<int, CancellationToken> wait;
        private readonly Action<string, string, string> report;
        private volatile bool paused;
        private bool pauseSent;
        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public int Limit;
        public StartMode StartFrom = StartMode.FirstUnsolved;
        public AutomationRunner(IBridge bridge, Func<PuzzleItem, CancellationToken, Candidate> obtain,
            Action<string, string, string> report, Action<int, CancellationToken> wait)
        { this.bridge = bridge; this.obtain = obtain; this.report = report; this.wait = wait; }
        public void Pause(bool value) { paused = value; }
        private void CheckPause(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (paused)
            {
                bridge.Call("pause", null); pauseSent = true;
                while (paused) wait(200, token);
            }
            if (pauseSent) { bridge.Call("resume", null); pauseSent = false; }
            token.ThrowIfCancellationRequested();
        }
        public void Run(CancellationToken token)
        {
            string currentId = StartFrom == StartMode.Current ? Data.Text(bridge.Call("status", null), "id") : "";
            var items = PuzzleItem.Select(bridge.Call("list", null), StartFrom, currentId);
            if (Limit > 0) items = items.Take(Limit).ToList();
            report("", "计划", "本次处理 " + items.Count + " 个关卡。" + (StartFrom == StartMode.FirstUnsolved ? "跳过已通过关卡。" : "包含已通过关卡的重新验证。"));
            foreach (var item in items)
            {
                CheckPause(token);
                report(item.Id, "准备", item.Title);
                Candidate candidate;
                try { candidate = obtain(item, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { Failed++; report(item.Id, "失败", "获取答案失败：" + error.Message); continue; }
                CheckPause(token);
                bridge.Call("menu", null);
                WaitFor("menu", "", token);
                bridge.Call("select", new Dictionary<string, object> { { "id", item.Id } });
                WaitFor("puzzle", item.Id, token);
                bridge.Call("run", new Dictionary<string, object> { { "id", item.Id }, { "code", candidate.Code } });
                var elapsed = Stopwatch.StartNew();
                report(item.Id, "验证中", candidate.Source == "verified" ? "使用已验证答案" : "使用本次求解答案");
                while (true)
                {
                    CheckPause(token);
                    var status = bridge.Call("status", null);
                    if (Data.Text(status, "id") != item.Id) throw new BridgeException("运行期间关卡发生变化，自动流程已停止。");
                    if (Data.Bool(status, "terminal"))
                    {
                        if (Data.Bool(status, "success")) { Passed++; report(item.Id, "通过", "游戏完整验证通过，耗时 " + elapsed.Elapsed.ToString(@"hh\:mm\:ss") + "。"); }
                        else { Failed++; report(item.Id, "失败", Data.Text(status, "state") + "\n" + Data.Text(status, "detail")); }
                        break;
                    }
                    report(item.Id, "验证中", "用例 " + Data.Number(status, "case_index") + "/" + Data.Number(status, "case_count")
                        + "，已用 " + elapsed.Elapsed.ToString(@"hh\:mm\:ss"));
                    wait(500, token);
                }
                bridge.Call("menu", null);
                WaitFor("menu", "", token);
            }
            report("", "完成", "通过 " + Passed + "，失败 " + Failed + "。结果已记录。");
        }
        private void WaitFor(string scene, string id, CancellationToken token)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var status = bridge.Call("status", null);
                if (Data.Text(status, "scene") == scene && (id == "" || Data.Text(status, "id") == id)) return;
                wait(100, token);
            }
            throw new BridgeException("等待游戏切换界面超时。");
        }
    }
}
