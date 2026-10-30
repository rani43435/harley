#!/bin/sh
# One-time setup of the Harley server on your Supabase account.
# Run in Terminal:  sh ~/harley/Tools/harley-server/deploy.sh
# It asks for your keys (typing is hidden) and stores them only as Supabase secrets.
set -e
cd "$(dirname "$0")"
if ! command -v supabase >/dev/null 2>&1; then
  echo "Installing the Supabase CLI (Homebrew)..."
  brew install supabase/tap/supabase
fi
echo "1) Log in to Supabase in the browser that opens."
supabase login
echo
echo "2) Make a project on https://supabase.com/dashboard (name: harley), then copy its Reference ID"
echo "   (Project Settings > General). Paste it here:"
read -r REF
supabase link --project-ref "$REF"
echo "3) Creating the usage table..."
supabase db push
echo
printf "Groq API key (gsk_...): "; stty -echo; read -r GROQ; stty echo; echo
printf "Gemini API key (optional, Enter to skip): "; stty -echo; read -r GEMINI; stty echo; echo
printf "Claude API key for Pro (optional, Enter to skip): "; stty -echo; read -r CLAUDE; stty echo; echo
printf "How many dollars of Claude credit did you buy? (e.g. 20, Enter = 0): "; read -r CREDIT
APPKEY=$(openssl rand -hex 24)
ADMIN=$(openssl rand -hex 16)
set -- GROQ_API_KEY="$GROQ" HARLEY_APP_KEY="$APPKEY" ADMIN_KEY="$ADMIN" CLAUDE_CREDIT="${CREDIT:-0}" CLAUDE_CREDIT_SINCE="$(date +%Y-%m-%d)" RESERVE_USD=20
[ -n "$GEMINI" ] && set -- "$@" GEMINI_API_KEY="$GEMINI"
[ -n "$CLAUDE" ] && set -- "$@" ANTHROPIC_API_KEY="$CLAUDE"
supabase secrets set "$@" >/dev/null
echo "4) Deploying the function..."
supabase functions deploy harley --no-verify-jwt
URL="https://$REF.supabase.co/functions/v1/harley"
echo
curl -s "$URL/health"; echo
echo
echo "Done. In Unity > CompanionConfig > Harley server, paste:"
echo "  Server Url:     $URL"
echo "  Server App Key: $APPKEY"
echo
echo "Your dashboard (keep this link private):"
echo "  $URL/admin?key=$ADMIN"
