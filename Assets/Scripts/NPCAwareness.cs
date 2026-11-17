using System.Collections.Generic;
using UnityEngine;
using Tavern.Dialogue;

/// <summary>
/// Makes the NPC notice and react to the player before any dialogue starts.
/// - Looks at the player (head/eye IK through NPCLookAt, Awareness priority) while the player is
///   within noticeRange and inside the NPC's horizontal field of view. After a max stare it relaxes
///   into occasional short glances so the NPC doesn't lock onto the player forever.
/// - Greets the player once per visit on entering greetingRange (respecting reactionCooldown) via
///   NPCGestures, briefly pausing an NPCWanderer first so the greeting isn't done mid-stride.
/// - Stays out of the way while it is the active dialogue NPC. During someone else's dialogue it
///   never greets and only glances at the player at Ambient priority.
/// Attach to the NPC root (same object as NPCIdentity).
/// </summary>
public class NPCAwareness : MonoBehaviour
{
    [Header("Detection")]
    [SerializeField] private float noticeRange = 5f;        // When NPC notices player
    [SerializeField] private float greetingRange = 3f;      // When NPC does greeting animation
    [SerializeField] private float fieldOfView = 120f;      // NPC's vision cone (horizontal, degrees)
    [SerializeField] private LayerMask playerLayer;         // Legacy: unused (player is found by tag)

    [Header("Reaction")]
    [SerializeField] private ReactionType reactionType = ReactionType.Wave;
    [SerializeField] private float reactionCooldown = 30f;  // Don't repeat too often
    [SerializeField] private AudioClip greetingSound;       // Optional "Hey!" sound
    [SerializeField] private float greetingSoundVolume = 0.5f;

    [Header("Animation Triggers (fallback when no NPCGestures)")]
    [SerializeField] private string waveAnimTrigger = "Wave";
    [SerializeField] private string nodAnimTrigger = "Nod";
    [SerializeField] private string glanceAnimTrigger = "Glance";

    public enum ReactionType
    {
        None,
        Wave,       // Friendly wave (Greta, Shara)
        Nod,        // Subtle acknowledgment (Wu, Varien)
        Glance,     // Just looks (Jiro, Sable)
        Custom      // Use custom trigger name
    }

    [SerializeField] private string customTrigger = "";

    [Header("Look Behaviour")]
    [Tooltip("Longest continuous stare at the player before switching to occasional glances.")]
    [SerializeField] private float maxStareSeconds = 6f;
    [Tooltip("How long the player may be out of range/view before the NPC stops tracking them.")]
    [SerializeField] private float leaveGraceSeconds = 0.75f;
    [Tooltip("Time between glances once the stare has ended (seconds, min/max).")]
    [SerializeField] private Vector2 glanceIntervalSeconds = new Vector2(4f, 8f);
    [Tooltip("Length of each glance (seconds, min/max).")]
    [SerializeField] private Vector2 glanceDurationSeconds = new Vector2(1.5f, 2.5f);
    [Tooltip("While the player talks to someone else: no glances at them for this many seconds after each " +
             "player line, so they don't read as a reaction to it. 0 = off.")]
    [Min(0f)] [SerializeField] private float quietAfterPlayerLine = 7f;
    [Tooltip("Human reaction time before the NPC first looks over (seconds, min/max).")]
    [SerializeField] private Vector2 noticeDelaySeconds = new Vector2(0.15f, 0.5f);

    [Header("Greeting")]
    [Tooltip("How long a wandering NPC stops to greet the player.")]
    [SerializeField] private float greetPauseSeconds = 3.5f;

    // Tuning constants
    private const float MaxVerticalOffset = 2.5f;        // Ignore the player on another floor
    private const float LostAngleExtra = 40f;            // Once noticed, keep tracking a bit outside the FOV
    private const float MaxLostAngle = 170f;
    private const float GestureSettleSeconds = 0.6f;     // Max wait for a wanderer to stop before gesturing
    private const float GestureRetrySeconds = 2.5f;      // Extra time to wait out another gesture before giving up
    private const float PostDialogueQuietSeconds = 3f;   // Let NPCLookAt finish easing back after dialogue
    private const float PlayerSearchInterval = 1f;

