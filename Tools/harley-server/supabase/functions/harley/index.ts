// Harley server: the app's AI calls go through here, so the real API keys never ship inside the app.
// Supabase Edge Function (Deno).
//
// Plans
//   free : Groq (your free key), a small daily allowance
//   pro  : Claude Haiku (your Claude credit) with Groq as backup, a big daily allowance
// The allowance is measured like Claude's usage meter: what each request really used (tokens, answers
// weigh more than questions; listening and pictures count too), shown as a percentage that resets at 00:00 UTC.
//
// Routes (all need  x-harley-key: <HARLEY_APP_KEY>  and  x-harley-device: <anonymous headset id>)
//   GET  /harley/me                              -> plan, percent of today's allowance used, seconds until reset
//   POST /harley/claude/messages                 -> Claude (pro only)
//   POST /harley/groq/chat/completions           -> Groq (OpenAI format)
//   POST /harley/groq/audio/transcriptions       -> Groq Whisper (speech to text)
//   POST /harley/groq/audio/speech               -> Groq TTS (only if the headset voice is off)
//   GET  /harley/gemini/models                   -> Gemini model list
//   POST /harley/gemini/models/<m>:generateContent -> Gemini (looking at pictures, free plan)
//   POST /harley/pro/verify                      -> checks a Meta store purchase, makes the headset pro
// Open:
//   GET  /harley/health                          -> {"ok":true}
//   GET  /harley/admin?key=<ADMIN_KEY>           -> your dashboard (plans, usage, cost, Claude credit left)
//
// Secrets (supabase secrets set ...):
//   GROQ_API_KEY, GEMINI_API_KEY, ANTHROPIC_API_KEY, HARLEY_APP_KEY, ADMIN_KEY
//   CLAUDE_CREDIT (dollars you put on the Claude account), CLAUDE_CREDIT_SINCE (YYYY-MM-DD you put it), RESERVE_USD
//   optional: FREE_DAILY_UNITS, PRO_DAILY_UNITS, FREE_LIMIT_<KIND>, PRO_LIMIT_<KIND> (safety caps), PRO_DEVICES (comma list), META_APP_ID, META_APP_SECRET, PRO_SKUS
import { createClient } from "jsr:@supabase/supabase-js@2";

const env = (k: string, d = "") => Deno.env.get(k) ?? d;
const GROQ = env("GROQ_BASE", "https://api.groq.com/openai/v1/");
const GEMINI = env("GEMINI_BASE", "https://generativelanguage.googleapis.com/v1beta/");
const CLAUDE = env("CLAUDE_BASE", "https://api.anthropic.com/v1/");
const META = env("META_BASE", "https://graph.oculus.com/");

type Kind = "chat" | "listen" | "speak" | "vision" | "other";
type Plan = "free" | "pro";
// Safety caps on the number of requests (only against abuse; the real limit is the daily allowance below)
const DEFAULTS: Record<Plan, Record<Kind, number>> = {
  free: { chat: 300, listen: 300, speak: 50, vision: 30, other: 50 },
  pro: { chat: 3000, listen: 3000, speak: 300, vision: 600, other: 300 },
};
// Daily allowance in units. One answer of Harley is roughly 3,000-5,000 units.
const DAILY_UNITS: Record<Plan, number> = {
  free: Number(env("FREE_DAILY_UNITS", "150000")),
  pro: Number(env("PRO_DAILY_UNITS", "3000000")),
};
const OUTPUT_WEIGHT = 4;         // a generated token costs about 4-5x a read one
const LISTEN_UNITS = 1500;       // one spoken sentence through Whisper
const SPEAK_UNITS_PER_CHAR = 3;  // cloud voice (only if the headset voice is off)
const unitsToday = async (device: string) => Number((await db.rpc("harley_units_today", { p_device: device })).data ?? 0);
const limit = (plan: Plan, kind: Kind) =>
  Number(env(`${plan.toUpperCase()}_LIMIT_${kind.toUpperCase()}`, String(DEFAULTS[plan][kind])));

const list = (v: string) => new Set(v.split(",").map((s) => s.trim()).filter(Boolean));
const GROQ_MODELS = list(env("GROQ_MODELS",
  "openai/gpt-oss-120b,openai/gpt-oss-20b,groq/compound-mini,groq/compound,whisper-large-v3-turbo,whisper-large-v3," +
    "canopylabs/orpheus-v1-english,canopylabs/orpheus-arabic-saudi"));
const CLAUDE_MODELS = list(env("CLAUDE_MODELS", "claude-haiku-5-5"));
const PRO_DEVICES = list(env("PRO_DEVICES", ""));

