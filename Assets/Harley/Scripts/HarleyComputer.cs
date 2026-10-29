using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
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
        public bool Available => Config != null && Config.HasComputer;

        void Awake()
        {
            Instance = this;
            if (!brain) brain = GetComponent<CompanionBrain>();
        }

        IEnumerator Start()
        {
            if (!Available) yield break;
            // Which agents does this computer have? (also checks the address and token)
            yield return Get("/agents", resp =>
            {
                string want = (Config.computerAgent ?? "").Trim().ToLowerInvariant();
                string def = resp.TryGetValue("default", out var d) ? d as string : "";
                AgentName = string.IsNullOrEmpty(want) ? def : want;
                Debug.Log("[Harley] Computer bridge OK, agent: " + AgentName);
            }, err => Debug.LogWarning("[Harley] Computer bridge not reachable: " + err));
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
            if (!string.IsNullOrWhiteSpace(Config.computerAgent)) body["agent"] = Config.computerAgent.Trim().ToLowerInvariant();
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

        IEnumerator Get(string path, Action<Dictionary<string, object>> ok, Action<string> fail) => Send("GET", path, null, ok, fail);

        IEnumerator Send(string method, string path, string json, Action<Dictionary<string, object>> ok, Action<string> fail)
        {
            string url = Config.computerBridgeUrl.Trim().TrimEnd('/') + path;
            using (var req = new UnityWebRequest(url, method))
            {
                if (json != null) req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", "Bearer " + Config.computerBridgeToken.Trim());
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
