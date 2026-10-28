using UnityEngine;

namespace Harley
{
    /// <summary>
    /// All the life in the character: floating bob, leaning into movement,
    /// blinking, eyes that follow you, emotions, talking mouth, glowing halo
    /// (listening / thinking), squash when poked, waving, spinning and dancing.
    ///
    /// The parts are built by the editor menu "Harley > Create Harley In Scene",
    /// but you can swap in your own model: keep the same slots.
    /// </summary>
    public class CompanionVisual : MonoBehaviour
    {
        [Header("Parts (filled by the builder)")]
        public Transform squashRoot;
        public Transform eyeL, eyeR, mouth;
        public Renderer cheekL, cheekR;
        public Transform handL, handR;
        public LineRenderer halo;
        public TrailRenderer trail;
        public ParticleSystem sparkles;
        public Renderer[] glowRenderers; // eyes + mouth: tinted by emotion

        [Header("Links")]
        public CompanionMotion movement;
        public CompanionVoice voice;

        [Header("Colours")]
        public Color purple = new Color(0.357f, 0.129f, 0.714f);     // #5B21B6 deep royal purple
        public Color lavender = new Color(0.769f, 0.710f, 0.992f);   // #C4B5FD
        public Color pink = new Color(0.957f, 0.447f, 0.714f);       // #F472B6
        public Color sadViolet = new Color(0.388f, 0.400f, 0.945f);  // #6366F1
        [Range(0f, 4f)] public float glowIntensity = 1.0f;

        [Header("Size")]
        public float haloRadius = 0.07f;
        public float haloHeight = 0.125f;
        [Tooltip("How far the hands travel in gestures (wave, dance, listening). The robot body uses bigger moves.")]
        public float gestureScale = 1f;
        [Tooltip("Where the left hand goes while listening (cupped near the head), relative to its rest spot.")]
        public Vector3 listenHandOffset = new Vector3(-0.005f, 0.06f, -0.01f);
        [Tooltip("When set, the right hand reaches for this (handing something over).")]
        public Transform reachTargetR;

        public Emotion CurrentEmotion => Time.time < _overrideUntil ? _overrideEmotion : _baseEmotion;

        Emotion _baseEmotion = Emotion.Happy, _overrideEmotion;
        float _overrideUntil;
        bool _listening, _thinking;

        // animation state
        Vector3 _eyeLBase, _eyeRBase, _mouthBase, _handLBase, _handRBase;
        Vector3 _eyeScale, _mouthScale;
        float _nextBlink, _blinkT = -1f;
        float _squash, _squashVel;
        float _spinT = -1f, _danceT = -1f, _waveT = -1f;
        float _haloSpin;
        Vector3 _eyeShiftL, _eyeShiftR;
        float _sitBlend;
        float _spawnT = -1f;
        float _tumble;
        Color _glowNow;
        MaterialPropertyBlock _mpb;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            if (movement == null) movement = GetComponentInParent<CompanionMotion>();
            if (voice == null) voice = GetComponentInParent<CompanionVoice>();
            if (eyeL) { _eyeLBase = eyeL.localPosition; _eyeScale = eyeL.localScale; }
            if (eyeR) _eyeRBase = eyeR.localPosition;
            if (mouth) { _mouthBase = mouth.localPosition; _mouthScale = mouth.localScale; }
            if (handL) _handLBase = handL.localPosition;
            if (handR) _handRBase = handR.localPosition;
            _glowNow = purple;
            _nextBlink = Time.time + Random.Range(1.5f, 4f);
            BuildHalo();
        }

        // ---------- public API (called by the brain) ----------

        public void SetEmotion(Emotion e) { _baseEmotion = e; }

        /// <summary>Show an emotion for a few seconds, then go back.</summary>
        public void FlashEmotion(Emotion e, float seconds) { _overrideEmotion = e; _overrideUntil = Time.time + seconds; }

        public void SetListening(bool on) { _listening = on; }
        public void SetThinking(bool on) { _thinking = on; }

        public void Poke()
        {
            _squashVel += 9f;
            FlashEmotion(Emotion.Surprised, 1.2f);
        }

        public void Perform(CompanionAction a)
        {
            switch (a)
            {
                case CompanionAction.Spin: _spinT = 0f; break;
                case CompanionAction.Dance: _danceT = 0f; break;
                case CompanionAction.Wave: _waveT = 0f; break;
            }
        }

        public void Wave() { _waveT = 0f; }

        /// <summary>Pop into the room: grow from nothing with a sparkle burst.</summary>
        public void PopIn()
        {
            _spawnT = 0f;
            transform.localScale = Vector3.zero;
            Burst(36, lavender);
        }

