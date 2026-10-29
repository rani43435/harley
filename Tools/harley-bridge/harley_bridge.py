#!/usr/bin/env python3
"""
Harley Bridge: lets Harley (on the Quest) hand tasks to the AI agent on YOUR computer.

Everyone uses their own account: log in to your agent's CLI once (claude, codex, gemini...),
pick it in bridge_config.json, run this script, and put the address + token it prints into
Harley's config (CompanionConfig > Computer). No API keys pass through Harley or this bridge.

  python3 harley_bridge.py            start (creates bridge_config.json the first time)
  python3 harley_bridge.py --agents   show which agents are installed

Only Python 3.8+ is needed (standard library only). Works on macOS, Linux and Windows.
"""
import hmac
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(HERE, "bridge_config.json")

# How each agent is run without a terminal. "{prompt}" is replaced by the task;
# "stdin": true sends the task on standard input instead (safer for odd characters).
DEFAULT_AGENTS = {
    "claude": {
        "name": "Claude Code (Claude Pro / Max or API account)",
        "login": "claude   (then /login)",
        "command": ["claude", "-p", "--permission-mode", "acceptEdits", "--allowedTools",
                    "Read,Edit,Write,Glob,Grep,Bash(git status:*),Bash(git diff:*),Bash(git log:*),"
                    "Bash(git add:*),Bash(git commit:*),Bash(git push:*),Bash(ls:*)"],
        "stdin": True,
    },
    "codex": {
        "name": "OpenAI Codex CLI (ChatGPT account or API key)",
        "login": "codex login",
        "command": ["codex", "exec", "--sandbox", "workspace-write", "--skip-git-repo-check", "-"],
        "stdin": True,
    },
    "gemini": {
        "name": "Gemini CLI (Google account, free tier)",
        "login": "gemini   (then sign in with Google)",
        "command": ["gemini", "-p", "{prompt}"],
        "stdin": False,
    },
    "hermes": {
        "name": "Hermes Agent (any model you set up in hermes)",
        "login": "hermes setup",
        "command": ["hermes", "chat", "--query-file", "-"],
        "stdin": True,
    },
}

DEFAULT_CONFIG = {
    "agent": "claude",
    "host": "0.0.0.0",
    "port": 8765,
    "token": "",
    "workdir": "~",
    "timeout_seconds": 900,
    "max_output_chars": 4000,
    "agents": DEFAULT_AGENTS,
}

PREFIX = ("You are being asked by voice, through Harley (a VR companion) on the user's headset. "
          "Do the task in this folder. When done, end with a short plain summary (max 3 sentences, no markdown) "
          "of what you did or found, because it will be read out loud.\n\nTask: ")

JOBS = {}
JOBS_LOCK = threading.Lock()
RUN_LOCK = threading.Lock()  # one task at a time


def load_config():
    if not os.path.exists(CONFIG_PATH):
        cfg = dict(DEFAULT_CONFIG)
        cfg["token"] = secrets.token_urlsafe(24)
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
        try:
            os.chmod(CONFIG_PATH, 0o600)
        except OSError:
            pass
        print("Created", CONFIG_PATH, "- edit it to pick your agent and folder.")
    with open(CONFIG_PATH, encoding="utf-8") as f:
        cfg = json.load(f)
    for k, v in DEFAULT_CONFIG.items():
        cfg.setdefault(k, v)
    for k, v in DEFAULT_AGENTS.items():
        cfg["agents"].setdefault(k, v)
    if not cfg.get("token") or len(cfg["token"]) < 16:
        sys.exit("bridge_config.json: 'token' must be at least 16 characters.")
    return cfg


def installed(cfg):
    return {k: bool(shutil.which(a["command"][0])) for k, a in cfg["agents"].items()}


def run_job(cfg, job_id, prompt, agent_key):
    agent = cfg["agents"][agent_key]
    task = PREFIX + prompt
    cmd = [task if part == "{prompt}" else part for part in agent["command"]]
    exe = shutil.which(cmd[0])
    if not exe:
        finish(job_id, "error", f"{agent_key} is not installed on this computer.")
        return
    cmd[0] = exe
    workdir = os.path.expanduser(cfg["workdir"])
    with RUN_LOCK:
        set_status(job_id, "running")
        try:
            p = subprocess.run(cmd, input=task if agent.get("stdin") else None, cwd=workdir,
                               capture_output=True, text=True, timeout=cfg["timeout_seconds"])
            out = (p.stdout or "").strip() or (p.stderr or "").strip() or "(no output)"
            finish(job_id, "done" if p.returncode == 0 else "error", out[-cfg["max_output_chars"]:])
        except subprocess.TimeoutExpired:
            finish(job_id, "error", "The task took too long and was stopped.")
        except Exception as e:  # noqa: BLE001
            finish(job_id, "error", f"Could not run {agent_key}: {e}")


