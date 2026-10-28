using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Free alternative brain: Google Gemini (free tier from aistudio.google.com).
    /// Same job as the Claude path in ClaudeClient: text + picture in, Harley's JSON reply out,
    /// with the weather / web search tools. ClaudeClient switches to this when the config says Gemini.
    /// </summary>
    public static class GeminiClient
    {
        const string Api = "https://generativelanguage.googleapis.com/v1beta/";
        const int MaxToolRounds = 4;

        // Model names change often; when the configured one is gone we pick a current Flash model once.
        static string _resolvedModel;

        const string JsonRule =
            "\n\nAnswer with ONLY one JSON object, no markdown, exactly these keys: " +
            "{\"say\": string, \"emotion\": one of neutral|happy|excited|curious|surprised|sad|sleepy|love, " +
            "\"action\": one of stay|follow|come_close|wander|look_at_thing|sit|hide|spin|dance|wave|go_to|give|take_back|hide_and_seek|sleep|celebrate|open_app|close_app, " +
            "\"target\": string, \"remember\": string, \"forget\": boolean}";

        public static IEnumerator Send(CompanionConfig config, string system, List<ChatTurn> turns, CompanionTools tools,
                                       Action<string> onText, Action<string> onError, Action<string> onToolStart)
        {
            bool hasImage = turns.Exists(t => !string.IsNullOrEmpty(t.jpegBase64));
            string firstError = null;
            bool done = false;
            // Pictures on the free tier can be slow or refused when Google is busy: then answer without the picture.
            yield return SendOnce(config, system, turns, tools, t => { done = true; onText?.Invoke(t); }, e => firstError = e, onToolStart,
                                  hasImage ? ImageTimeout : config.requestTimeout);
            if (done || firstError == null) yield break;
            if (hasImage && IsSlowOrBusy(firstError))
            {
                Debug.LogWarning("[Harley] Gemini with picture failed (" + Short(firstError) + "), trying without it.");
                var textOnly = turns.ConvertAll(t => new ChatTurn { fromUser = t.fromUser, text = t.text });
                if (textOnly.Count > 0) textOnly[textOnly.Count - 1].text += "\n(You couldn't see anything this time; don't mention a picture.)";
                yield return SendOnce(config, system, textOnly, tools, onText, onError, onToolStart, config.requestTimeout);
                yield break;
            }
            onError?.Invoke(firstError);
        }

        const int ImageTimeout = 18;

        static bool IsSlowOrBusy(string e) =>
            e.Contains("HTTP 0") || e.Contains("HTTP 503") || e.Contains("HTTP 500") || e.Contains("timeout") || e.Contains("Timeout") || e.Contains("HTTP 429");

        static string Short(string e) { int n = e.IndexOf('\n'); return n > 0 ? e.Substring(0, n) : e; }

        static IEnumerator SendOnce(CompanionConfig config, string system, List<ChatTurn> turns, CompanionTools tools,
                                    Action<string> onText, Action<string> onError, Action<string> onToolStart, int timeout)
        {
            var contents = BuildContents(turns);
            var functions = new List<object>();
            if (tools != null)
                foreach (var o in tools.Definitions())
                    if (o is Dictionary<string, object> d)
                        functions.Add(MiniJson.Obj("name", d.Str("name"), "description", d.Str("description"),
                            "parameters", CleanSchema(d.ObjAt("input_schema"))));

            for (int round = 0; round <= MaxToolRounds; round++)
            {
                var body = MiniJson.Obj(
                    "system_instruction", MiniJson.Obj("parts", new List<object> { MiniJson.Obj("text", system + JsonRule) }),
                    "contents", contents,
                    "generationConfig", MiniJson.Obj("maxOutputTokens", Mathf.Max(600, config.maxTokens), "temperature", 0.9));
                if (functions.Count > 0)
                    body["tools"] = new List<object> { MiniJson.Obj("functionDeclarations", functions) };

                Dictionary<string, object> resp = null;
                string error = null;
                yield return Generate(config, MiniJson.Serialize(body), r => resp = r, e => error = e, timeout);
                if (error != null) { onError?.Invoke(error); yield break; }

                var cand = First(resp.ListAt("candidates"));
                var content = cand.ObjAt("content");
                var parts = content.ListAt("parts") ?? new List<object>();

                var calls = new List<Dictionary<string, object>>();
                var text = new StringBuilder();
                foreach (var p in parts)
                {
                    if (!(p is Dictionary<string, object> part)) continue;
                    var fc = part.ObjAt("functionCall");
                    if (fc != null) calls.Add(fc);
                    else if (part.Str("text") is string t && !(part.TryGetValue("thought", out var th) && th is bool b && b)) text.Append(t);
                }

                if (calls.Count > 0 && tools != null)
                {
                    contents.Add(content); // the model's turn, unchanged (keeps thought signatures)
                    var replies = new List<object>();
                    foreach (var fc in calls)
                    {
                        string name = fc.Str("name");
                        onToolStart?.Invoke(name);
                        string result = null;
                        yield return tools.Run(name, fc.ObjAt("args") ?? new Dictionary<string, object>(), r => result = r);
                        var fr = MiniJson.Obj("name", name, "response", MiniJson.Obj("result", string.IsNullOrWhiteSpace(result) ? "No result." : result));
                        if (fc.Str("id") is string id) fr["id"] = id;
                        replies.Add(MiniJson.Obj("functionResponse", fr));
                    }
                    contents.Add(MiniJson.Obj("role", "user", "parts", replies));
                    continue;
                }

                if (text.Length == 0)
                {
                    string why = cand.Str("finishReason") ?? resp.ObjAt("promptFeedback")?.Str("blockReason") ?? "unknown";
                    onError?.Invoke("Empty reply (" + why + ")");
                    yield break;
                }
                onText?.Invoke(text.ToString());
                yield break;
            }
            onError?.Invoke("Too many tool rounds.");
        }

        /// <summary>Web search with Gemini's built-in Google Search grounding.</summary>
        public static IEnumerator Search(CompanionConfig config, string query, string city, Action<string> done)
        {
            string where = string.IsNullOrWhiteSpace(city) ? "" : " The user lives near " + city + ".";
            var body = MiniJson.Obj(
                "contents", new List<object> { MiniJson.Obj("role", "user", "parts", new List<object> { MiniJson.Obj("text",
                    "Search the web and answer with the key facts (numbers, names, dates, scores) for: " + query +
                    "\nToday is " + DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "." + where +
                    "\nAnswer in plain text, at most 90 words, no markdown, no links.") }) },
                "tools", new List<object> { MiniJson.Obj("google_search", MiniJson.Obj()) },
                "generationConfig", MiniJson.Obj("maxOutputTokens", 900));

            Dictionary<string, object> resp = null;
            string error = null;
            yield return Generate(config, MiniJson.Serialize(body), r => resp = r, e => error = e);
            if (error != null) { done?.Invoke("Search failed: " + error); yield break; }

            var sb = new StringBuilder();
            foreach (var p in First(resp.ListAt("candidates")).ObjAt("content").ListAt("parts") ?? new List<object>())
                if (p is Dictionary<string, object> part && part.Str("text") is string t) sb.Append(t);
            done?.Invoke(sb.Length > 0 ? sb.ToString().Trim() : "The search found nothing useful.");
        }

        // ---------- plumbing ----------

        /// <summary>Tried in order when a model is busy (503) or gone (404). The configured model comes first.</summary>
        static readonly string[] Fallbacks = { "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-flash-lite-latest" };

        static IEnumerator Generate(CompanionConfig config, string json, Action<Dictionary<string, object>> ok, Action<string> fail, int timeout = 0)
        {
            if (string.IsNullOrWhiteSpace(config.geminiApiKey)) { fail("No API key. Put your Gemini key in the CompanionConfig asset."); yield break; }

            var models = new List<string>();
            string first = _resolvedModel ?? (string.IsNullOrWhiteSpace(config.geminiModel) ? Fallbacks[0] : config.geminiModel.Trim());
            models.Add(first);
            foreach (var f in Fallbacks) if (!models.Contains(f)) models.Add(f);

            for (int attempt = 0; attempt < models.Count; attempt++)
            {
                string model = models[attempt];
                long code = 0;
                string raw = null, err = null;
                yield return Post(config, Api + "models/" + model + ":generateContent", json, (c, r, e) => { code = c; raw = r; err = e; }, timeout);

                if (err == null)
                {
                    Dictionary<string, object> resp = null;
                    try { resp = MiniJson.Parse(raw) as Dictionary<string, object>; } catch (Exception e) { fail("Bad response JSON: " + e.Message); yield break; }
                    if (resp == null) { fail("Empty response"); yield break; }
                    ok(resp);
                    yield break;
                }

                // Busy or gone: try the next model (a timeout is not retried here, the caller decides).
                bool next = code == 503 || code == 500 || code == 404 || (code == 429 && raw != null && raw.Contains("model"));
                if (next && attempt + 1 < models.Count)
                {
                    Debug.LogWarning($"[Harley] Gemini {model}: HTTP {code}, trying {models[attempt + 1]}");
                    continue;
                }
                if (code == 404 && _resolvedModel == null)
                {
                    string found = null;
                    yield return FindFlashModel(config, m => found = m);
                    if (found != null && !models.Contains(found)) { _resolvedModel = found; models.Add(found); Debug.Log("[Harley] Using Gemini model " + found); continue; }
                }
                fail(err);
                yield break;
            }
        }

        static IEnumerator FindFlashModel(CompanionConfig config, Action<string> done)
        {
            using (var req = UnityWebRequest.Get(Api + "models?pageSize=200"))
            {
                req.SetRequestHeader("x-goog-api-key", config.geminiApiKey.Trim());
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { done(null); yield break; }
                object parsed = null;
                try { parsed = MiniJson.Parse(req.downloadHandler.text); } catch { }
                string best = null;
                foreach (var o in (parsed as Dictionary<string, object>).ListAt("models") ?? new List<object>())
                {
                    if (!(o is Dictionary<string, object> m)) continue;
                    string name = (m.Str("name") ?? "").Replace("models/", "");
                    var methods = m.ListAt("supportedGenerationMethods");
                    if (methods == null || !methods.Contains("generateContent")) continue;
                    if (!name.Contains("flash") || name.Contains("image") || name.Contains("tts") || name.Contains("live") || name.Contains("audio")) continue;
                    if (name.Contains("lite")) { if (best == null) best = name; continue; }
                    if (best == null || best.Contains("lite") || string.CompareOrdinal(name, best) > 0) best = name;
                }
                done(best);
            }
        }

        static IEnumerator Post(CompanionConfig config, string url, string json, Action<long, string, string> done, int timeout = 0)
        {
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("content-type", "application/json");
                req.SetRequestHeader("x-goog-api-key", config.geminiApiKey.Trim());
                req.timeout = Mathf.Max(5, timeout > 0 ? timeout : config.requestTimeout);
                yield return req.SendWebRequest();
                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (req.result != UnityWebRequest.Result.Success)
                {
                    // Gemini's "wrong key" comes back as 400 API_KEY_INVALID: report it like Claude's 401.
                    long code = req.responseCode == 400 && raw.Contains("API_KEY_INVALID") ? 401 : req.responseCode;
                    done(req.responseCode, raw, $"HTTP {code}: {req.error}\n{raw}");
                }
                else done(req.responseCode, raw, null);
            }
        }

        static List<object> BuildContents(List<ChatTurn> turns)
        {
            var list = new List<object>();
            foreach (var t in turns)
            {
                var parts = new List<object>();
                if (t.fromUser && !string.IsNullOrEmpty(t.jpegBase64))
                    parts.Add(MiniJson.Obj("inline_data", MiniJson.Obj("mime_type", "image/jpeg", "data", t.jpegBase64)));
                parts.Add(MiniJson.Obj("text", string.IsNullOrEmpty(t.text) ? "..." : t.text));
                list.Add(MiniJson.Obj("role", t.fromUser ? "user" : "model", "parts", parts));
            }
            return list;
        }

        /// <summary>Gemini's schema dialect: no additionalProperties, no empty "required".</summary>
        static Dictionary<string, object> CleanSchema(Dictionary<string, object> s)
        {
            var o = new Dictionary<string, object>();
            if (s == null) return MiniJson.Obj("type", "object", "properties", MiniJson.Obj());
            foreach (var kv in s)
            {
                if (kv.Key == "additionalProperties") continue;
                if (kv.Key == "required" && kv.Value is List<object> l && l.Count == 0) continue;
                o[kv.Key] = kv.Value;
            }
            return o;
        }

        static Dictionary<string, object> First(List<object> l) =>
            l != null && l.Count > 0 ? l[0] as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
    }
}
