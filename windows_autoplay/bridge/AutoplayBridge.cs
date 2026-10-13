using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace A2BAutoplay
{
    [BepInPlugin("local.a2b.autoplay", "A2B Autoplay Bridge", "0.1.0")]
    [BepInProcess("A2B.exe")]
    public sealed class AutoplayBridge : BaseUnityPlugin
    {
        public const int Port = 17850;
        private readonly Queue<Request> requests = new Queue<Request>();
        private TcpListener listener;
        private volatile bool stopping;
        private bool pausedByBridge;
        private Manager pendingRun;
        private int runAfterFrame;
        private string runError;

        private sealed class Request
        {
            public JObject Body;
            public object Result;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        private void Awake()
        {
            Application.runInBackground = true;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                var thread = new Thread(Listen) { IsBackground = true, Name = "A2B bridge" };
                thread.Start();
                Logger.LogInfo("A2B bridge ready on 127.0.0.1:" + Port);
            }
            catch (Exception error) { Logger.LogError(error); }
        }

        private void Listen()
        {
            while (!stopping)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
                }
                catch (SocketException) { if (!stopping) Logger.LogWarning("Bridge socket closed."); }
                catch (ObjectDisposedException) { break; }
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;
                try
                {
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        var line = reader.ReadLine();
                        if (line == null) return;
                        var request = new Request { Body = JObject.Parse(line) };
                        lock (requests) requests.Enqueue(request);
                        if (!request.Done.WaitOne(15000))
                            writer.WriteLine(JsonConvert.SerializeObject(new { ok = false, error = "Game main thread did not respond." }));
                        else { writer.WriteLine(JsonConvert.SerializeObject(request.Result)); request.Done.Close(); }
                    }
                }
                catch (Exception error) { Logger.LogWarning("Bridge request: " + error.Message); }
            }
        }

        private void Update()
        {
            if (pendingRun != null && Time.frameCount >= runAfterFrame && !pausedByBridge)
            {
                var manager = pendingRun;
                pendingRun = null;
                try { manager.Run(-1f); }
                catch (Exception error) { runError = error.Message; Logger.LogWarning(error); }
            }
            for (int index = 0; index < 8; index++)
            {
                Request request;
                lock (requests)
                {
                    if (requests.Count == 0) return;
                    request = requests.Dequeue();
                }
                try { request.Result = Handle(request.Body); }
                catch (Exception error) { Logger.LogWarning(error); request.Result = new { ok = false, error = error.Message }; }
                finally { request.Done.Set(); }
            }
        }

        private object Handle(JObject body)
        {
            var global = UnityEngine.Object.FindObjectOfType<GlobalManager>();
            var manager = UnityEngine.Object.FindObjectOfType<Manager>();
            var command = (string)body["command"];
            if (command == "ping") return new { ok = true, version = "0.1.0", ready = global != null };
            if (command == "quit") { Application.Quit(); return new { ok = true }; }
            if (global == null) throw new InvalidOperationException("Game is still loading.");
            if (command == "list")
            {
                var items = new List<object>();
                foreach (var level in global.levels)
                {
                    if (!Regex.IsMatch(level.id ?? "", @"^c[1-6]_\d+_.+$")) continue;
                    bool solved = global.solved.Contains(level.id);
                    foreach (var state in global.sv.data)
                        if (state.id == level.id) solved = state.solved;
                    items.Add(new { id = level.id, title = level.title_ch, solved = solved });
                }
                return new { ok = true, puzzles = items, save_slot = global.setting.saveslot };
            }
            if (command == "select")
            {
                if (manager != null) throw new InvalidOperationException("Return to the menu before selecting another puzzle.");
                var id = (string)body["id"];
                foreach (var level in global.levels)
                {
                    if (level.id != id) continue;
                    if (!Regex.IsMatch(id, @"^c[1-6]_\d+_.+$")) break;
                    pausedByBridge = false;
                    global.ChooseLevel(level);
                    return new { ok = true };
                }
                throw new InvalidOperationException("Unknown puzzle: " + id);
            }
            if (command == "menu")
            {
                pendingRun = null;
                runError = null;
                pausedByBridge = false;
                if (manager != null)
                {
                    manager.ResetProg();
                    manager.ReturnToMenu();
                }
                return new { ok = true };
            }
            if (manager == null)
            {
                if (command == "pause" || command == "resume") return new { ok = true };
                if (command == "status") return new { ok = true, scene = "menu", id = "", state = "Menu", terminal = false };
                throw new InvalidOperationException("No puzzle is open.");
            }
            if (command == "run")
            {
                if ((string)body["id"] != manager.FileName) throw new InvalidOperationException("Selected puzzle changed.");
                if (manager.gameState == GameState.Run) throw new InvalidOperationException("A program is already running.");
                var code = (string)body["code"];
                if (code == null) throw new InvalidOperationException("Missing program text.");
                manager.ResetProg();
                manager.isCustomTest = false;
                typeof(Manager).GetField("customtest2", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, false);
                manager.program.inputField.text = code.Replace("\r\n", "\n");
                manager.prog = manager.program.inputField.text;
                pausedByBridge = false;
                manager.program.inputField.ForceLabelUpdate();
                pendingRun = manager;
                runAfterFrame = Time.frameCount + 2;
                runError = null;
                return new { ok = true };
            }
            if (command == "pause")
            {
                if (pendingRun != null) pausedByBridge = true;
                if (manager.gameState == GameState.Run) { manager.PauseRun(); pausedByBridge = true; }
                return new { ok = true };
            }
            if (command == "resume")
            {
                if (pausedByBridge && manager.gameState == GameState.Pause)
                {
                    manager.gameState = GameState.Run;
                }
                pausedByBridge = false;
                return new { ok = true };
            }
            if (command == "status")
            {
                bool terminal = runError != null || manager.gameState == GameState.Win || manager.gameState == GameState.Lose
                    || manager.gameState == GameState.Error || (manager.gameState == GameState.Pause && !pausedByBridge);
                bool success = manager.gameState == GameState.Win && !manager.isCustomTest
                    && manager.input_str != null && manager.running_test >= manager.input_str.Count;
                var log = manager.gamelog.inputField.text ?? "";
                if (log.Length > 6000) log = log.Substring(log.Length - 6000);
                return new
                {
                    ok = true, scene = "puzzle", id = manager.FileName, state = runError != null ? "RunError" : pendingRun != null ? "Preparing" : manager.gameState.ToString(),
                    terminal = terminal, success = success, paused = pausedByBridge,
                    case_index = manager.running_test, case_count = manager.input_str == null ? 0 : manager.input_str.Count,
                    detail = runError == null ? log : runError + "\n" + log
                };
            }
            throw new InvalidOperationException("Unknown command: " + command);
        }

        private void OnDestroy()
        {
            stopping = true;
            if (listener != null) listener.Stop();
        }
    }
}
