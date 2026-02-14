using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person player controller using the new Input System.
/// Attach to a GameObject with a CharacterController component.
/// Place a Camera as a child object at head height (~1.7 Y).
/// </summary>
public class PlayerMovement : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4f;
    [SerializeField] private float gravity = -9.81f;

    [Header("Jump")]
    [SerializeField] private float jumpForce = 5f;

    [Header("Mouse Look")]
    [SerializeField] private float mouseSensitivity = 0.3f;
    [SerializeField] private float maxLookAngle = 80f;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip[] footstepSounds;
    [SerializeField] private AudioClip jumpSound;
    [SerializeField] private AudioClip landSound;
    [SerializeField] private float footstepInterval = 0.4f;
    [SerializeField] private float footstepVolume = 0.3f;

    [Header("References")]
    [SerializeField] private Transform cameraTransform;

    private CharacterController controller;
    private float verticalVelocity;
    private float cameraPitch;
    private float footstepTimer;
    private bool wasGrounded;

    // Input actions
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction jumpAction;

    // Public flag so the dialogue system can freeze the player
    [HideInInspector] public bool canMove = true;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();

        // Auto-find camera if not assigned
        if (cameraTransform == null)
            cameraTransform = GetComponentInChildren<Camera>().transform;

        // Auto-find or create audio source
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
                audioSource = gameObject.AddComponent<AudioSource>();
        }

        // Create input actions directly (no InputActionAsset needed)
        moveAction = new InputAction("Move", InputActionType.Value);
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");

        lookAction = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
        
        jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
    }

    private void OnEnable()
    {
        moveAction.Enable();
        lookAction.Enable();
        jumpAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        lookAction.Disable();
        jumpAction.Disable();
    }

    private void Start()
    {
        if (canMove)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        wasGrounded = true;
    }

    private void Update()
    {
        if (canMove)
        {
            HandleMouseLook();
            HandleMovement();
            HandleJump();
            HandleFootsteps();
        }

        // Check for landing
        if (controller.isGrounded && !wasGrounded)
        {
            OnLand();
        }
        wasGrounded = controller.isGrounded;
    }

    private void HandleMouseLook()
    {
        Vector2 lookInput = lookAction.ReadValue<Vector2>();

        float mouseX = lookInput.x * mouseSensitivity;
        float mouseY = lookInput.y * mouseSensitivity;

        // Horizontal rotation - rotate the whole player
        transform.Rotate(Vector3.up * mouseX);

        // Vertical rotation - tilt the camera only
        cameraPitch -= mouseY;
        cameraPitch = Mathf.Clamp(cameraPitch, -maxLookAngle, maxLookAngle);
        cameraTransform.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
    }

    private void HandleMovement()
    {
        Vector2 moveInput = moveAction.ReadValue<Vector2>();

        Vector3 move = transform.right * moveInput.x + transform.forward * moveInput.y;
        controller.Move(move * moveSpeed * Time.deltaTime);

        // Gravity
        if (controller.isGrounded && verticalVelocity < 0)
        {
            verticalVelocity = -2f;
        }
        else
        {
            verticalVelocity += gravity * Time.deltaTime;
        }

        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }

    private void HandleJump()
    {
        if (jumpAction.WasPressedThisFrame() && controller.isGrounded)
        {
            verticalVelocity = jumpForce;
            PlaySound(jumpSound, footstepVolume);
        }
    }

    private void HandleFootsteps()
    {
        // Only play footsteps when moving on ground
        Vector2 moveInput = moveAction.ReadValue<Vector2>();
        bool isMoving = moveInput.sqrMagnitude > 0.1f;

        if (isMoving && controller.isGrounded)
        {
            footstepTimer -= Time.deltaTime;
            if (footstepTimer <= 0f)
            {
                PlayFootstep();
                footstepTimer = footstepInterval;
            }
        }
        else
        {
            footstepTimer = 0f; // Reset so first step plays immediately
        }
    }

    private void PlayFootstep()
    {
        if (footstepSounds == null || footstepSounds.Length == 0) return;

        AudioClip clip = footstepSounds[Random.Range(0, footstepSounds.Length)];
        PlaySound(clip, footstepVolume);
    }

    private void OnLand()
    {
        if (landSound != null)
        {
            PlaySound(landSound, footstepVolume * 1.5f);
        }
        else
        {
            // Use footstep as landing sound fallback
            PlayFootstep();
        }
    }

    private void PlaySound(AudioClip clip, float volume)
    {
        if (clip != null && audioSource != null)
        {
            audioSource.pitch = Random.Range(0.9f, 1.1f); // Slight variation
            audioSource.PlayOneShot(clip, volume);
        }
    }
}