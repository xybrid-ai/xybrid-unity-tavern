using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Tavern.Dialogue;

/// <summary>
/// Drives an ambient conversation between NPCs sharing a table. Members take
/// turns talking (animator "isTalking"); the speaker addresses one listener and
/// sometimes turns to another, listeners watch the speaker and occasionally nod.
/// When the player starts dialogue with a member the chatter stops, the others
/// watch that exchange (sneaking glances at the player), and the table resumes
/// shortly after the dialogue ends.
/// Attach to an empty GameObject at the table, not to an NPC. Attention is
/// expressed through NPCLookAt (Social priority) and NPCGestures; members' root
/// transforms are never moved.
/// </summary>
public class TableConversation : MonoBehaviour
{
    [Header("Members")]
    [Tooltip("NPCs taking part in this table's conversation (2 or more).")]
    [SerializeField] private NPCIdentity[] members;

    [Tooltip("Optional relative chance of each member taking the next turn (same order as Members). " +
             "Missing entries default to 1; 0 = only listens.")]
    [SerializeField] private float[] talkWeights;

    [Header("Timing")]
    [Tooltip("Min/max seconds a speaker holds the floor.")]
    [SerializeField] private Vector2 turnSeconds = new Vector2(3.5f, 8f);

    [Tooltip("Min/max seconds of silence between turns.")]
    [SerializeField] private Vector2 pauseSeconds = new Vector2(0.4f, 1.4f);

    [Header("Reactions")]
    [Tooltip("Chance per turn that a listener nods part-way through.")]
    [Range(0f, 1f)] [SerializeField] private float nodChance = 0.4f;

    [Tooltip("Chance per turn that the speaker points or ponders.")]
    [Range(0f, 1f)] [SerializeField] private float speakerGestureChance = 0.3f;

    [Header("Player Dialogue")]
    [Tooltip("Seconds after the player's dialogue with a member ends before the table resumes chatting.")]
    [Min(0f)] [SerializeField] private float resumeDelay = 2f;

    // Turn texture (kept out of the Inspector to keep it focused)
    private const float AddresseeSwitchChance = 0.45f;     // speaker turns to another listener mid-turn
    private const float ListenerSideGlanceChance = 0.25f;  // a listener briefly looks at another listener
    private const float AddresseeReplyBias = 2f;           // whoever was just addressed is likelier to answer
    private const float AddresseeNodBias = 0.7f;           // nods usually come from the addressed listener
    private const float MaxListenerReactionDelay = 0.35f;  // listeners turn to a new speaker staggered, not in unison
    private static readonly Vector2 PlayerGlanceInterval = new Vector2(3f, 7f);
    private static readonly Vector2 PlayerGlanceDuration = new Vector2(1.6f, 2.4f);
    private const float MaxPlayerGlanceAngle = 110f;   // Same "can I see it" limit NPCIdleLife uses

    private const string IsTalkingParam = "isTalking";
    private static readonly int IsTalkingHash = Animator.StringToHash(IsTalkingParam);
    private static readonly WaitForSeconds RetryWait = new WaitForSeconds(1f);

    /// <summary>Cached components and transient look state for one member.</summary>
    private sealed class Member
    {
        public NPCIdentity Identity;
        public Animator Animator;
        public NPCLookAt LookAt;
        public NPCGestures Gestures;
        public Transform Head;
        public float Weight;
        public int TalkParamState;      // 0 = unknown, 1 = "isTalking" exists, -1 = missing

        // Deferred Social look target (lets listeners react one after another)
        public Transform PendingLook;
        public float PendingLookTime;
    }

    private enum Mode { Idle, Chatting, Resuming, WatchingDialogue }

    private readonly List<Member> _members = new List<Member>();
    private bool _initialized;
    private Mode _mode = Mode.Idle;
    private int _lastSpeaker = -1;
    private int _lastAddressee = -1;
    private int _currentSpeaker = -1;
    private int _currentAddressee = -1;

    /// <summary>True while the turn-taking loop runs (false while paused for, or resuming after, player dialogue).</summary>
    public bool IsChatting => _mode == Mode.Chatting;

    // ================================================================
    // Lifecycle
    // ================================================================

    private void OnEnable()
    {
        DialogueManagerV2.DialogueStarted += OnDialogueStarted;
        DialogueManagerV2.DialogueEnded += OnDialogueEnded;

        // The first enable is handled by Start, once members are cached
        if (_initialized)
            Begin();
    }

