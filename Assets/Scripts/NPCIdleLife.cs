using System.Collections.Generic;
using UnityEngine;
using Tavern.Dialogue;

/// <summary>
/// Ambient "idle life" for NPCs that are not driven by a TableConversation.
/// Every random interval the NPC either glances at the player (when nearby), looks at one of its
/// points of interest, or plays a small gesture from its list. Looks use Ambient priority, so
/// dialogue, awareness and social looks always win over these fidgets.
/// Skips its turn while the NPC is in dialogue, walking, talking or already gesturing.
/// Attach to the NPC root (same object as NPCIdentity / Animator).
/// </summary>
public class NPCIdleLife : MonoBehaviour
{
    [Header("Fidgets")]
    [Tooltip("Gestures picked at random (played through NPCGestures).")]
    [SerializeField] private NPCGesture[] gestures;
    [Tooltip("Time between idle actions (seconds, min/max).")]
    [SerializeField] private Vector2 intervalSeconds = new Vector2(7f, 15f);

    [Header("Looking Around")]
    [Tooltip("Chance that an idle action is a glance at the player (when within glanceRange).")]
    [Range(0f, 1f)]
    [SerializeField] private float glanceAtPlayerChance = 0.35f;
    [SerializeField] private float glanceRange = 7f;
    [Tooltip("Things worth a look: the fire, the door, the bar, another table...")]
    [SerializeField] private Transform[] pointsOfInterest;
    [Tooltip("When not glancing at the player: chance to look at a point of interest instead of gesturing.")]
    [Range(0f, 1f)]
    [SerializeField] private float pointOfInterestChance = 0.5f;
    [Tooltip("How long each glance / look lasts (seconds, min/max).")]
    [SerializeField] private Vector2 lookSeconds = new Vector2(1.5f, 3f);

    // Tuning constants
    private const float MaxLookAngle = 110f;         // Don't twist the head toward things behind the NPC
    private const float MaxEyeHeightOffset = 4f;     // Ignore the player on another floor (eyes sit ~1.7m above feet)
    private const float MinInterval = 0.5f;
    private const string IsTalkingParam = "isTalking";
    private static readonly int IsTalkingHash = Animator.StringToHash(IsTalkingParam);

    // Cached components (any of these may be missing; everything degrades gracefully)
    private NPCIdentity _identity;
    private NPCLookAt _lookAt;
    private NPCGestures _gestures;
    private NPCWanderer _wanderer;
    private Animator _animator;
    private readonly HashSet<string> _params = new HashSet<string>();
    private bool _paramsCached;

    // State
    private float _nextActionTime;
    private int _lastGestureIndex = -1;
    private Transform _lastPointOfInterest;

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

    private void OnEnable()
    {
        // Randomize the first delay per instance so NPCs don't fidget in sync
        float max = Mathf.Max(MinInterval, Mathf.Max(intervalSeconds.x, intervalSeconds.y));
        _nextActionTime = Time.time + Random.Range(Mathf.Min(2f, max), max);
    }

    private void Start()
    {
        CacheAnimatorParams();
    }

    private void Update()
    {
        if (Time.time < _nextActionTime) return;

        if (!CanAct())
        {
            // Busy right now: check again soon instead of waiting a full interval
            _nextActionTime = Time.time + Random.Range(1f, 2.5f);
            return;
        }

        DoIdleAction();
        _nextActionTime = Time.time + Mathf.Max(MinInterval, RandomIn(intervalSeconds));
    }

    private bool CanAct()
    {
        // In dialogue with the player: DialogueManagerV2 and NPCLookAt own this NPC
        if (_identity != null && DialogueManagerV2.ActiveNPC == _identity) return false;

        // Walking between stops
        if (_wanderer != null && _wanderer.IsMoving) return false;

        // Talking (table chatter or scripted talking state)
        if (_animator != null && _animator.isActiveAndEnabled && HasParam(IsTalkingParam)
            && _animator.GetBool(IsTalkingHash)) return false;

        // Already mid-gesture
        if (_gestures != null && _gestures.IsPlaying) return false;

        // Noticing or greeting the player: a fidget (glance away, head shake) would break eye contact
        if (_lookAt != null && _lookAt.IsLookingAtOrAbove(LookPriority.Awareness)) return false;

        return true;
    }

    private void DoIdleAction()
    {
        // 1) Glance at the player if they're close enough
        if (Random.value < glanceAtPlayerChance && TryGlanceAtPlayer())
            return;

        // 2) Look at something in the room, or 3) fidget
        bool hasPoi = HasAny(pointsOfInterest);
        bool hasGesture = gestures != null && gestures.Length > 0;

        if (hasPoi && (!hasGesture || Random.value < pointOfInterestChance))
        {
            if (TryLookAtPointOfInterest()) return;
        }

        if (hasGesture)
            TryPlayGesture();
    }

