using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using Tavern.Dialogue;

/// <summary>
/// Lets a standing NPC wander between authored stop points on the NavMesh
/// (bar, hearth, window...), lingering at each one and occasionally gesturing.
/// Pauses and turns to face the player during dialogue, when asked by other
/// behaviours (e.g. NPCAwareness), or when the player blocks the way.
/// Drives the animator float "Speed" so the standing.idle blend tree walks in step
/// with the agent. Attach to the NPC root (same object as Animator / NPCIdentity).
/// Requires a baked NavMesh; without one the NPC simply stays where it was placed.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
[DisallowMultipleComponent]
public class NPCWanderer : MonoBehaviour
{
    /// <summary>A place the NPC can walk to and linger at.</summary>
    [System.Serializable]
    public class Stop
    {
        public Transform point;
        public Vector2 dwellSeconds = new Vector2(4f, 9f);
        public bool faceStopForward = true;      // Turn to the point's blue (Z) axis on arrival
        public NPCGesture[] gestures;            // One may play while dwelling
        public float weight = 1f;                // Relative pick chance (0 = never)
    }

    private enum Phase
    {
        Waiting,    // Standing around with no stop context (start-up, after a failed leg)
        Departing,  // Path requested; turning toward it before setting off
        Walking,    // Agent moving
        Arriving,   // At a stop, turning to face its forward
        Dwelling    // Lingering at a stop
    }

    [Header("Route")]
    [SerializeField] private Stop[] stops = new Stop[0];
    [SerializeField] private Vector2 initialDelaySeconds = new Vector2(1f, 4f);
    [SerializeField] private float navMeshSampleRadius = 1f;   // How far a stop may be from the NavMesh

    [Header("Agent")]
    [SerializeField] private bool applyAgentSettings = true;   // Overwrite the NavMeshAgent values below on Start
    [SerializeField] private float walkSpeed = 1.2f;
    [SerializeField] private float angularSpeed = 300f;
    [SerializeField] private float acceleration = 6f;
    [SerializeField] private float stoppingDistance = 0.15f;

    [Header("Turning")]
    [SerializeField] private float turnSpeed = 180f;           // Max deg/s for manual turns (arrive, depart, pause)
    [SerializeField] private float departAlignAngle = 25f;     // Start walking once roughly facing the path

    [Header("Gestures At Stops")]
    [Range(0f, 1f)]
    [SerializeField] private float gestureChance = 0.6f;

    [Header("Dialogue")]
    [SerializeField] private float resumeAfterDialogueDelay = 1.5f;

    [Header("Player Courtesy")]
    [SerializeField] private bool yieldToPlayer = true;        // Stop and wait if the player is in the way
    [SerializeField] private float yieldDistance = 0.9f;

    [Header("Animation")]
    [SerializeField] private string speedParameter = "Speed";
    [SerializeField] private float speedDampTime = 0.1f;

    [Header("Safety")]
    [SerializeField] private float stuckSeconds = 3f;          // Give up on a leg if barely moving this long

    private const float MovingVelocityThreshold = 0.05f;
    private const float ArriveTurnTimeout = 2.5f;
    private const float DepartTurnTimeout = 1.5f;
    private const float PathPendingTimeout = 3f;
    private const float StartAtStopRadius = 0.6f;
    private const float YieldPauseSeconds = 1.2f;
    private const int MaxYieldsPerLeg = 4;
    private const float PlayerSearchInterval = 1f;

    // Cached components (all optional except the agent)
    private NavMeshAgent _agent;
    private Animator _animator;
    private NPCIdentity _identity;
    private NPCLookAt _lookAt;
    private NPCGestures _gestures;

    private Transform _player;
    private Transform _playerEyes;
    private float _nextPlayerSearchTime;

    private int _speedHash;
    private bool _hasSpeedParam;
    private bool _speedParamResolved;   // Parameters were readable when we looked (not an early empty list)