// Dollars per million tokens [input, output]. Groq on a free key costs nothing (GROQ_FREE=true).
const PRICES: Record<string, [number, number]> = {
  "claude-haiku-5-5": [0.10, 0.50],
  "claude-sonnet-5-5": [2, 10],
  "openai/gpt-oss-120b": [0.15, 0.60],
  "openai/gpt-oss-20b": [0.075, 0.30],
  ...JSON.parse(env("PRICES", "{}")),
};
const GROQ_FREE = env("GROQ_FREE", "true") === "true";

const db = createClient(env("SUPABASE_URL"), env("SUPABASE_SERVICE_ROLE_KEY"));

const reply = (status: number, obj: unknown, extra: Record<string, string> = {}) =>
  new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json", ...extra } });

function same(a: string, b: string) { // constant-time compare
  if (!a || !b || a.length !== b.length) return false;
  let x = 0;
  for (let i = 0; i < a.length; i++) x |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return x === 0;
}

async function planOf(device: string): Promise<Plan> {
  if (PRO_DEVICES.has(device)) return "pro";
  const { data } = await db.from("harley_devices").select("plan, pro_until").eq("device", device).maybeSingle();
  if (data?.plan === "pro" && (!data.pro_until || new Date(data.pro_until) > new Date())) return "pro";
  return "free";
}

const today = () => new Date().toISOString().slice(0, 10);

Deno.serve(async (req) => {
  const url = new URL(req.url);
  const at = url.pathname.indexOf("/harley/");
  const rest = at >= 0 ? url.pathname.slice(at + "/harley/".length) : "";

  if (rest === "health") return reply(200, { ok: true });
  if (rest === "admin") return admin(url);

  if (!same(req.headers.get("x-harley-key") ?? "", env("HARLEY_APP_KEY"))) return reply(401, { error: "bad app key" });
  const device = req.headers.get("x-harley-device") ?? "";
  if (!/^[A-Za-z0-9_-]{16,64}$/.test(device)) return reply(400, { error: "missing device id" });
  await db.from("harley_devices").upsert({ device, last_seen: new Date().toISOString() }, { onConflict: "device" });
  const plan = await planOf(device);

  if (rest === "me") {
    const used = await unitsToday(device);
    return reply(200, { plan, percent: Math.min(100, Math.round((used / DAILY_UNITS[plan]) * 100)), resets_in: secondsToMidnight() });
  }
  if (rest === "pro/verify" && req.method === "POST") return verifyPro(req, device);

  // Where it goes, and what it counts as
  let target = "", kind: Kind = "other", provider = "", model = "";
  const headers = new Headers();
  const ct = req.headers.get("content-type");
  if (ct) headers.set("content-type", ct); // keeps the multipart boundary for audio uploads
  const body: ArrayBuffer | undefined = req.method === "GET" ? undefined : await req.arrayBuffer();
  if (body && body.byteLength > 8_000_000) return reply(413, { error: "too big" });
  const json = () => { try { return JSON.parse(new TextDecoder().decode(body)); } catch { return null; } };

  if (rest.startsWith("claude/")) {
    if (rest !== "claude/messages") return reply(404, { error: "not allowed" });
    if (plan !== "pro") return reply(403, { error: "pro only" });
    const j = json();
    if (!j) return reply(400, { error: "bad json" });
    model = j.model ?? "";
    if (!CLAUDE_MODELS.has(model)) return reply(403, { error: `model not allowed: ${model}` });
    if ((j.tools ?? []).some((t: { type?: string }) => t.type && t.type !== "custom")) {
      return reply(403, { error: "server tools are not allowed" }); // e.g. paid web search
    }
    kind = "chat"; provider = "claude"; target = CLAUDE + "messages";
    headers.set("x-api-key", env("ANTHROPIC_API_KEY"));
    headers.set("anthropic-version", req.headers.get("anthropic-version") ?? "2023-06-01");
  } else if (rest.startsWith("groq/")) {
    const p = rest.slice(5);
    if (p === "chat/completions") kind = "chat";
    else if (p === "audio/transcriptions") kind = "listen";
    else if (p === "audio/speech") kind = "speak";
    else return reply(404, { error: "not allowed" });
    if (kind !== "listen") {
      model = json()?.model ?? "";
      if (!GROQ_MODELS.has(model)) return reply(403, { error: `model not allowed: ${model}` });
    }
    provider = "groq"; target = GROQ + p;
    headers.set("authorization", "Bearer " + env("GROQ_API_KEY"));
  } else if (rest.startsWith("gemini/")) {
    const p = rest.slice(7);
    if (req.method === "GET" && p === "models") kind = "other";
    else if (req.method === "POST" && /^models\/[\w.\-]+:generateContent$/.test(p)) kind = "vision";
    else return reply(404, { error: "not allowed" });
    provider = "gemini"; target = GEMINI + p; model = p.split("/")[1]?.split(":")[0] ?? "";
    headers.set("x-goog-api-key", env("GEMINI_API_KEY"));
  } else {
    return reply(404, { error: "unknown route" });
  }

  // Today's allowance (percent, like Claude's usage meter) and a safety cap on the number of requests
  const limitReached = () => reply(429, { error: "daily limit reached", plan, percent: 100, resets_in: secondsToMidnight() },
    { "retry-after": String(secondsToMidnight()) });
  if ((await unitsToday(device)) >= DAILY_UNITS[plan]) return limitReached();
  const { data: count, error } = await db.rpc("harley_count", { p_device: device, p_kind: kind });
  if (error) return reply(500, { error: "usage db: " + error.message });
  if ((count as number) > limit(plan, kind)) return limitReached();

  const r = await fetch(target + url.search, { method: req.method, headers, body });
  const out = new Headers({ "content-type": r.headers.get("content-type") ?? "application/json" });
  const ra = r.headers.get("retry-after");
  if (ra) out.set("retry-after", ra);

  // Answers: read the token counts so the dashboard knows what it cost
  if (kind === "chat" || kind === "vision") {
    const text = await r.text();
    if (r.ok) {
      try {
        const [tin, tout] = tokens(provider, JSON.parse(text));
        const price = PRICES[model] ?? [0, 0];
        const free = provider === "gemini" || (provider === "groq" && GROQ_FREE);
        const cost = free ? 0 : (tin * price[0] + tout * price[1]) / 1e6;
        const units = Math.round(tin + tout * OUTPUT_WEIGHT);
        await db.rpc("harley_add_usage", { p_device: device, p_kind: kind, p_provider: provider, p_in: Math.round(tin), p_out: tout, p_cost: cost, p_units: units });
      } catch { /* the answer still goes through */ }
    }
    return new Response(text, { status: r.status, headers: out });
  }
  if (r.ok) {
    const units = kind === "listen" ? LISTEN_UNITS
      : kind === "speak" ? SPEAK_UNITS_PER_CHAR * String(json()?.input ?? "").length
      : 0;
    if (units) await db.rpc("harley_add_usage", { p_device: device, p_kind: kind, p_provider: "", p_in: 0, p_out: 0, p_cost: 0, p_units: units });
  }
  return new Response(r.body, { status: r.status, headers: out });
});