    // Cached components (any of these may be missing; everything degrades gracefully)
    private Transform _player;
    private Transform _playerEyesFallback;
    private NPCIdentity _identity;
    private NPCLookAt _lookAt;
    private NPCGestures _gestures;
    private NPCWanderer _wanderer;
    private Animator _animator;
    private AudioSource _audioSource;
    private readonly HashSet<string> _triggerParams = new HashSet<string>();
    private bool _paramsCached;
    private float _nextPlayerSearchTime;

    // Greeting state
    private float _lastReactionTime = -999f;
    private bool _hasReactedThisVisit;
    private float _lastNearTime = -999f;
    private bool _gesturePending;
    private float _gestureDeadline;

    // Look state
    private bool _aware;                         // Player has been noticed this visit
    private float _candidateSince = -1f;         // When the player first became visible (notice delay)
    private float _noticeDelay;
    private float _lastSeenTime;
    private bool _staring;
    private float _stareEndTime;
    private float _nextGlanceTime;
    private float _awarenessLookUntil = -1f;     // We own the Awareness look slot until this time
    private bool _ambientOnly;                   // After chatting with the player: head-only ambient glances

    // Dialogue state tracking
    private bool _wasActiveDialogueNPC;
    private bool _wasOtherDialogue;
    private float _suppressUntil;

    private void Awake()
    {
        _identity = GetComponent<NPCIdentity>();
        if (_identity == null)
            _identity = GetComponentInParent<NPCIdentity>();

        _lookAt = GetComponent<NPCLookAt>();
        _gestures = GetComponent<NPCGestures>();
        if (_gestures == null)
            _gestures = GetComponentInChildren<NPCGestures>();
        _wanderer = GetComponent<NPCWanderer>();

        _animator = GetComponent<Animator>();
        if (_animator == null)
            _animator = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null && greetingSound != null)
            _audioSource = gameObject.AddComponent<AudioSource>();

