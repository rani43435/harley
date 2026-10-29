using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Hands tasks to the AI agent on the player's own computer (Claude Code, Codex, Gemini CLI, Hermes...)
    /// through Tools/harley-bridge. Each player uses their own account there; Harley only knows the
    /// bridge address and token. Tasks run in the background; when one finishes, Harley tells you.
    /// </summary>
    public class HarleyComputer : MonoBehaviour
    {
        public static HarleyComputer Instance { get; private set; }

        public CompanionBrain brain;
        public bool Busy { get; private set; }
        public string AgentName { get; private set; } = "";
        public string CurrentTask { get; private set; } = "";

        CompanionConfig Config => brain != null ? brain.config : null;

        // Paired in the app (menu > Computer) wins over the Unity config
        const string PrefUrl = "harley.pc.url", PrefToken = "harley.pc.token", PrefAgent = "harley.pc.agent", PrefName = "harley.pc.name";
        public string Url => PlayerPrefs.GetString(PrefUrl, Config != null ? Config.computerBridgeUrl : "").Trim().TrimEnd('/');
        string Token => PlayerPrefs.GetString(PrefToken, Config != null ? Config.computerBridgeToken : "").Trim();
        public string Agent => PlayerPrefs.GetString(PrefAgent, Config != null ? Config.computerAgent : "").Trim().ToLowerInvariant();
        public string ComputerName => PlayerPrefs.GetString(PrefName, "");
        public bool Available => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(Token);
        public bool Connected { get; private set; }

        public class Found { public string name, url, agent; }
        public class AgentInfo { public string id, name; public bool installed; }
        public readonly List<AgentInfo> Agents = new List<AgentInfo>();

        void Awake()
        {
            Instance = this;
            if (!brain) brain = GetComponent<CompanionBrain>();
        }

        IEnumerator Start()
        {
            yield return Check(null);
        }

        /// <summary>Talks to the bridge: is it there, which agents does it have.</summary>
        public IEnumerator Check(Action<bool> done)
        {
            Connected = false;
            if (!Available) { done?.Invoke(false); yield break; }
            yield return Get("/agents", resp =>
            {
                Agents.Clear();
                if (resp.TryGetValue("agents", out var list) && list is List<object> l)
                    foreach (var o in l)
                        if (o is Dictionary<string, object> a)
                            Agents.Add(new AgentInfo { id = a["id"] as string, name = a["name"] as string, installed = a.TryGetValue("installed", out var i) && i is bool b && b });
                string def = resp.TryGetValue("default", out var d) ? d as string : "";
                AgentName = string.IsNullOrEmpty(Agent) ? def : Agent;
                Connected = true;
                Debug.Log("[Harley] Computer bridge OK, agent: " + AgentName);
            }, err => Debug.LogWarning("[Harley] Computer bridge not reachable: " + err));
            done?.Invoke(Connected);
        }

        // ---------- pairing (menu > Computer) ----------

        /// <summary>Looks for computers running the bridge on this Wi-Fi (about 2 seconds).</summary>
        public IEnumerator Discover(List<Found> into)
        {
            into.Clear();
            var found = new List<Found>();
            var thread = new Thread(() =>
            {
                try
                {
                    using (var udp = new UdpClient())
                    {
                        udp.EnableBroadcast = true;
                        udp.Client.ReceiveTimeout = 400;
                        var msg = Encoding.ASCII.GetBytes("HARLEY_DISCOVER");
                        DateTime end = DateTime.UtcNow.AddSeconds(2);
                        for (int round = 0; DateTime.UtcNow < end; round++)
                        {
                            if (round % 3 == 0) udp.Send(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, 8766));
                            try
                            {
                                var from = new IPEndPoint(IPAddress.Any, 0);
                                var data = udp.Receive(ref from);
                                if (MiniJson.Parse(Encoding.UTF8.GetString(data)) is Dictionary<string, object> d && d.ContainsKey("harley_bridge"))
                                {
                                    string port = d.TryGetValue("port", out var p) ? Convert.ToString(p) : "8765";
                                    var f = new Found { name = d["name"] as string, url = "http://" + from.Address + ":" + port, agent = d.TryGetValue("agent", out var ag) ? ag as string : "" };
                                    lock (found) if (!found.Exists(x => x.url == f.url)) found.Add(f);
                                }
                            }
                            catch (SocketException) { }
                        }
                    }
                }
                catch (Exception e) { Debug.LogWarning("[Harley] Computer search: " + e.Message); }
            }) { IsBackground = true };
            thread.Start();
            while (thread.IsAlive) yield return null;
            lock (found) into.AddRange(found);
        }

        /// <summary>Step 1: the computer shows a 6-digit code.</summary>
        public IEnumerator PairStart(string url, Action<bool, string> done)
        {
            yield return Send("POST", url, "/pair/start", "{}", null, r => done(true, ""), e => done(false, e));
        }

        /// <summary>Step 2: the code the player typed. On success Harley remembers this computer.</summary>
        public IEnumerator PairFinish(string url, string code, string name, Action<bool, string> done)
        {
            Dictionary<string, object> ok = null; string err = null;
            yield return Send("POST", url, "/pair/finish", MiniJson.Serialize(new Dictionary<string, object> { { "code", code } }), null, r => ok = r, e => err = e);
            if (ok == null || !(ok.TryGetValue("token", out var t) && t is string token)) { done(false, err != null && err.Contains("401") ? "wrong code" : err); yield break; }
            PlayerPrefs.SetString(PrefUrl, url);
            PlayerPrefs.SetString(PrefToken, token);
            PlayerPrefs.SetString(PrefName, ok.TryGetValue("name", out var n) ? n as string : (name ?? ""));
            PlayerPrefs.DeleteKey(PrefAgent);
            PlayerPrefs.Save();
            yield return Check(null);
            done(true, "");
        }

        public void SetAgent(string id)
        {
            PlayerPrefs.SetString(PrefAgent, id ?? "");
            PlayerPrefs.Save();
            AgentName = id;
        }

        public void Disconnect()
        {
            // Empty strings so the Unity config can't sneak back in after you unpair
            PlayerPrefs.SetString(PrefUrl, ""); PlayerPrefs.SetString(PrefToken, "");
            PlayerPrefs.DeleteKey(PrefAgent); PlayerPrefs.DeleteKey(PrefName);
            PlayerPrefs.Save();
            Connected = false; AgentName = ""; Agents.Clear();
        }

        /// <summary>Sends a task. Returns false if one is already running or there is no bridge.</summary>
        public bool Run(string task)
        {
            if (!Available || Busy || string.IsNullOrWhiteSpace(task)) return false;
            StartCoroutine(RunRoutine(task.Trim()));
            return true;
        }

        IEnumerator RunRoutine(string task)
        {
            Busy = true;
            CurrentTask = task;
            string id = null, error = null;
            var body = new Dictionary<string, object> { { "prompt", task } };
            if (!string.IsNullOrEmpty(Agent)) body["agent"] = Agent;
            yield return Send("POST", "/run", MiniJson.Serialize(body), r => id = r.TryGetValue("id", out var v) ? v as string : null, e => error = e);

            string status = "error", output = error ?? "no job id";
            if (id != null)
            {
                float started = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - started < 1000f)
                {
                    yield return new WaitForSecondsRealtime(3f);
                    Dictionary<string, object> job = null; string pollErr = null;
                    yield return Get("/job/" + id, r => job = r, e => pollErr = e);
                    if (job == null) { if (pollErr != null && pollErr.Contains("404")) { output = "the computer forgot the task"; break; } continue; }
                    status = job.TryGetValue("status", out var s) ? s as string : "";
                    if (status == "done" || status == "error") { output = job.TryGetValue("output", out var o) ? o as string : ""; break; }
                }
            }
            Busy = false;
            CurrentTask = "";
            if (output != null && output.Length > 1500) output = "..." + output.Substring(output.Length - 1500);
            string agent = string.IsNullOrEmpty(AgentName) ? "the computer" : AgentName;
            brain?.ComputerFinished(status == "done"
                ? $"[event] Your helper on the player's computer ({agent}) finished the task \"{Short(task)}\". Its report: {output}\nTell the player the result in one to three spoken sentences, in their language."
                : $"[event] The task \"{Short(task)}\" on the player's computer ({agent}) didn't work: {output}\nTell the player briefly and kindly; suggest checking that the bridge is running on the computer if it couldn't connect.");
        }

        static string Short(string s) => s.Length > 120 ? s.Substring(0, 120) + "..." : s;

        IEnumerator Get(string path, Action<Dictionary<string, object>> ok, Action<string> fail) => Send("GET", Url, path, null, Token, ok, fail);
        IEnumerator Send(string method, string path, string json, Action<Dictionary<string, object>> ok, Action<string> fail) => Send(method, Url, path, json, Token, ok, fail);

        IEnumerator Send(string method, string baseUrl, string path, string json, string token, Action<Dictionary<string, object>> ok, Action<string> fail)
        {
            string url = baseUrl.Trim().TrimEnd('/') + path;
            using (var req = new UnityWebRequest(url, method))
            {
                if (json != null) req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", "Bearer " + token);
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { fail($"HTTP {req.responseCode}: {req.error}"); yield break; }
                Dictionary<string, object> resp = null;
                try { resp = MiniJson.Parse(req.downloadHandler.text) as Dictionary<string, object>; } catch (Exception e) { fail(e.Message); yield break; }
                if (resp == null) fail("empty"); else ok(resp);
            }
        }

        /// <summary>For the prompt.</summary>
        public string DescribeForPrompt()
        {
            if (!Available) return "";
            string agent = string.IsNullOrEmpty(AgentName) ? "an AI agent" : AgentName;
            return "\nThe player's computer: you can hand tasks to " + agent + " running on their computer (it can read and change files in their projects, use git, explain code, check things). " +
                   "Use action computer with target = a clear, complete task in English (include what they said and which project). " +
                   "Use it only when the player asks for something to be done on their computer. " +
                   "Before anything that changes things (editing code, commit, push, deleting, installing), first say what will happen and ask if they're sure; only send it after they say yes. Reading, checking and explaining need no question. " +
                   "Tasks take a while: say you're on it; you'll get an [event] with the result. " +
                   (Busy ? "A task is running right now: \"" + Short(CurrentTask) + "\" (only one at a time)." : "");
        }
    }
}