        /// <summary>A soft squish, e.g. when landing after a throw.</summary>
        public void Squish(float amount = 6f) { _squashVel += amount; }

        /// <summary>Little glowing sparkles around the body.</summary>
        public void Burst(int count, Color color)
        {
            if (sparkles == null) return;
            var p = new ParticleSystem.EmitParams { startColor = color, applyShapeToPosition = true };
            sparkles.Emit(p, count);
        }

        // ---------- per frame ----------

        void LateUpdate()
        {
            float t = Time.time;
            float dt = Time.deltaTime;
            Emotion emo = CurrentEmotion;
            bool sitting = movement != null && movement.IsSitting;
            _sitBlend = Mathf.MoveTowards(_sitBlend, sitting ? 1f : 0f, dt * 2f);
            float talk = voice != null ? voice.TalkAmount : 0f;

            // --- float / bob ---
            float bobSpeed = emo == Emotion.Excited ? 1.4f : emo == Emotion.Sad || emo == Emotion.Sleepy ? 0.35f : 0.6f;
            float bobAmp = Mathf.Lerp(0.012f, 0.002f, _sitBlend);
            float y = Mathf.Sin(t * Mathf.PI * 2f * bobSpeed) * bobAmp;

            // --- lean into movement ---
            Transform basis = transform.parent != null ? transform.parent : transform;
            Vector3 localVel = movement != null ? basis.InverseTransformDirection(movement.Velocity) : Vector3.zero;
            float pitch = Mathf.Clamp(localVel.z * 18f, -15f, 15f);
            float roll = Mathf.Clamp(-localVel.x * 18f, -15f, 15f);
            if (emo == Emotion.Curious) roll += 12f;
            if (_thinking) pitch -= 6f; // look up a bit while thinking

            // --- spin / dance ---
            float yaw = 0f;
            if (_spinT >= 0f)
            {
                _spinT += dt / 0.8f;
                float e = EaseInOut(Mathf.Clamp01(_spinT));
                yaw = e * 360f;
                y += Mathf.Sin(Mathf.Clamp01(_spinT) * Mathf.PI) * 0.05f;
                if (_spinT >= 1f) _spinT = -1f;
            }
            if (_danceT >= 0f)
            {
                _danceT += dt;
                yaw = Mathf.Sin(_danceT * Mathf.PI * 2f * 1.2f) * 28f;
                y += Mathf.Abs(Mathf.Sin(_danceT * Mathf.PI * 2f * 2f)) * 0.035f;
                roll += Mathf.Sin(_danceT * Mathf.PI * 2f * 1.2f) * 10f;
                if (_danceT > 3.2f) _danceT = -1f;
            }

            // Carried in your hand: dangle. Thrown: tumble.
            if (movement != null && movement.IsHeld) roll += Mathf.Sin(t * 7f) * 9f;
            if (movement != null && movement.IsFlying) _tumble += dt * 720f;
            else _tumble = Mathf.MoveTowards(_tumble % 360f, _tumble % 360f > 180f ? 360f : 0f, dt * 900f);
            roll += _tumble;

            // Pop-in: elastic grow from nothing.
            if (_spawnT >= 0f)
            {
                _spawnT += dt / 0.7f;
                float k01 = Mathf.Clamp01(_spawnT);
                float scale = k01 >= 1f ? 1f : 1f - Mathf.Cos(k01 * Mathf.PI * 3.5f) * Mathf.Exp(-6f * k01);
                transform.localScale = Vector3.one * Mathf.Max(0f, scale);
                if (_spawnT >= 1f) { _spawnT = -1f; transform.localScale = Vector3.one; }
            }

            transform.localPosition = new Vector3(0f, y - _sitBlend * 0.01f, 0f);
            transform.localRotation = Quaternion.Euler(pitch, yaw, roll);

            // --- squash & stretch (spring) ---
            float k = 120f, damp = 9f;
            float targetSquash = _sitBlend * 0.07f + talk * 0.03f;
            _squashVel += (-(_squash - targetSquash) * k - _squashVel * damp) * dt;
            _squash += _squashVel * dt;
            if (squashRoot)
            {
                float s = Mathf.Clamp(_squash, -0.35f, 0.35f);
                squashRoot.localScale = new Vector3(1f + s * 0.5f, 1f - s, 1f + s * 0.5f);
            }

            // --- eyes: shape, blink, follow ---
            UpdateEyes(emo, t, dt);

            // --- mouth ---
            if (mouth)
            {
                float open = talk;
                Vector3 ms = _mouthScale;
                if (emo == Emotion.Surprised) open = Mathf.Max(open, 0.7f);
                ms.y = _mouthScale.y * (1f + open * 2.2f);
                ms.x = _mouthScale.x * (emo == Emotion.Happy || emo == Emotion.Love || emo == Emotion.Excited ? 1.25f : emo == Emotion.Sad ? 0.75f : 1f);
                mouth.localScale = ms;
                mouth.localPosition = _mouthBase + (emo == Emotion.Sad ? Vector3.down * 0.004f : Vector3.zero);
            }

            // --- cheeks ---
            bool blush = emo == Emotion.Love || emo == Emotion.Happy || emo == Emotion.Excited;
            if (cheekL) cheekL.enabled = blush;
            if (cheekR) cheekR.enabled = blush;

            // --- hands ---
            UpdateHands(t, dt, talk);

            // --- glow colour ---
            Color target = emo == Emotion.Love ? pink : emo == Emotion.Sad ? sadViolet : emo == Emotion.Excited ? lavender : purple;
            float intensity = glowIntensity * (emo == Emotion.Sad || emo == Emotion.Sleepy ? 0.6f : emo == Emotion.Excited ? 1.2f : 1f);
            if (_listening) intensity *= 1.1f + Mathf.Sin(t * 6f) * 0.1f;
            _glowNow = Color.Lerp(_glowNow, target, 1f - Mathf.Exp(-6f * dt));
            ApplyGlow(_glowNow, intensity);

            // --- halo ---
            UpdateHalo(t, dt, talk);

            // --- trail only while flying ---
            if (trail) trail.emitting = movement != null && (movement.Velocity.magnitude > 0.35f || movement.IsFlying);
        }

