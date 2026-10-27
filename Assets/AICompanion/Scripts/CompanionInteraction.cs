using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace AICompanion
{
    /// <summary>
    /// Physical interaction with your real hands (Quest hand tracking):
    ///  - Poke it with a finger
    ///  - Pet its head (stroke the top for about a second)
    ///  - Wave at it
    ///  - Look at it for a moment to start talking (eye contact = it listens)
    ///  - Or press A on the right controller to talk
    ///
    /// Fingertips are found automatically by name (Meta OVRSkeleton, Interaction
    /// SDK and XR Hands all have an "index tip" bone). You can also drag them in.
    /// </summary>
    public class CompanionInteraction : MonoBehaviour
    {
        [Tooltip("Centre of the character's body.")]
        public Transform body;
        public float bodyRadius = 0.1f;
        public Transform head;

        [Header("Hands")]
        public Transform leftIndexTip;
        public Transform rightIndexTip;
        public bool autoFindHands = true;

        [Header("Talk trigger")]
        public bool lookToTalk = true;
        [Tooltip("How close to the centre of your view it must be (degrees).")]
        public float lookAngle = 9f;
        public float lookHoldSeconds = 0.9f;
        public float maxTalkDistance = 3f;
        public bool controllerButtonToTalk = true;

        public event Action Poked;
        public event Action Petted;
        public event Action Waved;
        /// <summary>Player wants to talk. true = pressed the button, false = looked at the companion.</summary>
        public event Action<bool> TalkRequested;

        /// <summary>True while the player is looking straight at the companion.</summary>
        public bool IsPlayerLooking { get; private set; }

        /// <summary>Seconds since the player last did anything with the companion.</summary>
        public float IdleSeconds => Time.time - _lastActivity;

        class Tip
        {
            public Transform t;
            public Vector3 lastPos, vel;
            public float lastMoved;
            public bool inside;
            public float petTime;
            public readonly List<float> reversals = new List<float>();
            public float lastVx;
        }

        readonly Tip _l = new Tip(), _r = new Tip();
        float _nextSearch, _lookTimer, _lastActivity;
        bool _lookArmed = true;
        float _pokeCooldown, _petCooldown, _waveCooldown, _talkCooldown;
        bool _btnWasDown;

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (body == null) body = transform;
            _lastActivity = Time.time;
        }

        public void MarkActivity() => _lastActivity = Time.time;

        void Update()
        {
            if (head == null)
            {
                if (Camera.main != null) head = Camera.main.transform; else return;
            }

            if (autoFindHands && (leftIndexTip == null || rightIndexTip == null) && Time.time >= _nextSearch)
                FindTips();

            _l.t = leftIndexTip;
            _r.t = rightIndexTip;
            UpdateTip(_l);
            UpdateTip(_r);

            UpdateLook();
            UpdateButton();
        }

        // ---------- hands ----------

        void UpdateTip(Tip tip)
        {
            if (tip.t == null || !tip.t.gameObject.activeInHierarchy) { tip.inside = false; return; }

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 p = tip.t.position;
            Vector3 v = (p - tip.lastPos) / dt;
            tip.vel = Vector3.Lerp(tip.vel, v, 0.5f);
            if ((p - tip.lastPos).sqrMagnitude > 0.0000001f) tip.lastMoved = Time.time;
            tip.lastPos = p;

            // A tip that has not moved for a while is an untracked (frozen) hand: ignore it.
            if (Time.time - tip.lastMoved > 0.75f) { tip.inside = false; tip.petTime = 0f; return; }

            Vector3 c = body.position;
            float dist = Vector3.Distance(p, c);

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
                    MarkActivity();
                    Petted?.Invoke();
                }
            }
            else tip.petTime = Mathf.Max(0f, tip.petTime - Time.deltaTime * 0.5f);

            // --- poke: finger enters the body (not from the top while petting) ---
            bool inside = dist < bodyRadius + 0.015f;
            if (inside && !tip.inside && !onTop && Time.time > _pokeCooldown)
            {
                _pokeCooldown = Time.time + 0.8f;
                MarkActivity();
                Poked?.Invoke();
            }
            tip.inside = inside;

            // --- wave: hand up at head height, swinging side to side, away from the companion ---
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
                    MarkActivity();
                    Waved?.Invoke();
                }
            }
            if (!raised) { tip.lastVx = 0f; tip.reversals.Clear(); }
        }

        void FindTips()
        {
            _nextSearch = Time.time + 2f;
            var all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var t in all)
            {
                string n = t.name.ToLowerInvariant();
                bool isIndexTip = n.Contains("indextip") || n.Contains("index_tip") || n.Contains("index_finger_tip") || n.Contains("indexfingertip");
                if (!isIndexTip) continue;
                int side = SideOf(t);
                if (side < 0 && leftIndexTip == null) leftIndexTip = t;
                else if (side > 0 && rightIndexTip == null) rightIndexTip = t;
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
            bool looking = d < maxTalkDistance && d > 0.01f &&
                           Vector3.Angle(head.forward, to) < lookAngle;

            IsPlayerLooking = looking;
            if (!lookToTalk) return;
            if (looking)
            {
                _lookTimer += Time.deltaTime;
                if (_lookArmed && _lookTimer >= lookHoldSeconds && Time.time > _talkCooldown)
                {
                    _lookArmed = false;
                    _talkCooldown = Time.time + 3f;
                    MarkActivity();
                    TalkRequested?.Invoke(false);
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
                MarkActivity();
                TalkRequested?.Invoke(true);
            }
            _btnWasDown = down;
        }
    }
}
