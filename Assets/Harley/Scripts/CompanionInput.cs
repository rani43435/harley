using System;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// How the player touches and calls Harley. Two kinds:
    ///  - CompanionInteraction: real hands and controllers in mixed reality
    ///  - PanelInput: pointer clicks and drags inside the app window
    /// </summary>
    public abstract class CompanionInput : MonoBehaviour
    {
        public event Action Poked;
        public event Action Petted;
        public event Action Waved;
        /// <summary>Player wants to talk. true = pressed a button, false = looked at Harley.</summary>
        public event Action<bool> TalkRequested;
        public event Action Grabbed;
        /// <summary>Let go. Argument: hand velocity (a fast one is a throw).</summary>
        public event Action<Vector3> Released;

        /// <summary>True while the player is paying attention to Harley (looking at it, or using its window).</summary>
        public bool IsPlayerLooking { get; protected set; }

        /// <summary>Seconds since the player last did anything with Harley.</summary>
        public float IdleSeconds => Time.time - _lastActivity;
        float _lastActivity;

        protected virtual void Start() { _lastActivity = Time.time; }

        public void MarkActivity() => _lastActivity = Time.time;

        /// <summary>Start talking, e.g. from a UI button.</summary>
        public void RequestTalk() { MarkActivity(); TalkRequested?.Invoke(true); }

        protected void RaisePoked() { MarkActivity(); Poked?.Invoke(); }
        protected void RaisePetted() { MarkActivity(); Petted?.Invoke(); }
        protected void RaiseWaved() { MarkActivity(); Waved?.Invoke(); }
        protected void RaiseTalk(bool byButton) { MarkActivity(); TalkRequested?.Invoke(byButton); }
        protected void RaiseGrabbed() { MarkActivity(); Grabbed?.Invoke(); }
        protected void RaiseReleased(Vector3 v) { MarkActivity(); Released?.Invoke(v); }
    }
}