    // Route state
    private bool _initialized;
    private bool _canWander;
    private bool[] _stopValid = new bool[0];
    private Phase _phase = Phase.Waiting;
    private float _phaseElapsed;
    private float _waitRemaining;
    private int _targetStop = -1;     // Stop currently being walked to
    private int _lastStop = -1;       // Stop last reached (never picked twice in a row)
    private int _dwellStop = -1;      // Stop currently dwelling at
    private float _dwellRemaining;
    private bool _gesturePending;
    private float _gestureAtRemaining;
    private float _stuckTimer;
    private int _yieldCount;

    // Pause state
    private bool _paused;
    private bool _pauseIndefinite;
    private float _pauseUntil;
    private Transform _pauseFaceTarget;
    private bool _pausedForDialogue;
    private Coroutine _resumeRoutine;

    /// <summary>True while the agent has a path and is actually moving.</summary>
    public bool IsMoving =>
        !_paused &&
        IsAgentReady &&
        _agent.hasPath &&
        _agent.velocity.sqrMagnitude > MovingVelocityThreshold * MovingVelocityThreshold;

    private bool IsAgentReady => _agent != null && _agent.isActiveAndEnabled && _agent.isOnNavMesh;

    // ─────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponent<Animator>();
        if (_animator == null)
            _animator = GetComponentInChildren<Animator>();
        _identity = GetComponent<NPCIdentity>();
        _lookAt = GetComponent<NPCLookAt>();
        _gestures = GetComponent<NPCGestures>();
    }

    private void OnEnable()
    {
        DialogueManagerV2.DialogueStarted += HandleDialogueStarted;
        DialogueManagerV2.DialogueEnded += HandleDialogueEnded;

        // Re-enabled after Start: pick up wandering again shortly
        if (_initialized)
        {
            _targetStop = -1;
            EnterWaiting(Random.Range(0.5f, 1.5f));
        }
    }

    private void OnDisable()
    {
        DialogueManagerV2.DialogueStarted -= HandleDialogueStarted;
        DialogueManagerV2.DialogueEnded -= HandleDialogueEnded;

        if (_resumeRoutine != null)
        {
            StopCoroutine(_resumeRoutine);
            _resumeRoutine = null;
        }

        // Don't let the agent keep walking (and sliding) without us driving the animator
        if (IsAgentReady)
        {
            _agent.ResetPath();
            _agent.velocity = Vector3.zero;
            _agent.isStopped = true;
        }

        if (_hasSpeedParam && _animator != null && _animator.isActiveAndEnabled)
            _animator.SetFloat(_speedHash, 0f);
    }

    private void Start()
    {
        CacheAnimatorParameters();

        if (_agent == null)
        {
            Debug.LogWarning($"[NPCWanderer] {name} has no NavMeshAgent — wandering disabled.");
            enabled = false;
            return;
        }

        if (applyAgentSettings)
        {
            _agent.speed = walkSpeed;
            _agent.angularSpeed = angularSpeed;
            _agent.acceleration = acceleration;
            _agent.stoppingDistance = stoppingDistance;
            _agent.autoBraking = true;
        }

        // The walk cycle lives in standing.idle's blend tree; other postures would slide
        var setup = GetComponent<NPCAnimationSetup>();
        if (setup != null && setup.posture != NPCAnimationSetup.Posture.Standing)
        {
            Debug.LogWarning($"[NPCWanderer] {name} posture is {setup.posture}, not Standing — " +
                             "the walk blend tree only exists in the standing state, so the NPC may slide.");
        }

        if (!_agent.isOnNavMesh)
            TrySnapToNavMesh();

        if (!_agent.isOnNavMesh)
        {
            Debug.LogWarning($"[NPCWanderer] {name} is not on a NavMesh — wandering disabled. " +
                             "Bake a NavMesh (NavMeshSurface) that covers the tavern floor.");
            _agent.enabled = false;
            enabled = false;
            return;
        }

        ValidateStops();

        _agent.isStopped = true;
        _agent.updateRotation = false;

        // If we were placed on a stop, treat it as the last one so we head elsewhere first
        _lastStop = FindStopNear(transform.position, StartAtStopRadius);
        EnterWaiting(RandomInRange(initialDelaySeconds));

        _initialized = true;
    }

    private void Update()
    {
        if (!_initialized) return;

        SyncDialoguePause();

        if (_paused)
        {
            if (!_pauseIndefinite && Time.time >= _pauseUntil)
            {
                Resume();
            }
            else
            {
                HoldStill();
                Transform face = _pauseFaceTarget;
                if (face != null)
                    TurnTowardsDirection(face.position - transform.position);
            }
        }

        if (!_paused && _canWander && IsAgentReady)
            TickPhase(Time.deltaTime);

        UpdateAnimatorSpeed();
    }

    // ─────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────

    /// <summary>
    /// Stops for a while (extends an existing timed pause; never shortens an indefinite one),
    /// optionally turning to face a target.
    /// </summary>
    public void PauseFor(float seconds, Transform faceTarget = null)
    {
        if (seconds <= 0f) return;

        if (_paused && _pauseIndefinite)
        {
            // Already held (e.g. in dialogue) — only borrow the face target if we had none
            if (_pauseFaceTarget == null)
                _pauseFaceTarget = faceTarget;
            return;
        }

        bool wasPaused = _paused;
        float until = Time.time + seconds;
        _pauseUntil = wasPaused ? Mathf.Max(_pauseUntil, until) : until;
        _paused = true;

        if (faceTarget != null || !wasPaused)
            _pauseFaceTarget = faceTarget;

        HoldStill();
    }

    /// <summary>Stops indefinitely until Resume() is called, optionally facing a target.</summary>
    public void Pause(Transform faceTarget = null)
    {
        _paused = true;
        _pauseIndefinite = true;
        _pauseFaceTarget = faceTarget;
        HoldStill();
    }

    /// <summary>Ends any pause and carries on with the route.</summary>
    public void Resume()
    {
        if (_resumeRoutine != null)
        {
            StopCoroutine(_resumeRoutine);
            _resumeRoutine = null;
        }
        _pausedForDialogue = false;

        if (!_paused) return;

        _paused = false;
        _pauseIndefinite = false;
        _pauseUntil = 0f;
        _pauseFaceTarget = null;

        if (!_initialized || !IsAgentReady) return;

        switch (_phase)
        {
            case Phase.Walking:
            case Phase.Departing:
                // Re-align with the path before setting off again (avoids moonwalking)
                SetPhase(Phase.Departing);
                break;

            case Phase.Dwelling:
            case Phase.Arriving:
                // We may have turned toward the player; turn back to the stop's view
                if (IsValidStop(_dwellStop) && stops[_dwellStop].faceStopForward)
                    SetPhase(Phase.Arriving);
                break;
        }
    }

    // ─────────────────────────────────────────────
    // Route state machine
    // ─────────────────────────────────────────────

    private void TickPhase(float dt)
    {
        _phaseElapsed += dt;

        switch (_phase)
        {
            case Phase.Waiting:
                _waitRemaining -= dt;
                if (_waitRemaining <= 0f)
                    BeginNextLeg();
                break;

            case Phase.Departing:
                TickDeparting();
                break;

            case Phase.Walking:
                TickWalking(dt);
                break;

            case Phase.Arriving:
                TickArriving();
                break;

            case Phase.Dwelling:
                TickDwelling(dt);
                break;
        }
    }

    private void BeginNextLeg()
    {
        int next = PickNextStop();
        if (next < 0)
        {
            // Nowhere else to go (single stop): linger here a while longer
            if (IsValidStop(_lastStop))
                BeginDwell(_lastStop);
            else
                _canWander = false;
            return;
        }

        if (!TryGetStopPosition(next, out Vector3 destination))
        {
            FailLeg(next);
            return;
        }

        _targetStop = next;
        _yieldCount = 0;
        _agent.isStopped = true;
        _agent.updateRotation = false;

        if (!_agent.SetDestination(destination))
        {
            FailLeg(next);
            return;
        }

        SetPhase(Phase.Departing);
    }

    private void TickDeparting()
    {
        if (_agent.pathPending)
        {
            if (_phaseElapsed > PathPendingTimeout)
                FailLeg(_targetStop);
            return;
        }

        // Unreachable (invalid, or a partial path to a disconnected NavMesh island)
        if (_agent.pathStatus != NavMeshPathStatus.PathComplete)
        {
            FailLeg(_targetStop);
            return;
        }

        if (!_agent.hasPath || _agent.remainingDistance <= _agent.stoppingDistance + 0.05f)
        {
            // Already there (e.g. resumed right next to the stop), otherwise give up on this leg
            if (IsNearTargetStop())
                OnArrived();
            else
                FailLeg(_targetStop);
            return;
        }

        float angle = TurnTowardsDirection(_agent.steeringTarget - transform.position);
        if (angle <= departAlignAngle || _phaseElapsed > DepartTurnTimeout)
            StartWalking();
    }

    private void StartWalking()
    {
        _agent.isStopped = false;
        _agent.updateRotation = true;
        _stuckTimer = 0f;
        SetPhase(Phase.Walking);
    }

    private void TickWalking(float dt)
    {
        if (yieldToPlayer && IsPlayerBlockingPath())
        {
            // Wait facing the way we were going (head look is NPCLookAt's job);
            // if the player keeps blocking, pick somewhere else instead
            _yieldCount++;
            if (_yieldCount > MaxYieldsPerLeg)
                FailLeg(_targetStop);
            else
                PauseFor(YieldPauseSeconds);
            return;
        }

        if (_agent.pathPending) return;

        if (_agent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            FailLeg(_targetStop);
            return;
        }

        if (!_agent.hasPath || _agent.remainingDistance <= _agent.stoppingDistance + 0.05f)
        {
            OnArrived();
            return;
        }

        // Stuck detection (blocked by another agent, bad geometry...)
        if (_agent.velocity.sqrMagnitude < MovingVelocityThreshold * MovingVelocityThreshold)
        {
            _stuckTimer += dt;
            if (_stuckTimer > stuckSeconds)
                FailLeg(_targetStop);
        }
        else
        {
            _stuckTimer = 0f;
        }
    }

    private void OnArrived()
    {
        StopAgent();
        _lastStop = _targetStop;
        _targetStop = -1;

        if (IsValidStop(_lastStop))
            BeginDwell(_lastStop);
        else
            EnterWaiting(Random.Range(1f, 2.5f));
    }

    private void BeginDwell(int stopIndex)
    {
        Stop stop = stops[stopIndex];
        _dwellStop = stopIndex;
        _dwellRemaining = Mathf.Max(0.5f, RandomInRange(stop.dwellSeconds));

        // Maybe play one gesture somewhere in the middle of the dwell
        _gesturePending = _gestures != null &&
                          stop.gestures != null && stop.gestures.Length > 0 &&
                          Random.value < gestureChance;
        _gestureAtRemaining = _dwellRemaining * Random.Range(0.3f, 0.7f);

        SetPhase(stop.faceStopForward ? Phase.Arriving : Phase.Dwelling);
    }

    private void TickArriving()
    {
        if (!IsValidStop(_dwellStop))
        {
            SetPhase(Phase.Dwelling);
            return;
        }

        float angle = TurnTowardsDirection(stops[_dwellStop].point.forward);
        if (angle < 2f || _phaseElapsed > ArriveTurnTimeout)
            SetPhase(Phase.Dwelling);
    }

    private void TickDwelling(float dt)
    {
        _dwellRemaining -= dt;

        if (_gesturePending && _dwellRemaining <= _gestureAtRemaining)
            TryPlayStopGesture();

        if (_dwellRemaining <= 0f)
            BeginNextLeg();
    }

    private void TryPlayStopGesture()
    {
        _gesturePending = false;
        if (_gestures == null || !IsValidStop(_dwellStop)) return;

        NPCGesture[] options = stops[_dwellStop].gestures;
        if (options == null || options.Length == 0) return;

        if (_gestures.IsPlaying)
        {
            // Something else is mid-gesture; try once more a second later if there's time
            _gestureAtRemaining = _dwellRemaining - 1f;
            _gesturePending = _gestureAtRemaining > 0.5f;
            return;
        }

        _gestures.Play(options[Random.Range(0, options.Length)]);
    }

    private void FailLeg(int stopIndex)
    {
        StopAgent();

        // Don't immediately retry the stop that just failed
        if (stopIndex >= 0)
            _lastStop = stopIndex;
        _targetStop = -1;

        EnterWaiting(Random.Range(1f, 2.5f));
    }

    private void EnterWaiting(float seconds)
    {
        _waitRemaining = seconds;
        _dwellStop = -1;
        _gesturePending = false;
        SetPhase(Phase.Waiting);
    }

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        _phaseElapsed = 0f;
    }

    // ─────────────────────────────────────────────
    // Stops
    // ─────────────────────────────────────────────

    private void ValidateStops()
    {
        int count = stops != null ? stops.Length : 0;
        _stopValid = new bool[count];
        int valid = 0;

        for (int i = 0; i < count; i++)
        {
            Stop stop = stops[i];
            if (stop == null || stop.point == null)
                continue;

            if (TryGetStopPosition(i, out _))
            {
                _stopValid[i] = true;
                valid++;
            }
            else
            {
                Debug.LogWarning($"[NPCWanderer] {name}: stop '{stop.point.name}' is further than " +
                                 $"{navMeshSampleRadius}m from the NavMesh — ignored.");
            }
        }

        _canWander = valid > 0;
        if (!_canWander)
            Debug.LogWarning($"[NPCWanderer] {name} has no usable stops — standing still.");
    }

    private bool IsValidStop(int index)
    {
        return index >= 0 &&
               stops != null && index < stops.Length &&
               index < _stopValid.Length && _stopValid[index] &&
               stops[index] != null && stops[index].point != null;
    }

    /// <summary>Weighted random stop, never the one we were last at. -1 if none.</summary>
    private int PickNextStop()
    {
        if (stops == null) return -1;

        float total = 0f;
        int candidates = 0;
        for (int i = 0; i < stops.Length; i++)
        {
            if (i == _lastStop || !IsValidStop(i)) continue;
            candidates++;
            total += Mathf.Max(0f, stops[i].weight);
        }

        if (candidates == 0) return -1;

        // All weights zero: fall back to a uniform pick
        if (total <= 0f)
        {
            int pick = Random.Range(0, candidates);
            for (int i = 0; i < stops.Length; i++)
            {
                if (i == _lastStop || !IsValidStop(i)) continue;
                if (pick-- == 0) return i;
            }
            return -1;
        }

        float roll = Random.value * total;
        int lastWeighted = -1;
        for (int i = 0; i < stops.Length; i++)
        {
            if (i == _lastStop || !IsValidStop(i)) continue;
            float w = Mathf.Max(0f, stops[i].weight);
            if (w <= 0f) continue;

            lastWeighted = i;
            if (roll < w) return i;
            roll -= w;
        }

        return lastWeighted; // Float rounding fallback
    }

    /// <summary>Stop position snapped onto the NavMesh for this agent type.</summary>
    private bool TryGetStopPosition(int index, out Vector3 position)
    {
        position = Vector3.zero;
        if (stops == null || index < 0 || index >= stops.Length) return false;

        Stop stop = stops[index];
        if (stop == null || stop.point == null || _agent == null) return false;

        var filter = new NavMeshQueryFilter
        {
            agentTypeID = _agent.agentTypeID,
            areaMask = _agent.areaMask
        };

        if (NavMesh.SamplePosition(stop.point.position, out NavMeshHit hit, navMeshSampleRadius, filter))
        {
            position = hit.position;
            return true;
        }
        return false;
    }

    private bool IsNearTargetStop()
    {
        if (!TryGetStopPosition(_targetStop, out Vector3 stopPosition)) return false;

        Vector3 delta = stopPosition - transform.position;
        delta.y = 0f;
        float reach = _agent.stoppingDistance + 0.3f;
        return delta.sqrMagnitude <= reach * reach;
    }

    private int FindStopNear(Vector3 position, float radius)
    {
        if (stops == null) return -1;

        float bestSqr = radius * radius;
        int best = -1;
        for (int i = 0; i < stops.Length; i++)
        {
            if (!IsValidStop(i)) continue;

            Vector3 delta = stops[i].point.position - position;
            delta.y = 0f;
            float sqr = delta.sqrMagnitude;
            if (sqr <= bestSqr)
            {
                bestSqr = sqr;
                best = i;
            }
        }
        return best;
    }

    private void TrySnapToNavMesh()
    {
        if (!_agent.enabled) return;

        var filter = new NavMeshQueryFilter
        {
            agentTypeID = _agent.agentTypeID,
            areaMask = _agent.areaMask
        };

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, navMeshSampleRadius, filter))
            _agent.Warp(hit.position);
    }

    // ─────────────────────────────────────────────
    // Dialogue
    // ─────────────────────────────────────────────

    private void HandleDialogueStarted(NPCIdentity npc)
    {
        if (npc == null || _identity == null || npc != _identity) return;
        BeginDialoguePause();
    }

    private void HandleDialogueEnded(NPCIdentity npc)
    {
        if (npc == null || _identity == null || npc != _identity) return;
        if (!_pausedForDialogue || !isActiveAndEnabled) return;

        if (_resumeRoutine != null)
            StopCoroutine(_resumeRoutine);
        _resumeRoutine = StartCoroutine(ResumeAfterDialogue());
    }

    private void BeginDialoguePause()
    {
        if (_resumeRoutine != null)
        {
            StopCoroutine(_resumeRoutine);
            _resumeRoutine = null;
        }

        _pausedForDialogue = true;
        Pause(GetPlayerEyes());
    }

    private IEnumerator ResumeAfterDialogue()
    {
        yield return new WaitForSeconds(resumeAfterDialogueDelay);
        _resumeRoutine = null;

        // Player re-opened dialogue with us during the delay
        if (_identity != null && DialogueManagerV2.ActiveNPC == _identity)
            yield break;

        Resume();
    }

    /// <summary>
    /// Safety net in case an event was missed: stay held while we are the active
    /// dialogue NPC, and schedule the resume once we no longer are.
    /// </summary>
    private void SyncDialoguePause()
    {
        if (_identity == null) return;

        bool isActive = DialogueManagerV2.ActiveNPC == _identity;
        if (isActive)
        {
            if (!_pausedForDialogue || !_paused || _resumeRoutine != null)
                BeginDialoguePause();
        }
        else if (_pausedForDialogue && _resumeRoutine == null)
        {
            _resumeRoutine = StartCoroutine(ResumeAfterDialogue());
        }
    }

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    private void HoldStill()
    {
        if (!IsAgentReady) return;

        if (!_agent.isStopped)
            _agent.isStopped = true;
        _agent.velocity = Vector3.zero;
        _agent.updateRotation = false;
    }

    private void StopAgent()
    {
        if (!IsAgentReady) return;

        _agent.ResetPath();
        _agent.velocity = Vector3.zero;
        _agent.isStopped = true;
        _agent.updateRotation = false;
    }

    /// <summary>
    /// Rotates the root (yaw only) toward a world direction, easing out near the end.
    /// Returns the remaining angle in degrees.
    /// </summary>
    private float TurnTowardsDirection(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return 0f;

        Quaternion target = Quaternion.LookRotation(direction, Vector3.up);
        float angle = Quaternion.Angle(transform.rotation, target);
        if (angle < 0.1f) return 0f;

        // Slow down over the last ~45 degrees so turns settle softly
        float speed = Mathf.Clamp(angle * 4f, 25f, turnSpeed);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, speed * Time.deltaTime);
        return Quaternion.Angle(transform.rotation, target);
    }

    private bool IsPlayerBlockingPath()
    {
        Transform player = GetPlayer();
        if (player == null) return false;

        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        float sqr = toPlayer.sqrMagnitude;
        if (sqr > yieldDistance * yieldDistance || sqr < 0.0001f) return false;

        // Only when the player is roughly ahead of us
        return Vector3.Dot(transform.forward, toPlayer / Mathf.Sqrt(sqr)) > 0.5f;
    }

    private Transform GetPlayer()
    {
        if (_player == null && Time.time >= _nextPlayerSearchTime)
        {
            _nextPlayerSearchTime = Time.time + PlayerSearchInterval;
            GameObject playerObj = GameObject.FindWithTag("Player");
            if (playerObj != null)
                _player = playerObj.transform;
        }
        return _player;
    }

    private Transform GetPlayerEyes()
    {
        if (_lookAt != null)
        {
            Transform eyes = _lookAt.PlayerEyes;
            if (eyes != null) return eyes;
        }

        if (_playerEyes != null) return _playerEyes;

        Transform player = GetPlayer();
        if (player == null) return null;

        Transform cam = player.Find("Camera");
        _playerEyes = cam != null ? cam : player;
        return _playerEyes;
    }

    private void CacheAnimatorParameters()
    {
        _speedHash = Animator.StringToHash(speedParameter);
        _hasSpeedParam = false;

        if (_animator == null || _animator.runtimeAnimatorController == null) return;

        foreach (AnimatorControllerParameter param in _animator.parameters)
        {
            if (param.type == AnimatorControllerParameterType.Float && param.name == speedParameter)
            {
                _hasSpeedParam = true;
                break;
            }
        }
    }

    private void UpdateAnimatorSpeed()
    {
        if (_animator == null || !_animator.isActiveAndEnabled) return;
        // The Animator can report no parameters during its first frames; keep retrying until it does
        if (!_hasSpeedParam && _animator.parameterCount > 0 && !_speedParamResolved)
        {
            CacheAnimatorParameters();
            _speedParamResolved = true;
        }
        if (!_hasSpeedParam) return;

        float speed = (_paused || !IsAgentReady) ? 0f : _agent.velocity.magnitude;
        _animator.SetFloat(_speedHash, speed, speedDampTime, Time.deltaTime);
    }

    private static float RandomInRange(Vector2 range)
    {
        return Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    // ─────────────────────────────────────────────
    // Gizmos
    // ─────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        if (stops == null) return;

        Gizmos.color = new Color(0.95f, 0.75f, 0.2f, 0.9f);
        for (int i = 0; i < stops.Length; i++)
        {
            Stop stop = stops[i];
            if (stop == null || stop.point == null) continue;

            Vector3 p = stop.point.position;
            Gizmos.DrawWireSphere(p, 0.2f);

            if (stop.faceStopForward)
            {
                Vector3 tip = p + stop.point.forward * 0.6f;
                Gizmos.DrawLine(p, tip);
                Gizmos.DrawSphere(tip, 0.04f);
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (stops == null) return;

        // Faint lines from the NPC to every stop it can pick
        Gizmos.color = new Color(0.95f, 0.75f, 0.2f, 0.35f);
        Vector3 origin = transform.position + Vector3.up * 0.05f;
        for (int i = 0; i < stops.Length; i++)
        {
            Stop stop = stops[i];
            if (stop == null || stop.point == null) continue;
            Gizmos.DrawLine(origin, stop.point.position);
        }

        // Current path while playing
        if (Application.isPlaying && IsAgentReady && _agent.hasPath)
        {
            Gizmos.color = Color.cyan;
            Vector3[] corners = _agent.path.corners;
            for (int i = 1; i < corners.Length; i++)
                Gizmos.DrawLine(corners[i - 1], corners[i]);
        }
    }
}