    private void Start()
    {
        CacheMembers();
        _initialized = true;
        Begin();
    }

    private void OnDisable()
    {
        DialogueManagerV2.DialogueStarted -= OnDialogueStarted;
        DialogueManagerV2.DialogueEnded -= OnDialogueEnded;

        // Disabling a component does not stop its coroutines on its own
        StopAllCoroutines();
        _mode = Mode.Idle;
        ReleaseMembers();
    }

    private void OnValidate()
    {
        turnSeconds.x = Mathf.Max(0.5f, turnSeconds.x);
        turnSeconds.y = Mathf.Max(0.5f, turnSeconds.y);
        pauseSeconds.x = Mathf.Max(0f, pauseSeconds.x);
        pauseSeconds.y = Mathf.Max(0f, pauseSeconds.y);
    }

    /// <summary>Starts in the right mode, including when enabled mid-dialogue.</summary>
    private void Begin()
    {
        if (_members.Count < 2) return;

        int active = IndexOf(DialogueManagerV2.ActiveNPC);
        if (active >= 0)
            EnterWatchMode(active);
        else
            StartChatLoop(Random.Range(0.3f, 2f), resuming: false); // Desync tables from each other
    }

    private void CacheMembers()
    {
        _members.Clear();
        if (members == null) return;

        for (int i = 0; i < members.Length; i++)
        {
            NPCIdentity identity = members[i];
            if (identity == null) continue;

            var member = new Member { Identity = identity };

            member.Animator = identity.TryGetComponent(out Animator animator)
                ? animator
                : identity.GetComponentInChildren<Animator>();
            member.LookAt = FindComponent<NPCLookAt>(identity, member.Animator);
            member.Gestures = FindComponent<NPCGestures>(identity, member.Animator);
            member.Head = NPCLookAt.HeadOf(identity);
            member.Weight = talkWeights != null && i < talkWeights.Length ? Mathf.Max(0f, talkWeights[i]) : 1f;

            // Parameters can only be read from an active Animator; otherwise resolve lazily
            if (member.Animator != null && member.Animator.isActiveAndEnabled && member.Animator.parameterCount > 0)
                member.TalkParamState = HasBoolParameter(member.Animator, IsTalkingParam) ? 1 : -1;

            _members.Add(member);
        }

        if (_members.Count < 2)
            Debug.LogWarning($"[TableConversation] {name} needs at least 2 members; conversation disabled.", this);
    }

    // ================================================================
    // Dialogue events
    // ================================================================

    private void OnDialogueStarted(NPCIdentity npc)
    {
        if (!_initialized) return; // Start() picks up ActiveNPC itself

        // Dialogue with someone elsewhere in the tavern: keep chatting
        int index = IndexOf(npc);
        if (index >= 0)
            EnterWatchMode(index);
    }

    private void OnDialogueEnded(NPCIdentity npc)
    {
        if (!_initialized || IndexOf(npc) < 0) return;

        StartChatLoop(resumeDelay, resuming: true);
    }

    // ================================================================
    // Chatting
    // ================================================================

    private void StartChatLoop(float delay, bool resuming)
    {
        StopAllCoroutines();
        _currentSpeaker = -1;
        _currentAddressee = -1;

        if (_members.Count < 2)
        {
            _mode = Mode.Idle;
            return;
        }

        _mode = resuming ? Mode.Resuming : Mode.Chatting;
        StartCoroutine(ChatLoop(delay));
    }

    private IEnumerator ChatLoop(float startDelay)
    {
        if (startDelay > 0f)
            yield return new WaitForSeconds(startDelay);

        _mode = Mode.Chatting;
        _lastSpeaker = -1;
        _lastAddressee = -1;
        SetTalkingOnly(-1);

        while (true)
        {
            // Not enough members present (e.g. one deactivated): idle and re-check
            int speaker = CountAvailable() >= 2 ? PickSpeaker() : -1;
            if (speaker < 0)
            {
                yield return RetryWait;
                continue;
            }

            yield return StartCoroutine(RunTurn(speaker));

            // Brief beat of silence between turns
            SetTalkingOnly(-1);
            _currentSpeaker = -1;
            _currentAddressee = -1;
            yield return new WaitForSeconds(RandomIn(pauseSeconds));
        }
    }

