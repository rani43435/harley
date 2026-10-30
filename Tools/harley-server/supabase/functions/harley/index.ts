// Harley server: the app's AI calls go through here, so the real API keys never ship inside the app,
// and every headset gets a daily allowance. Supabase Edge Function (Deno).
//
//   POST /harley/groq/chat/completions        -> Groq (OpenAI format)
//   POST /harley/groq/audio/transcriptions    -> Groq Whisper (speech to text)
//   POST /harley/groq/audio/speech            -> Groq TTS (only if the headset voice is off)
//   GET  /harley/gemini/models                -> Gemini model list
//   POST /harley/gemini/models/<m>:generateContent -> Gemini (looking at pictures)
//   GET  /harley/health                       -> {"ok":true}
//
// Every call needs  x-harley-key: <HARLEY_APP_KEY>  and  x-harley-device: <anonymous headset id>.
// Secrets (supabase secrets set ...): GROQ_API_KEY, GEMINI_API_KEY, HARLEY_APP_KEY, optional LIMIT_* and GROQ_MODELS.
import { createClient } from "jsr:@supabase/supabase-js@2";

const GROQ = Deno.env.get("GROQ_BASE") ?? "https://api.groq.com/openai/v1/";
const GEMINI = Deno.env.get("GEMINI_BASE") ?? "https://generativelanguage.googleapis.com/v1beta/";
const env = (k: string, d = "") => Deno.env.get(k) ?? d;

type Kind = "chat" | "listen" | "speak" | "vision" | "other";
const LIMITS: Record<Kind, number> = {
  chat: Number(env("LIMIT_CHAT", "300")),
  listen: Number(env("LIMIT_LISTEN", "300")),
  speak: Number(env("LIMIT_SPEAK", "50")),
  vision: Number(env("LIMIT_VISION", "40")),
  other: Number(env("LIMIT_OTHER", "50")),
};
// Only the models Harley uses (so a leaked app key can't run anything expensive)
const GROQ_MODELS = new Set(
  env("GROQ_MODELS", "openai/gpt-oss-120b,openai/gpt-oss-20b,groq/compound-mini,groq/compound," +
    "whisper-large-v3-turbo,whisper-large-v3,canopylabs/orpheus-v1-english,canopylabs/orpheus-arabic-saudi")
    .split(",").map((s) => s.trim()).filter(Boolean),
);

const db = createClient(env("SUPABASE_URL"), env("SUPABASE_SERVICE_ROLE_KEY"));

const reply = (status: number, obj: unknown, extra: Record<string, string> = {}) =>
  new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json", ...extra } });

function same(a: string, b: string) { // constant-time compare
  if (a.length !== b.length) return false;
  let x = 0;
  for (let i = 0; i < a.length; i++) x |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return x === 0;
}

Deno.serve(async (req) => {
  const url = new URL(req.url);
  const at = url.pathname.indexOf("/harley/");
  const rest = at >= 0 ? url.pathname.slice(at + "/harley/".length) : "";

  if (rest === "health") return reply(200, { ok: true });

  const appKey = env("HARLEY_APP_KEY");
  if (!appKey || !same(req.headers.get("x-harley-key") ?? "", appKey)) return reply(401, { error: "bad app key" });
  const device = req.headers.get("x-harley-device") ?? "";
  if (!/^[A-Za-z0-9_-]{16,64}$/.test(device)) return reply(400, { error: "missing device id" });

  // Where it goes, and what it counts as
  let target = "", kind: Kind = "other";
  const headers = new Headers();
  const ct = req.headers.get("content-type");
  if (ct) headers.set("content-type", ct); // keeps the multipart boundary for audio uploads
  let body: ArrayBuffer | undefined = req.method === "GET" ? undefined : await req.arrayBuffer();
  if (body && body.byteLength > 8_000_000) return reply(413, { error: "too big" });

  if (rest.startsWith("groq/")) {
    const p = rest.slice(5);
    if (p === "chat/completions") kind = "chat";
    else if (p === "audio/transcriptions") kind = "listen";
    else if (p === "audio/speech") kind = "speak";
    else return reply(404, { error: "not allowed" });
    if (kind !== "listen" && body) {
      try {
        const model = JSON.parse(new TextDecoder().decode(body)).model ?? "";
        if (!GROQ_MODELS.has(model)) return reply(403, { error: `model not allowed: ${model}` });
      } catch { return reply(400, { error: "bad json" }); }
    }
    target = GROQ + p;
    headers.set("authorization", "Bearer " + env("GROQ_API_KEY"));
  } else if (rest.startsWith("gemini/")) {
    const p = rest.slice(7);
    if (req.method === "GET" && p === "models") kind = "other";
    else if (req.method === "POST" && /^models\/[\w.\-]+:generateContent$/.test(p)) kind = "vision";
    else return reply(404, { error: "not allowed" });
    target = GEMINI + p;
    headers.set("x-goog-api-key", env("GEMINI_API_KEY"));
  } else {
    return reply(404, { error: "unknown route" });
  }

  // Daily allowance per headset
  const { data: used, error } = await db.rpc("harley_count", { p_device: device, p_kind: kind });
  if (error) return reply(500, { error: "usage db: " + error.message });
  if ((used as number) > LIMITS[kind]) {
    return reply(429, { error: `daily limit reached (${kind})` }, { "retry-after": String(secondsToMidnight()) });
  }

  const r = await fetch(target + url.search, { method: req.method, headers, body });
  const out = new Headers({ "content-type": r.headers.get("content-type") ?? "application/json" });
  const ra = r.headers.get("retry-after");
  if (ra) out.set("retry-after", ra);
  return new Response(r.body, { status: r.status, headers: out });
});

function secondsToMidnight() {
  const now = new Date();
  const next = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1);
  return Math.ceil((next - now.getTime()) / 1000);
}
