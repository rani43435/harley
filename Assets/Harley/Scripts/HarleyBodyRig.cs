using UnityEngine;

namespace Harley
{
    /// <summary>
    /// For Harley's Blender model: each arm is one piece that swings from the shoulder so its
    /// hand end follows the hand target CompanionVisual animates (wave, dance, listening, holding
    /// things out). Also makes the chest core and the hover ring breathe with the voice.
    /// Runs after CompanionVisual.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class HarleyBodyRig : MonoBehaviour
    {
        [System.Serializable]
        public class Arm
        {
            public Transform arm;     // pivot at the shoulder
            public Transform target;  // the hand point CompanionVisual moves
            [HideInInspector] public Vector3 restDirLocal;      // shoulder -> hand at rest, in the arm's parent space
            [HideInInspector] public Quaternion restRotLocal;
            [HideInInspector] public bool ready;
        }

        public CompanionVisual visual;
        public CompanionVoice voice;
        public Arm left = new Arm(), right = new Arm();
        public Renderer core;
        public Renderer[] hoverGlow;

        MaterialPropertyBlock _mpb;
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            if (!visual) visual = GetComponentInChildren<CompanionVisual>();
            if (!voice) voice = GetComponentInParent<CompanionVoice>();
            Prepare(left);
            Prepare(right);
        }

        static void Prepare(Arm a)
        {
            if (a.arm == null || a.target == null || a.arm.parent == null) return;
            a.restDirLocal = a.arm.parent.InverseTransformDirection(a.target.position - a.arm.position).normalized;
            a.restRotLocal = a.arm.localRotation;
            a.ready = true;
        }

        void LateUpdate()
        {
            Aim(left);
            Aim(right);

            float talk = voice != null ? voice.TalkAmount : 0f;
            Color c = visual != null ? visual.purple : new Color(0.36f, 0.13f, 0.71f);
            float t = Time.time;
            if (core) Glow(core, c * 2.2f * (1f + Mathf.Sin(t * 2.2f) * 0.25f + talk * 1.6f));
            if (hoverGlow != null)
                foreach (var r in hoverGlow) if (r) Glow(r, c * 2.2f * (1.1f + Mathf.Sin(t * 3.1f) * 0.3f));
        }

        void Aim(Arm a)
        {
            if (!a.ready || a.arm == null || a.target == null) return;
            Transform p = a.arm.parent;
            Vector3 want = p.InverseTransformDirection(a.target.position - a.arm.position);
            if (want.sqrMagnitude < 1e-8f) return;
            Quaternion swing = Quaternion.FromToRotation(a.restDirLocal, want.normalized);
            a.arm.localRotation = Quaternion.Slerp(a.arm.localRotation, swing * a.restRotLocal, 1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        void Glow(Renderer r, Color emission)
        {
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(EmissionId, emission);
            r.SetPropertyBlock(_mpb);
        }
    }
}
