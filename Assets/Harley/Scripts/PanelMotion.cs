using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Window app version: Harley lives inside its own app window. It floats at
    /// its spot, follows your pointer with its eyes, can be dragged around and
    /// flicked so it bounces off the window edges, sits on the bottom, hides,
    /// wanders around the window, and glances over at your other apps.
    /// </summary>
    public class PanelMotion : CompanionMotion
    {
        public Camera cam;
        public float bodyRadius = 0.1f;

        [Header("Throwing")]
        public float throwGravity = 2.5f;
        [Range(0f, 1f)] public float bounciness = 0.55f;
        public float maxThrowSpeed = 6f;

        /// <summary>Where the pointer is on Harley's plane, set by PanelInput. Null when the pointer is elsewhere.</summary>
        public Vector3? PointerWorld { get; set; }

        enum Place { Home, Held, Flying, Resting, Placed, Away, Returning }

        Place _place = Place.Home;
        Vector3 _home, _vel, _holdTarget, _awayTarget, _lookPoint, _glance, _lastPos;
        float _until, _flyUntil, _lastBounce, _nextGlance, _lookAwayUntil;
        bool _sitting;
        readonly List<Vector3> _wander = new List<Vector3>();

        public override Vector3 Velocity => _vel;
        public override Vector3 LookPoint => _lookPoint;
        public override bool IsHeld => _place == Place.Held;
        public override bool IsFlying => _place == Place.Flying;
        public override bool IsSitting => _sitting;

        void Start()
        {
            if (cam == null) cam = Camera.main;
            if (head == null && cam != null) head = cam.transform;
            _home = transform.position;
            _lastPos = _home;
        }

        // ---------- commands ----------

        public override void Perform(CompanionAction action, CompanionRoom.Thing thing = null)
        {
            if (IsHeld || IsFlying) return;
            _sitting = false;
            _wander.Clear();
            Bounds(out float minX, out float maxX, out float floorY, out float topY);

            switch (action)
            {
                case CompanionAction.ComeClose:
                    GoAway(_home + (cam != null ? -cam.transform.forward : Vector3.forward) * 0.18f, 4f);
                    break;
                case CompanionAction.Hide:
                    GoAway(new Vector3(_home.x, floorY - bodyRadius * 3f, _home.z), 2.5f); // duck below the window edge, then peek back
                    break;
                case CompanionAction.Wander:
                    for (int i = 0; i < 3; i++)
                        _wander.Add(new Vector3(Random.Range(minX, maxX), Random.Range(floorY + 0.05f, topY), _home.z));
                    GoAway(_wander[0], 2.5f);
                    break;
                case CompanionAction.Sit:
                case CompanionAction.GoTo:
                    GoAway(new Vector3(_home.x, floorY, _home.z), 25f);
                    _sitting = true;
                    break;
                case CompanionAction.LookAtThing:
                    _lookAwayUntil = Time.time + 4f; // glance over at the other app windows
                    break;
                default: // follow, stay, spin, dance, wave
                    if (_place != Place.Home) _place = Place.Returning;
                    break;
            }
        }

        void GoAway(Vector3 target, float seconds)
        {
            _awayTarget = target;
            _until = Time.time + seconds;
            _place = Place.Away;
        }

        // ---------- grab & throw ----------

        public override void BeginHold()
        {
            _place = Place.Held;
            _sitting = false;
            _holdTarget = transform.position;
        }

        public override void HoldAt(Vector3 worldPoint) => _holdTarget = worldPoint;

        public override void Release(Vector3 velocity)
        {
            if (_place != Place.Held) return;
            if (velocity.magnitude > 0.9f)
            {
                _place = Place.Flying;
                _vel = Vector3.ClampMagnitude(velocity * 1.1f, maxThrowSpeed);
                _flyUntil = Time.time + 2.5f;
            }
            else
            {
                _place = Place.Placed; // put it somewhere: it stays there a while
                _until = Time.time + 8f;
            }
        }

        // ---------- per frame ----------

        void Update()
        {
            if (cam == null) { cam = Camera.main; if (cam == null) return; }
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Bounds(out float minX, out float maxX, out float floorY, out float topY);
            Vector3 p = transform.position;

            switch (_place)
            {
                case Place.Held:
                    p = Vector3.Lerp(p, _holdTarget, 1f - Mathf.Exp(-25f * dt));
                    _vel = (p - _lastPos) / dt;
                    break;

                case Place.Flying:
                {
                    _vel.y -= throwGravity * dt;
                    _vel *= Mathf.Exp(-0.8f * dt);
                    p += _vel * dt;
                    p.z = Mathf.Lerp(p.z, _home.z, 1f - Mathf.Exp(-4f * dt));
                    float impact = 0f;
                    if (p.x < minX) { p.x = minX; impact = Mathf.Abs(_vel.x); _vel.x = Mathf.Abs(_vel.x) * bounciness; }
                    if (p.x > maxX) { p.x = maxX; impact = Mathf.Abs(_vel.x); _vel.x = -Mathf.Abs(_vel.x) * bounciness; }
                    if (p.y > topY) { p.y = topY; impact = Mathf.Abs(_vel.y); _vel.y = -Mathf.Abs(_vel.y) * bounciness; }
                    if (p.y < floorY)
                    {
                        p.y = floorY;
                        if (_vel.y < 0f) { impact = -_vel.y; _vel.y = -_vel.y * bounciness * 0.8f; _vel.x *= 0.75f; }
                    }
                    if (impact > 0.4f && Time.time - _lastBounce > 0.12f) { _lastBounce = Time.time; RaiseBounced(impact); }
                    if ((p.y <= floorY + 0.005f && _vel.magnitude < 0.25f) || Time.time > _flyUntil)
                    {
                        _place = Place.Resting;
                        _until = Time.time + 1.6f;
                        _vel = Vector3.zero;
                        RaiseLanded();
                    }
                    break;
                }

                case Place.Resting:
                case Place.Placed:
                    _vel = Vector3.zero;
                    if (Time.time > _until) _place = Place.Returning;
                    break;

                case Place.Away:
                    p = Vector3.SmoothDamp(p, _awayTarget, ref _vel, 0.45f, 2f);
                    if (_wander.Count > 0 && Vector3.Distance(p, _awayTarget) < 0.02f && Time.time > _until - 1.5f)
                    {
                        _wander.RemoveAt(0);
                        if (_wander.Count > 0) GoAway(_wander[0], 2.5f);
                    }
                    if (Time.time > _until) { _place = Place.Returning; _sitting = false; }
                    break;

                case Place.Returning:
                    p = Vector3.SmoothDamp(p, _home, ref _vel, 0.4f, 2f);
                    if (Vector3.Distance(p, _home) < 0.003f) { p = _home; _vel = Vector3.zero; _place = Place.Home; }
                    break;

                default: // Home
                    p = Vector3.Lerp(p, _home, 1f - Mathf.Exp(-8f * dt));
                    _vel = (p - _lastPos) / dt;
                    break;
            }

            transform.position = p;
            _lastPos = p;
            UpdateLook(minX, maxX);
            FaceLookPoint(dt);
        }

        void UpdateLook(float minX, float maxX)
        {
            Vector3 camPos = cam.transform.position;
            if (Time.time < _lookAwayUntil)
            {
                // Glance sideways, toward the other app windows.
                _lookPoint = transform.position + new Vector3((maxX - minX) * 3f, 0f, 0.2f);
                return;
            }
            if (PointerWorld.HasValue)
            {
                Vector3 offset = PointerWorld.Value - transform.position;
                _lookPoint = camPos + offset * 1.5f;
                return;
            }
            if (Time.time > _nextGlance)
            {
                _nextGlance = Time.time + Random.Range(2.5f, 6f);
                _glance = Random.value < 0.5f ? Vector3.zero : new Vector3(Random.Range(-0.25f, 0.25f), Random.Range(-0.1f, 0.12f), 0f);
            }
            _lookPoint = camPos + _glance;
        }

        void FaceLookPoint(float dt)
        {
            if (cam == null) return;
            Vector3 toCam = cam.transform.position - transform.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 0.0001f) return;
            Vector3 toLook = _lookPoint - transform.position;
            toLook.y = 0f;
            Quaternion facing = Quaternion.LookRotation(toCam.normalized, Vector3.up);
            Quaternion want = toLook.sqrMagnitude > 0.0001f
                ? Quaternion.RotateTowards(facing, Quaternion.LookRotation(toLook.normalized, Vector3.up), 25f)
                : facing;
            transform.rotation = Quaternion.Slerp(transform.rotation, want, 1f - Mathf.Exp(-6f * dt));
        }

        /// <summary>The part of the plane through Harley's home that the camera can see, minus Harley's size.</summary>
        void Bounds(out float minX, out float maxX, out float floorY, out float topY)
        {
            if (cam == null)
            {
                minX = _home.x - 0.2f; maxX = _home.x + 0.2f; floorY = _home.y - 0.1f; topY = _home.y + 0.1f;
                return;
            }
            float d = Mathf.Max(0.05f, Vector3.Dot(_home - cam.transform.position, cam.transform.forward));
            float halfH = cam.orthographic ? cam.orthographicSize : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * d;
            float halfW = halfH * cam.aspect;
            Vector3 c = cam.transform.position + cam.transform.forward * d;
            minX = c.x - halfW + bodyRadius * 1.3f;
            maxX = c.x + halfW - bodyRadius * 1.3f;
            floorY = c.y - halfH + bodyRadius * 1.05f;
            topY = c.y + halfH - bodyRadius * 1.7f; // leave room for the halo
            if (maxX < minX) { float m = (minX + maxX) * 0.5f; minX = maxX = m; }
            if (topY < floorY) topY = floorY;
        }
    }
}
