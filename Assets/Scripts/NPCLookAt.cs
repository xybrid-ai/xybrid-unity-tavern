using UnityEngine;

/// <summary>
/// Who gets to decide where an NPC looks. Higher values override lower ones;
/// lower-priority requests stay registered and resume once the higher one clears.
/// </summary>
public enum LookPriority
{
    Ambient = 0,    // Idle glances, points of interest, glances during someone else's dialogue
    Social = 1,     // NPC-to-NPC table conversation
    Awareness = 2,  // Noticing the player walking up
    Dialogue = 3    // Talking with the player
}

/// <summary>
/// Drives where an NPC looks using humanoid look-at IK (eyes, head and a little spine).
/// Several systems can request a target at different <see cref="LookPriority"/> levels; the
/// highest active one wins, and the IK weight and look point blend smoothly between targets.
/// Standing NPCs may also turn their root (yaw only) toward Awareness/Dialogue targets that are
/// too far to the side for the head alone. Seated/bartending/floor NPCs never rotate their root, and
/// an enabled NPCWanderer on the same object owns the root rotation instead (head IK still runs).
/// Attach to the NPC root, on the same GameObject as the Animator (OnAnimatorIK is only sent there).
/// The Animator's Base Layer must have IK Pass enabled.
/// </summary>
[DisallowMultipleComponent]
public class NPCLookAt : MonoBehaviour
{
    private const int PriorityCount = 4;

    // ---- Legacy V1 fields ----
    // Kept so existing scene data deserializes unchanged. The old head-bone LateUpdate rotation and
    // full-body slerp are gone; posture now comes from NPCAnimationSetup.
#pragma warning disable 0414 // Serialized but intentionally unused
    [HideInInspector] [SerializeField] private bool headOnly = false;     // Legacy (unused)
    [HideInInspector] [SerializeField] private float rotationSpeed = 5f;  // Legacy (unused)
#pragma warning restore 0414

    [Header("Head")]
    [Tooltip("Optional head bone override. Auto-found from the humanoid Animator when empty.")]
    [SerializeField] private Transform headBone;
    [Tooltip("Maximum yaw (degrees) of the look direction relative to the body's forward.")]
    [SerializeField] private float maxHeadAngle = 70f;
    [Tooltip("Non-dialogue targets further behind than this (degrees) are ignored instead of tracked.")]
    [SerializeField] private float lookBehindLimit = 120f;

    [Header("IK Weights")]
    [SerializeField, Range(0f, 1f)] private float bodyWeight = 0.3f;        // Standing
    [SerializeField, Range(0f, 1f)] private float seatedBodyWeight = 0.15f; // Sitting / bartending / floor
    [SerializeField, Range(0f, 1f)] private float headWeight = 0.85f;
    [SerializeField, Range(0f, 1f)] private float eyesWeight = 1f;
    [SerializeField, Range(0f, 1f)] private float clampWeight = 0.5f;

    [Header("Smoothing")]
    [Tooltip("How fast the overall IK weight fades in/out (weight per second).")]
    [SerializeField] private float blendSpeed = 2.5f;
    [Tooltip("How quickly the look point swings to a new target (higher = snappier).")]
    [SerializeField] private float lookTurnSharpness = 6f;

    [Header("Body Turn (Standing only)")]
    [Tooltip("Root yaw turn speed in degrees per second.")]
    [SerializeField] private float bodyTurnSpeed = 120f;
    [Tooltip("Start turning the root once the yaw error exceeds this (head IK covers the rest).")]
    [SerializeField] private float bodyTurnThreshold = 30f;
    [Tooltip("Once turning, keep going until the yaw error drops below this.")]
    [SerializeField] private float bodyTurnSettleAngle = 10f;
    [Tooltip("After the body turned toward the player, ease back to the starting yaw once nobody needs it.")]
    [SerializeField] private bool returnHomeWhenIdle = true;
    [Tooltip("Seconds without an Awareness/Dialogue target before easing back to the starting yaw.")]
    [SerializeField] private float returnHomeDelay = 1f;

    [Header("Auto-Find")]
    [SerializeField] private Transform player;

    // Look requests per priority: a persistent slot and a timed slot (timed overrides persistent until it expires)
    private readonly Transform[] _targets = new Transform[PriorityCount];
    private readonly Transform[] _timedTargets = new Transform[PriorityCount];
    private readonly float[] _timedUntil = new float[PriorityCount];

    // Cached components
    private Animator _animator;
    private Transform _head;
    private NPCAnimationSetup _setup;
    private NPCWanderer _wanderer;

    // Player lookup
    private Transform _playerEyes;
    private float _nextPlayerSearchTime;