function tokens(provider: string, j: Record<string, any>): [number, number] {
  if (provider === "claude") {
    const u = j.usage ?? {};
    return [(u.input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0) * 0.1 + (u.cache_creation_input_tokens ?? 0) * 1.25, u.output_tokens ?? 0];
  }
  if (provider === "groq") return [j.usage?.prompt_tokens ?? 0, j.usage?.completion_tokens ?? 0];
  if (provider === "gemini") return [j.usageMetadata?.promptTokenCount ?? 0, j.usageMetadata?.candidatesTokenCount ?? 0];
  return [0, 0];
}

// ---------- Pro: a purchase in the Meta store ----------
// The app sends the Meta user id + a one-time proof (Users.GetUserProof) after a purchase or at start.
// We check both with Meta, then mark this headset pro for 35 days (checked again at each start).
async function verifyPro(req: Request, device: string) {
  const appId = env("META_APP_ID"), secret = env("META_APP_SECRET");
  if (!appId || !secret) return reply(501, { error: "store not set up yet" });
  let b: { user_id?: string; nonce?: string; sku?: string };
  try { b = await req.json(); } catch { return reply(400, { error: "bad json" }); }
  const userId = String(b.user_id ?? ""), nonce = String(b.nonce ?? ""), sku = String(b.sku ?? "");
  if (!/^\d+$/.test(userId) || !nonce || !list(env("PRO_SKUS", "harley_pro")).has(sku)) return reply(400, { error: "bad request" });
  const token = `OC|${appId}|${secret}`;
  const form = (o: Record<string, string>) => new URLSearchParams({ access_token: token, ...o });

  const n = await fetch(META + "user_nonce_validate", { method: "POST", body: form({ nonce, user_id: userId }) });
  if (!(await n.json().catch(() => ({})))?.is_valid) return reply(403, { error: "proof not valid" });
  const e = await fetch(META + `${appId}/verify_entitlement`, { method: "POST", body: form({ user_id: userId, sku }) });
  if (!(await e.json().catch(() => ({})))?.success) return reply(402, { error: "no purchase found" });

  const until = new Date(Date.now() + 35 * 864e5).toISOString();
  await db.from("harley_devices").upsert({ device, plan: "pro", pro_until: until, meta_user: userId }, { onConflict: "device" });
  return reply(200, { plan: "pro", pro_until: until });
}