    // ─── Actions ─────────────────────────────────────────────────────────

    private bool TryGlanceAtPlayer()
    {
        if (_lookAt == null) return false;

        Transform eyes = _lookAt.PlayerEyes;
        if (eyes == null) return false;

        Vector3 toPlayer = eyes.position - transform.position;
        if (Mathf.Abs(toPlayer.y) > MaxEyeHeightOffset) return false;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude > glanceRange * glanceRange) return false;
        if (!IsLookable(toPlayer)) return false;

        _lookAt.LookAtFor(eyes, RandomIn(lookSeconds), LookPriority.Ambient);
        return true;
    }

    private bool TryLookAtPointOfInterest()
    {
        if (_lookAt == null || pointsOfInterest == null) return false;

        // Count valid candidates (non-null, in front, not the one we just looked at)
        int count = 0;
        for (int i = 0; i < pointsOfInterest.Length; i++)
        {
            if (IsValidPointOfInterest(pointsOfInterest[i], true)) count++;
        }

        // If the only valid option was the previous one, allow repeating it
        bool avoidLast = count > 0;
        if (!avoidLast)
        {
            for (int i = 0; i < pointsOfInterest.Length; i++)
            {
                if (IsValidPointOfInterest(pointsOfInterest[i], false)) count++;
            }
        }
        if (count == 0) return false;

        int pick = Random.Range(0, count);
        for (int i = 0; i < pointsOfInterest.Length; i++)
        {
            Transform poi = pointsOfInterest[i];
            if (!IsValidPointOfInterest(poi, avoidLast)) continue;
            if (pick-- > 0) continue;

            // An NPC as point of interest: look at its face, not its feet
            Transform aim = poi.TryGetComponent(out NPCIdentity npc) ? NPCLookAt.HeadOf(npc) : poi;
            _lookAt.LookAtFor(aim, RandomIn(lookSeconds), LookPriority.Ambient);
            _lastPointOfInterest = poi;
            return true;
        }
        return false;
    }

    private bool IsValidPointOfInterest(Transform poi, bool avoidLast)
    {
        if (poi == null) return false;
        if (avoidLast && poi == _lastPointOfInterest) return false;

        Vector3 toPoi = poi.position - transform.position;
        toPoi.y = 0f;
        return IsLookable(toPoi);
    }

    private bool TryPlayGesture()
    {
        if (gestures == null || gestures.Length == 0) return false;

        // Random gesture, avoiding an immediate repeat when there's a choice
        int index = Random.Range(0, gestures.Length);
        if (gestures.Length > 1 && index == _lastGestureIndex)
            index = (index + Random.Range(1, gestures.Length)) % gestures.Length;
        _lastGestureIndex = index;

        NPCGesture gesture = gestures[index];

        if (_gestures != null)
            return _gestures.Play(gesture);

        // Fallback: raw animator trigger, only if the controller actually has it
        string trigger = NPCGestures.TriggerName(gesture);
        if (_animator != null && _animator.isActiveAndEnabled && HasParam(trigger))
        {
            _animator.SetTrigger(trigger);
            return true;
        }
        return false;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// True if a flat direction is within the head's comfortable turning range.
    /// </summary>
    private bool IsLookable(Vector3 flatDirection)
    {
        if (flatDirection.sqrMagnitude < 0.0001f) return false;

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) return true;

        return Vector3.Angle(forward, flatDirection) <= MaxLookAngle;
    }

    private static bool HasAny(Transform[] items)
    {
        if (items == null) return false;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] != null) return true;
        }
        return false;
    }

    private void CacheAnimatorParams()
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return;

        _params.Clear();
        foreach (AnimatorControllerParameter p in _animator.parameters)
            _params.Add(p.name);

        // An inactive animator reports no parameters; retry on next use in that case
        _paramsCached = _animator.parameterCount > 0;
    }

    private bool HasParam(string paramName)
    {
        if (string.IsNullOrEmpty(paramName) || _animator == null) return false;
        if (!_paramsCached) CacheAnimatorParams();
        return _params.Contains(paramName);
    }

    private static float RandomIn(Vector2 range)
    {
        return Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    // Visual debug
    private void OnDrawGizmosSelected()
    {
        // Player glance range
        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, glanceRange);

        // Points of interest
        if (pointsOfInterest == null) return;
        Gizmos.color = Color.cyan;
        Vector3 head = transform.position + Vector3.up * 1.5f;
        for (int i = 0; i < pointsOfInterest.Length; i++)
        {
            if (pointsOfInterest[i] == null) continue;
            Gizmos.DrawLine(head, pointsOfInterest[i].position);
            Gizmos.DrawWireSphere(pointsOfInterest[i].position, 0.1f);
        }
    }
}