    // IK state
    private float _weight;            // Raw 0..1, eased before being applied
    private Vector3 _lookDirection;   // Smoothed world-space direction from the head
    private float _lookDistance = 2f;
    private Vector3 _lookPosition;
    private bool _ikEverCalled;
    private float _dialogueStartTime = -1f;
    private bool _warnedNoIK;

    // Root turn state
    private float _originalYaw;
    private bool _isBodyTurning;
    private float _currentTurnSpeed;
    private bool _rootTurnedByUs;
    private bool _returnHomePending;
    private float _lastBodyTargetTime;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// The player's first-person camera (child "Camera" of the "Player"-tagged object), falling back
    /// to the player transform. Resolved lazily and cached; null if no player exists yet.
    /// </summary>
    public Transform PlayerEyes
    {
        get
        {
            if (_playerEyes == null) ResolvePlayerEyes();
            return _playerEyes;
        }
    }

    /// <summary>The highest-priority active look target, or null when the NPC is free-looking.</summary>
    public Transform CurrentTarget => GetActiveTarget(out _);

    /// <summary>True when the active look target was set at <paramref name="priority"/> or higher.</summary>
    public bool IsLookingAtOrAbove(LookPriority priority)
    {
        return GetActiveTarget(out LookPriority active) != null && active >= priority;
    }

    /// <summary>
    /// Registers a persistent look target at the given priority (replaces any previous one at that priority).
    /// Passing null clears the persistent target at that priority.
    /// </summary>
    public void SetLookTarget(Transform target, LookPriority priority)
    {
        _targets[Index(priority)] = target;
    }

    /// <summary>Clears both the persistent and the timed target at the given priority.</summary>
    public void ClearLookTarget(LookPriority priority)
    {
        int i = Index(priority);
        _targets[i] = null;
        _timedTargets[i] = null;
        _timedUntil[i] = 0f;
    }

    /// <summary>
    /// Looks at a target for a limited time at the given priority, then falls back automatically.
    /// While active it overrides the persistent target of the same priority.
    /// </summary>
    public void LookAtFor(Transform target, float seconds, LookPriority priority)
    {
        if (target == null || seconds <= 0f) return;

        int i = Index(priority);
        _timedTargets[i] = target;
        _timedUntil[i] = Time.time + seconds;
    }

    /// <summary>
    /// Call when dialogue starts. Looks at the player's eyes at Dialogue priority; standing NPCs may
    /// also turn their body toward the player.
    /// </summary>
    public void StartLookingAtPlayer()
    {
        Transform eyes = PlayerEyes;
        if (eyes == null)
        {
            Debug.LogWarning($"[NPCLookAt] {name}: no GameObject tagged 'Player' found; cannot look at the player.");
            return;
        }

        SetLookTarget(eyes, LookPriority.Dialogue);
        _dialogueStartTime = Time.time;
    }

    /// <summary>
    /// Call when dialogue ends. Clears the Dialogue target and, if requested, eases the root back to the
    /// yaw it had at Start once no Awareness/Dialogue target needs the body (skipped for wandering NPCs).
    /// </summary>
    public void StopLookingAtPlayer(bool returnToOriginal = false)
    {
        ClearLookTarget(LookPriority.Dialogue);
        _dialogueStartTime = -1f;

        if (returnToOriginal && !WandererOwnsRoot && _rootTurnedByUs)
        {
            _returnHomePending = true;
            _lastBodyTargetTime = Time.time;
        }
    }

