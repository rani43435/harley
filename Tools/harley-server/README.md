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
2. Install the Supabase CLI (`brew install supabase/tap/supabase`) and in this folder run:
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

## Daily allowance per headset (change with `supabase secrets set`)

| Secret | Default | What |
|---|---|---|
| LIMIT_CHAT | 300 | answers (Groq) |
| LIMIT_LISTEN | 300 | speech-to-text (Whisper) |
| LIMIT_VISION | 40 | looking at a picture (Gemini) |
| LIMIT_SPEAK | 50 | cloud voice (only used if the headset voice is off) |

Usage today, in the Supabase SQL editor:
```
select kind, count(distinct device) as headsets, sum(count) as calls
from harley_usage where day = current_date group by kind;
```

## Later, before a big public release
- Check that the app was bought / installed from the store (Meta Platform SDK user proof +
  `graph.oculus.com/user_nonce_validate`) instead of only the app key, which can be pulled out of the APK.
- Groq paid ("Developer") plan for higher limits.