    private IEnumerator RunTurn(int speakerIndex)
    {
        Member speaker = _members[speakerIndex];
        int addressee = PickRandomAvailable(speakerIndex, -1);
        if (addressee < 0) yield break;

        float duration = RandomIn(turnSeconds);
        int listenerCount = CountAvailable() - 1;

        _lastSpeaker = speakerIndex;
        _currentSpeaker = speakerIndex;
        _currentAddressee = addressee;

        ClearPendingLooks();
        SetTalkingOnly(speakerIndex);

        // Speaker addresses one listener; listeners turn toward the speaker one after another
        SetSocialLook(speaker, _members[addressee].Head);
        for (int i = 0; i < _members.Count; i++)
        {
            if (i == speakerIndex || !IsAvailable(i)) continue;
            ScheduleSocialLook(_members[i], speaker.Head, Random.Range(0.05f, MaxListenerReactionDelay));
        }

        // Plan this turn's beats (seconds from turn start; negative = not this turn)
        float switchAt = listenerCount >= 2 && Random.value < AddresseeSwitchChance
            ? duration * Random.Range(0.35f, 0.7f) : -1f;
        float gestureAt = Random.value < speakerGestureChance
            ? duration * Random.Range(0.15f, 0.6f) : -1f;
        float nodAt = Random.value < nodChance
            ? duration * Random.Range(0.3f, 0.85f) : -1f;
        float sideGlanceAt = listenerCount >= 2 && Random.value < ListenerSideGlanceChance
            ? duration * Random.Range(0.2f, 0.6f) : -1f;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (!IsAvailable(speakerIndex)) break; // Speaker left the table (deactivated)

            FlushDueLooks();

            // Speaker turns to address a different listener
            if (switchAt >= 0f && elapsed >= switchAt)
            {
                switchAt = -1f;
                int next = PickRandomAvailable(speakerIndex, _currentAddressee);
                if (next >= 0)
                {
                    _currentAddressee = next;
                    SetSocialLook(speaker, _members[next].Head);
                }
            }

            // Speaker emphasises a point
            if (gestureAt >= 0f && elapsed >= gestureAt)
            {
                gestureAt = -1f;
                PlayGesture(speaker, Random.value < 0.5f ? NPCGesture.Point : NPCGesture.Ponder);
            }

            // A listener acknowledges, usually the one being addressed
            if (nodAt >= 0f && elapsed >= nodAt)
            {
                nodAt = -1f;
                int nodder = Random.value < AddresseeNodBias && IsAvailable(_currentAddressee)
                    ? _currentAddressee
                    : PickRandomAvailable(speakerIndex, -1);
                if (nodder >= 0)
                    PlayGesture(_members[nodder], NPCGesture.Nod);
            }

            // A listener glances at another listener, then back to the speaker
            if (sideGlanceAt >= 0f && elapsed >= sideGlanceAt)
            {
                sideGlanceAt = -1f;
                int glancer = PickRandomAvailable(speakerIndex, -1);
                int other = glancer >= 0 ? PickRandomAvailable(speakerIndex, glancer) : -1;
                if (other >= 0)
                {
                    Member member = _members[glancer];
                    SetSocialLook(member, _members[other].Head);
                    ScheduleSocialLook(member, speaker.Head, Random.Range(1f, 1.8f));
                }
            }

            yield return null;
            elapsed += Time.deltaTime;
        }