        void UpdateEyes(Emotion emo, float t, float dt)
        {
            if (!eyeL || !eyeR) return;

            Vector2 shapeL = Vector2.one, shapeR = Vector2.one;
            float lift = 0f;
            switch (emo)
            {
                case Emotion.Happy:     shapeL = shapeR = new Vector2(1.05f, 0.45f); lift = 0.003f; break;
                case Emotion.Excited:   shapeL = shapeR = new Vector2(1.15f, 1.15f); break;
                case Emotion.Curious:   shapeL = new Vector2(1.15f, 1.15f); shapeR = new Vector2(0.9f, 0.8f); break;
                case Emotion.Surprised: shapeL = shapeR = new Vector2(1.3f, 1.3f); break;
                case Emotion.Sad:       shapeL = shapeR = new Vector2(0.9f, 0.7f); lift = -0.005f; break;
                case Emotion.Sleepy:    shapeL = shapeR = new Vector2(1.05f, 0.22f); lift = -0.004f; break;
                case Emotion.Love:      shapeL = shapeR = new Vector2(1.1f, 0.55f); lift = 0.002f; break;
            }

            // Blink
            if (_blinkT < 0f && t >= _nextBlink && emo != Emotion.Sleepy) _blinkT = 0f;
            float blink = 1f;
            if (_blinkT >= 0f)
            {
                _blinkT += dt / 0.14f;
                blink = 1f - Mathf.Sin(Mathf.Clamp01(_blinkT) * Mathf.PI) * 0.92f;
                if (_blinkT >= 1f)
                {
                    _blinkT = -1f;
                    _nextBlink = t + Random.Range(2f, 5.5f);
                    if (Random.value < 0.15f) _nextBlink = t + 0.18f; // double blink
                }
            }

            // Look toward the look point (player face or a thing), within the face.
            Vector3 lookShift = Vector3.zero;
            if (movement != null)
            {
                Vector3 local = transform.InverseTransformPoint(movement.LookPoint).normalized;
                lookShift = new Vector3(Mathf.Clamp(local.x, -1f, 1f) * 0.008f, Mathf.Clamp(local.y, -1f, 1f) * 0.006f, 0f);
            }
            if (_thinking) lookShift = new Vector3(Mathf.Sin(t * 1.3f) * 0.006f, 0.007f, 0f); // eyes up, searching
            float f = 1f - Mathf.Exp(-10f * dt);
            _eyeShiftL = Vector3.Lerp(_eyeShiftL, lookShift + Vector3.up * lift, f);
            _eyeShiftR = Vector3.Lerp(_eyeShiftR, lookShift + Vector3.up * lift, f);

            eyeL.localPosition = _eyeLBase + _eyeShiftL;
            eyeR.localPosition = _eyeRBase + _eyeShiftR;
            eyeL.localScale = Vector3.Lerp(eyeL.localScale, new Vector3(_eyeScale.x * shapeL.x, _eyeScale.y * shapeL.y * blink, _eyeScale.z), Mathf.Max(f, _blinkT >= 0f ? 1f : 0f));
            eyeR.localScale = Vector3.Lerp(eyeR.localScale, new Vector3(_eyeScale.x * shapeR.x, _eyeScale.y * shapeR.y * blink, _eyeScale.z), Mathf.Max(f, _blinkT >= 0f ? 1f : 0f));
        }

