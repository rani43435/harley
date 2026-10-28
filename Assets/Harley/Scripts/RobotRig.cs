using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Harley's robot body below the head: arms that follow the hands (two-bone IK,
    /// so every hand gesture in CompanionVisual becomes a real arm movement),
    /// legs that dangle while hovering, swing when flying and bend when sitting,
    /// and a little core light in the chest that breathes with the voice.
    /// Runs after CompanionVisual.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class RobotRig : MonoBehaviour
    {
        [System.Serializable]
        public class Arm
        {
            public Transform shoulder, upper, lower, hand;
            public float upperLength = 0.075f, lowerLength = 0.075f;
            [Tooltip("Which way the elbow bends (local to the body): out and back.")]
            public Vector3 elbowHint = new Vector3(1f, -0.3f, -1f);
        }

        [System.Serializable]
        public class Leg
        {
            public Transform hip, thigh, shin, foot;
            public float thighLength = 0.07f, shinLength = 0.07f;
            [HideInInspector] public float side = 1f;
        }

        public CompanionVisual visual;
        public CompanionMotion movement;
        public CompanionVoice voice;
        public Transform body;   // the torso group (rotates with the lean)
        public Arm left = new Arm(), right = new Arm();
        public Leg legL = new Leg(), legR = new Leg();
        public Renderer core;     // chest light
        [Tooltip("Thickness of arm / leg segments.")]
        public float limbWidth = 0.022f;

        float _sit, _swing;
        MaterialPropertyBlock _mpb;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            if (!visual) visual = GetComponentInChildren<CompanionVisual>();
            if (!movement) movement = GetComponentInParent<CompanionMotion>();
            if (!voice) voice = GetComponentInParent<CompanionVoice>();
            left.elbowHint = new Vector3(-1f, -0.3f, -1f);
            legL.side = -1f; legR.side = 1f;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime, t = Time.time;
            bool sitting = movement != null && movement.IsSitting;
            _sit = Mathf.MoveTowards(_sit, sitting ? 1f : 0f, dt * 2.5f);
            Vector3 vel = movement != null ? movement.Velocity : Vector3.zero;
            Transform basis = body != null ? body : transform;

            SolveArm(left, basis);
            SolveArm(right, basis);

            // Legs: hover = hang slightly back, flying = trail behind, sitting = thighs forward, shins down
            float speed = vel.magnitude;
            _swing += dt * (2f + speed * 6f);
            float flyBack = Mathf.Clamp01(speed * 1.5f) * 35f;
            PoseLeg(legL, basis, t, flyBack, 0f);
            PoseLeg(legR, basis, t, flyBack, Mathf.PI);

            // Core light breathes, brighter while talking
            if (core)
            {
                float talk = voice != null ? voice.TalkAmount : 0f;
                Color c = visual != null ? visual.purple : new Color(0.55f, 0.36f, 0.97f);
                float k = 0.8f + Mathf.Sin(t * 2.2f) * 0.2f + talk * 1.4f;
                core.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, c * k);
                core.SetPropertyBlock(_mpb);
            }
        }

        void SolveArm(Arm a, Transform basis)
        {
            if (!a.shoulder || !a.hand || !a.upper || !a.lower) return;
            Vector3 s = a.shoulder.position, h = a.hand.position;
            Vector3 to = h - s;
            float L1 = a.upperLength * Scale(), L2 = a.lowerLength * Scale();
            float d = Mathf.Clamp(to.magnitude, 0.001f, (L1 + L2) * 0.999f);
            Vector3 dir = to.sqrMagnitude > 1e-8f ? to.normalized : -basis.up;

            // Law of cosines: distance from shoulder along dir to the elbow's foot, and the elbow's height off the line
            float a1 = (L1 * L1 - L2 * L2 + d * d) / (2f * d);
            float hgt = Mathf.Sqrt(Mathf.Max(0f, L1 * L1 - a1 * a1));
            Vector3 hint = basis.TransformDirection(a.elbowHint.normalized);
            Vector3 bend = Vector3.ProjectOnPlane(hint, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(-basis.forward, dir);
            Vector3 elbow = s + dir * a1 + bend.normalized * hgt;

            // The hand may not reach (stretched past the arm): keep the hand where the arm ends
            Vector3 handEnd = s + dir * d;
            Place(a.upper, s, elbow);
            Place(a.lower, elbow, handEnd);
        }

        void PoseLeg(Leg l, Transform basis, float t, float flyBack, float phase)
        {
            if (!l.hip || !l.thigh || !l.shin) return;
            float sc = Scale();
            Vector3 hip = l.hip.position;
            Vector3 down = -basis.up, fwd = basis.forward, side = basis.right * l.side;

            // Hovering: legs hang, toes a bit back, gentle swing. Sitting: thighs forward, shins hang.
            float swing = Mathf.Sin(_swing + phase) * (6f + flyBack * 0.2f);
            Quaternion hang = Quaternion.AngleAxis(-(10f + flyBack + swing), basis.right);
            Vector3 thighDir = Vector3.Slerp(hang * down, (fwd * 0.96f + down * 0.28f).normalized, _sit);
            thighDir = (thighDir + side * 0.08f).normalized;
            Vector3 knee = hip + thighDir * l.thighLength * sc;

            Quaternion kneeBend = Quaternion.AngleAxis(Mathf.Lerp(18f + swing * 0.6f, 0f, _sit), basis.right);
            Vector3 shinDir = Vector3.Slerp(kneeBend * (hang * down), (down + fwd * 0.12f).normalized, _sit);
            Vector3 ankle = knee + shinDir * l.shinLength * sc;

            Place(l.thigh, hip, knee);
            Place(l.shin, knee, ankle);
            if (l.foot)
            {
                l.foot.position = ankle + down * 0.006f * sc + fwd * 0.01f * sc;
                l.foot.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(fwd, basis.up).normalized, basis.up);
            }
        }

        /// <summary>Stretches a capsule between two points (capsule's long axis is local Y).</summary>
        void Place(Transform seg, Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            seg.position = (a + b) * 0.5f;
            if (len > 1e-5f) seg.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            float w = limbWidth * Scale();
            // Capsule mesh is 2 units tall and 1 wide at scale 1; its own parent scale is cancelled by lossy scale.
            Vector3 parentScale = seg.parent != null ? seg.parent.lossyScale : Vector3.one;
            float ps = Mathf.Max(1e-5f, parentScale.x);
            seg.localScale = new Vector3(w, Mathf.Max(len * 0.5f, w * 0.5f), w) / ps;
        }

        float Scale() => Mathf.Max(0.0001f, transform.lossyScale.x);
    }
}
