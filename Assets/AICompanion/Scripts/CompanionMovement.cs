using System.Collections.Generic;
using UnityEngine;

namespace AICompanion
{
    /// <summary>
    /// Moves the companion around the player in mixed reality:
    /// lazy follow (it does not stick to your face), wander, fly to what you
    /// look at, sit on a table, hide behind you, come close.
    ///
    /// It uses physics colliders for the room. With Meta MRUK, add an
    /// "Effect Mesh" (or the Room Mesh building block) with colliders on, and
    /// the companion will avoid walls and find tables/couches to sit on.
    /// Without colliders it still works, it just flies through walls.
    /// </summary>
    public class CompanionMovement : MonoBehaviour
    {
        [Tooltip("Player head. Empty = Main Camera.")]
        public Transform head;

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
        [Tooltip("Never closer to your face than this.")]
        public float personalSpace = 0.45f;
        public float turnSpeed = 6f;

        [Header("Room")]
        public LayerMask roomLayers = ~0;
        [Tooltip("Floor height. Detected automatically if Detect Floor is on and the room has colliders.")]
        public float floorY = 0f;
        public bool detectFloor = true;
        [Tooltip("Radius of the character, used to sit on surfaces and keep off walls.")]
        public float bodyRadius = 0.1f;

        public CompanionAction CurrentAction { get; private set; } = CompanionAction.Follow;
        public Vector3 Velocity => _vel;
        public Vector3 LookPoint { get; private set; }
        public bool IsSitting { get; private set; }
        public bool LookingAtPlayer { get; private set; } = true;

        Vector3 _target, _vel;
        float _actionUntil = float.PositiveInfinity;
        bool _anchored;
        Vector3 _anchorHeadPos;
        float _anchorYaw;
        int _side = 1;
        float _nextFloorCheck;
        bool _floorFound;

        // wander / multi-step actions
        readonly List<Vector3> _waypoints = new List<Vector3>();
        float _nextWaypointAt;
        int _hideStep;
        Vector3 _sitSpot;

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
            _side = Random.value < 0.5f ? -1 : 1;
        }

        public void Perform(CompanionAction action)
        {
            if (head == null) return;
            CurrentAction = action;
            IsSitting = false;
            LookingAtPlayer = true;
            _waypoints.Clear();
            _hideStep = 0;

            switch (action)
            {
                case CompanionAction.Stay:        _actionUntil = Time.time + 8f; _target = transform.position; break;
                case CompanionAction.Follow:      _actionUntil = float.PositiveInfinity; _anchored = false; break;
                case CompanionAction.ComeClose:   _actionUntil = Time.time + 6f; break;
                case CompanionAction.Wander:      BuildWander(); _actionUntil = Time.time + 20f; break;
                case CompanionAction.LookAtThing: AimAtThing(); _actionUntil = Time.time + 7f; break;
                case CompanionAction.Sit:         _sitSpot = FindSitSpot(); _actionUntil = float.PositiveInfinity; break;
                case CompanionAction.Hide:        _actionUntil = Time.time + 5.5f; break;
                case CompanionAction.Spin:        _actionUntil = Time.time + 1.2f; _target = transform.position; break;
                case CompanionAction.Dance:       _actionUntil = Time.time + 3.5f; _target = transform.position; break;
                case CompanionAction.Wave:        _actionUntil = Time.time + 2f; _target = transform.position; break;
            }
        }

        void Update()
        {
            if (head == null)
            {
                if (Camera.main != null) head = Camera.main.transform; else return;
            }

            if (detectFloor && Time.time >= _nextFloorCheck) DetectFloor();
            if (Time.time >= _actionUntil) Perform(CompanionAction.Follow);

            float smooth = smoothTime;
            LookPoint = head.position;

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
                    LookingAtPlayer = false;
                    LookPoint = _target + _vel.normalized * 0.5f;
                    if (Vector3.Distance(transform.position, _target) < 0.12f)
                    {
                        LookingAtPlayer = true;
                        LookPoint = head.position;
                        if (_nextWaypointAt <= 0f) _nextWaypointAt = Time.time + Random.Range(1.5f, 3.5f);
                        if (Time.time >= _nextWaypointAt) { _waypoints.RemoveAt(0); _nextWaypointAt = 0f; }
                    }
                    break;

                case CompanionAction.LookAtThing:
                    LookingAtPlayer = false;
                    LookPoint = _thingPoint;
                    break;

                case CompanionAction.Sit:
                    _target = _sitSpot + Vector3.up * bodyRadius * 0.92f;
                    IsSitting = Vector3.Distance(transform.position, _target) < 0.05f;
                    // Player walked away: get up and follow.
                    if (Vector3.Distance(Flat3(head.position), Flat3(_sitSpot)) > 2.8f) Perform(CompanionAction.Follow);
                    break;

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
                    break;
            }

            Vector3 goal = SafeTarget(_target);
            transform.position = Vector3.SmoothDamp(transform.position, goal, ref _vel, smooth, maxSpeed);

            // Face whatever we are looking at (yaw only, the eyes do the rest).
            Vector3 lookDir = Flat3(LookPoint - transform.position);
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                Quaternion want = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, want, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));
            }
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
                float ang = Random.Range(-75f, 75f);
                float dist = Random.Range(0.8f, 2.2f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * f;
                float y = Random.Range(floorY + 0.5f, head.position.y + 0.1f);
                Vector3 p = new Vector3(head.position.x, y, head.position.z) + dir * dist;
                _waypoints.Add(p);
            }
        }

        // ---------- look at thing ----------

        Vector3 _thingPoint;

        void AimAtThing()
        {
            if (Physics.Raycast(head.position, head.forward, out RaycastHit hit, 5f, roomLayers, QueryTriggerInteraction.Ignore))
            {
                _thingPoint = hit.point;
                Vector3 toHead = (head.position - hit.point).normalized;
                // Float next to the thing, slightly toward you so both are in view.
                _target = hit.point + hit.normal * 0.25f + toHead * 0.2f + Vector3.up * 0.08f;
            }
            else
            {
                _thingPoint = head.position + head.forward * 3f;
                _target = head.position + head.forward * 1.5f;
            }
        }

        // ---------- sit ----------

        Vector3 FindSitSpot()
        {
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
            // No furniture found: sit on the floor in front of the player.
            Vector3 floorSpot = head.position + f * 1.1f;
            floorSpot.y = floorY;
            return floorSpot;
        }

        // ---------- helpers ----------

        Vector3 SafeTarget(Vector3 t)
        {
            // Not inside your face.
            Vector3 fromHead = t - head.position;
            if (fromHead.magnitude < personalSpace)
                t = head.position + (fromHead.sqrMagnitude > 0.0001f ? fromHead.normalized : Flat(head.forward)) * personalSpace;

            // Not through walls: keep line of sight from the player.
            Vector3 d = t - head.position;
            float len = d.magnitude;
            if (len > 0.01f && Physics.SphereCast(head.position, bodyRadius * 0.6f, d / len, out RaycastHit hit, len, roomLayers, QueryTriggerInteraction.Ignore))
                t = head.position + d / len * Mathf.Max(personalSpace, hit.distance - bodyRadius);

            // Not under the floor.
            t.y = Mathf.Max(t.y, floorY + bodyRadius * 0.92f);
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
