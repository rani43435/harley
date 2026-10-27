using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>One turn in the conversation. Images are only attached to the newest user turn.</summary>
    public class ChatTurn
    {
        public bool fromUser;
        public string text;
        public string jpegBase64; // optional, user turns only
    }

    /// <summary>
    /// Minimal Claude Messages API client for Unity (no packages needed).
    /// - Sends text + an optional picture (camera or screen).
    /// - Asks for a reply that matches Harley's JSON schema (structured outputs).
    /// - Runs tools in a loop (web search, weather) before the final reply.
    /// - SearchWeb() is a separate small call that uses Claude's web search tool.
    /// </summary>
    public class ClaudeClient : MonoBehaviour
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string ApiVersion = "2023-06-01";
        const int MaxToolRounds = 4;

        public CompanionConfig config;

        public bool Busy { get; private set; }

        // The shape every reply must have. Enums keep the actions to things Harley can actually do.
        const string ReplySchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""say"":     { ""type"": ""string"" },
    ""emotion"": { ""type"": ""string"", ""enum"": [""neutral"",""happy"",""excited"",""curious"",""surprised"",""sad"",""sleepy"",""love""] },
    ""action"":  { ""type"": ""string"", ""enum"": [""stay"",""follow"",""come_close"",""wander"",""look_at_thing"",""sit"",""hide"",""spin"",""dance"",""wave"",""go_to""] },
    ""target"":  { ""type"": ""string"" },
    ""remember"": { ""type"": ""string"" },
    ""forget"":  { ""type"": ""boolean"" }
  },
  ""required"": [""say"",""emotion"",""action"",""target"",""remember"",""forget""],
  ""additionalProperties"": false
}";

        static object _schema;
        static object Schema => _schema ??= MiniJson.Parse(ReplySchema);

        /// <summary>
        /// Gets Harley's reply. If tools are given, Claude may call them first
        /// (onToolStart tells you which one, e.g. to show "checking the weather").
        /// </summary>
        public IEnumerator Send(string system, List<ChatTurn> turns, CompanionTools tools,
                                Action<string> onText, Action<string> onError, Action<string> onToolStart = null)
        {
            if (config != null && config.provider == AiProvider.Gemini)
            {
                Busy = true;
                try { yield return GeminiClient.Send(config, system, turns, tools, onText, onError, onToolStart); }
                finally { Busy = false; }
                yield break;
            }
            if (!HasKey(onError)) yield break;
            Busy = true;
            try
            {
                var messages = BuildMessages(turns);
                List<object> toolDefs = tools != null ? tools.Definitions() : null;

                for (int round = 0; round <= MaxToolRounds; round++)
                {
                    var body = MiniJson.Obj(
                        "model", config.model,
                        "max_tokens", config.maxTokens,
                        "system", system,
                        "messages", messages,
                        "output_config", MiniJson.Obj("format", MiniJson.Obj("type", "json_schema", "schema", Schema)));
                    if (toolDefs != null && toolDefs.Count > 0) body["tools"] = toolDefs; // keep defined while tool blocks are in the history

                    Dictionary<string, object> resp = null;
                    string error = null;
                    yield return Post(MiniJson.Serialize(body), r => resp = r, e => error = e);
                    if (error != null) { onError?.Invoke(error); yield break; }

                    List<object> content = resp.ListAt("content") ?? new List<object>();
                    string stop = resp.Str("stop_reason");

                    if (stop == "tool_use" && tools != null)
                    {
                        messages.Add(MiniJson.Obj("role", "assistant", "content", content));
                        var results = new List<object>();
                        foreach (var o in content)
                        {
                            if (!(o is Dictionary<string, object> block) || block.Str("type") != "tool_use") continue;
                            string name = block.Str("name");
                            onToolStart?.Invoke(name);
                            string result = null;
                            yield return tools.Run(name, block.ObjAt("input") ?? new Dictionary<string, object>(), r => result = r);
                            results.Add(MiniJson.Obj(
                                "type", "tool_result",
                                "tool_use_id", block.Str("id"),
                                "content", string.IsNullOrWhiteSpace(result) ? "No result." : result));
                        }
                        messages.Add(MiniJson.Obj("role", "user", "content", results));
                        continue;
                    }

                    string text = CollectText(content);
                    if (string.IsNullOrEmpty(text)) { onError?.Invoke("Empty reply (stop_reason: " + stop + ")"); yield break; }
                    onText?.Invoke(text);
                    yield break;
                }
                onError?.Invoke("Too many tool rounds.");
            }
            finally { Busy = false; }
        }

        /// <summary>
        /// Looks something up on the web with Claude's web search tool and returns a short plain-text answer.
        /// fallbackCity / fallbackCountry: the player's guessed location, used when Home City isn't set.
        /// If the configured tool version or model is refused, it retries with the long-standing web search version.
        /// </summary>
        public IEnumerator SearchWeb(string query, Action<string> done, string fallbackCity = "", string fallbackCountry = "")
        {
            if (config != null && config.provider == AiProvider.Gemini)
            {
                string c = !string.IsNullOrWhiteSpace(config.homeCity) ? config.homeCity : fallbackCity;
                yield return GeminiClient.Search(config, query, c, done);
                yield break;
            }
            if (!HasKey(e => done?.Invoke("Search failed: " + e))) yield break;

            string model = string.IsNullOrWhiteSpace(config.searchModel) ? config.model : config.searchModel;
            var attempts = new List<(string model, string toolType)> { (model, config.webSearchToolType) };
            if (config.webSearchToolType != "web_search_20250305") attempts.Add((model, "web_search_20250305"));
            if (model != config.model) attempts.Add((config.model, "web_search_20250305"));

            string lastError = null;
            foreach (var attempt in attempts)
            {
                string answer = null;
                lastError = null;
                yield return SearchOnce(query, attempt.model, attempt.toolType, fallbackCity, fallbackCountry, a => answer = a, e => lastError = e);
                if (lastError == null)
                {
                    done?.Invoke(string.IsNullOrWhiteSpace(answer) ? "The search found nothing useful." : answer.Trim());
                    yield break;
                }
                // Only a refused request (bad tool version / model) is worth another try.
                if (!lastError.StartsWith("HTTP 400", StringComparison.Ordinal) && !lastError.StartsWith("HTTP 404", StringComparison.Ordinal)) break;
                Debug.LogWarning("[Harley] Web search attempt refused (" + attempt.model + ", " + attempt.toolType + "): " + lastError);
            }
            done?.Invoke("Search failed: " + lastError);
        }

        IEnumerator SearchOnce(string query, string model, string toolType, string fallbackCity, string fallbackCountry,
                               Action<string> ok, Action<string> fail)
        {
            var tool = MiniJson.Obj(
                "type", toolType,
                "name", "web_search",
                "max_uses", Mathf.Clamp(config.maxSearchesPerQuestion, 1, 5));
            if (string.CompareOrdinal(toolType, "web_search_20260209") >= 0)
                tool["allowed_callers"] = new List<object> { "direct" };
            string city = !string.IsNullOrWhiteSpace(config.homeCity) ? config.homeCity.Split(',')[0].Trim() : (fallbackCity ?? "").Trim();
            string country = !string.IsNullOrWhiteSpace(config.countryCode) ? config.countryCode.Trim() : (fallbackCountry ?? "").Trim();
            if (city.Length > 0 || country.Length == 2)
            {
                var loc = MiniJson.Obj("type", "approximate");
                if (city.Length > 0) loc["city"] = city;
                if (country.Length == 2) loc["country"] = country.ToUpperInvariant();
                tool["user_location"] = loc; // so "weather" or "news" searches are about the player's area
            }

            var messages = new List<object>
            {
                MiniJson.Obj("role", "user", "content",
                    "Search the web and answer with the key facts (numbers, names, dates, scores) for: " + query +
                    "\nAnswer in plain text, at most 90 words, no markdown, no links.")
            };
            string system = "You are a fast research helper. Today is " + DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm",
                System.Globalization.CultureInfo.InvariantCulture) + " local time.";

            var answer = new StringBuilder();
            for (int round = 0; round < 3; round++) // pause_turn can ask us to continue
            {
                var body = MiniJson.Obj(
                    "model", model,
                    "max_tokens", 900,
                    "system", system,
                    "messages", messages,
                    "tools", new List<object> { tool });

                Dictionary<string, object> resp = null;
                string error = null;
                yield return Post(MiniJson.Serialize(body), r => resp = r, e => error = e);
                if (error != null) { fail(error); yield break; }

                List<object> content = resp.ListAt("content") ?? new List<object>();
                answer.Append(CollectText(content));
                if (resp.Str("stop_reason") != "pause_turn") break;
                messages.Add(MiniJson.Obj("role", "assistant", "content", content)); // continue the paused turn unchanged
            }
            ok(answer.ToString());
        }

        // ---------- plumbing ----------

        bool HasKey(Action<string> onError)
        {
            if (config != null && !string.IsNullOrWhiteSpace(config.apiKey)) return true;
            onError?.Invoke("No API key. Put your key in the CompanionConfig asset.");
            return false;
        }

        IEnumerator Post(string json, Action<Dictionary<string, object>> onOk, Action<string> onError)
        {
            using (var req = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("content-type", "application/json");
                req.SetRequestHeader("x-api-key", config.apiKey.Trim());
                req.SetRequestHeader("anthropic-version", ApiVersion);
                req.timeout = Mathf.Max(5, config.requestTimeout);

                yield return req.SendWebRequest();

                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (req.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke($"HTTP {req.responseCode}: {req.error}\n{raw}");
                    yield break;
                }
                Dictionary<string, object> resp = null;
                try { resp = MiniJson.Parse(raw) as Dictionary<string, object>; }
                catch (Exception e) { onError?.Invoke("Bad response JSON: " + e.Message); yield break; }
                if (resp == null) { onError?.Invoke("Empty response"); yield break; }
                onOk?.Invoke(resp);
            }
        }

        static List<object> BuildMessages(List<ChatTurn> turns)
        {
            var messages = new List<object>();
            foreach (var t in turns)
            {
                var content = new List<object>();
                if (t.fromUser && !string.IsNullOrEmpty(t.jpegBase64))
                    content.Add(MiniJson.Obj("type", "image", "source",
                        MiniJson.Obj("type", "base64", "media_type", "image/jpeg", "data", t.jpegBase64)));
                content.Add(MiniJson.Obj("type", "text", "text", string.IsNullOrEmpty(t.text) ? "..." : t.text));
                messages.Add(MiniJson.Obj("role", t.fromUser ? "user" : "assistant", "content", content));
            }
            return messages;
        }

        // Pick blocks by type, never by position.
        static string CollectText(List<object> content)
        {
            var sb = new StringBuilder();
            foreach (var o in content)
                if (o is Dictionary<string, object> b && b.Str("type") == "text") sb.Append(b.Str("text"));
            return sb.ToString();
        }
    }
}
