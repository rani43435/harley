using System;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Opens the Quest system keyboard and hands back what you typed.
    /// (Needs "Requires System Keyboard" in the Meta project settings; Harley's setup turns it on.)
    /// </summary>
    public class HarleyKeyboard : MonoBehaviour
    {
        static HarleyKeyboard _instance;
        TouchScreenKeyboard _kb;
        Action<string> _done;
        Action _cancelled;

        public static bool IsOpen => _instance != null && _instance._kb != null;

        public static void Open(string text, string placeholder, Action<string> done, Action cancelled = null)
        {
            if (_instance == null)
            {
                var go = new GameObject("Harley Keyboard");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<HarleyKeyboard>();
            }
            _instance.Begin(text, placeholder, done, cancelled);
        }

        void Begin(string text, string placeholder, Action<string> done, Action cancelled)
        {
            if (_kb != null && _kb.active) _kb.active = false;
            _done = done;
            _cancelled = cancelled;
            _kb = TouchScreenKeyboard.Open(text ?? "", TouchScreenKeyboardType.Default, true, false, false, false, placeholder ?? "");
            if (_kb == null) { Debug.LogWarning("[Harley] The system keyboard is not available (turn on Requires System Keyboard)."); _cancelled?.Invoke(); }
        }

        void Update()
        {
            if (_kb == null) return;
            switch (_kb.status)
            {
                case TouchScreenKeyboard.Status.Done:
                    var text = _kb.text;
                    _kb = null;
                    var d = _done; _done = null; _cancelled = null;
                    d?.Invoke(text);
                    break;
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus:
                    _kb = null;
                    var c = _cancelled; _done = null; _cancelled = null;
                    c?.Invoke();
                    break;
            }
        }
    }
}
