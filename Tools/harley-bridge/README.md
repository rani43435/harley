# Harley Bridge

Lets Harley hand tasks to the AI agent on your own computer ("Harley, ask my computer to fix the Unity error").
Everyone uses their own account. Harley only gets the bridge address and a token.

## Setup (once)
1. Install and log in to the agent you want to use:
   - Claude Code (Claude Pro/Max): `npm i -g @anthropic-ai/claude-code`, then `claude` and `/login`
   - Codex (ChatGPT): `npm i -g @openai/codex`, then `codex login`
   - Gemini CLI (free Google account): `npm i -g @google/gemini-cli`, then `gemini`
   - Hermes Agent: see hermes-agent.nousresearch.com, then `hermes setup`
2. `python3 harley_bridge.py --agents` creates `bridge_config.json` and shows which agents it found.
3. Edit `bridge_config.json`:
   - `agent`: `claude`, `codex`, `gemini` or `hermes`
   - `workdir`: the folder the agent may work in (e.g. `~/harley`)
   - `agents.<name>.command`: what each agent may do (Claude's allowed tools are listed there)
4. Run `python3 harley_bridge.py` and keep the window open.
5. In Harley's menu tap **Computer** > **Search**, pick your computer, and type the 6-digit code it shows.
   Then **Agent** switches between the agents that computer has. (No Wi-Fi discovery? Use **Type address**.)

## Safety
- Every request needs the token. Keep `bridge_config.json` private and never commit it.
- Use it on your home Wi-Fi, or with Tailscale away from home. Don't open the port to the internet.
- Harley asks you before tasks that change things (edit, commit, push, delete, install).
- Only one task runs at a time. A task stops after `timeout_seconds`.
