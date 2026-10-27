using System;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Where Harley's body is and how it moves. Two kinds:
    ///  - CompanionMovement: flies around your real room (mixed reality version)
    ///  - PanelMotion: lives inside its app window (window app version)
    /// The brain, the visual and the input only talk to this base class.
    /// </summary>
    public abstract class CompanionMotion : MonoBehaviour
    {
        [Tooltip("Player head (Main Camera). Empty = found automatically.")]
        public Transform head;

        public abstract Vector3 Velocity { get; }
        /// <summary>World point Harley's eyes look at.</summary>
        public abstract Vector3 LookPoint { get; }
        public virtual bool IsSitting => false;
        public virtual bool IsHeld => false;
        public virtual bool IsFlying => false;
        /// <summary>Name of the room object it went to with go_to, if any.</summary>
        public virtual string CurrentPlace => "";

        /// <summary>Hit a wall or the floor while thrown. Value = impact speed.</summary>
        public event Action<float> Bounced;
        /// <summary>Came to rest after being thrown.</summary>
        public event Action Landed;

        protected void RaiseBounced(float impact) => Bounced?.Invoke(impact);
        protected void RaiseLanded() => Landed?.Invoke();

        public abstract void Perform(CompanionAction action, CompanionRoom.Thing thing = null);

        // Grab & throw (called by the input)
        public virtual void BeginHold() { }
        public virtual void HoldAt(Vector3 worldPoint) { }
        public virtual void Release(Vector3 velocity) { }
    }
}