        FlushAllLooks();
        _lastAddressee = _currentAddressee;
    }

    /// <summary>
    /// Weighted pick among available members, never the previous speaker.
    /// The member just addressed gets a bonus so replies feel natural.
    /// </summary>
    private int PickSpeaker()
    {
        float total = 0f;
        for (int i = 0; i < _members.Count; i++)
            total += SpeakerWeight(i);

        // Every eligible weight is zero: fall back to anyone other than the last speaker
        if (total <= 0f)
            return PickRandomAvailable(_lastSpeaker, -1);

        float roll = Random.value * total;
        int lastEligible = -1;
        for (int i = 0; i < _members.Count; i++)
        {
            float weight = SpeakerWeight(i);
            if (weight <= 0f) continue;
            if (roll < weight) return i;
            roll -= weight;
            lastEligible = i;
        }

        return lastEligible; // Floating-point leftovers
    }

    private float SpeakerWeight(int index)
    {
        if (index == _lastSpeaker || !IsAvailable(index)) return 0f;

        float weight = _members[index].Weight;
        return index == _lastAddressee ? weight * AddresseeReplyBias : weight;
    }

    // ================================================================
    // Watching the player's dialogue with a member
    // ================================================================

    private void EnterWatchMode(int addressedIndex)
    {
        StopAllCoroutines();
        _mode = Mode.WatchingDialogue;
        _currentSpeaker = -1;
        _currentAddressee = -1;
        StartCoroutine(WatchDialogue(addressedIndex));
    }

    private IEnumerator WatchDialogue(int addressedIndex)
    {
        Member addressed = _members[addressedIndex];
        Transform focus = addressed.Head;

        ClearPendingLooks();

        // The addressed member is driven by DialogueManagerV2 (Dialogue priority, isTalking)
        if (addressed.LookAt != null)
            addressed.LookAt.ClearLookTarget(LookPriority.Social);

        // Everyone else falls quiet and turns toward the exchange, one after another
        for (int i = 0; i < _members.Count; i++)
        {
            if (i == addressedIndex) continue;
            SetTalking(_members[i], false);
            ScheduleSocialLook(_members[i], focus, Random.Range(0.1f, 0.6f));
        }

        float settle = 0f;
        while (settle < 0.6f)
        {
            FlushDueLooks();
            yield return null;
            settle += Time.deltaTime;
        }
        FlushAllLooks();

        // Now and then someone sneaks a look at the player
        while (true)
        {
            yield return new WaitForSeconds(RandomIn(PlayerGlanceInterval));

            int watcher = PickRandomAvailable(addressedIndex, -1);
            if (watcher < 0) continue;

            Member member = _members[watcher];
            Transform eyes = member.LookAt != null ? member.LookAt.PlayerEyes : null;
            if (eyes == null) continue;

            // A seated NPC can't turn round: if the player is behind them, keep watching the speaker
            Vector3 toPlayer = eyes.position - member.Identity.transform.position;
            toPlayer.y = 0f;
            if (Vector3.Angle(member.Identity.transform.forward, toPlayer) > MaxPlayerGlanceAngle) continue;

            // Social outranks Ambient, so release it for the glance and restore it afterwards
            float glance = RandomIn(PlayerGlanceDuration);
            member.LookAt.ClearLookTarget(LookPriority.Social);
            member.LookAt.LookAtFor(eyes, glance, LookPriority.Ambient);

            yield return new WaitForSeconds(glance);

            if (IsAvailable(watcher))
                SetSocialLook(member, focus);
        }
    }

    // ================================================================
    // Member helpers
    // ================================================================

    private bool IsAvailable(int index)
    {
        if (index < 0 || index >= _members.Count) return false;

        NPCIdentity identity = _members[index].Identity;
        return identity != null
            && identity.gameObject.activeInHierarchy
            && identity != DialogueManagerV2.ActiveNPC;
    }

    private int CountAvailable()
    {
        int count = 0;
        for (int i = 0; i < _members.Count; i++)
            if (IsAvailable(i)) count++;
        return count;
    }

    /// <summary>Uniform pick among available members, skipping up to two indices. -1 if none.</summary>
    private int PickRandomAvailable(int excludeA, int excludeB)
    {
        int count = 0;
        for (int i = 0; i < _members.Count; i++)
            if (i != excludeA && i != excludeB && IsAvailable(i)) count++;

        if (count == 0) return -1;

        int pick = Random.Range(0, count);
        for (int i = 0; i < _members.Count; i++)
        {
            if (i == excludeA || i == excludeB || !IsAvailable(i)) continue;
            if (pick == 0) return i;
            pick--;
        }

        return -1;
    }

    private int IndexOf(NPCIdentity npc)
    {
        if (npc == null) return -1;

        for (int i = 0; i < _members.Count; i++)
            if (_members[i].Identity == npc) return i;

        return -1;
    }

    /// <summary>Sets isTalking on the speaker only (-1 = everyone quiet). Skips the player's dialogue NPC.</summary>
    private void SetTalkingOnly(int speakerIndex)
    {
        NPCIdentity active = DialogueManagerV2.ActiveNPC;
        for (int i = 0; i < _members.Count; i++)
        {
            Member member = _members[i];
            if (member.Identity == null || member.Identity == active) continue;
            SetTalking(member, i == speakerIndex);
        }
    }

    private static void SetTalking(Member member, bool talking)
    {
        Animator animator = member.Animator;
        if (animator == null || !animator.isActiveAndEnabled) return;

        // Only decide once the Animator exposes its parameters (it can read as empty early on)
        if (member.TalkParamState == 0 && animator.parameterCount > 0)
            member.TalkParamState = HasBoolParameter(animator, IsTalkingParam) ? 1 : -1;
        if (member.TalkParamState <= 0) return;

        animator.SetBool(IsTalkingHash, talking);
    }

    private static void PlayGesture(Member member, NPCGesture gesture)
    {
        if (member.Gestures != null && member.Gestures.isActiveAndEnabled)
            member.Gestures.Play(gesture);
    }

    private static void SetSocialLook(Member member, Transform target)
    {
        member.PendingLook = null;
        ApplySocialLook(member, target);
    }

    private static void ApplySocialLook(Member member, Transform target)
    {
        if (member.LookAt != null && target != null)
            member.LookAt.SetLookTarget(target, LookPriority.Social);
    }

    private static void ScheduleSocialLook(Member member, Transform target, float delay)
    {
        if (target == null) return;
        member.PendingLook = target;
        member.PendingLookTime = Time.time + delay;
    }

    private void FlushDueLooks()
    {
        float now = Time.time;
        for (int i = 0; i < _members.Count; i++)
        {
            Member member = _members[i];
            if (member.PendingLook == null || now < member.PendingLookTime) continue;

            Transform target = member.PendingLook;
            member.PendingLook = null;
            ApplySocialLook(member, target);
        }
    }

    private void FlushAllLooks()
    {
        for (int i = 0; i < _members.Count; i++)
        {
            Member member = _members[i];
            if (member.PendingLook == null) continue;

            Transform target = member.PendingLook;
            member.PendingLook = null;
            ApplySocialLook(member, target);
        }
    }

    private void ClearPendingLooks()
    {
        for (int i = 0; i < _members.Count; i++)
            _members[i].PendingLook = null;
    }

    /// <summary>Hands members back to their own behaviours: no Social look, not talking.</summary>
    private void ReleaseMembers()
    {
        NPCIdentity active = DialogueManagerV2.ActiveNPC;
        for (int i = 0; i < _members.Count; i++)
        {
            Member member = _members[i];
            member.PendingLook = null;

            if (member.LookAt != null)
                member.LookAt.ClearLookTarget(LookPriority.Social);

            // The member in player dialogue is animated by DialogueManagerV2: leave it alone
            if (member.Identity != null && member.Identity != active)
                SetTalking(member, false);
        }

        _currentSpeaker = -1;
        _currentAddressee = -1;
    }

    /// <summary>Finds T on the NPC root, then on the Animator's object if the rig nests it.</summary>
    private static T FindComponent<T>(NPCIdentity identity, Animator animator) where T : Component
    {
        if (identity.TryGetComponent(out T component)) return component;
        if (animator != null && animator.gameObject != identity.gameObject && animator.TryGetComponent(out component))
            return component;
        return null;
    }

    private static bool HasBoolParameter(Animator animator, string parameterName)
    {
        if (animator == null || animator.runtimeAnimatorController == null) return false;

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].type == AnimatorControllerParameterType.Bool && parameters[i].name == parameterName)
                return true;
        }
        return false;
    }

    private static float RandomIn(Vector2 range)
    {
        float min = Mathf.Max(0f, Mathf.Min(range.x, range.y));
        float max = Mathf.Max(0f, Mathf.Max(range.x, range.y));
        return Random.Range(min, max);
    }

    // ================================================================
    // Gizmos
    // ================================================================

    private void OnDrawGizmos()
    {
        if (members == null) return;

        // Table links (tavern gold)
        Gizmos.color = new Color(0.83f, 0.69f, 0.22f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, 0.2f);
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i] == null) continue;

            Vector3 a = GizmoAnchor(members[i]);
            Gizmos.DrawLine(transform.position, a);
            for (int j = i + 1; j < members.Length; j++)
            {
                if (members[j] == null) continue;
                Gizmos.DrawLine(a, GizmoAnchor(members[j]));
            }
        }

        // Live: who is talking to whom
        if (!Application.isPlaying || _mode != Mode.Chatting) return;
        if (_currentSpeaker < 0 || _currentSpeaker >= _members.Count) return;
        if (_currentAddressee < 0 || _currentAddressee >= _members.Count) return;

        Transform from = _members[_currentSpeaker].Head;
        Transform to = _members[_currentAddressee].Head;
        if (from == null || to == null) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(from.position, 0.15f);
        Gizmos.DrawLine(from.position, to.position);
    }

    private static Vector3 GizmoAnchor(NPCIdentity npc)
    {
        return npc.transform.position + Vector3.up * 1.2f; // Roughly seated head height
    }
}
