using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Groq (console.groq.com, free key, no card): very fast and with much bigger free limits than Gemini.
    ///  - Brain: openai/gpt-oss-120b (about 1000 requests a day), with the weather / search tools.
    ///  - Hearing: Whisper (about 2000 a day).
    ///  - Voice: Orpheus, English and Arabic (about 100 a day).
    ///  - Web search: groq/compound-mini (has search built in).
    /// It can't see pictures: questions about what you're looking at still go to Gemini.
    /// </summary>
    public static class GroqClient
    {
        const string Api = "https://api.groq.com/openai/v1/";
        const int MaxToolRounds = 4;
        static readonly string[] Fallbacks = { "openai/gpt-oss-120b", "qwen/qwen3.8-27b", "openai/gpt-oss-20b" };

        public static bool Has(CompanionConfig c) => c != null && (HarleyServer.On(c) || !string.IsNullOrWhiteSpace(c.groqApiKey));

        const string JsonRule =
            "\n\nAnswer with ONLY one JSON object, no markdown, exactly these keys: " +
            "{\"say\": string, \"emotion\": one of neutral|happy|excited|curious|surprised|sad|sleepy|love, " +
            "\"action\": one of stay|follow|come_close|wander|look_at_thing|sit|hide|spin|dance|wave|go_to|give|take_back|hide_and_seek|sleep|celebrate|open_app|close_app|start_lesson|next_word|stop_lesson|computer, " +
            "\"target\": string, \"remember\": string, \"forget\": boolean}";

        // ---------- brain ----------

        public static IEnumerator Send(CompanionConfig config, string system, List<ChatTurn> turns, CompanionTools tools,
                                       Action<string> onText, Action<string> onError, Action<string> onToolStart)
        {
            var messages = new List<object> { MiniJson.Obj("role", "system", "content", system + JsonRule) };
            foreach (var t in turns)
            {
                string text = string.IsNullOrEmpty(t.text) ? "..." : t.text;
                if (t.fromUser && !string.IsNullOrEmpty(t.jpegBase64)) text += "\n(You can't see a picture right now.)";
                messages.Add(MiniJson.Obj("role", t.fromUser ? "user" : "assistant", "content", text));
            }

            var toolDefs = new List<object>();
            if (tools != null)
                foreach (var o in tools.Definitions())
                    if (o is Dictionary<string, object> d)
                        toolDefs.Add(MiniJson.Obj("type", "function", "function",
                            MiniJson.Obj("name", d.Str("name"), "description", d.Str("description"), "parameters", d.ObjAt("input_schema") ?? MiniJson.Obj("type", "object", "properties", MiniJson.Obj()))));

            for (int round = 0; round <= MaxToolRounds; round++)
            {
                var body = MiniJson.Obj("messages", messages, "temperature", 0.9, "max_completion_tokens", 1500,
                                        "reasoning_effort", "low");
                bool wantJson = toolDefs.Count == 0 || round == MaxToolRounds;
                if (toolDefs.Count > 0 && round < MaxToolRounds) body["tools"] = toolDefs;
                if (wantJson) body["response_format"] = MiniJson.Obj("type", "json_object");

                Dictionary<string, object> resp = null;
                string error = null;
                yield return Chat(config, body, r => resp = r, e => error = e);
                if (error != null) { onError?.Invoke(error); yield break; }

                var choice = First(resp.ListAt("choices"));
                var msg = choice.ObjAt("message") ?? new Dictionary<string, object>();
                var calls = msg.ListAt("tool_calls");
                if (calls != null && calls.Count > 0 && tools != null)
                {
                    messages.Add(MiniJson.Obj("role", "assistant", "content", msg.Str("content") ?? "", "tool_calls", calls));
                    foreach (var c in calls)
                    {
                        if (!(c is Dictionary<string, object> call)) continue;
                        var fn = call.ObjAt("function");
                        string name = fn?.Str("name") ?? "";
                        Dictionary<string, object> args = null;
                        try { args = MiniJson.Parse(fn?.Str("arguments") ?? "{}") as Dictionary<string, object>; } catch { }
                        onToolStart?.Invoke(name);
                        string result = null;
                        yield return tools.Run(name, args ?? new Dictionary<string, object>(), r => result = r);
                        messages.Add(MiniJson.Obj("role", "tool", "tool_call_id", call.Str("id") ?? "", "content", string.IsNullOrWhiteSpace(result) ? "No result." : result));
                    }
                    continue;
                }

                string content = msg.Str("content");
                if (string.IsNullOrWhiteSpace(content)) { onError?.Invoke("Empty reply (" + (choice.Str("finish_reason") ?? "?") + ")"); yield break; }
                onText?.Invoke(content);
                yield break;
            }
            onError?.Invoke("Too many tool rounds.");
        }

        /// <summary>Web search with Groq's compound model (search built in).</summary>
        public static IEnumerator Search(CompanionConfig config, string query, string city, Action<string> done)
        {
            string where = string.IsNullOrWhiteSpace(city) ? "" : " The user lives near " + city + ".";
            var body = MiniJson.Obj("model", "groq/compound-mini", "messages", new List<object> {
                MiniJson.Obj("role", "user", "content",
                    "Search the web and answer with the key facts (numbers, names, dates, scores) for: " + query +
                    "\nToday is " + DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "." + where +
                    "\nAnswer in plain text, at most 90 words, no markdown, no links.") });
            Dictionary<string, object> resp = null;
            string error = null;
            yield return Post(config, "chat/completions", MiniJson.Serialize(body), r => resp = r, e => error = e, 30);
            if (error != null) { done?.Invoke(null); yield break; }
            string text = First(resp.ListAt("choices")).ObjAt("message")?.Str("content");
            done?.Invoke(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
        }

        static string _model;

        static IEnumerator Chat(CompanionConfig config, Dictionary<string, object> body, Action<Dictionary<string, object>> ok, Action<string> fail)
        {
            var models = new List<string>();
            if (_model != null) models.Add(_model);
            if (!string.IsNullOrWhiteSpace(config.groqModel) && !models.Contains(config.groqModel.Trim())) models.Add(config.groqModel.Trim());
            foreach (var f in Fallbacks) if (!models.Contains(f)) models.Add(f);

            string lastError = null;
            foreach (var m in models)
            {
                body["model"] = m;
                Dictionary<string, object> resp = null;
                string error = null;
                yield return Post(config, "chat/completions", MiniJson.Serialize(body), r => resp = r, e => error = e, config.requestTimeout);
                if (error == null) { _model = m; ok(resp); yield break; }
                lastError = error;
                // reasoning_effort / json mode not supported by this model: try once more without them
                if (error.Contains("HTTP 400") && (error.Contains("reasoning") || error.Contains("response_format")))
                {
                    body.Remove("reasoning_effort");
                    if (error.Contains("response_format")) body.Remove("response_format");
                    error = null; resp = null;
                    yield return Post(config, "chat/completions", MiniJson.Serialize(body), r => resp = r, e => error = e, config.requestTimeout);
                    if (error == null) { _model = m; ok(resp); yield break; }
                    lastError = error;
                }
                bool next = error.Contains("HTTP 404") || error.Contains("HTTP 429") || error.Contains("HTTP 503") || error.Contains("HTTP 500") || error.Contains("decommissioned") || error.Contains("model_not_found");
                if (!next) break;
                Debug.LogWarning("[Harley] Groq " + m + ": " + Short(error) + ", trying the next model.");
            }
            fail(lastError ?? "Groq failed");
        }

        // ---------- hearing (Whisper) ----------

        public static IEnumerator Transcribe(CompanionConfig config, byte[] pcm16k, Action<string> ok, Action<string> fail)
        {
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", Wav(pcm16k, 16000), "speech.wav", "audio/wav"),
                new MultipartFormDataSection("model", "whisper-large-v3-turbo"),
                new MultipartFormDataSection("response_format", "json"),
                new MultipartFormDataSection("temperature", "0"),
            };
            using (var req = UnityWebRequest.Post(HarleyServer.Groq(config) + "audio/transcriptions", form))
            {
                HarleyServer.AuthGroq(req, config);
                req.timeout = 20;
                yield return req.SendWebRequest();
                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (req.result != UnityWebRequest.Result.Success) { fail($"HTTP {req.responseCode}: {req.error}\n{raw}"); yield break; }
                string text = null;
                try { text = (MiniJson.Parse(raw) as Dictionary<string, object>)?.Str("text"); } catch { }
                ok((text ?? "").Trim());
            }
        }

        // ---------- voice (Orpheus) ----------

        public static IEnumerator Speak(CompanionConfig config, string text, Action<AudioClip> ok, Action<string> fail)
        {
            bool arabic = false;
            foreach (char ch in text) if (ch >= '؀' && ch <= 'ۿ') { arabic = true; break; }
            string model = arabic ? "canopylabs/orpheus-arabic-saudi" : "canopylabs/orpheus-v1-english";
            string voice = arabic ? (string.IsNullOrWhiteSpace(config.groqVoiceArabic) ? "noura" : config.groqVoiceArabic.Trim())
                                  : (string.IsNullOrWhiteSpace(config.groqVoiceEnglish) ? "hannah" : config.groqVoiceEnglish.Trim());

            var samples = new List<float>();
            int rate = 24000, channels = 1;
            foreach (string piece in Chunks(text, 195))
            {
                var body = MiniJson.Obj("model", model, "input", arabic ? piece : "[cheerful] " + piece, "voice", voice, "response_format", "wav");
                byte[] wav = null; string error = null;
                using (var req = new UnityWebRequest(HarleyServer.Groq(config) + "audio/speech", UnityWebRequest.kHttpVerbPOST))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(MiniJson.Serialize(body)));
                    req.downloadHandler = new DownloadHandlerBuffer();
                    HarleyServer.AuthGroq(req, config);
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 20;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) wav = req.downloadHandler.data;
                    else
                    {
                        error = "voice HTTP " + req.responseCode;
                        string wait = req.GetResponseHeader("retry-after");
                        if (!string.IsNullOrEmpty(wait)) error += " retry-after=" + wait.Trim();
                    }
                }
                if (wav == null) { if (samples.Count == 0) { fail(error); yield break; } break; } // keep what we have
                if (!ReadWav(wav, samples, out rate, out channels)) { fail("bad voice audio"); yield break; }
            }
            if (samples.Count == 0) { fail("empty voice"); yield break; }
            var clip = AudioClip.Create("Harley voice", samples.Count / channels, channels, rate, false);
            clip.SetData(samples.ToArray(), 0);
            ok(clip);
        }

        static IEnumerable<string> Chunks(string text, int max)
        {
            text = text.Trim();
            while (text.Length > max)
            {
                int cut = -1;
                foreach (char p in new[] { '.', '!', '?', '؟', '،', ',', ';', ' ' })
                {
                    int i = text.LastIndexOf(p, max - 1);
                    if (i > max / 3) { cut = i + 1; break; }
                }
                if (cut < 0) cut = max;
                yield return text.Substring(0, cut).Trim();
                text = text.Substring(cut).Trim();
            }
            if (text.Length > 0) yield return text;
        }

        static bool ReadWav(byte[] wav, List<float> into, out int rate, out int channels)
        {
            rate = 24000; channels = 1;
            if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return false;
            int bits = 16, pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(wav, pos + 10);
                    rate = BitConverter.ToInt32(wav, pos + 12);
                    bits = BitConverter.ToInt16(wav, pos + 22);
                }
                else if (id == "data")
                {
                    int start = pos + 8;
                    int len = size <= 0 || start + size > wav.Length ? wav.Length - start : size;
                    if (bits != 16) return false;
                    for (int i = 0; i + 1 < len; i += 2) into.Add(BitConverter.ToInt16(wav, start + i) / 32768f);
                    return true;
                }
                pos += 8 + Math.Max(0, size) + (size & 1);
            }
            return false;
        }

        // ---------- plumbing ----------

        static IEnumerator Post(CompanionConfig config, string path, string json, Action<Dictionary<string, object>> ok, Action<string> fail, int timeout)
        {
            using (var req = new UnityWebRequest(HarleyServer.Groq(config) + path, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                HarleyServer.AuthGroq(req, config);
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = Mathf.Max(5, timeout);
                yield return req.SendWebRequest();
                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (req.result != UnityWebRequest.Result.Success)
                {
                    long code = req.responseCode == 403 ? 401 : req.responseCode;
                    fail($"HTTP {code}: {req.error}\n{raw}");
                    yield break;
                }
                Dictionary<string, object> resp = null;
                try { resp = MiniJson.Parse(raw) as Dictionary<string, object>; } catch (Exception e) { fail("Bad response JSON: " + e.Message); yield break; }
                if (resp == null) { fail("Empty response"); yield break; }
                ok(resp);
            }
        }

        static byte[] Wav(byte[] pcm, int rate)
        {
            var wav = new byte[44 + pcm.Length];
            void S(int at, string s) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)s[i]; }
            void I32(int at, int v) { BitConverter.GetBytes(v).CopyTo(wav, at); }
            void I16(int at, short v) { BitConverter.GetBytes(v).CopyTo(wav, at); }
            S(0, "RIFF"); I32(4, 36 + pcm.Length); S(8, "WAVE");
            S(12, "fmt "); I32(16, 16); I16(20, 1); I16(22, 1); I32(24, rate); I32(28, rate * 2); I16(32, 2); I16(34, 16);
            S(36, "data"); I32(40, pcm.Length);
            pcm.CopyTo(wav, 44);
            return wav;
        }

        static string Short(string e) { int n = e.IndexOf('\n'); return n > 0 ? e.Substring(0, n) : e; }

        static Dictionary<string, object> First(List<object> l) =>
            l != null && l.Count > 0 ? l[0] as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
    }
}
