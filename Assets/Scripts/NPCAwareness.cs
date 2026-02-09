using UnityEngine;

/// <summary>
/// Makes NPC react when player approaches (before dialogue).
/// Attach to NPC parent (same as NPCIdentity).
/// </summary>
public class NPCAwareness : MonoBehaviour
{
    [Header("Detection")]
    [SerializeField] private float noticeRange = 5f;        // When NPC notices player
    [SerializeField] private float greetingRange = 3f;      // When NPC does greeting animation
    [SerializeField] private float fieldOfView = 120f;      // NPC's vision cone
    [SerializeField] private LayerMask playerLayer;

    [Header("Reaction")]
    [SerializeField] private ReactionType reactionType = ReactionType.Wave;
    [SerializeField] private float reactionCooldown = 30f;  // Don't repeat too often
    [SerializeField] private AudioClip greetingSound;       // Optional "Hey!" sound
    [SerializeField] private float greetingSoundVolume = 0.5f;

    [Header("Animation Triggers")]
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

    private Transform _player;
    private Animator _animator;
    private AudioSource _audioSource;
    private float _lastReactionTime = -999f;
    private bool _playerInRange;
    private bool _hasReactedThisVisit;

    private void Start()
    {
        _animator = GetComponentInChildren<Animator>();
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
            _audioSource = gameObject.AddComponent<AudioSource>();

        // Find player
        var playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null)
            _player = playerObj.transform;
    }

    private void Update()
    {
        if (_player == null || _animator == null) return;

        float distance = Vector3.Distance(transform.position, _player.position);

        // Check if player just entered greeting range
        bool wasInRange = _playerInRange;
        _playerInRange = distance <= greetingRange;

        // Player just entered range
        if (_playerInRange && !wasInRange)
        {
            _hasReactedThisVisit = false;
        }

        // Player left range - reset for next visit
        if (!_playerInRange && wasInRange)
        {
            _hasReactedThisVisit = false;
        }

        // Try to react
        if (_playerInRange && !_hasReactedThisVisit && CanReact())
        {
            if (IsPlayerInFieldOfView())
            {
                DoReaction();
            }
        }
    }

    private bool CanReact()
    {
        // Check cooldown
        return Time.time - _lastReactionTime >= reactionCooldown;
    }

    private bool IsPlayerInFieldOfView()
    {
        Vector3 directionToPlayer = (_player.position - transform.position).normalized;
        directionToPlayer.y = 0; // Ignore vertical

        Vector3 forward = transform.forward;
        forward.y = 0;

        float angle = Vector3.Angle(forward, directionToPlayer);
        return angle <= fieldOfView * 0.5f;
    }

    private void DoReaction()
    {
        _hasReactedThisVisit = true;
        _lastReactionTime = Time.time;

        // Trigger animation
        string trigger = reactionType switch
        {
            ReactionType.Wave => waveAnimTrigger,
            ReactionType.Nod => nodAnimTrigger,
            ReactionType.Glance => glanceAnimTrigger,
            ReactionType.Custom => customTrigger,
            _ => null
        };

        if (!string.IsNullOrEmpty(trigger))
        {
            _animator.SetTrigger(trigger);
        }

        // Play greeting sound
        if (greetingSound != null)
        {
            _audioSource.PlayOneShot(greetingSound, greetingSoundVolume);
        }

        Debug.Log($"[NPCAwareness] {gameObject.name} reacted to player with {reactionType}");
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
        Gizmos.DrawRay(transform.position + Vector3.up, leftBoundary * greetingRange);
        Gizmos.DrawRay(transform.position + Vector3.up, rightBoundary * greetingRange);
    }
}