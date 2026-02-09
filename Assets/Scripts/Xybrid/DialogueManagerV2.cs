using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using Tavern.Dialogue;

/// <summary>
/// Manages the dialogue UI and conversation flow.
/// Supports both scripted (V1) and AI-generated (V2) dialogue.
/// Uses streaming inference for AI dialogue — text appears token-by-token.
/// </summary>
public class DialogueManagerV2 : MonoBehaviour
{
    [Header("Provider Settings")]
    [SerializeField] private bool useAIDialogue = false;
    [SerializeField] private WorldLore worldLore;

    [Header("UI References")]
    [SerializeField] private GameObject dialoguePanel;
    [SerializeField] private TMP_Text npcNameText;
    [SerializeField] private TMP_Text npcDialogueText;
    [SerializeField] private Button[] optionButtons;
    [SerializeField] private TMP_Text[] optionButtonTexts;
    [SerializeField] private Button closeButton;
    [SerializeField] private TMP_InputField playerInputField; // For free-form AI input
    [SerializeField] private Button sendButton;           // Submit free-form input
    [SerializeField] private GameObject thinkingIndicator; // "NPC is thinking..." UI

    [Header("Demo Overlay")]
    [SerializeField] private GameObject demoOverlay;
    [SerializeField] private TMP_Text latencyText;
    [SerializeField] private TMP_Text modelText;
    [SerializeField] private TMP_Text locationText;

    [Header("Typewriter Effect")]
    [SerializeField] private float typeSpeed = 0.03f;
    [SerializeField] private AudioSource typingAudioSource;
    [SerializeField] private AudioClip[] typingSounds;      // Multiple clips for variety
    [SerializeField] private float typingVolume = 0.3f;
    [SerializeField] private float typingPitchVariation = 0.1f;  // Slight pitch randomization

    // Providers
    private IDialogueProvider _provider;
    private XybridDialogueProvider _xybridProvider;
    private ScriptedDialogueProvider _scriptedProvider;

    // State
    private NPCIdentity _currentNPC;
    private int _currentExchangeIndex;
    private Coroutine _typewriterCoroutine;
    private Coroutine _panelFadeCoroutine;
    private List<string> _conversationHistory = new List<string>();
    private bool _isProcessing;
    private CanvasGroup _panelCanvasGroup;

    // Streaming token queue — filled by background thread, consumed by typewriter coroutine
    private readonly ConcurrentQueue<string> _tokenQueue = new ConcurrentQueue<string>();
    private volatile bool _streamingComplete;

    [Header("Panel Animation")]
    [SerializeField] private float panelFadeDuration = 0.2f;

