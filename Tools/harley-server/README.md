# Harley server

Harley's AI calls (Groq for thinking and hearing, Gemini for looking at pictures) go through this small
Supabase Edge Function, so:
- the real API keys stay here, never inside the app;
- each headset gets a daily allowance (so one person can't use up everything);
- only the models Harley uses are allowed;
- you can see the usage in the database.

The headset voice (Piper) runs on the Quest itself and never touches the server.

## Set up (once, about 10 minutes)

1. Make a free project on supabase.com (any name, e.g. `harley`).
2. Easiest: run `sh Tools/harley-server/deploy.sh` (it does all of the below and asks for the keys). Or by hand: install the Supabase CLI (`brew install supabase/tap/supabase`) and in this folder run:
   ```
   supabase login
   supabase link --project-ref <your project ref>
   supabase db push                      # creates the usage table
   supabase secrets set GROQ_API_KEY=... GEMINI_API_KEY=... HARLEY_APP_KEY=<any long random text>
   supabase functions deploy harley --no-verify-jwt
   ```
   (`openssl rand -hex 24` makes a good HARLEY_APP_KEY.)
3. In Unity, CompanionConfig > Harley server:
   - Server Url: `https://<your project ref>.supabase.co/functions/v1/harley`
   - Server App Key: the same HARLEY_APP_KEY
   Then you can empty the Groq / Gemini keys in the config: the app doesn't need them anymore.

Check it works: `curl https://<ref>.supabase.co/functions/v1/harley/health` gives `{"ok":true}`.

## Plans

| | Free | Pro |
|---|---|---|
| Brain | Groq (your free key) | Claude Haiku 5.5 (your Claude credit), Groq as backup |
| Daily allowance | 150,000 units (`FREE_DAILY_UNITS`) | 3,000,000 units (`PRO_DAILY_UNITS`) |
| Pictures | Gemini | Claude sees them itself |

The allowance works like Claude's usage meter: every request counts what it really used (tokens read +
4x tokens written; a spoken sentence through Whisper = 1,500), the app shows it as a percent bar with the
time it resets (00:00 UTC). One answer of Harley is roughly 3,000-5,000 units. Change with
`supabase secrets set FREE_DAILY_UNITS=200000`. There are also high safety caps on the number of requests
(`FREE_LIMIT_CHAT` etc.) only against abuse.
Only `claude-haiku-5-5` is allowed for Claude (`CLAUDE_MODELS`), and Claude's paid server tools (web search) are blocked.

**Making a headset Pro**
- Yours, by hand: open the dashboard, copy your headset's id, then `supabase secrets set PRO_DEVICES=<id>`.
- Buyers: the app sends the Meta store purchase to `/pro/verify` (needs `META_APP_ID`, `META_APP_SECRET`,
  `PRO_SKUS`, set once the app exists on the Meta store). Pro lasts 35 days and is checked again at each start.

## Your dashboard and the Claude credit

`https://<ref>.supabase.co/functions/v1/harley/admin?key=<ADMIN_KEY>` (deploy.sh prints it) shows:
Pro headsets, active headsets, calls and cost today and this month, and an estimate of the Claude credit left.
It turns red when the credit goes under the reserve (`RESERVE_USD`, default $20): time to top up.

After each top-up on console.anthropic.com, tell the server the new total and the date:
```
supabase secrets set CLAUDE_CREDIT=40 CLAUDE_CREDIT_SINCE=2026-11-15
```
Also set a spend alert on console.anthropic.com (Billing), so you hear about it even without the dashboard.

## Later, before a big public release
- Check that the app was bought / installed from the store (Meta Platform SDK user proof +
  `graph.oculus.com/user_nonce_validate`) instead of only the app key, which can be pulled out of the APK.
- Groq paid ("Developer") plan for higher limits.