        TryFindPlayer();
        CacheAnimatorParams();
    }

    private void OnDisable()
    {
        ReleaseAwarenessLook();
        _aware = false;
        _staring = false;
        _candidateSince = -1f;
        _gesturePending = false;
    }

    private void Update()
    {
        if (_player == null)
        {
            if (Time.time < _nextPlayerSearchTime) return;
            _nextPlayerSearchTime = Time.time + PlayerSearchInterval;
            if (!TryFindPlayer()) return;
        }

        NPCIdentity activeNPC = DialogueManagerV2.ActiveNPC;
        bool isActiveSelf = _identity != null && activeNPC == _identity;
        bool otherDialogue = activeNPC != null && !isActiveSelf;

        // --- This NPC is in dialogue: NPCLookAt (Dialogue priority) and DialogueManagerV2 own it ---
        if (isActiveSelf)
        {
            if (!_wasActiveDialogueNPC)
                OnOwnDialogueStarted();
            return;
        }
        if (_wasActiveDialogueNPC)
            OnOwnDialogueEnded();

        // --- Someone else's dialogue started: drop anything that could turn the body or greet ---
        if (otherDialogue && !_wasOtherDialogue)
        {
            ReleaseAwarenessLook();
            _staring = false;
            _gesturePending = false;
            ScheduleNextGlance();
        }
        _wasOtherDialogue = otherDialogue;

        if (Time.time < _suppressUntil) return;

        // --- Geometry (horizontal) ---
        Vector3 toPlayer = _player.position - transform.position;
        bool sameFloor = Mathf.Abs(toPlayer.y) <= MaxVerticalOffset;
        toPlayer.y = 0f;
        float sqrDist = toPlayer.sqrMagnitude;
        float angle = HorizontalAngleTo(toPlayer);

        bool inNoticeRange = sameFloor && sqrDist <= noticeRange * noticeRange;
        bool inGreetingRange = sameFloor && sqrDist <= greetingRange * greetingRange;
        bool inView = angle <= fieldOfView * 0.5f;

        // A "visit" lasts while the player stays near; leaving resets the once-per-visit greeting
        float visitRange = Mathf.Max(noticeRange, greetingRange);
        if (sameFloor && sqrDist <= visitRange * visitRange)
            _lastNearTime = Time.time;
        else if (Time.time - _lastNearTime > leaveGraceSeconds)
            _hasReactedThisVisit = false;

        UpdateLook(inNoticeRange, inView, angle, otherDialogue);

        if (!otherDialogue)
        {
            UpdateGreeting(inGreetingRange, inView);
            UpdatePendingGesture();
        }
    }

    // ─── Look-at ─────────────────────────────────────────────────────────

    private void UpdateLook(bool inRange, bool inView, float angle, bool otherDialogue)
    {
        if (_lookAt == null) return;

        Transform eyes = GetPlayerEyes();
        if (eyes == null) return;

        // Noticing needs the FOV; once noticed we keep tracking a little outside it
        float lostAngle = Mathf.Min(fieldOfView * 0.5f + LostAngleExtra, MaxLostAngle);
        bool visible = inRange && (_aware ? angle <= lostAngle : inView);
        if (visible)
            _lastSeenTime = Time.time;

        if (!_aware)
        {
            if (!visible)
            {
                _candidateSince = -1f;
                return;
            }

            // Small human reaction delay so nearby NPCs don't all snap round in the same frame
            if (_candidateSince < 0f)
            {
                _candidateSince = Time.time;
                _noticeDelay = RandomIn(noticeDelaySeconds);
            }
            if (Time.time - _candidateSince < _noticeDelay) return;

            _aware = true;
            _candidateSince = -1f;

            if (otherDialogue || _ambientOnly)
                ScheduleNextGlance();
            else
                BeginStare(eyes);
            return;
        }

        // Player left range (or swung well behind us) for longer than the grace period
        if (Time.time - _lastSeenTime > leaveGraceSeconds)
        {
            LoseAwareness();
            return;
        }

        if (_staring)
        {
            if (Time.time >= _stareEndTime)
            {
                StopStare();
                ScheduleNextGlance();
            }
            return;
        }

        // Occasional glances while the player hangs around
        if (Time.time < _nextGlanceTime) return;

        // The player is mid-conversation with someone else and just spoke: hold off, so a glance
        // now isn't mistaken for a reaction to the line
        if (otherDialogue && DialogueManagerV2.PlayerSpokeWithin(quietAfterPlayerLine))
        {
            _nextGlanceTime = Time.time + 0.5f;
            return;
        }

        if (!visible)
        {
            // Inside the grace window: try again shortly
            _nextGlanceTime = Time.time + 0.5f;
            return;
        }

        float duration = RandomIn(glanceDurationSeconds);
        if (otherDialogue || _ambientOnly)
        {
            _lookAt.LookAtFor(eyes, duration, LookPriority.Ambient);
        }
        else
        {
            _lookAt.LookAtFor(eyes, duration, LookPriority.Awareness);
            _awarenessLookUntil = Time.time + duration;
        }
        _nextGlanceTime = Time.time + duration + RandomIn(glanceIntervalSeconds);
    }

    private void BeginStare(Transform eyes)
    {
        if (_lookAt == null || eyes == null) return;

        _lookAt.SetLookTarget(eyes, LookPriority.Awareness);
        _staring = true;
        _stareEndTime = Time.time + Mathf.Max(0.5f, maxStareSeconds) * Random.Range(0.75f, 1f);
        _awarenessLookUntil = float.MaxValue;
    }

    private void StopStare()
    {
        _staring = false;
        ReleaseAwarenessLook();
    }

    private void LoseAwareness()
    {
        ReleaseAwarenessLook();
        _aware = false;
        _staring = false;
        _candidateSince = -1f;
        _ambientOnly = false;
    }

    private void ScheduleNextGlance()
    {
        _nextGlanceTime = Time.time + RandomIn(glanceIntervalSeconds);
    }

    /// <summary>
    /// Clears the Awareness look slot, but only if this component still owns it.
    /// </summary>
    private void ReleaseAwarenessLook()
    {
        if (_awarenessLookUntil < 0f) return;

        if (_lookAt != null && Time.time < _awarenessLookUntil)
            _lookAt.ClearLookTarget(LookPriority.Awareness);
        _awarenessLookUntil = -1f;
    }

    // ─── Greeting ────────────────────────────────────────────────────────

    private void UpdateGreeting(bool inGreetingRange, bool inView)
    {
        if (reactionType == ReactionType.None) return;
        if (!inGreetingRange || !inView) return;
        if (_hasReactedThisVisit || _gesturePending) return;
        if (!CanReact()) return;

        DoReaction();
    }

    private bool CanReact()
    {
        // Check cooldown
        return Time.time - _lastReactionTime >= reactionCooldown;
    }

    private void DoReaction()
    {
        _hasReactedThisVisit = true;
        _lastReactionTime = Time.time;

        Transform eyes = GetPlayerEyes();

        // Stop a wandering NPC and turn it toward the player before greeting
        if (_wanderer != null && _wanderer.isActiveAndEnabled)
            _wanderer.PauseFor(greetPauseSeconds, eyes != null ? eyes : _player);

        // Look at the player while greeting (restarts the stare window)
        if (_lookAt != null && eyes != null)
        {
            _aware = true;
            _candidateSince = -1f;
            _lastSeenTime = Time.time;
            BeginStare(eyes);
        }

        // Gesture fires once the wanderer has stopped (or right away for static NPCs)
        _gesturePending = true;
        _gestureDeadline = Time.time + GestureSettleSeconds;
        UpdatePendingGesture();

        Debug.Log($"[NPCAwareness] {gameObject.name} reacted to player with {reactionType}");
    }

    private void UpdatePendingGesture()
    {
        if (!_gesturePending) return;

        bool stillMoving = _wanderer != null && _wanderer.isActiveAndEnabled && _wanderer.IsMoving;
        if (stillMoving && Time.time < _gestureDeadline) return;

        // Another gesture (table nod, idle sip...) may still be playing: keep retrying for a moment
        // instead of silently losing the greeting for this visit
        bool played = PerformReactionGesture();
        if (!played && Time.time < _gestureDeadline + GestureRetrySeconds) return;

        _gesturePending = false;
        PlayGreetingSound();
    }

    /// <summary>Returns false only when the gesture was refused and is worth retrying.</summary>
    private bool PerformReactionGesture()
    {
        if (_gestures != null)
        {
            switch (reactionType)
            {
                case ReactionType.Wave:
                    return _gestures.Play(NPCGesture.Wave);
                case ReactionType.Nod:
                    return _gestures.Play(NPCGesture.Nod);
                case ReactionType.Glance:
                    // The Awareness stare (head IK) is the glance. The GlanceL/R clips override the
                    // head layer and would turn the head away from the player, so they stay ambient-only.
                    return true;
                case ReactionType.Custom:
                    return string.IsNullOrEmpty(customTrigger) || _gestures.PlayTrigger(customTrigger);
                default:
                    return true;
            }
        }

        // Fallback: raw animator trigger, only if the controller actually has it
        string trigger = GetFallbackTrigger();
        if (trigger != null && _animator != null && _animator.isActiveAndEnabled)
            _animator.SetTrigger(trigger);
        return true;
    }

    private string GetFallbackTrigger()
    {
        switch (reactionType)
        {
            case ReactionType.Wave:
                return HasTrigger(waveAnimTrigger) ? waveAnimTrigger : null;
            case ReactionType.Nod:
                return HasTrigger(nodAnimTrigger) ? nodAnimTrigger : null;
            case ReactionType.Glance:
                if (HasTrigger(glanceAnimTrigger)) return glanceAnimTrigger;
                // New controller has directional glances instead of a single "Glance"
                string sided = NPCGestures.TriggerName(GlanceSideToward(_player));
                return HasTrigger(sided) ? sided : null;
            case ReactionType.Custom:
                return HasTrigger(customTrigger) ? customTrigger : null;
            default:
                return null;
        }
    }

    private NPCGesture GlanceSideToward(Transform target)
    {
        if (target == null) return NPCGesture.GlanceRight;
        Vector3 local = transform.InverseTransformPoint(target.position);
        return local.x < 0f ? NPCGesture.GlanceLeft : NPCGesture.GlanceRight;
    }

    private void PlayGreetingSound()
    {
        if (greetingSound == null) return;

        if (_audioSource == null)
        {
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();
        }
        _audioSource.PlayOneShot(greetingSound, greetingSoundVolume);
    }

    // ─── Dialogue transitions ────────────────────────────────────────────

    private void OnOwnDialogueStarted()
    {
        _wasActiveDialogueNPC = true;
        ReleaseAwarenessLook();
        _aware = false;
        _staring = false;
        _candidateSince = -1f;
        _gesturePending = false;

        // Already talking to the player: no greeting when the dialogue closes
        _hasReactedThisVisit = true;
    }

    private void OnOwnDialogueEnded()
    {
        _wasActiveDialogueNPC = false;

        // Give NPCLookAt time to ease back, then only glance with the head (Ambient) until the
        // player leaves, so the body doesn't swing back round right after the conversation.
        _suppressUntil = Time.time + PostDialogueQuietSeconds;
        _ambientOnly = true;
        _aware = true;
        _lastSeenTime = _suppressUntil;
        ScheduleNextGlance();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private bool TryFindPlayer()
    {
        var playerObj = GameObject.FindWithTag("Player");
        if (playerObj == null) return false;

        _player = playerObj.transform;

        // Eyes fallback for when NPCLookAt is missing or can't resolve them
        Transform cam = _player.Find("Camera");
        if (cam == null)
        {
            Camera childCam = _player.GetComponentInChildren<Camera>(true);
            if (childCam != null)
                cam = childCam.transform;
        }
        _playerEyesFallback = cam != null ? cam : _player;
        return true;
    }

    private Transform GetPlayerEyes()
    {
        if (_lookAt != null)
        {
            Transform eyes = _lookAt.PlayerEyes;
            if (eyes != null) return eyes;
        }
        return _playerEyesFallback;
    }

    private float HorizontalAngleTo(Vector3 flatDirection)
    {
        if (flatDirection.sqrMagnitude < 0.0001f) return 0f;

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) return 0f;

        return Vector3.Angle(forward, flatDirection);
    }

    private void CacheAnimatorParams()
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return;

        _triggerParams.Clear();
        foreach (AnimatorControllerParameter p in _animator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Trigger)
                _triggerParams.Add(p.name);
        }
        // An inactive animator reports no parameters; retry on next use in that case
        _paramsCached = _animator.parameterCount > 0;
    }

    private bool HasTrigger(string triggerName)
    {
        if (string.IsNullOrEmpty(triggerName) || _animator == null) return false;
        if (!_paramsCached) CacheAnimatorParams();
        return _triggerParams.Contains(triggerName);
    }

    private static float RandomIn(Vector2 range)
    {
        return Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    // Visual debug
    private void OnDrawGizmosSelected()
    {
        // Notice range
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, noticeRange);

        // Greeting range
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, greetingRange);

        // Field of view
        Gizmos.color = Color.blue;
        Vector3 leftBoundary = Quaternion.Euler(0, -fieldOfView * 0.5f, 0) * transform.forward;
        Vector3 rightBoundary = Quaternion.Euler(0, fieldOfView * 0.5f, 0) * transform.forward;
        Gizmos.DrawRay(transform.position + Vector3.up, leftBoundary * noticeRange);
        Gizmos.DrawRay(transform.position + Vector3.up, rightBoundary * noticeRange);

        // Current awareness of the player (play mode)
        if (Application.isPlaying && _aware && _player != null)
        {
            Gizmos.color = _staring ? Color.red : new Color(1f, 0.5f, 0f);
            Transform eyes = GetPlayerEyes();
            Gizmos.DrawLine(transform.position + Vector3.up * 1.5f, eyes != null ? eyes.position : _player.position);
        }
    }
}