def set_status(job_id, status):
    with JOBS_LOCK:
        JOBS[job_id]["status"] = status


def finish(job_id, status, output):
    with JOBS_LOCK:
        JOBS[job_id].update(status=status, output=output, finished=time.time())
    print(f"[{time.strftime('%H:%M:%S')}] job {job_id[:8]} {status}")


class Handler(BaseHTTPRequestHandler):
    cfg = None

    def log_message(self, *a):
        pass

    def send(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def authed(self):
        got = self.headers.get("Authorization", "")
        ok = hmac.compare_digest(got.encode(), ("Bearer " + self.cfg["token"]).encode())
        if not ok:
            time.sleep(0.5)  # slow down guessing
            self.send(401, {"error": "bad token"})
        return ok

    def do_GET(self):
        if self.path == "/health":
            return self.send(200, {"ok": True})
        if not self.authed():
            return
        if self.path == "/agents":
            inst = installed(self.cfg)
            return self.send(200, {"default": self.cfg["agent"],
                                   "agents": [{"id": k, "name": a["name"], "installed": inst[k]}
                                              for k, a in self.cfg["agents"].items()]})
        if self.path.startswith("/job/"):
            with JOBS_LOCK:
                job = JOBS.get(self.path[5:])
                job = dict(job) if job else None
            return self.send(200 if job else 404, job or {"error": "no such job"})
        self.send(404, {"error": "not found"})

    def do_POST(self):
        if not self.authed():
            return
        if self.path != "/run":
            return self.send(404, {"error": "not found"})
        try:
            n = min(int(self.headers.get("Content-Length", "0")), 20000)
            data = json.loads(self.rfile.read(n) or b"{}")
        except Exception:  # noqa: BLE001
            return self.send(400, {"error": "bad json"})
        prompt = (data.get("prompt") or "").strip()
        agent = (data.get("agent") or "").strip().lower() or self.cfg["agent"]
        if not prompt:
            return self.send(400, {"error": "empty prompt"})
        if agent not in self.cfg["agents"]:
            return self.send(400, {"error": f"unknown agent '{agent}'"})
        job_id = uuid.uuid4().hex
        with JOBS_LOCK:
            JOBS[job_id] = {"id": job_id, "agent": agent, "status": "queued", "output": "", "started": time.time()}
            for k in [k for k, j in JOBS.items() if time.time() - j["started"] > 3600]:
                JOBS.pop(k, None)
        print(f"[{time.strftime('%H:%M:%S')}] job {job_id[:8]} -> {agent}: {prompt[:80]}")
        threading.Thread(target=run_job, args=(self.cfg, job_id, prompt, agent), daemon=True).start()
        self.send(200, {"id": job_id, "agent": agent})


def local_ips():
    ips = set()
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        ips.add(s.getsockname()[0])
        s.close()
    except OSError:
        pass
    return sorted(ips) or ["127.0.0.1"]


def main():
    cfg = load_config()
    if "--agents" in sys.argv:
        inst = installed(cfg)
        for k, a in cfg["agents"].items():
            print(f"  {'OK ' if inst[k] else '-- '} {k:8} {a['name']}   (log in: {a['login']})")
        return
    if cfg["agent"] not in cfg["agents"]:
        sys.exit(f"Unknown agent '{cfg['agent']}' in bridge_config.json")
    if not shutil.which(cfg["agents"][cfg["agent"]]["command"][0]):
        print(f"Warning: '{cfg['agent']}' is not installed. Run with --agents to see what you have.")
    Handler.cfg = cfg
    srv = ThreadingHTTPServer((cfg["host"], cfg["port"]), Handler)
    print("\nHarley Bridge is running.")
    print("  Agent:  ", cfg["agent"], "-", cfg["agents"][cfg["agent"]]["name"])
    print("  Folder: ", os.path.expanduser(cfg["workdir"]))
    for ip in local_ips():
        print(f"  Address: http://{ip}:{cfg['port']}")
    print("  Token:   in bridge_config.json (copy it into Harley's config yourself)")
    print("Keep this window open. Ctrl+C to stop.\n")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
