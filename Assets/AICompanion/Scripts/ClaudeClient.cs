using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace AICompanion
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
    /// Sends text + an optional camera frame and asks for a reply that matches
    /// the companion's JSON schema (structured outputs), so parsing never breaks.
    /// </summary>
    public class ClaudeClient : MonoBehaviour
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string ApiVersion = "2023-06-01";

        public CompanionConfig config;

        public bool Busy { get; private set; }

        // The shape every reply must have. Enums keep the actions to things the
        // character can actually do.
        const string ReplySchema = @"{
  ""type"": ""object"",
  ""properties"": {
    ""say"":     { ""type"": ""string"" },
    ""emotion"": { ""type"": ""string"", ""enum"": [""neutral"",""happy"",""excited"",""curious"",""surprised"",""sad"",""sleepy"",""love""] },
    ""action"":  { ""type"": ""string"", ""enum"": [""stay"",""follow"",""come_close"",""wander"",""look_at_thing"",""sit"",""hide"",""spin"",""dance"",""wave""] }
  },
  ""required"": [""say"",""emotion"",""action""],
  ""additionalProperties"": false
}";

        public IEnumerator Send(string system, List<ChatTurn> turns,
                                Action<string> onText, Action<string> onError)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.apiKey))
            {
                onError?.Invoke("No API key. Put your key in the CompanionConfig asset.");
                yield break;
            }

            Busy = true;
            byte[] body = Encoding.UTF8.GetBytes(BuildBody(system, turns));

            using (var req = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("content-type", "application/json");
                req.SetRequestHeader("x-api-key", config.apiKey.Trim());
                req.SetRequestHeader("anthropic-version", ApiVersion);
                req.timeout = Mathf.Max(5, config.requestTimeout);

                yield return req.SendWebRequest();
                Busy = false;

                string raw = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (req.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke($"HTTP {req.responseCode}: {req.error}\n{raw}");
                    yield break;
                }

                ApiResponse resp = null;
                try { resp = JsonUtility.FromJson<ApiResponse>(raw); }
                catch (Exception e) { onError?.Invoke("Bad response JSON: " + e.Message + "\n" + raw); yield break; }

                // Pick blocks by type, never by position.
                var sb = new StringBuilder();
                if (resp != null && resp.content != null)
                    foreach (var block in resp.content)
                        if (block != null && block.type == "text") sb.Append(block.text);

                if (sb.Length == 0)
                {
                    onError?.Invoke("Empty reply (stop_reason: " + (resp != null ? resp.stop_reason : "?") + ")");
                    yield break;
                }
                onText?.Invoke(sb.ToString());
            }
        }

        string BuildBody(string system, List<ChatTurn> turns)
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"model\":").Append(Q(config.model)).Append(',');
            sb.Append("\"max_tokens\":").Append(config.maxTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"system\":").Append(Q(system)).Append(',');
            sb.Append("\"output_config\":{\"format\":{\"type\":\"json_schema\",\"schema\":").Append(ReplySchema).Append("}},");
            sb.Append("\"messages\":[");
            for (int i = 0; i < turns.Count; i++)
            {
                var t = turns[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":").Append(t.fromUser ? "\"user\"" : "\"assistant\"").Append(",\"content\":[");
                bool needComma = false;
                if (t.fromUser && !string.IsNullOrEmpty(t.jpegBase64))
                {
                    sb.Append("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/jpeg\",\"data\":\"")
                      .Append(t.jpegBase64).Append("\"}}");
                    needComma = true;
                }
                if (needComma) sb.Append(',');
                sb.Append("{\"type\":\"text\",\"text\":").Append(Q(string.IsNullOrEmpty(t.text) ? "..." : t.text)).Append('}');
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>JSON string literal with proper escaping.</summary>
        static string Q(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        [Serializable] class ContentBlock { public string type; public string text; }
        [Serializable] class ApiResponse { public ContentBlock[] content; public string stop_reason; }
    }
}
