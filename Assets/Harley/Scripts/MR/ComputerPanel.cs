using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Menu > Computer: connect Harley to the AI agent on your own computer (Tools/harley-bridge).
    /// Harley searches your Wi-Fi, you pick your computer, it shows a 6-digit code, you type it here. Done.
    /// Then pick which agent to use (Claude Code, Codex, Gemini CLI, Hermes) from what that computer has.
    /// </summary>
    public class ComputerPanel : SpatialPanel
    {
        static ComputerPanel _instance;

        public static void Show(Transform head)
        {
            if (_instance == null)
            {
                var go = new GameObject("Harley Computer");
                _instance = go.AddComponent<ComputerPanel>();
                _instance.head = head;
                _instance.EnsureBuilt();
            }
            _instance.gameObject.SetActive(true);
            _instance.ShowInFront(0.6f, 0.12f);
            _instance.Refresh();
        }

        const float W = 440f, L = 20f;
        const int MaxFound = 4;
        TextMeshProUGUI _status;
        PanelButton _search, _manual, _agent, _disconnect;
        readonly List<PanelButton> _foundButtons = new List<PanelButton>();
        readonly List<HarleyComputer.Found> _found = new List<HarleyComputer.Found>();
        bool _busy;

        HarleyComputer PC => HarleyComputer.Instance;

        protected override void Awake()
        {
            base.Awake();
            size = new Vector2((W + 2 * L) / PixelsPerMeter, 0.40f);
        }

        protected override void Build()
        {
            var c = Content;
            var title = Label(c, "Computer", 28, text, TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold;
            PlaceTL(title.rectTransform, L, 12, 300, 36);
            PlaceTL(AddButton(c, "X", () => gameObject.SetActive(false), null, 20).rect, L + W - 44, 12, 44, 36);

            _status = Label(c, "", 16, muted, TextAlignmentOptions.TopLeft);
            _status.enableWordWrapping = true;
            PlaceTL(_status.rectTransform, L, 52, W, 64);

            float y = 122f;
            for (int i = 0; i < MaxFound; i++)
            {
                int k = i;
                var b = AddButton(c, "", () => Pick(k), null, 18);
                PlaceTL(b.rect, L, y + i * 44f, W, 38);
                _foundButtons.Add(b);
            }
            y += MaxFound * 44f + 8f;
            _search = AddButton(c, "Search", () => StartCoroutine(Search()), primary, 18);
            PlaceTL(_search.rect, L, y, 140, 40);
            _manual = AddButton(c, "Type address", TypeAddress, null, 16);
            PlaceTL(_manual.rect, L + 150, y, 140, 40);
            _agent = AddButton(c, "Agent", NextAgent, null, 16);
            PlaceTL(_agent.rect, L + 150, y, 140, 40);
            _disconnect = AddButton(c, "Disconnect", Disconnect, null, 16);
            PlaceTL(_disconnect.rect, L + 300, y, 140, 40);
        }

        void Refresh()
        {
            if (PC == null) { _status.text = "Computer link isn't running."; return; }
            if (PC.Available) StartCoroutine(CheckNow());
            else ShowState("Run harley_bridge.py on your computer (same Wi-Fi), then tap Search.");
        }

        IEnumerator CheckNow()
        {
            ShowState("Checking your computer...");
            yield return PC.Check(null);
            ShowState(null);
        }

        void ShowState(string message)
        {
            bool paired = PC != null && PC.Available;
            bool online = paired && PC.Connected;
            string name = paired && !string.IsNullOrEmpty(PC.ComputerName) ? PC.ComputerName : (paired ? PC.Url : "");
            if (message != null) _status.text = message;
            else if (online) _status.text = $"<color=#4ADE80>Connected</color> to {name}.\nAgent: <b>{AgentLabel()}</b>. Ask Harley things like \"ask my computer what changed in my project\".";
            else if (paired) _status.text = $"<color=#F87171>Can't reach</color> {name}. Is harley_bridge.py running and are you on the same Wi-Fi?";

            for (int i = 0; i < _foundButtons.Count; i++)
            {
                bool on = !paired && i < _found.Count;
                _foundButtons[i].rect.gameObject.SetActive(on);
                if (on) _foundButtons[i].label.text = $"{_found[i].name}   <size=70%>({_found[i].agent})</size>";
            }
            _search.rect.gameObject.SetActive(!paired);
            _manual.rect.gameObject.SetActive(!paired);
            _agent.rect.gameObject.SetActive(online && PC.Agents.Count > 0);
            _agent.label.text = "Agent: " + AgentLabel();
            _disconnect.rect.gameObject.SetActive(paired);
            if (paired && !online && message == null) _search.rect.gameObject.SetActive(false);
        }

        string AgentLabel() => PC != null && !string.IsNullOrEmpty(PC.AgentName) ? PC.AgentName : "default";

        IEnumerator Search()
        {
            if (_busy || PC == null) yield break;
            _busy = true;
            ShowState("Looking for computers on your Wi-Fi...");
            yield return PC.Discover(_found);
            _busy = false;
            ShowState(_found.Count > 0 ? "Pick your computer:" : "Found nothing. Is harley_bridge.py running on your computer, on the same Wi-Fi? You can also type its address.");
        }

        void Pick(int i)
        {
            if (i >= _found.Count || _busy) return;
            StartCoroutine(Pair(_found[i].url, _found[i].name));
        }

        void TypeAddress()
        {
            HarleyKeyboard.Open("", "192.168.1.20", ip =>
            {
                ip = (ip ?? "").Trim();
                if (ip.Length == 0) return;
                if (!ip.StartsWith("http")) ip = "http://" + ip;
                if (ip.IndexOf(':', 7) < 0) ip += ":8765";
                StartCoroutine(Pair(ip, ip));
            });
        }

        IEnumerator Pair(string url, string name)
        {
            _busy = true;
            ShowState("Asking " + name + " for a code...");
            bool ok = false; string err = null;
            yield return PC.PairStart(url, (o, e) => { ok = o; err = e; });
            if (!ok) { _busy = false; ShowState("Couldn't reach " + name + ". (" + err + ")"); yield break; }
            ShowState("A 6-digit code is showing on " + name + ". Type it in.");
            string code = null; bool cancelled = false;
            HarleyKeyboard.Open("", "6-digit code", t => code = t, () => cancelled = true);
            while (code == null && !cancelled) yield return null;
            if (cancelled) { _busy = false; ShowState("Cancelled."); yield break; }
            ShowState("Connecting...");
            yield return PC.PairFinish(url, code, name, (o, e) => { ok = o; err = e; });
            _busy = false;
            if (ok)
            {
                _found.Clear();
                ShowState(null);
                HarleyCompanionPing();
            }
            else ShowState((err == "wrong code" ? "Wrong code. " : "Didn't work (" + err + "). ") + "Tap your computer to try again.");
        }

        void HarleyCompanionPing()
        {
            var brain = FindAnyObjectByType<CompanionBrain>();
            brain?.React("The player just connected you to their computer (" + (PC.ComputerName ?? "") + "), so now you can hand tasks to " + AgentLabel() + " on it. Say so happily in one short sentence.", Emotion.Excited);
        }

        void NextAgent()
        {
            if (PC == null || PC.Agents.Count == 0) return;
            var usable = PC.Agents.FindAll(a => a.installed);
            if (usable.Count == 0) { ShowState("That computer has no agent installed yet (Claude Code, Codex, Gemini CLI or Hermes)."); return; }
            int i = usable.FindIndex(a => a.id == PC.AgentName);
            PC.SetAgent(usable[(i + 1) % usable.Count].id);
            ShowState(null);
        }

        void Disconnect()
        {
            PC?.Disconnect();
            _found.Clear();
            ShowState("Disconnected. Tap Search to connect again.");
        }
    }
}
