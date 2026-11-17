using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One-shot upper-body gestures available on the NPC Animator's "Gestures" layer.
/// Glance directions are from the NPC's own point of view.
/// </summary>
public enum NPCGesture
{
    Wave,
    Nod,
    Shake,
    GlanceLeft,
    GlanceRight,
    Point,
    Sip,
    Ponder,
    /// <summary>Full-body seated laugh on the Base Layer (the Animator only takes it while seated, posture 1).</summary>
    Laugh
}

/// <summary>
/// Plays one-shot gestures (wave, nod, glance, sip...) through Animator triggers on the "Gestures"
/// (arms + head) and "HeadGestures" (head only) override layers. Every call is guarded: a missing trigger parameter, a gesture already in progress,
/// or a gesture fired too recently simply returns false instead of warning or stacking triggers.
/// Attach to the NPC root, next to the Animator.
/// </summary>
[DisallowMultipleComponent]
public class NPCGestures : MonoBehaviour
{
    [Header("Animator")]
    [Tooltip("Auto-found on this GameObject (or its children) when empty.")]
    [SerializeField] private Animator animator;
    [SerializeField] private string gestureLayerName = "Gestures";
    [Tooltip("Head-only gestures (nod, shake, glance) live on their own masked layer so seated arms stay put.")]
    [SerializeField] private string headGestureLayerName = "HeadGestures";
    [Tooltip("The gesture layers' resting state. Anything else means a gesture is playing.")]
    [SerializeField] private string emptyStateName = "Empty";
    [Tooltip("Force the Gestures layer weight to 1 at startup (new layers default to 0 in the Animator window).")]
    [SerializeField] private bool forceLayerWeight = true;

    [Header("Timing")]
    [Tooltip("Minimum seconds between two gestures.")]
    [SerializeField] private float minInterval = 1.5f;
    [Tooltip("A trigger the Animator hasn't consumed after this many seconds is reset, so it can't fire later by surprise.")]
    [SerializeField] private float triggerTimeout = 0.5f;
    [Tooltip("The seated laugh clip runs about 8 s. After this many seconds the NPC fades back to the pose it " +
             "laughed from, so the laugh doesn't run on into the next line. 0 = play the whole clip.")]
    [Min(0f)] [SerializeField] private float maxLaughSeconds = 3f;
    [SerializeField] private float laughFadeSeconds = 0.6f;

    [Header("Debug")]
    [SerializeField] private bool logGestures = false;

    private readonly HashSet<string> _triggerParams = new HashSet<string>();
    private readonly HashSet<string> _warnedTriggers = new HashSet<string>();
    private readonly List<int> _layers = new List<int>();
    private readonly List<int> _idleStateHashes = new List<int>();
    private bool _cacheBuilt;
    private float _lastGestureTime = -999f;
    private string _pendingTrigger;
    private float _pendingSince;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// True while a gesture is queued or playing: the trigger hasn't been consumed yet, the Gestures
    /// layer is transitioning, or its current state isn't the empty resting state.
    /// </summary>
    /// <summary>The last trigger <see cref="PlayTrigger"/> accepted, and when (Time.time; -999 before any).</summary>
    public string LastTrigger { get; private set; }
    public float LastGestureTime => _lastGestureTime;

    public bool IsPlaying
    {
        get
        {
            if (_pendingTrigger != null) return true;
            if (!EnsureCache() || !IsAnimatorLive()) return false;
            return AnyLayerBusy();
        }
    }

    /// <summary>
    /// Plays a gesture. Returns false if the trigger parameter is missing, a gesture is already
    /// playing, or the last gesture started less than minInterval seconds ago.
    /// </summary>
    public bool Play(NPCGesture gesture)
    {
        int baseStateBefore = gesture == NPCGesture.Laugh && EnsureCache() && IsAnimatorLive()
            ? animator.GetCurrentAnimatorStateInfo(0).fullPathHash
            : 0;
        bool played = PlayTrigger(TriggerName(gesture));
        if (played && gesture == NPCGesture.Laugh && maxLaughSeconds > 0f && baseStateBefore != 0)
            StartCoroutine(CapLaugh(baseStateBefore));
        return played;
    }

    /// <summary>Fires a raw Animator trigger with the same safety checks as <see cref="Play"/>.</summary>
    public bool PlayTrigger(string triggerName)
    {
        if (string.IsNullOrEmpty(triggerName)) return false;
        if (!EnsureCache() || !IsAnimatorLive()) return false;
        if (!_triggerParams.Contains(triggerName)) return false;
        if (IsPlaying) return false;
        if (Time.time - _lastGestureTime < minInterval) return false;

        animator.SetTrigger(triggerName);
        _pendingTrigger = triggerName;
        _pendingSince = Time.time;
        _lastGestureTime = Time.time;
        LastTrigger = triggerName;

        if (logGestures)
            Debug.Log($"[NPCGestures] {name} plays '{triggerName}'");

        return true;
    }

    /// <summary>
    /// Plays a sideways glance toward a world position (GlanceLeft when it is on the NPC's left,
    /// GlanceRight otherwise) and returns which one was chosen. The glance is subject to the usual
    /// checks, so it may not actually play; use <see cref="IsPlaying"/> if that matters.
    /// </summary>
    public NPCGesture GlanceToward(Vector3 worldPosition)
    {
        Vector3 local = transform.InverseTransformPoint(worldPosition);
        NPCGesture glance = local.x < 0f ? NPCGesture.GlanceLeft : NPCGesture.GlanceRight;
        Play(glance);
        return glance;
    }