    private async void Start()
    {
        // Get or add CanvasGroup for fade animation
        _panelCanvasGroup = dialoguePanel.GetComponent<CanvasGroup>();
        if (_panelCanvasGroup == null)
            _panelCanvasGroup = dialoguePanel.AddComponent<CanvasGroup>();
        _panelCanvasGroup.alpha = 0f;

        // Initialize providers
        _scriptedProvider = new ScriptedDialogueProvider();

        if (useAIDialogue)
        {
            try
            {
                // Ensure the model service is ready
                var service = XybridModelService.Instance;
                if (service == null)
                {
                    throw new System.InvalidOperationException(
                        "XybridModelService.Instance is null. Add a GameObject with XybridModelService to the scene.");
                }
                await service.InitializeAsync();

                _xybridProvider = new XybridDialogueProvider();
                if (worldLore != null)
                {
                    _xybridProvider.SetWorldLore(worldLore);
                }

                _provider = _xybridProvider;
                Debug.Log("[DialogueManager] Using Xybrid AI dialogue");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[DialogueManager] Xybrid init failed: {ex.GetType().Name}: {ex.Message}");
                Debug.LogError($"[DialogueManager] Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    Debug.LogError($"[DialogueManager] Inner: {ex.InnerException.Message}");
                Debug.LogWarning("[DialogueManager] Falling back to scripted dialogue");
                _provider = _scriptedProvider;
            }
        }
        else
        {
            _provider = _scriptedProvider;
            Debug.Log("[DialogueManager] Using scripted dialogue");
        }

        // Setup UI
        dialoguePanel.SetActive(false);
        if (thinkingIndicator != null) thinkingIndicator.SetActive(false);
        if (demoOverlay != null) demoOverlay.SetActive(false);

        closeButton.onClick.AddListener(EndDialogue);

        // Option buttons (for scripted dialogue)
        for (int i = 0; i < optionButtons.Length; i++)
        {
            int index = i;
            optionButtons[i].onClick.AddListener(() => OnOptionSelected(index));
        }

        // Free-form input (for AI dialogue)
        if (sendButton != null)
        {
            sendButton.onClick.AddListener(OnSendClicked);
        }
        if (playerInputField != null)
        {
            playerInputField.onEndEdit.AddListener(OnInputEndEdit);
        }

        UpdateInputMode();
    }

    /// <summary>
    /// Toggle between scripted and AI dialogue at runtime.
    /// </summary>
    public void SetUseAIDialogue(bool useAI)
    {
        useAIDialogue = useAI;
        _provider = useAI ? (IDialogueProvider)_xybridProvider : _scriptedProvider;
        UpdateInputMode();
        Debug.Log($"[DialogueManager] Switched to {_provider.ProviderName}");
    }

    private void UpdateInputMode()
    {
        bool freeInput = _provider?.SupportsFreeInput ?? false;

        // Show/hide appropriate input UI
        if (playerInputField != null)
            playerInputField.gameObject.SetActive(freeInput);
        if (sendButton != null)
            sendButton.gameObject.SetActive(freeInput);

        foreach (var btn in optionButtons)
        {
            btn.gameObject.SetActive(!freeInput);
        }
    }

    /// <summary>
    /// Called by PlayerInteraction when player interacts with an NPC.
    /// </summary>
    public async void StartDialogue(NPCIdentity npc)
    {
        Debug.Log($"[DialogueManagerV2] StartDialogue called for {npc.npcName}");
        Debug.Log($"[DialogueManagerV2] Provider: {_provider?.ProviderName ?? "null"}");

        if (_isProcessing) return;

        _currentNPC = npc;
        _currentExchangeIndex = 0;
        _conversationHistory.Clear();

        ShowPanel();
        npcNameText.text = npc.npcName;

        // NPC looks at player
        npc.GetComponent<NPCLookAt>()?.StartLookingAtPlayer();

        // Camera focuses on NPC
        var cameraFocus = FindFirstObjectByType<DialogueCameraFocus>();
        cameraFocus?.FocusOn(npc.transform);

        // Show thinking indicator while loading
        SetThinking(true);

        // Get greeting — streaming or non-streaming
        DialogueResponse response;
        if (_provider.SupportsStreaming)
        {
            // Start streaming typewriter before awaiting
            ClearTokenQueue();
            _streamingComplete = false;
            _typewriterCoroutine = StartCoroutine(StreamingTypewriterEffect());

            response = await _provider.GetGreetingStreamingAsync(npc, OnStreamToken);
            _streamingComplete = true;
        }
        else
        {
            response = await _provider.GetGreetingAsync(npc);
        }

        SetThinking(false);
        UpdateDemoOverlay(response);

        if (!_provider.SupportsStreaming)
        {
            // Non-streaming: show full text with typewriter
            ShowNPCText(response.Text);
        }
        // Streaming: typewriter coroutine is already displaying tokens.
        // The await above blocks until inference is done, so _streamingComplete is set.
        // The coroutine will drain any remaining tokens in the queue on its own.

        _conversationHistory.Add($"{npc.npcName}: {response.Text}");

        // Show options or input field
        await ShowPlayerInput();
    }

    private async void OnOptionSelected(int optionIndex)
    {
        if (_isProcessing) return;

        var options = await _provider.GetPlayerOptionsAsync(_currentNPC, _currentExchangeIndex);
        if (options == null || optionIndex >= options.Length) return;

        string playerChoice = options[optionIndex];
        await ProcessPlayerInput(playerChoice, optionIndex);
    }

    private void OnSendClicked()
    {
        if (_isProcessing || string.IsNullOrWhiteSpace(playerInputField.text)) return;

        string input = playerInputField.text.Trim();
        playerInputField.text = "";
        _ = ProcessPlayerInput(input, -1);
    }

    private void OnInputEndEdit(string text)
    {
        // Submit on Enter key
        if (Keyboard.current != null &&
            (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame))
        {
            OnSendClicked();
        }
    }

    private async System.Threading.Tasks.Task ProcessPlayerInput(string playerInput, int optionIndex)
    {
        _isProcessing = true;
        _conversationHistory.Add($"Player: {playerInput}");

        SetThinking(true);
        HideOptions();

        DialogueResponse response;

        if (_provider.SupportsFreeInput)
        {
            if (_provider.SupportsStreaming)
            {
                // Streaming AI dialogue
                ClearTokenQueue();
                _streamingComplete = false;
                _typewriterCoroutine = StartCoroutine(StreamingTypewriterEffect());

                response = await _provider.GetResponseStreamingAsync(
                    _currentNPC,
                    playerInput,
                    _conversationHistory.ToArray(),
                    OnStreamToken);
                _streamingComplete = true;
            }
            else
            {
                // Non-streaming AI dialogue
                response = await _provider.GetResponseAsync(
                    _currentNPC,
                    playerInput,
                    _conversationHistory.ToArray());
            }
        }
        else
        {
            // Scripted dialogue — get predetermined response
            var exchanges = _currentNPC.dialogueData.exchanges;
            if (_currentExchangeIndex < exchanges.Length && optionIndex >= 0)
            {
                string npcResponse = exchanges[_currentExchangeIndex].npcResponses[optionIndex];
                response = DialogueResponse.FromText(npcResponse);
            }
            else
            {
                response = DialogueResponse.FromText("...");
            }
        }

        SetThinking(false);
        UpdateDemoOverlay(response);

        if (!_provider.SupportsStreaming)
        {
            ShowNPCText(response.Text);
        }

        _conversationHistory.Add($"{_currentNPC.npcName}: {response.Text}");

        _currentExchangeIndex++;

        await ShowPlayerInput();
        _isProcessing = false;
    }

    // ================================================================
    // Streaming token handling
    // ================================================================

    /// <summary>
    /// Callback invoked per token on the background inference thread.
    /// Enqueues the token for the main thread typewriter to consume.
    /// </summary>
    private void OnStreamToken(string token)
    {
        if (!string.IsNullOrEmpty(token))
            _tokenQueue.Enqueue(token);
    }

    private void ClearTokenQueue()
    {
        while (_tokenQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Coroutine that displays streamed tokens as they arrive.
    /// Polls the ConcurrentQueue each frame and appends text.
    /// Hides the thinking indicator on first token and starts talking animation.
    /// </summary>
    private IEnumerator StreamingTypewriterEffect()
    {
        npcDialogueText.text = "";
        bool firstToken = true;

        while (!_streamingComplete || _tokenQueue.Count > 0)
        {
            if (_tokenQueue.TryDequeue(out string token))
            {
                if (firstToken)
                {
                    firstToken = false;
                    // Hide thinking indicator and start talking on first token
                    SetThinking(false);
                    if (_currentNPC != null)
                    {
                        var animator = _currentNPC.GetComponentInChildren<Animator>();
                        if (animator != null)
                            animator.SetBool("isTalking", true);
                    }
                }
                // Display token character-by-character for a smooth typewriter effect
                foreach (char c in token)
                {
                    npcDialogueText.text += c;

                    // Play typing sound (skip for spaces)
                    if (typingAudioSource != null && typingSounds != null && typingSounds.Length > 0 && !char.IsWhiteSpace(c))
                    {
                        AudioClip clip = typingSounds[Random.Range(0, typingSounds.Length)];
                        typingAudioSource.pitch = 1f + Random.Range(-typingPitchVariation, typingPitchVariation);
                        typingAudioSource.PlayOneShot(clip, typingVolume);
                    }

                    yield return new WaitForSeconds(typeSpeed);
                }
            }
            else
            {
                // No tokens available yet — wait a frame
                yield return null;
            }
        }

        // Reset pitch
        if (typingAudioSource != null)
            typingAudioSource.pitch = 1f;
    }

    // ================================================================
    // Non-streaming typewriter (used for scripted dialogue)
    // ================================================================

    private async System.Threading.Tasks.Task ShowPlayerInput()
    {
        if (_provider.SupportsFreeInput)
        {
            // Show text input for AI dialogue
            if (playerInputField != null)
            {
                playerInputField.gameObject.SetActive(true);
                playerInputField.ActivateInputField();
            }
            HideOptions();
        }
        else
        {
            // Show options for scripted dialogue
            var options = await _provider.GetPlayerOptionsAsync(_currentNPC, _currentExchangeIndex);
            if (options != null && options.Length > 0)
            {
                ShowOptions(options);
            }
            else
            {
                HideOptions();
            }
        }
    }

    private void ShowOptions(string[] options)
    {
        for (int i = 0; i < optionButtons.Length; i++)
        {
            if (i < options.Length)
            {
                optionButtons[i].gameObject.SetActive(true);
                optionButtonTexts[i].text = options[i];
            }
            else
            {
                optionButtons[i].gameObject.SetActive(false);
            }
        }
    }

    private void HideOptions()
    {
        foreach (var btn in optionButtons)
        {
            btn.gameObject.SetActive(false);
        }
    }

    private void ShowNPCText(string text)
    {
        if (_typewriterCoroutine != null)
            StopCoroutine(_typewriterCoroutine);

        // Start talking animation
        if (_currentNPC != null)
        {
            var animator = _currentNPC.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.SetBool("isTalking", true);
            }
        }

        _typewriterCoroutine = StartCoroutine(TypewriterEffect(text));
    }

    private IEnumerator TypewriterEffect(string text)
    {
        npcDialogueText.text = "";
        foreach (char c in text)
        {
            npcDialogueText.text += c;

            // Play typing sound (skip for spaces and punctuation)
            if (typingAudioSource != null && typingSounds != null && typingSounds.Length > 0 && !char.IsWhiteSpace(c))
            {
                // Random clip from array
                AudioClip clip = typingSounds[Random.Range(0, typingSounds.Length)];

                // Slight pitch variation for natural feel
                typingAudioSource.pitch = 1f + Random.Range(-typingPitchVariation, typingPitchVariation);
                typingAudioSource.PlayOneShot(clip, typingVolume);
            }

            yield return new WaitForSeconds(typeSpeed);
        }

        // Reset pitch
        if (typingAudioSource != null)
            typingAudioSource.pitch = 1f;
    }

    // ================================================================
    // UI state helpers
    // ================================================================

    private void SetThinking(bool isThinking)
    {
        // UI indicator
        if (thinkingIndicator != null)
            thinkingIndicator.SetActive(isThinking);

        if (isThinking)
            npcDialogueText.text = "";

        // Trigger thinking animation on NPC
        if (_currentNPC != null)
        {
            var animator = _currentNPC.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                animator.SetBool("isThinking", isThinking);
                // Stop talking while thinking
                if (isThinking)
                    animator.SetBool("isTalking", false);
            }
        }
    }

    private void UpdateDemoOverlay(DialogueResponse response)
    {
        if (demoOverlay == null) return;

        if (latencyText != null)
            latencyText.text = $"Latency: {response.LatencyMs}ms";

        if (modelText != null)
            modelText.text = $"Model: {response.ModelId}";

        if (locationText != null)
            locationText.text = $"Location: {response.InferenceLocation}";
    }

    /// <summary>
    /// Toggle the demo overlay visibility.
    /// </summary>
    public void ToggleDemoOverlay()
    {
        if (demoOverlay != null)
            demoOverlay.SetActive(!demoOverlay.activeSelf);
    }

    public void EndDialogue()
    {
        if (_typewriterCoroutine != null)
            StopCoroutine(_typewriterCoroutine);

        // Stop talking animation
        _currentNPC?.GetComponentInChildren<Animator>()?.SetBool("isTalking", false);

        // NPC stops looking at player
        _currentNPC?.GetComponent<NPCLookAt>()?.StopLookingAtPlayer(returnToOriginal: true);

        // Camera unfocuses
        var cameraFocus = FindFirstObjectByType<DialogueCameraFocus>();
        cameraFocus?.Unfocus();

        // Clear NPC conversation context (preserves system prompt)
        if (_currentNPC != null && _xybridProvider != null)
        {
            _xybridProvider.ClearNPCContext(_currentNPC.npcName);
        }

        HidePanel();
        _currentNPC = null;
        _conversationHistory.Clear();
        ClearTokenQueue();

        // Unfreeze player
        var player = FindFirstObjectByType<PlayerInteraction>();
        if (player != null)
            player.EndDialogue();
    }

    private void ShowPanel()
    {
        dialoguePanel.SetActive(true);
        if (_panelFadeCoroutine != null)
            StopCoroutine(_panelFadeCoroutine);
        _panelFadeCoroutine = StartCoroutine(FadePanel(1f));
    }

    private void HidePanel()
    {
        if (_panelFadeCoroutine != null)
            StopCoroutine(_panelFadeCoroutine);
        _panelFadeCoroutine = StartCoroutine(FadePanel(0f, deactivateOnComplete: true));
    }

    private IEnumerator FadePanel(float targetAlpha, bool deactivateOnComplete = false)
    {
        float startAlpha = _panelCanvasGroup.alpha;
        float elapsed = 0f;

        while (elapsed < panelFadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / panelFadeDuration;
            _panelCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            yield return null;
        }

        _panelCanvasGroup.alpha = targetAlpha;

        if (deactivateOnComplete && targetAlpha == 0f)
            dialoguePanel.SetActive(false);
    }

    private void OnDestroy()
    {
        _xybridProvider?.Dispose();
    }

    private void Update()
    {
        // Toggle demo overlay with Tab
        if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
        {
            ToggleDemoOverlay();
        }

        // Toggle between AI/Scripted with F1
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
        {
            SetUseAIDialogue(!useAIDialogue);
        }
    }
}
