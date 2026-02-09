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

    private Camera _camera;
    private Transform _currentTarget;
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
            Vector3 targetPoint = _currentTarget.position + Vector3.up * 1.5f; // Head height
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
    }
}