    /// <summary>The Animator trigger parameter name for a gesture.</summary>
    public static string TriggerName(NPCGesture g)
    {
        return g switch
        {
            NPCGesture.Wave => "Wave",
            NPCGesture.Nod => "Nod",
            NPCGesture.Shake => "Shake",
            NPCGesture.GlanceLeft => "GlanceL",
            NPCGesture.GlanceRight => "GlanceR",
            NPCGesture.Point => "Point",
            NPCGesture.Sip => "Sip",
            NPCGesture.Ponder => "Ponder",
            NPCGesture.Laugh => "Laugh",
            _ => null
        };
    }

    // ================================================================
    // Unity lifecycle
    // ================================================================

    private void Awake()
    {
        if (animator == null && !TryGetComponent(out animator))
            animator = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        EnsureCache();
    }

    private void Update()
    {
        if (_pendingTrigger == null) return;

        if (!IsAnimatorLive())
        {
            _pendingTrigger = null;
            return;
        }

        // Consumed by a transition on any layer (the seated laugh lives on the Base Layer, not a gesture layer)
        if (Time.time > _pendingSince && !animator.GetBool(_pendingTrigger))
        {
            _pendingTrigger = null;
            return;
        }

        if (_layers.Count > 0)
        {
            // Consumed: a gesture layer left its resting state (or started blending out of it)
            if (AnyLayerBusy())
            {
                _pendingTrigger = null;
                return;
            }

            // Never consumed (no transition for it, or the Animator is culled): drop it
            if (Time.time - _pendingSince > triggerTimeout)
            {
                animator.ResetTrigger(_pendingTrigger);
                if (_warnedTriggers.Add(_pendingTrigger))
                {
                    Debug.LogWarning($"[NPCGestures] {name}: trigger '{_pendingTrigger}' was not consumed. " +
                                     $"Is there a transition from '{emptyStateName}' on the '{gestureLayerName}' or '{headGestureLayerName}' layer?");
                }
                _pendingTrigger = null;
            }
        }
        else if (Time.time - _pendingSince > 0.25f)
        {
            // No Gestures layer to observe; assume another layer handled it
            _pendingTrigger = null;
        }
    }

    // Cuts the Base Layer laugh short: finds the state the trigger led to, then fades back to the pose the
    // NPC laughed from if it's still laughing after maxLaughSeconds. A laugh that wasn't taken (not seated)
    // or that already ended is left alone.
    private IEnumerator CapLaugh(int baseStateBefore)
    {
        int laughState = 0;
        float waited = 0f;
        while (laughState == 0 && waited < 0.5f)
        {
            yield return null;
            waited += Time.deltaTime;
            if (animator == null || !IsAnimatorLive()) yield break;
            int now = animator.IsInTransition(0)
                ? animator.GetNextAnimatorStateInfo(0).fullPathHash
                : animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (now != baseStateBefore) laughState = now;
        }
        if (laughState == 0) yield break;

        yield return new WaitForSeconds(Mathf.Max(0f, maxLaughSeconds - waited));
        if (animator == null || !IsAnimatorLive() || animator.IsInTransition(0)) yield break;
        if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash == laughState)
            animator.CrossFadeInFixedTime(baseStateBefore, laughFadeSeconds, 0);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool IsAnimatorLive()
    {
        return animator != null && animator.isActiveAndEnabled && animator.isInitialized;
    }

    private bool AnyLayerBusy()
    {
        for (int i = 0; i < _layers.Count; i++)
        {
            int layer = _layers[i];
            if (animator.IsInTransition(layer)) return true;
            if (animator.GetCurrentAnimatorStateInfo(layer).shortNameHash != _idleStateHashes[i]) return true;
        }
        return false;
    }

    /// <summary>Caches trigger names and the Gestures layer once the Animator is initialized.</summary>
    private bool EnsureCache()
    {
        if (animator == null) return false;
        if (_cacheBuilt) return true;
        if (animator.runtimeAnimatorController == null || !animator.isInitialized)
            return false;
        // Parameters/layers can still read as empty during the first frames; retry later instead of
        // caching "no triggers" for good
        if (animator.parameterCount == 0 || animator.layerCount == 0)
            return false;

        _triggerParams.Clear();
        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Trigger)
                _triggerParams.Add(p.name);
        }

        _layers.Clear();
        _idleStateHashes.Clear();
        AddLayer(gestureLayerName);
        AddLayer(headGestureLayerName);

        _cacheBuilt = true;
        return true;
    }

    private void AddLayer(string layerName)
    {
        int layer = string.IsNullOrEmpty(layerName) ? -1 : animator.GetLayerIndex(layerName);
        if (layer < 0) return;

        int idleHash = Animator.StringToHash(emptyStateName);
        int fullPathHash = Animator.StringToHash(layerName + "." + emptyStateName);
        if (!animator.HasState(layer, idleHash) && !animator.HasState(layer, fullPathHash))
        {
            // Resting state named differently: treat the layer's current (default) state as idle
            idleHash = animator.GetCurrentAnimatorStateInfo(layer).shortNameHash;
            Debug.LogWarning($"[NPCGestures] {name}: no '{emptyStateName}' state on the '{layerName}' layer; " +
                             "using its default state as the resting state.");
        }

        if (forceLayerWeight)
            animator.SetLayerWeight(layer, 1f);

        _layers.Add(layer);
        _idleStateHashes.Add(idleHash);
    }
}