        void UpdateHands(float t, float dt, float talk)
        {
            if (!handL || !handR) return;
            float floatL = Mathf.Sin(t * 2.1f) * 0.006f;
            float floatR = Mathf.Sin(t * 2.1f + 1.3f) * 0.006f;
            Vector3 l = _handLBase + Vector3.up * floatL;
            Vector3 r = _handRBase + Vector3.up * floatR;

            // Little gestures while talking
            if (talk > 0.05f)
            {
                l += new Vector3(0f, Mathf.Sin(t * 5f) * 0.008f, 0.01f) * talk;
                r += new Vector3(0f, Mathf.Sin(t * 5f + 2f) * 0.008f, 0.01f) * talk;
            }

            if (_waveT >= 0f)
            {
                _waveT += dt;
                float up = Mathf.Clamp01(_waveT * 5f) * Mathf.Clamp01((1.8f - _waveT) * 5f);
                r = Vector3.Lerp(r, _handRBase + new Vector3(0.03f + Mathf.Sin(_waveT * 16f) * 0.022f, 0.11f * gestureScale, 0.02f), up);
                if (_waveT > 1.8f) _waveT = -1f;
            }

            if (_danceT >= 0f)
            {
                float a = Mathf.Sin(_danceT * Mathf.PI * 2f * 2f);
                l += new Vector3(0f, (0.05f + a * 0.03f) * gestureScale, 0f);
                r += new Vector3(0f, (0.05f - a * 0.03f) * gestureScale, 0f);
            }

            if (_listening) // hands near "ears"
            {
                l = Vector3.Lerp(l, _handLBase + listenHandOffset, 0.6f);
            }

            if (reachTargetR != null && handR.parent != null)
                r = handR.parent.InverseTransformPoint(reachTargetR.position);

            float f = 1f - Mathf.Exp(-14f * dt);
            handL.localPosition = Vector3.Lerp(handL.localPosition, l, f);
            handR.localPosition = Vector3.Lerp(handR.localPosition, r, f);
        }

        void UpdateHalo(float t, float dt, float talk)
        {
            if (!halo) return;
            float speed = _thinking ? 360f : _listening ? 120f : 30f;
            _haloSpin += speed * dt;
            float radius = haloRadius * (_listening ? 1.2f : 1f) * (1f + Mathf.Sin(t * 2f) * 0.03f + talk * 0.08f);
            halo.transform.localPosition = new Vector3(0f, haloHeight, 0f);
            halo.transform.localRotation = Quaternion.Euler(12f, _haloSpin, 0f);
            halo.transform.localScale = Vector3.one * (radius / Mathf.Max(0.0001f, haloRadius));

            Color c = _glowNow;
            float alpha = _thinking ? 0.6f + Mathf.Sin(t * 10f) * 0.4f : _listening ? 1f : 0.75f;
            // Dashed look while thinking: fade the ends of the ring.
            Color end = c; end.a = _thinking ? 0.05f : alpha;
            c.a = alpha;
            halo.startColor = c;
            halo.endColor = end;
        }

        void ApplyGlow(Color c, float intensity)
        {
            if (glowRenderers == null) return;
            foreach (var r in glowRenderers)
            {
                if (!r) continue;
                r.GetPropertyBlock(_mpb);
                Color albedo = c * 0.2f; albedo.a = 1f; // dark base, the glow comes from emission
                _mpb.SetColor(BaseColorId, albedo);
                _mpb.SetColor(ColorId, albedo);
                _mpb.SetColor(EmissionId, c * intensity);
                r.SetPropertyBlock(_mpb);
            }
        }

        void BuildHalo()
        {
            if (!halo) return;
            const int segments = 48;
            halo.useWorldSpace = false;
            halo.loop = true;
            halo.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                halo.SetPosition(i, new Vector3(Mathf.Cos(a) * haloRadius, 0f, Mathf.Sin(a) * haloRadius));
            }
        }

        static float EaseInOut(float x) => x < 0.5f ? 2f * x * x : 1f - Mathf.Pow(-2f * x + 2f, 2f) / 2f;
    }
}
