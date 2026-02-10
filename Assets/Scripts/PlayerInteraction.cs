using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Raycasts from the camera to detect interactable NPCs.
/// Shows "Press E to talk" prompt and triggers dialogue.
/// Attach to the same Player GameObject as PlayerMovement.
/// </summary>
public class PlayerInteraction : MonoBehaviour
{
    [Header("Interaction Settings")]
    [SerializeField] private float interactionRange = 3f;
    [SerializeField] private LayerMask npcLayer;

    [Header("UI")]
    [SerializeField] private GameObject interactionPrompt; // Assign a UI text "Press E to talk"

    private Camera playerCamera;
    private NPCIdentity currentNPC;
    private InputAction interactAction;
    private bool _inDialogue;

    private void Awake()
    {
        interactAction = new InputAction("Interact", InputActionType.Button, "<Keyboard>/e");
    }

    private void OnEnable()
    {
        interactAction.Enable();
    }

    private void OnDisable()
    {
        interactAction.Disable();
    }

    private void Start()
    {
        playerCamera = GetComponentInChildren<Camera>();

        if (interactionPrompt != null)
            interactionPrompt.SetActive(false);
    }

    private void Update()
    {
        if (_inDialogue) return;

        CheckForNPC();

        if (currentNPC != null && interactAction.WasPressedThisFrame())
        {
            StartDialogue();
        }
    }

    private void CheckForNPC()
    {
        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, interactionRange, npcLayer))
        {
            NPCIdentity npc = hit.collider.GetComponentInParent<NPCIdentity>();
            if (npc != null)
            {
                currentNPC = npc;
                if (interactionPrompt != null)
                    interactionPrompt.SetActive(true);
                return;
            }
        }

        // Nothing found
        currentNPC = null;
        if (interactionPrompt != null)
            interactionPrompt.SetActive(false);
    }

    private void StartDialogue()
    {
        _inDialogue = true;
        interactAction.Disable();

        if (interactionPrompt != null)
            interactionPrompt.SetActive(false);

        // Try V2 first, fall back to V1
        DialogueManagerV2 dialogueManagerV2 = FindFirstObjectByType<DialogueManagerV2>();
        if (dialogueManagerV2 != null)
        {
            dialogueManagerV2.StartDialogue(currentNPC);

            // Freeze player movement during dialogue
            PlayerMovement movement = GetComponent<PlayerMovement>();
            if (movement != null)
                movement.canMove = false;

            // Show cursor for dialogue UI
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }

        // Fallback to V1
        DialogueManager dialogueManager = FindFirstObjectByType<DialogueManager>();
        if (dialogueManager != null)
        {
            dialogueManager.StartDialogue(currentNPC);

            PlayerMovement movement = GetComponent<PlayerMovement>();
            if (movement != null)
                movement.canMove = false;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>
    /// Called by DialogueManager when conversation ends.
    /// </summary>
    public void EndDialogue()
    {
        _inDialogue = false;
        interactAction.Enable();

        PlayerMovement movement = GetComponent<PlayerMovement>();
        if (movement != null)
            movement.canMove = true;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}