// ---------- your dashboard ----------
async function admin(url: URL) {
  if (!same(url.searchParams.get("key") ?? "", env("ADMIN_KEY"))) return new Response("Not found", { status: 404 });
  const day = today(), month = day.slice(0, 7) + "-01";
  const since = env("CLAUDE_CREDIT_SINCE", month);
  const [{ data: devs }, { data: todayRows }, { data: monthRows }, { data: claudeRows }] = await Promise.all([
    db.from("harley_devices").select("device, plan, pro_until, last_seen"),
    db.from("harley_usage").select("device, kind, count, cost_usd").eq("day", day),
    db.from("harley_usage").select("cost_usd, provider_costs").gte("day", month),
    db.from("harley_usage").select("provider_costs").gte("day", since),
  ]);
  const now = new Date();
  const pro = (devs ?? []).filter((d) => d.plan === "pro" && (!d.pro_until || new Date(d.pro_until) > now)).length + PRO_DEVICES.size;
  const active = new Set((todayRows ?? []).map((r) => r.device)).size;
  const sum = (rows: any[] | null, f: (r: any) => number) => (rows ?? []).reduce((a, r) => a + (f(r) || 0), 0);
  const calls: Record<string, number> = {};
  for (const r of todayRows ?? []) calls[r.kind] = (calls[r.kind] ?? 0) + r.count;
  const costToday = sum(todayRows, (r) => Number(r.cost_usd));
  const costMonth = sum(monthRows, (r) => Number(r.cost_usd));
  const claudeSpent = sum(claudeRows, (r) => Number(r.provider_costs?.claude ?? 0));
  const credit = Number(env("CLAUDE_CREDIT", "0")), reserve = Number(env("RESERVE_USD", "20"));
  const left = credit - claudeSpent;
  const low = credit > 0 && left < reserve;
  const usd = (v: number) => "$" + v.toFixed(v < 1 ? 4 : 2);
  const row = (k: string, v: string) => `<tr><td>${k}</td><td>${v}</td></tr>`;
  const html = `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Harley server</title>
<style>body{font:16px system-ui;background:#141022;color:#efeafb;margin:0;padding:24px}h1{margin:0 0 16px}
.card{background:#211a36;border-radius:14px;padding:18px;margin:0 0 14px;max-width:560px}
td{padding:6px 18px 6px 0}.big{font-size:32px;font-weight:700}.low{color:#ff8a8a}.ok{color:#8be3a5}small{color:#a99cc9}</style>
<h1>Harley</h1>
<div class="card"><small>Claude credit left (estimate)</small><div class="big ${low ? "low" : "ok"}">${credit ? usd(left) : "set CLAUDE_CREDIT"}</div>
<small>${credit ? `of ${usd(credit)} since ${since} · reserve ${usd(reserve)}${low ? " · <b>time to top up</b>" : ""}` : ""}</small></div>
<div class="card"><table>${row("Pro headsets", String(pro))}${row("Headsets in total", String((devs ?? []).length))}${row("Active today", String(active))}</table></div>
<div class="card"><small>Today (${day})</small><table>${Object.entries(calls).map(([k, v]) => row(k, String(v))).join("") || row("calls", "0")}
${row("cost today", usd(costToday))}${row("cost this month", usd(costMonth))}</table></div>
<div class="card"><small>Headsets seen lately (to make yours pro: add its id to PRO_DEVICES)</small><table>${
    (devs ?? []).sort((a, b) => String(b.last_seen).localeCompare(String(a.last_seen))).slice(0, 15)
      .map((d) => row(`<code>${d.device}</code>`, `${d.plan}${d.last_seen ? " · " + String(d.last_seen).slice(0, 16).replace("T", " ") : ""}`)).join("")}</table></div>
<small>The real balance is on console.anthropic.com. After a top-up: supabase secrets set CLAUDE_CREDIT=… CLAUDE_CREDIT_SINCE=${day}</small>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8" } });
}

function secondsToMidnight() {
  const now = new Date();
  const next = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1);
  return Math.ceil((next - now.getTime()) / 1000);
}
