using UnityEngine;

/// <summary>
/// Smoothly adjusts camera FOV and position toward NPC during dialogue.
/// Attach to the Player GameObject (same as PlayerMovement).
/// </summary>
public class DialogueCameraFocus : MonoBehaviour
{
    [Header("Focus Settings")]
    [SerializeField] private float focusFOV = 50f;          // Narrower FOV = slight zoom
    [SerializeField] private float normalFOV = 60f;         // Default FOV
    [SerializeField] private float transitionSpeed = 3f;

    [Header("Optional: Look At NPC")]
    [SerializeField] private bool lookAtNPC = true;
    [SerializeField] private float lookAtWeight = 0.3f;     // 0 = no look, 1 = full look
    [SerializeField] private float headAimOffset = 0.08f;   // Lift from head bone pivot (base of skull) to eye level

    private Camera _camera;
    private Transform _currentTarget;
    private Transform _currentHead;                         // Humanoid head bone of the focused NPC (null = fixed offset)
    private bool _isFocused;
    private Quaternion _originalRotation;
    private float _targetFOV;

    private void Start()
    {
        _camera = GetComponentInChildren<Camera>();
        if (_camera != null)
        {
            normalFOV = _camera.fieldOfView;
            _targetFOV = normalFOV;
        }
    }

    private void LateUpdate()
    {
        if (_camera == null) return;

        // Smooth FOV transition
        _camera.fieldOfView = Mathf.Lerp(
            _camera.fieldOfView,
            _targetFOV,
            transitionSpeed * Time.deltaTime
        );

        // Subtle look-at during dialogue
        if (_isFocused && lookAtNPC && _currentTarget != null)
        {
            // Aim at the humanoid head bone when available (correct for both seated and
            // standing NPCs); otherwise fall back to a fixed head-height offset from the root.
            Vector3 targetPoint = _currentHead != null
                ? _currentHead.position + Vector3.up * headAimOffset
                : _currentTarget.position + Vector3.up * 1.5f; // Head height
            Vector3 directionToTarget = targetPoint - _camera.transform.position;
            Quaternion targetRotation = Quaternion.LookRotation(directionToTarget);

            _camera.transform.rotation = Quaternion.Slerp(
                _camera.transform.rotation,
                targetRotation,
                lookAtWeight * transitionSpeed * Time.deltaTime
            );
        }
    }

    /// <summary>
    /// Call when dialogue starts.
    /// </summary>
    public void FocusOn(Transform target)
    {
        _currentTarget = target;
        _currentHead = FindHeadBone(target);
        _isFocused = true;
        _targetFOV = focusFOV;
        _originalRotation = _camera != null ? _camera.transform.localRotation : Quaternion.identity;
    }

    /// <summary>
    /// Call when dialogue ends.
    /// </summary>
    public void Unfocus()
    {
        _isFocused = false;
        _targetFOV = normalFOV;
        _currentTarget = null;
        _currentHead = null;
    }

    /// <summary>
    /// Returns the humanoid head bone of the target's Animator, or null if the
    /// target has no humanoid Animator (caller then uses the fixed offset).
    /// </summary>
    private static Transform FindHeadBone(Transform target)
    {
        if (target == null) return null;

        Animator animator = target.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman) return null;

        return animator.GetBoneTransform(HumanBodyBones.Head);
    }
}
