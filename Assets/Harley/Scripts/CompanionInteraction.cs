using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Harley
{
    /// <summary>
    /// Physical interaction with your real hands (Quest hand tracking) or controllers:
    ///  - Poke it with a finger
    ///  - Pet its head (stroke the top for about a second)
    ///  - Wave at it
    ///  - Pinch it (or squeeze the grip button) to pick it up; let go to place it,
    ///    or throw it and watch it bounce off your walls
    ///  - Look at it for a moment to start talking (eye contact = it listens),
    ///    or press A on the right controller
    ///
    /// Fingertips and controller anchors are found automatically by name
    /// (OVRSkeleton, Interaction SDK, XR Hands, OVRCameraRig). You can also drag them in.
    /// </summary>
    public class CompanionInteraction : CompanionInput
    {
        [Tooltip("Centre of Harley's body.")]
        public Transform body;
        public float bodyRadius = 0.1f;
        public Transform head;
        public CompanionMotion movement;

        [Header("Hands")]
        public Transform leftIndexTip;
        public Transform rightIndexTip;
        public Transform leftThumbTip;
        public Transform rightThumbTip;
        [Tooltip("Controller anchors, for grabbing with the grip button.")]
        public Transform leftControllerAnchor;
        public Transform rightControllerAnchor;
        public bool autoFindHands = true;

        [Header("Grab")]
        public bool allowGrab = true;
        [Tooltip("Thumb and index closer than this = pinching.")]
        public float pinchOn = 0.022f;
        public float pinchOff = 0.04f;

        [Header("Talk trigger")]
        public bool lookToTalk = true;
        [Tooltip("How close to the centre of your view it must be (degrees).")]
        public float lookAngle = 9f;
        public float lookHoldSeconds = 0.9f;
        public float maxTalkDistance = 3f;
        public bool controllerButtonToTalk = true;

        public bool IsHolding => _holder != null;

        class Tip
        {
            public Transform t, thumb;
            public Vector3 lastPos, vel;
            public float lastMoved;
            public bool inside, pinching;
            public float petTime;
            public readonly List<float> reversals = new List<float>();
            public float lastVx;
        }

        class Holder
        {
            public Func<Vector3> point;
            public Func<bool> stillHolding;
            public Vector3 offset;
            public readonly Queue<(float t, Vector3 p)> trail = new Queue<(float, Vector3)>();
        }

        readonly Tip _l = new Tip(), _r = new Tip();
        Holder _holder;
        float _nextSearch, _lookTimer;
        bool _lookArmed = true;
        float _pokeCooldown, _petCooldown, _waveCooldown, _talkCooldown;
        bool _btnWasDown, _gripLWas, _gripRWas;

        protected override void Start()
        {
            base.Start();
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (body == null) body = transform;
        }

        void Update()
        {
            if (head == null)
            {
                if (Camera.main != null) head = Camera.main.transform; else return;
            }

            if (autoFindHands && Time.time >= _nextSearch &&
                (leftIndexTip == null || rightIndexTip == null || leftThumbTip == null || rightThumbTip == null ||
                 leftControllerAnchor == null || rightControllerAnchor == null))
                FindTips();

            _l.t = leftIndexTip; _l.thumb = leftThumbTip;
            _r.t = rightIndexTip; _r.thumb = rightThumbTip;

            if (_holder != null) UpdateHolding();

            UpdateTip(_l);
            UpdateTip(_r);
            UpdateControllers();

            UpdateLook();
            UpdateButton();
        }

        // ---------- hands ----------

        void UpdateTip(Tip tip)
        {
            if (tip.t == null || !tip.t.gameObject.activeInHierarchy) { tip.inside = false; tip.pinching = false; return; }

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 p = tip.t.position;
            Vector3 v = (p - tip.lastPos) / dt;
            tip.vel = Vector3.Lerp(tip.vel, v, 0.5f);
            if ((p - tip.lastPos).sqrMagnitude > 0.0000001f) tip.lastMoved = Time.time;
            tip.lastPos = p;

            // A tip that has not moved for a while is an untracked (frozen) hand: ignore it.
            if (Time.time - tip.lastMoved > 0.75f) { tip.inside = false; tip.petTime = 0f; tip.pinching = false; return; }

            Vector3 c = body.position;
            float dist = Vector3.Distance(p, c);

            // --- pinch to grab ---
            if (allowGrab && tip.thumb != null)
            {
                float pinch = Vector3.Distance(tip.thumb.position, p);
                bool was = tip.pinching;
                tip.pinching = was ? pinch < pinchOff : pinch < pinchOn;
                Vector3 pinchPoint = (tip.thumb.position + p) * 0.5f;
                if (!was && tip.pinching && _holder == null && Vector3.Distance(pinchPoint, c) < bodyRadius + 0.07f && !HarleyGrab.NearAny(pinchPoint))
                {
                    Tip captured = tip;
                    StartHolding(() => (captured.thumb.position + captured.t.position) * 0.5f, () => captured.pinching);
                }
            }

            if (_holder != null) { tip.inside = false; return; } // no pokes or pets while carried

            // --- pet: stroking the top of the head ---
            Vector3 top = c + Vector3.up * bodyRadius * 0.85f;
            bool onTop = Vector3.Distance(p, top) < bodyRadius * 0.75f && p.y > c.y + bodyRadius * 0.35f;
            float horizSpeed = new Vector2(tip.vel.x, tip.vel.z).magnitude;
            if (onTop && horizSpeed > 0.04f && horizSpeed < 0.9f)
            {
                tip.petTime += Time.deltaTime;
                if (tip.petTime > 1.0f && Time.time > _petCooldown)
                {
                    _petCooldown = Time.time + 4f;
                    tip.petTime = 0f;
                    RaisePetted();
                }
            }
            else tip.petTime = Mathf.Max(0f, tip.petTime - Time.deltaTime * 0.5f);

            // --- poke: finger enters the body (not from the top while petting) ---
            bool inside = dist < bodyRadius + 0.015f;
            if (inside && !tip.inside && !onTop && !tip.pinching && Time.time > _pokeCooldown)
            {
                _pokeCooldown = Time.time + 0.8f;
                RaisePoked();
            }
            tip.inside = inside;

            // --- wave: hand up at head height, swinging side to side, away from Harley ---
            Vector3 local = head.InverseTransformPoint(p);
            Vector3 localVel = head.InverseTransformDirection(tip.vel);
            bool raised = local.y > -0.3f && local.z > 0.1f && dist > 0.3f;
            tip.reversals.RemoveAll(time => Time.time - time > 1.6f);
            if (raised && Mathf.Abs(localVel.x) > 0.25f)
            {
                float sign = Mathf.Sign(localVel.x);
                if (tip.lastVx != 0f && sign != tip.lastVx) tip.reversals.Add(Time.time);
                tip.lastVx = sign;
                if (tip.reversals.Count >= 3 && Time.time > _waveCooldown)
                {
                    _waveCooldown = Time.time + 5f;
                    tip.reversals.Clear();
                    RaiseWaved();
                }
            }
            if (!raised) { tip.lastVx = 0f; tip.reversals.Clear(); }
        }

        // ---------- controllers: grip to grab ----------

        void UpdateControllers()
        {
            if (!allowGrab) return;
            Grip(XRNode.LeftHand, leftControllerAnchor, ref _gripLWas);
            Grip(XRNode.RightHand, rightControllerAnchor, ref _gripRWas);
        }

        void Grip(XRNode node, Transform anchor, ref bool was)
        {
            InputDevice dev = InputDevices.GetDeviceAtXRNode(node);
            bool down = false;
            if (dev.isValid) dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out down);
            if (down && !was && anchor != null && _holder == null &&
                Vector3.Distance(anchor.position, body.position) < bodyRadius + 0.12f && !HarleyGrab.NearAny(anchor.position + anchor.forward * 0.04f))
            {
                StartHolding(() => anchor.position, () =>
                {
                    InputDevice d = InputDevices.GetDeviceAtXRNode(node);
                    return d.isValid && d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool g) && g;
                });
            }
            was = down;
        }

        // ---------- holding ----------

        void StartHolding(Func<Vector3> point, Func<bool> stillHolding)
        {
            _holder = new Holder { point = point, stillHolding = stillHolding };
            _holder.offset = Vector3.ClampMagnitude(body.position - point(), bodyRadius * 0.8f);
            movement?.BeginHold();
            RaiseGrabbed();
        }

        void UpdateHolding()
        {
            Vector3 p;
            bool holding;
            try { p = _holder.point(); holding = _holder.stillHolding(); }
            catch { p = body.position; holding = false; } // hand vanished

            float now = Time.time;
            _holder.trail.Enqueue((now, p));
            while (_holder.trail.Count > 0 && now - _holder.trail.Peek().t > 0.1f) _holder.trail.Dequeue();

            if (holding)
            {
                movement?.HoldAt(p + _holder.offset);
                return;
            }

            // Let go: velocity over the last ~0.1 s decides place vs throw.
            Vector3 vel = Vector3.zero;
            if (_holder.trail.Count >= 2)
            {
                var first = _holder.trail.Peek();
                float span = now - first.t;
                if (span > 0.01f) vel = (p - first.p) / span;
            }
            _holder = null;
            movement?.Release(vel);
            RaiseReleased(vel);
        }

        // ---------- finding hand bones ----------

        void FindTips()
        {
            _nextSearch = Time.time + 2f;
            var all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var t in all)
            {
                string n = t.name.ToLowerInvariant();
                bool index = n.Contains("indextip") || n.Contains("index_tip") || n.Contains("index_finger_tip") || n.Contains("indexfingertip");
                bool thumb = n.Contains("thumbtip") || n.Contains("thumb_tip") || n.Contains("thumb_finger_tip");
                bool ctrlL = n == "lefthandanchor" || n == "leftcontrolleranchor";
                bool ctrlR = n == "righthandanchor" || n == "rightcontrolleranchor";

                if (ctrlL && leftControllerAnchor == null) { leftControllerAnchor = t; continue; }
                if (ctrlR && rightControllerAnchor == null) { rightControllerAnchor = t; continue; }
                if (!index && !thumb) continue;

                int side = SideOf(t);
                if (index && side < 0 && leftIndexTip == null) leftIndexTip = t;
                else if (index && side > 0 && rightIndexTip == null) rightIndexTip = t;
                else if (thumb && side < 0 && leftThumbTip == null) leftThumbTip = t;
                else if (thumb && side > 0 && rightThumbTip == null) rightThumbTip = t;
            }
        }

        static int SideOf(Transform t)
        {
            for (Transform x = t; x != null; x = x.parent)
            {
                string n = x.name.ToLowerInvariant();
                if (n.Contains("left") || n.StartsWith("l_", StringComparison.Ordinal) || n.EndsWith("_l", StringComparison.Ordinal)) return -1;
                if (n.Contains("right") || n.StartsWith("r_", StringComparison.Ordinal) || n.EndsWith("_r", StringComparison.Ordinal)) return 1;
            }
            return 0;
        }

        // ---------- talk triggers ----------

        void UpdateLook()
        {
            Vector3 to = body.position - head.position;
            float d = to.magnitude;
            bool looking = d < maxTalkDistance && d > 0.01f && Vector3.Angle(head.forward, to) < lookAngle;

            IsPlayerLooking = looking;
            if (!lookToTalk || _holder != null) return;
            if (looking)
            {
                _lookTimer += Time.deltaTime;
                if (_lookArmed && _lookTimer >= lookHoldSeconds && Time.time > _talkCooldown)
                {
                    _lookArmed = false;
                    _talkCooldown = Time.time + 3f;
                    RaiseTalk(false);
                }
            }
            else
            {
                _lookTimer = 0f;
                _lookArmed = true; // look away and back to talk again
            }
        }

        void UpdateButton()
        {
            if (!controllerButtonToTalk) return;
            InputDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            bool down = false;
            if (right.isValid) right.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out down);
            if (down && !_btnWasDown)
            {
                RaiseTalk(true);
            }
            _btnWasDown = down;
        }
    }
}
