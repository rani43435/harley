using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Harley
{
    /// <summary>
    /// Window app version: touching Harley with the Quest pointer (or mouse in the Editor).
    ///  - Click Harley = poke
    ///  - Drag across the top of its head = pet
    ///  - Drag Harley = pick it up; flick and let go = throw it around the window
    ///  - Talk button (PanelControls) = start listening
    /// Pointing at Harley's window counts as paying attention, so it keeps listening
    /// after it asks you something.
    /// </summary>
    public class PanelInput : CompanionInput
    {
        public Camera cam;
        public Transform body;
        public float bodyRadius = 0.1f;
        public PanelMotion motion;
        [Tooltip("Seconds after the last pointer move that the player still counts as paying attention.")]
        public float attentionSeconds = 8f;

        bool _wasPressed, _down, _onBody, _onTop, _moved, _dragging;
        Vector2 _downPos, _lastPos;
        float _petDist, _petCooldown, _lastPointerAt = -999f;
        Vector3 _grabOffset;
        readonly Queue<(float t, Vector3 p)> _trail = new Queue<(float, Vector3)>();

        protected override void Start()
        {
            base.Start();
            if (cam == null) cam = Camera.main;
            if (motion == null) motion = GetComponent<PanelMotion>();
            if (body == null) body = transform;
        }

        void Update()
        {
            if (cam == null) { cam = Camera.main; if (cam == null) return; }

            bool has = ReadPointer(out Vector2 pos, out bool pressed);
            if (!has)
            {
                if (motion) motion.PointerWorld = null;
                IsPlayerLooking = Time.time - _lastPointerAt < attentionSeconds;
                return;
            }

            if ((pos - _lastPos).sqrMagnitude > 1f || pressed) _lastPointerAt = Time.time;
            _lastPos = pos;
            IsPlayerLooking = Time.time - _lastPointerAt < attentionSeconds;

            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            Vector3 world = PlanePoint(pos);
            if (motion) motion.PointerWorld = overUI ? (Vector3?)null : world;

            // press
            if (pressed && !_wasPressed && !overUI)
            {
                _down = true;
                _moved = false;
                _downPos = pos;
                _onBody = HitsBody(pos, out Vector3 hit);
                _onTop = _onBody && hit.y - body.position.y > bodyRadius * 0.55f;
                _petDist = 0f;
            }

            // drag
            if (pressed && _down)
            {
                if ((pos - _downPos).magnitude > 10f) _moved = true;

                if (_onTop)
                {
                    _petDist += Mathf.Abs(pos.x - _lastDragX);
                    if (_petDist > 140f && Time.time > _petCooldown)
                    {
                        _petDist = 0f;
                        _petCooldown = Time.time + 3f;
                        RaisePetted();
                    }
                }
                else if (_onBody && _moved && !_dragging)
                {
                    _dragging = true;
                    _grabOffset = body.position - world;
                    _trail.Clear();
                    motion?.BeginHold();
                    RaiseGrabbed();
                }

                if (_dragging)
                {
                    Vector3 target = world + _grabOffset;
                    motion?.HoldAt(target);
                    _trail.Enqueue((Time.time, target));
                    while (_trail.Count > 2 && Time.time - _trail.Peek().t > 0.1f) _trail.Dequeue();
                }
            }
            _lastDragX = pos.x;

            // release
            if (!pressed && _wasPressed && _down)
            {
                if (_dragging)
                {
                    Vector3 vel = Vector3.zero;
                    if (_trail.Count >= 2)
                    {
                        var first = _trail.Peek();
                        float span = Time.time - first.t;
                        if (span > 0.01f) vel = (world + _grabOffset - first.p) / span;
                    }
                    motion?.Release(vel);
                    RaiseReleased(vel);
                }
                else if (_onBody && !_onTop && !_moved)
                {
                    RaisePoked();
                }
                _down = _dragging = _onBody = _onTop = false;
            }

            _wasPressed = pressed;
        }

        float _lastDragX;

        bool ReadPointer(out Vector2 pos, out bool pressed)
        {
#if ENABLE_INPUT_SYSTEM
            var p = UnityEngine.InputSystem.Pointer.current;
            if (p == null) { pos = default; pressed = false; return false; }
            pos = p.position.ReadValue();
            pressed = p.press.isPressed;
            return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            pos = Input.mousePosition;
            pressed = Input.GetMouseButton(0);
            return Input.mousePresent || Input.touchCount > 0;
#else
            pos = default; pressed = false; return false;
#endif
        }

        Vector3 PlanePoint(Vector2 screen)
        {
            Ray ray = cam.ScreenPointToRay(screen);
            var plane = new Plane(-cam.transform.forward, body.position);
            return plane.Raycast(ray, out float d) ? ray.GetPoint(d) : body.position;
        }

        bool HitsBody(Vector2 screen, out Vector3 hit)
        {
            Ray ray = cam.ScreenPointToRay(screen);
            Vector3 oc = ray.origin - body.position;
            float b = Vector3.Dot(oc, ray.direction);
            float c = oc.sqrMagnitude - bodyRadius * bodyRadius * 1.1f; // a little forgiving
            float disc = b * b - c;
            if (disc < 0f) { hit = default; return false; }
            float t = -b - Mathf.Sqrt(disc);
            hit = ray.GetPoint(Mathf.Max(0f, t));
            return true;
        }
    }
}
