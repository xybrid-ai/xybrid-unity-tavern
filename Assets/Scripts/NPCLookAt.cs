using UnityEngine;

/// <summary>
/// Makes the NPC smoothly rotate to face the player during dialogue.
/// Attach to the NPC parent (same object as NPCIdentity).
/// </summary>
public class NPCLookAt : MonoBehaviour
{
    [Header("Mode")]
    [SerializeField] private bool headOnly = false; // For sitting NPCs
    [SerializeField] private Transform headBone;    // Assign if headOnly is true

    [Header("Settings")]
    [SerializeField] private float rotationSpeed = 5f;
    [SerializeField] private float maxHeadAngle = 70f; // How far head can turn

    [Header("Auto-Find")]
    [SerializeField] private Transform player;

    private bool _isInConversation;
    private Quaternion _originalRotation;
    private Quaternion _originalHeadRotation;
    private Animator _animator;

    private void Start()
    {
        _originalRotation = transform.rotation;
        _animator = GetComponentInChildren<Animator>();

        // Try to auto-find head bone if not assigned
        if (headOnly && headBone == null && _animator != null)
        {
            headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
        }

        if (headOnly && headBone != null)
        {
            _originalHeadRotation = headBone.localRotation;
        }

        // Auto-find player if not assigned
        if (player == null)
        {
            var playerObj = GameObject.FindWithTag("Player");
            if (playerObj != null)
                player = playerObj.transform;
        }
    }

    private void LateUpdate()
    {
        if (!_isInConversation || player == null) return;

        if (headOnly && headBone != null)
        {
            RotateHeadOnly();
        }
        else
        {
            RotateFullBody();
        }
    }

    private void RotateFullBody()
    {
        // Calculate direction to player
        Vector3 directionToPlayer = player.position - transform.position;
        directionToPlayer.y = 0; // Lock Y axis

        if (directionToPlayer.sqrMagnitude < 0.1f) return;

        Quaternion targetRotation = Quaternion.LookRotation(directionToPlayer);

        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            targetRotation,
            rotationSpeed * Time.deltaTime
        );
    }

    private void RotateHeadOnly()
    {
        // Calculate direction from head to player
        Vector3 directionToPlayer = player.position - headBone.position;

        // Get the angle relative to the NPC's forward direction
        Vector3 localDirection = transform.InverseTransformDirection(directionToPlayer);
        float horizontalAngle = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;

        // Clamp the angle so head doesn't spin around unnaturally
        horizontalAngle = Mathf.Clamp(horizontalAngle, -maxHeadAngle, maxHeadAngle);

        // Calculate vertical angle (looking up/down)
        float verticalAngle = -Mathf.Atan2(localDirection.y, localDirection.z) * Mathf.Rad2Deg;
        verticalAngle = Mathf.Clamp(verticalAngle, -25f, 25f);

        // Apply rotation to head bone
        Quaternion targetHeadRotation = _originalHeadRotation * Quaternion.Euler(verticalAngle, horizontalAngle, 0);

        headBone.localRotation = Quaternion.Slerp(
            headBone.localRotation,
            targetHeadRotation,
            rotationSpeed * Time.deltaTime
        );
    }

    /// <summary>
    /// Call when dialogue starts.
    /// </summary>
    public void StartLookingAtPlayer()
    {
        _isInConversation = true;
    }

    /// <summary>
    /// Call when dialogue ends.
    /// </summary>
    public void StopLookingAtPlayer(bool returnToOriginal = false)
    {
        _isInConversation = false;

        if (returnToOriginal)
        {
            StartCoroutine(ReturnToOriginalRotation());
        }
    }

    private System.Collections.IEnumerator ReturnToOriginalRotation()
    {
        float elapsed = 0f;
        float duration = 0.5f;
        Quaternion startRotation = transform.rotation;
        Quaternion startHeadRotation = headBone != null ? headBone.localRotation : Quaternion.identity;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            if (headOnly && headBone != null)
            {
                headBone.localRotation = Quaternion.Slerp(startHeadRotation, _originalHeadRotation, t);
            }
            else
            {
                transform.rotation = Quaternion.Slerp(startRotation, _originalRotation, t);
            }

            yield return null;
        }

        if (headOnly && headBone != null)
        {
            headBone.localRotation = _originalHeadRotation;
        }
        else
        {
            transform.rotation = _originalRotation;
        }
    }
}