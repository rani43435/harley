using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>One pointer touching a panel: where the ray comes from and where it hits.</summary>
    public struct PanelPointer
    {
        public int id;            // 0 = left, 1 = right
        public Vector3 origin;    // ray start (hand or controller)
        public Vector3 direction;
        public Vector3 world;     // hit point
        public Vector2 local;     // hit point on the panel in meters, (0,0) = centre, +x right, +y up
        public float distance;    // origin to hit
    }

    /// <summary>
    /// A flat rectangle floating in the room that hands and controllers can point at
    /// (HarleyPointer does the pointing). The panel faces the player: its forward axis
    /// points away from them, like a world-space canvas.
    /// </summary>
    public abstract class PanelSurface : MonoBehaviour
    {
        public static readonly List<PanelSurface> All = new List<PanelSurface>();

        [Tooltip("Width and height in meters.")]
        public Vector2 size = new Vector2(0.5f, 0.35f);

        public virtual bool Interactable => isActiveAndEnabled;

        protected virtual void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        protected virtual void OnDisable() { All.Remove(this); }

        /// <summary>Ray against this panel's rectangle. True with the hit if it lands inside.</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out PanelPointer hit, bool anywhereOnPlane = false)
        {
            hit = default;
            Vector3 n = transform.forward;
            float denom = Vector3.Dot(dir, n);
            if (Mathf.Abs(denom) < 1e-4f) return false;
            float t = Vector3.Dot(transform.position - origin, n) / denom;
            if (t <= 0f) return false;
            Vector3 p = origin + dir * t;
            Vector3 d = p - transform.position;
            float s = Mathf.Max(1e-4f, transform.lossyScale.x); // panels can be resized by scaling them
            var local = new Vector2(Vector3.Dot(d, transform.right), Vector3.Dot(d, transform.up)) / s;
            if (!anywhereOnPlane && (Mathf.Abs(local.x) > size.x * 0.5f || Mathf.Abs(local.y) > size.y * 0.5f)) return false;
            hit = new PanelPointer { origin = origin, direction = dir, world = p, local = local, distance = t };
            return true;
        }

        /// <summary>0..1 position on the panel, (0,0) bottom-left.</summary>
        public Vector2 LocalToUV(Vector2 local) => new Vector2(local.x / size.x + 0.5f, local.y / size.y + 0.5f);

        public virtual void PointerHover(PanelPointer p) { }
        public virtual void PointerDown(PanelPointer p) { }
        public virtual void PointerDrag(PanelPointer p) { }
        public virtual void PointerUp(PanelPointer p) { }
        public virtual void PointerExit(int id) { }
        /// <summary>Thumbstick / scroll, -1..1 per second.</summary>
        public virtual void PointerScroll(PanelPointer p, Vector2 amount) { }
    }
}
