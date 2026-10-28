using System;
using System.Collections.Generic;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Moves Harley around the player in mixed reality:
    /// lazy follow (it does not stick to your face), wander, fly to what you look
    /// at, go to a named room object (couch, window, screen...), sit on furniture,
    /// hide behind you, come close, and get grabbed / thrown with your hand.
    ///
    /// Room data comes from CompanionRoom (Meta MRUK scene) and from physics
    /// colliders (Effect Mesh / Room Mesh with colliders on). Without either it
    /// still works, it just flies through walls.
    /// </summary>
    public class CompanionMovement : CompanionMotion
    {
        public CompanionRoom room;

        [Header("Follow")]
        public float followDistance = 0.95f;
        [Tooltip("How far to the side it floats, so it never blocks your view.")]
        public float sideOffset = 0.42f;
        [Tooltip("How far below your eyes it floats.")]
        public float heightBelowEyes = 0.22f;
        [Tooltip("Only moves after you turn more than this. Makes it feel like a pet, not a HUD.")]
        public float reanchorAngle = 35f;
        public float reanchorDistance = 0.5f;

        [Header("Motion")]
        public float smoothTime = 0.5f;
        public float maxSpeed = 1.5f;
        [Tooltip("Never closer to your face than this (unless you hold it).")]
        public float personalSpace = 0.45f;
        public float turnSpeed = 6f;

        [Header("Throwing")]
        public float throwGravity = 3.5f;
        [Range(0f, 1f)] public float bounciness = 0.55f;
        public float maxThrowSpeed = 6f;

        [Header("Room")]
        public LayerMask roomLayers = ~0;
        [Tooltip("Floor height. Taken from the room scan, or detected with colliders.")]
        public float floorY = 0f;
        public bool detectFloor = true;
        [Tooltip("Radius of the character, used to sit on surfaces and keep off walls.")]
        public float bodyRadius = 0.1f;
        [Tooltip("Extra height above a surface for a body under the head (robot legs). 0 = just the round head.")]
        public float standHeight = 0f;

        public CompanionAction CurrentAction { get; private set; } = CompanionAction.Follow;
        public override Vector3 Velocity => _vel;
        public override Vector3 LookPoint => _lookPoint;
        public override bool IsSitting => _sitting;
        public override bool IsHeld => _held;
        public override bool IsFlying => _flying;
        public override string CurrentPlace => _place;

        Vector3 _lookPoint;
        bool _sitting, _held, _flying;
        string _place = "";

        Vector3 _target, _vel, _lastPos;
        float _actionUntil = float.PositiveInfinity;
        bool _anchored;
        Vector3 _anchorHeadPos;
        float _anchorYaw;
        int _side = 1;
        float _nextFloorCheck;
        bool _floorFound;
        bool _skipLineOfSight;

        readonly List<Vector3> _waypoints = new List<Vector3>();
        float _nextWaypointAt;
        Vector3 _sitSpot, _thingPoint, _goTarget;
        bool _goSeat;
        Vector3 _holdTarget, _throwVel;
        float _flyUntil, _lastBounce;

        void Start()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (head != null)
            {
                // Floor guess before any room data: floor-level rigs have y=0 at the floor.
                floorY = head.position.y > 0.8f ? 0f : head.position.y - 1.6f;
                ComputeFollowTarget(true);
                transform.position = _target;
            }
            _lastPos = transform.position;
            _side = UnityEngine.Random.value < 0.5f ? -1 : 1;
        }

        // ---------- commands ----------

        public override void Perform(CompanionAction action, CompanionRoom.Thing thing = null)
        {
            if (head == null || IsHeld || IsFlying) return;
            CurrentAction = action;
            _sitting = false;
            _skipLineOfSight = false;
            _waypoints.Clear();
            _place = "";

            switch (action)
            {
                case CompanionAction.Stay:        _actionUntil = Time.time + 8f; _target = transform.position; break;
                case CompanionAction.Follow:      _actionUntil = float.PositiveInfinity; _anchored = false; break;
                case CompanionAction.ComeClose:   _actionUntil = Time.time + 6f; break;
                case CompanionAction.Wander:      BuildWander(); _actionUntil = Time.time + 20f; break;
                case CompanionAction.LookAtThing: AimAtThing(); _actionUntil = Time.time + 7f; break;
                case CompanionAction.Sit:         _sitSpot = FindSitSpot(); _skipLineOfSight = true; _actionUntil = float.PositiveInfinity; break;
                case CompanionAction.Hide:        _actionUntil = Time.time + 5.5f; break;
                case CompanionAction.Spin:        _actionUntil = Time.time + 1.2f; _target = transform.position; break;
                case CompanionAction.Dance:       _actionUntil = Time.time + 3.5f; _target = transform.position; break;
                case CompanionAction.Wave:        _actionUntil = Time.time + 2f; _target = transform.position; break;
                case CompanionAction.GoTo:
                    if (thing == null) { Perform(CompanionAction.LookAtThing); return; }
                    PlanGoTo(thing);
                    _actionUntil = float.PositiveInfinity;
                    break;
            }
        }

        /// <summary>Fly to a point (games: fetching a ball), looking at it. Ends after the given seconds, then follows again.</summary>
        public void FlyTo(Vector3 point, float seconds, Vector3? lookAt = null)
        {
            if (head == null || IsHeld || IsFlying) return;
            CurrentAction = CompanionAction.Stay;
            _sitting = false;
            _skipLineOfSight = true;
            _waypoints.Clear();
            _place = "";
            point.y = Mathf.Max(point.y, floorY + (bodyRadius * 0.92f + standHeight));
            _target = point;
            _flyLook = lookAt ?? point;
            _flyLookUntil = Time.time + seconds;
            _actionUntil = Time.time + seconds;
        }

        Vector3 _flyLook;
        float _flyLookUntil;

        /// <summary>Stay put right here for a while (after being placed by hand).</summary>
        public void StayHere(float seconds)
        {
            CurrentAction = CompanionAction.Stay;
            _target = transform.position;
            _actionUntil = Time.time + seconds;
            _sitting = false;
        }

        // ---------- grab & throw ----------

        public override void BeginHold()
        {
            _held = true;
            _flying = false;
            _sitting = false;
            _holdTarget = transform.position;
        }

        public override void HoldAt(Vector3 p) => _holdTarget = p;

        public override void Release(Vector3 handVelocity)
        {
            if (!IsHeld) return;
            _held = false;
            if (handVelocity.magnitude > 1.1f)
            {
                _flying = true;
                _throwVel = Vector3.ClampMagnitude(handVelocity * 1.1f, maxThrowSpeed);
                _flyUntil = Time.time + 2.5f;
            }
            else
            {
                StayHere(25f); // you put it somewhere: it stays there a while
            }
        }

        // ---------- per frame ----------

        void Update()
        {
            if (head == null)
            {
                if (Camera.main != null) head = Camera.main.transform; else return;
            }
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            if (room != null && room.FloorY.HasValue) { floorY = room.FloorY.Value; _floorFound = true; }
            else if (detectFloor && Time.time >= _nextFloorCheck) DetectFloor();

            if (IsHeld) { UpdateHeld(dt); FaceLookPoint(); return; }
            if (IsFlying) { UpdateFlying(dt); return; }

            if (Time.time >= _actionUntil) Perform(CompanionAction.Follow);

            float smooth = smoothTime;
            _lookPoint = head.position;

            switch (CurrentAction)
            {
                case CompanionAction.Follow:
                    ComputeFollowTarget(false);
                    break;

                case CompanionAction.ComeClose:
                {
                    Vector3 f = Flat(head.forward);
                    _target = head.position + f * 0.6f + Vector3.up * -0.12f;
                    break;
                }

                case CompanionAction.Wander:
                    smooth = smoothTime * 1.6f;
                    if (_waypoints.Count == 0) { Perform(CompanionAction.Follow); break; }
                    _target = _waypoints[0];
                    _lookPoint = _target + _vel.normalized * 0.5f;
                    if (Vector3.Distance(transform.position, _target) < 0.12f)
                    {
                        _lookPoint = head.position;
                        if (_nextWaypointAt <= 0f) _nextWaypointAt = Time.time + UnityEngine.Random.Range(1.5f, 3.5f);
                        if (Time.time >= _nextWaypointAt) { _waypoints.RemoveAt(0); _nextWaypointAt = 0f; }
                    }
                    break;

                case CompanionAction.LookAtThing:
                    _lookPoint = _thingPoint;
                    break;

                case CompanionAction.Sit:
                    _target = _sitSpot + Vector3.up * (bodyRadius * 0.92f + standHeight);
                    _sitting = Vector3.Distance(transform.position, _target) < 0.05f;
                    if (Vector3.Distance(Flat3(head.position), Flat3(_sitSpot)) > 2.8f) Perform(CompanionAction.Follow);
                    break;

                case CompanionAction.GoTo:
                {
                    smooth = smoothTime * 1.3f;
                    _target = _goTarget;
                    bool arrived = Vector3.Distance(transform.position, _target) < 0.06f;
                    _sitting = _goSeat && arrived;
                    // On the way: look where it's going. There: seats look at you, other things get looked at.
                    _lookPoint = !arrived ? _thingPoint : (_goSeat ? head.position : _thingPoint);
                    if (Vector3.Distance(Flat3(head.position), Flat3(_goTarget)) > 3.5f) Perform(CompanionAction.Follow);
                    break;
                }

                case CompanionAction.Hide:
                {
                    Vector3 f = Flat(head.forward);
                    Vector3 r = Vector3.Cross(Vector3.up, f);
                    float left = _actionUntil - Time.time;
                    if (left > 2.5f) _target = head.position - f * 0.45f + r * 0.3f * _side + Vector3.up * -0.3f; // behind you
                    else _target = head.position + r * 0.55f * _side + f * 0.2f + Vector3.up * -0.15f;           // peekaboo
                    break;
                }

                default: // Stay, Spin, Dance, Wave: hold position
                    if (Time.time < _flyLookUntil) { _lookPoint = _flyLook; smooth = smoothTime * 0.7f; }
                    break;
            }

            Vector3 goal = SafeTarget(_target, !_skipLineOfSight);
            transform.position = Vector3.SmoothDamp(transform.position, goal, ref _vel, smooth, maxSpeed);
            FaceLookPoint();
            _lastPos = transform.position;
        }

        void FaceLookPoint()
        {
            Vector3 lookDir = Flat3(LookPoint - transform.position);
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                Quaternion want = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, want, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));
            }
        }

        void UpdateHeld(float dt)
        {
            Vector3 p = Vector3.Lerp(transform.position, _holdTarget, 1f - Mathf.Exp(-25f * dt));
            _vel = (p - _lastPos) / dt;
            transform.position = p;
            _lastPos = p;
            _lookPoint = head.position;
        }

        void UpdateFlying(float dt)
        {
            _throwVel.y -= throwGravity * dt;
            _throwVel *= Mathf.Exp(-0.8f * dt);
            Vector3 pos = transform.position;
            Vector3 step = _throwVel * dt;
            float dist = step.magnitude;

            if (dist > 0.0001f && Physics.SphereCast(pos, bodyRadius * 0.9f, step / dist, out RaycastHit hit, dist, roomLayers, QueryTriggerInteraction.Ignore))
            {
                pos += step / dist * Mathf.Max(0f, hit.distance - 0.005f);
                float impact = Vector3.Dot(_throwVel, -hit.normal);
                _throwVel = Vector3.Reflect(_throwVel, hit.normal) * bounciness;
                FireBounce(impact);
            }
            else pos += step;

            float minY = floorY + (bodyRadius * 0.92f + standHeight);
            if (pos.y < minY)
            {
                pos.y = minY;
                if (_throwVel.y < 0f)
                {
                    FireBounce(-_throwVel.y);
                    _throwVel.y = -_throwVel.y * bounciness * 0.8f;
                    _throwVel.x *= 0.75f;
                    _throwVel.z *= 0.75f;
                }
            }

            _vel = _throwVel;
            transform.position = pos;
            _lastPos = pos;

            bool resting = pos.y <= minY + 0.01f && _throwVel.magnitude < 0.35f;
            if (resting || Time.time > _flyUntil)
            {
                _flying = false;
                _vel = Vector3.zero;
                StayHere(1.8f); // shake it off, then come back
                RaiseLanded();
            }
        }

        void FireBounce(float impact)
        {
            if (impact < 0.4f || Time.time - _lastBounce < 0.12f) return;
            _lastBounce = Time.time;
            RaiseBounced(impact);
        }

        // ---------- follow ----------

        void ComputeFollowTarget(bool force)
        {
            Vector3 f = Flat(head.forward);
            if (f.sqrMagnitude < 0.0001f) return;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;

            if (force || !_anchored
                || Mathf.Abs(Mathf.DeltaAngle(yaw, _anchorYaw)) > reanchorAngle
                || Vector3.Distance(Flat3(head.position), Flat3(_anchorHeadPos)) > reanchorDistance)
            {
                _anchored = true;
                _anchorYaw = yaw;
                _anchorHeadPos = head.position;
            }

            Quaternion rot = Quaternion.Euler(0f, _anchorYaw, 0f);
            Vector3 fwd = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;
            Vector3 basePos = new Vector3(_anchorHeadPos.x, head.position.y, _anchorHeadPos.z);
            _target = basePos + fwd * followDistance + right * sideOffset * _side + Vector3.up * -heightBelowEyes;

            // A wall on this side? Try the other side.
            if (Physics.Linecast(head.position, _target, roomLayers, QueryTriggerInteraction.Ignore))
            {
                Vector3 other = basePos + fwd * followDistance - right * sideOffset * _side + Vector3.up * -heightBelowEyes;
                if (!Physics.Linecast(head.position, other, roomLayers, QueryTriggerInteraction.Ignore))
                {
                    _side = -_side;
                    _target = other;
                }
            }
        }

        // ---------- wander ----------

        void BuildWander()
        {
            _waypoints.Clear();
            _nextWaypointAt = 0f;
            Vector3 f = Flat(head.forward);
            for (int i = 0; i < 4; i++)
            {
                float ang = UnityEngine.Random.Range(-75f, 75f);
                float dist = UnityEngine.Random.Range(0.8f, 2.2f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * f;
                float y = UnityEngine.Random.Range(floorY + 0.5f, head.position.y + 0.1f);
                _waypoints.Add(new Vector3(head.position.x, y, head.position.z) + dir * dist);
            }
        }

        // ---------- look at / go to ----------

        void AimAtThing()
        {
            if (Physics.Raycast(head.position, head.forward, out RaycastHit hit, 5f, roomLayers, QueryTriggerInteraction.Ignore))
            {
                _thingPoint = hit.point;
                Vector3 toHead = (head.position - hit.point).normalized;
                _target = hit.point + hit.normal * 0.25f + toHead * 0.2f + Vector3.up * 0.08f;
            }
            else
            {
                _thingPoint = head.position + head.forward * 3f;
                _target = head.position + head.forward * 1.5f;
            }
        }

        void PlanGoTo(CompanionRoom.Thing t)
        {
            _place = t.name;
            _thingPoint = t.center;
            _goSeat = false;

            if (t.isVolume)
            {
                float h = t.top.y - floorY;
                float footprint = Mathf.Max(t.size.x, t.size.z);
                if (h >= 0.25f && h <= 1.4f && footprint >= 0.3f)
                {
                    // Land on top of it (couch, table, bed, shelf).
                    _goSeat = true;
                    _skipLineOfSight = true;
                    _goTarget = t.top + Vector3.up * (bodyRadius * 0.92f + standHeight);
                    return;
                }
                // Small or tall things (lamp, plant): hover beside, facing it.
                Vector3 dir = Flat(head.position - t.center);
                if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
                _goTarget = t.center + dir * (footprint * 0.5f + 0.25f);
                _goTarget.y = Mathf.Clamp(t.top.y + 0.1f, floorY + 0.4f, head.position.y);
                return;
            }

            // Window, door, screen, picture: hover in front of it.
            Vector3 n = t.normal.sqrMagnitude > 0.0001f ? t.normal.normalized : Flat(head.position - t.center);
            _goTarget = t.center + n * 0.35f;
            _goTarget.y = Mathf.Clamp(_goTarget.y, floorY + 0.35f, head.position.y + 0.3f);
        }

        // ---------- sit ----------

        Vector3 FindSitSpot()
        {
            if (room != null && room.TryFindSeat(head, floorY, out Vector3 seat)) return seat;

            Vector3 f = Flat(head.forward);
            bool found = false;
            float bestScore = float.MaxValue;
            Vector3 best = Vector3.zero;

            float[] dists = { 0.7f, 1.1f, 1.5f, 1.9f, 2.4f };
            for (int a = -80; a <= 80; a += 20)
            {
                Vector3 dir = Quaternion.Euler(0f, a, 0f) * f;
                foreach (float d in dists)
                {
                    Vector3 p = head.position + dir * d;
                    Vector3 origin = new Vector3(p.x, head.position.y + 0.3f, p.z);
                    if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 3.5f, roomLayers, QueryTriggerInteraction.Ignore)) continue;
                    if (hit.normal.y < 0.9f) continue;
                    float h = hit.point.y - floorY;
                    if (h < 0.3f || h > 1.3f) continue; // tables, couches, shelves, desks
                    if (Physics.Linecast(head.position, hit.point + Vector3.up * 0.15f, roomLayers, QueryTriggerInteraction.Ignore)) continue;
                    float score = Mathf.Abs(a) * 0.01f + d * 0.3f;
                    if (score < bestScore) { bestScore = score; best = hit.point; found = true; }
                }
            }

            if (found) return best;
            Vector3 floorSpot = head.position + f * 1.1f; // nothing found: sit on the floor in front of you
            floorSpot.y = floorY;
            return floorSpot;
        }

        // ---------- helpers ----------

        Vector3 SafeTarget(Vector3 t, bool lineOfSight)
        {
            Vector3 fromHead = t - head.position;
            if (fromHead.magnitude < personalSpace)
                t = head.position + (fromHead.sqrMagnitude > 0.0001f ? fromHead.normalized : Flat(head.forward)) * personalSpace;

            if (lineOfSight)
            {
                Vector3 d = t - head.position;
                float len = d.magnitude;
                if (len > 0.01f && Physics.SphereCast(head.position, bodyRadius * 0.6f, d / len, out RaycastHit hit, len, roomLayers, QueryTriggerInteraction.Ignore))
                    t = head.position + d / len * Mathf.Max(personalSpace, hit.distance - bodyRadius);
            }

            t.y = Mathf.Max(t.y, floorY + (bodyRadius * 0.92f + standHeight));
            return t;
        }

        void DetectFloor()
        {
            _nextFloorCheck = Time.time + 2f;
            if (!Physics.Raycast(head.position, Vector3.down, out RaycastHit hit, 3f, roomLayers, QueryTriggerInteraction.Ignore)) return;
            if (hit.normal.y < 0.8f || head.position.y - hit.point.y < 0.9f) return;
            if (!_floorFound || hit.point.y < floorY + 0.05f)
            {
                floorY = hit.point.y;
                _floorFound = true;
            }
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.000001f ? v.normalized : Vector3.zero;
        }

        static Vector3 Flat3(Vector3 v) { v.y = 0f; return v; }
    }
}