    /// <summary>
    /// The head bone of an NPC (humanoid Animator on the object or its children), else the NPC's transform.
    /// Returns null only when <paramref name="npc"/> is null.
    /// </summary>
    public static Transform HeadOf(Component npc)
    {
        if (npc == null) return null;

        // Prefer the head already cached by that NPC's look-at component
        if (npc.TryGetComponent(out NPCLookAt lookAt) && lookAt._head != null)
            return lookAt._head;

        if (!npc.TryGetComponent(out Animator animator))
            animator = npc.GetComponentInChildren<Animator>();

        if (animator != null && animator.isHuman)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) return head;
        }

        return npc.transform;
    }

    // ================================================================
    // Unity lifecycle
    // ================================================================

    private void Awake()
    {
        if (!TryGetComponent(out _animator))
        {
            _animator = GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                Debug.LogWarning($"[NPCLookAt] {name}: Animator is on child '{_animator.name}'. " +
                                 "OnAnimatorIK is only sent to the Animator's GameObject, so look-at IK will not run. " +
                                 "Move NPCLookAt next to the Animator.");
            }
        }

        _head = headBone;
        if (_head == null && _animator != null && _animator.isHuman)
            _head = _animator.GetBoneTransform(HumanBodyBones.Head);

        if (!TryGetComponent(out _setup))
            _setup = GetComponentInChildren<NPCAnimationSetup>();

        TryGetComponent(out _wanderer);
    }

    private void Start()
    {
        _originalYaw = transform.eulerAngles.y;
        ResolvePlayerEyes();
    }

    private void OnDisable()
    {
        // IK stops being applied while disabled; restart the blend from zero when re-enabled
        _weight = 0f;
        _isBodyTurning = false;
        _currentTurnSpeed = 0f;
    }

    private void Update()
    {
        UpdateBodyTurn(Time.deltaTime);
        CheckIKPassEnabled();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || _animator == null) return;
        _ikEverCalled = true;

        float dt = Time.deltaTime;
        Vector3 headPos = HeadPosition();
        Transform target = GetActiveTarget(out LookPriority priority);

        float desiredWeight = 0f;
        if (target != null)
        {
            Vector3 toTarget = target.position - headPos;
            float distance = toTarget.magnitude;

            if (distance > 0.05f)
            {
                Vector3 dir = ClampToHeadRange(toTarget / distance, priority, out bool ignore);
                if (!ignore)
                {
                    desiredWeight = 1f;

                    if (_weight <= 0.001f)
                    {
                        // Fading in from nothing: the weight ramp itself provides the smooth turn
                        _lookDirection = dir;
                        _lookDistance = distance;
                    }
                    else
                    {
                        // Swing the look point toward the new/moving target instead of snapping
                        float k = 1f - Mathf.Exp(-lookTurnSharpness * dt);
                        _lookDirection = Vector3.Slerp(_lookDirection, dir, k).normalized;
                        _lookDistance = Mathf.Lerp(_lookDistance, distance, k);
                    }
                }
            }
        }

        // When the target goes away the look point stays put while the weight fades out
        _weight = Mathf.MoveTowards(_weight, desiredWeight, blendSpeed * dt);
        if (_weight <= 0.0001f)
        {
            _animator.SetLookAtWeight(0f);
            return;
        }

        _lookPosition = headPos + _lookDirection * Mathf.Max(_lookDistance, 0.5f);

        float easedWeight = Mathf.SmoothStep(0f, 1f, _weight);
        float body = IsStanding ? bodyWeight : seatedBodyWeight;

        _animator.SetLookAtPosition(_lookPosition);
        _animator.SetLookAtWeight(easedWeight, body, headWeight, eyesWeight, clampWeight);
    }

    // ================================================================
    // Targets
    // ================================================================

    private static int Index(LookPriority priority)
    {
        return Mathf.Clamp((int)priority, 0, PriorityCount - 1);
    }

    private Transform GetActiveTarget(out LookPriority priority)
    {
        float now = Time.time;

        for (int i = PriorityCount - 1; i >= 0; i--)
        {
            // Timed glance overrides the persistent target of the same priority until it expires
            if (_timedTargets[i] != null)
            {
                if (now < _timedUntil[i])
                {
                    priority = (LookPriority)i;
                    return _timedTargets[i];
                }
                _timedTargets[i] = null;
            }

            if (_targets[i] != null)
            {
                priority = (LookPriority)i;
                return _targets[i];
            }
        }

        priority = LookPriority.Ambient;
        return null;
    }

    private void ResolvePlayerEyes()
    {
        if (player == null)
        {
            // Throttle the tag search so a missing player doesn't cost a scene scan every frame
            if (Time.unscaledTime < _nextPlayerSearchTime) return;
            _nextPlayerSearchTime = Time.unscaledTime + 1f;

            var playerObj = GameObject.FindWithTag("Player");
            if (playerObj == null) return;
            player = playerObj.transform;
        }

        Transform eyes = player.Find("Camera");
        if (eyes == null)
        {
            var cam = player.GetComponentInChildren<Camera>();
            if (cam != null) eyes = cam.transform;
        }

        _playerEyes = eyes != null ? eyes : player;
    }

    // ================================================================
    // Head IK helpers
    // ================================================================

    private bool IsStanding => _setup != null && _setup.posture == NPCAnimationSetup.Posture.Standing;

    private Vector3 HeadPosition()
    {
        if (_head != null) return _head.position;
        return transform.position + transform.up * 1.6f;
    }

    /// <summary>
    /// Limits the look direction to +/- maxHeadAngle of yaw around the body's forward. Targets far behind
    /// are ignored (except in dialogue, or when the body is about to turn toward them).
    /// </summary>
    private Vector3 ClampToHeadRange(Vector3 worldDir, LookPriority priority, out bool ignore)
    {
        ignore = false;

        Vector3 local = transform.InverseTransformDirection(worldDir);
        float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        float absYaw = Mathf.Abs(yaw);

        if (absYaw > lookBehindLimit && priority != LookPriority.Dialogue && !CanTurnBody(priority))
        {
            ignore = true;
            return worldDir;
        }

        if (absYaw <= maxHeadAngle) return worldDir;

        // Rebuild the direction at the clamped yaw, keeping its pitch
        float horizontal = Mathf.Sqrt(local.x * local.x + local.z * local.z);
        float clampedYaw = Mathf.Sign(yaw) * maxHeadAngle * Mathf.Deg2Rad;
        local = new Vector3(Mathf.Sin(clampedYaw) * horizontal, local.y, Mathf.Cos(clampedYaw) * horizontal);
        return transform.TransformDirection(local).normalized;
    }

    // ================================================================
    // Root (body) turning — Standing NPCs only
    // ================================================================

    /// <summary>
    /// An enabled NPCWanderer is the single owner of the root rotation: it steers while walking, turns
    /// to each stop's forward, and turns to face the player itself while paused (greeting, dialogue).
    /// A wanderer that disabled itself (no NavMesh) hands the root back to this component.
    /// </summary>
    private bool WandererOwnsRoot => _wanderer != null && _wanderer.isActiveAndEnabled;

    private bool CanTurnBody(LookPriority priority)
    {
        if (!IsStanding) return false;
        if (priority != LookPriority.Awareness && priority != LookPriority.Dialogue) return false;
        if (WandererOwnsRoot) return false;
        return true;
    }

    private void UpdateBodyTurn(float dt)
    {
        bool rotated = false;
        Transform target = GetActiveTarget(out LookPriority priority);

        if (target != null && CanTurnBody(priority))
        {
            _lastBodyTargetTime = Time.time;

            Vector3 flat = target.position - transform.position;
            flat.y = 0f;

            if (flat.sqrMagnitude >= 0.0025f)
            {
                float targetYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                float error = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, targetYaw));

                // Hysteresis: start past the threshold, stop once mostly facing (head IK covers the rest)
                if (!_isBodyTurning && error > bodyTurnThreshold)
                    _isBodyTurning = true;
                else if (_isBodyTurning && error <= bodyTurnSettleAngle)
                    _isBodyTurning = false;

                if (_isBodyTurning)
                {
                    RotateRootToward(targetYaw, error, bodyTurnSpeed, dt);
                    rotated = true;
                    _rootTurnedByUs = true;
                    if (returnHomeWhenIdle)
                        _returnHomePending = true;
                }
            }
            else
            {
                _isBodyTurning = false;
            }
        }
        else
        {
            _isBodyTurning = false;

            // Ease back to the starting yaw once nothing needs the body turned
            if (_returnHomePending && !WandererOwnsRoot && IsStanding &&
                Time.time - _lastBodyTargetTime >= returnHomeDelay)
            {
                float error = Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, _originalYaw));
                if (error < 0.5f)
                {
                    _returnHomePending = false;
                    _rootTurnedByUs = false;
                }
                else
                {
                    RotateRootToward(_originalYaw, error, bodyTurnSpeed * 0.75f, dt);
                    rotated = true;
                }
            }
        }

        // Each new turn starts from rest so it eases in
        if (!rotated) _currentTurnSpeed = 0f;
    }

    private void RotateRootToward(float targetYaw, float error, float maxSpeed, float dt)
    {
        // Ease in (accelerate) and ease out (slow down near the goal)
        _currentTurnSpeed = Mathf.MoveTowards(_currentTurnSpeed, maxSpeed, maxSpeed * 3f * dt);
        float speed = Mathf.Min(_currentTurnSpeed, Mathf.Max(error * 3f, 15f));

        // Yaw-only goal around world up, preserving any existing tilt
        float delta = Mathf.DeltaAngle(transform.eulerAngles.y, targetYaw);
        Quaternion goal = Quaternion.AngleAxis(delta, Vector3.up) * transform.rotation;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, goal, speed * dt);
    }

    // ================================================================
    // Diagnostics
    // ================================================================

    private void CheckIKPassEnabled()
    {
        // During dialogue the NPC is on screen, so OnAnimatorIK should be firing; if not, IK Pass is off
        if (_warnedNoIK || _ikEverCalled || _dialogueStartTime < 0f || _animator == null) return;
        if (!_animator.isActiveAndEnabled || Time.time - _dialogueStartTime < 1f) return;

        _warnedNoIK = true;
        Debug.LogWarning($"[NPCLookAt] {name}: OnAnimatorIK was never called. Enable 'IK Pass' on the " +
                         "Base Layer of the Animator Controller, and keep NPCLookAt on the Animator's GameObject.");
    }

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || _weight <= 0f) return;

        Gizmos.color = Color.Lerp(Color.gray, Color.cyan, _weight);
        Gizmos.DrawLine(HeadPosition(), _lookPosition);
        Gizmos.DrawWireSphere(_lookPosition, 0.05f);
    }
}
