using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Full-screen loader overlay that blocks gameplay until the Xybrid model is ready.
    /// Shows loading status, then a Start button. Fades out to reveal the game.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class GameLoaderScreen : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private CanvasGroup _overlayCanvasGroup;
        [SerializeField] private TMP_Text _statusText;
        [SerializeField] private Button _startButton;
        [SerializeField] private TMP_Text _startButtonText;
        [SerializeField] private GameObject _errorPanel;
        [SerializeField] private TMP_Text _errorText;

        [Header("Settings")]
        [SerializeField] private float _fadeOutDuration = 1.0f;
        [SerializeField] private bool _requireAIModel = false;

        private PlayerMovement _playerMovement;
        private bool _isLoading;
        private float _dotTimer;
        private int _dotCount;
        private string _baseStatusText;

        private void Awake()
        {
            // Freeze the player during loading
            _playerMovement = FindFirstObjectByType<PlayerMovement>();
            if (_playerMovement != null)
                _playerMovement.canMove = false;

            // Show cursor for UI interaction
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Ensure overlay is fully visible and blocks input
            if (_overlayCanvasGroup != null)
            {
                _overlayCanvasGroup.alpha = 1f;
                _overlayCanvasGroup.interactable = true;
                _overlayCanvasGroup.blocksRaycasts = true;
            }

            // Hide interactive elements until needed
            if (_startButton != null)
                _startButton.gameObject.SetActive(false);
            if (_errorPanel != null)
                _errorPanel.SetActive(false);
        }

        private async void Start()
        {
            _isLoading = true;
            _baseStatusText = "Loading AI model";

            if (_statusText != null)
                _statusText.text = _baseStatusText;

            try
            {
                var service = XybridModelService.Instance;
                if (service == null)
                    throw new InvalidOperationException("XybridModelService not found in scene.");

                await service.InitializeAsync();
                OnLoadingComplete();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameLoaderScreen] Model loading failed: {ex.Message}");
                OnLoadingFailed(ex.Message);
            }
        }

        private void Update()
        {
            if (!_isLoading) return;

            // Animate dots: "Loading AI model.", "..", "..."
            _dotTimer += Time.unscaledDeltaTime;
            if (_dotTimer >= 0.5f)
            {
                _dotTimer = 0f;
                _dotCount = (_dotCount + 1) % 4;
                string dots = new string('.', _dotCount == 0 ? 3 : _dotCount);
                if (_statusText != null)
                    _statusText.text = _baseStatusText + dots;
            }
        }

        private void OnLoadingComplete()
        {
            _isLoading = false;

            if (_statusText != null)
                _statusText.text = "AI model loaded";

            ShowStartButton("Enter the Tavern");
        }

        private void OnLoadingFailed(string error)
        {
            _isLoading = false;

            if (_requireAIModel)
            {
                if (_statusText != null)
                    _statusText.text = "Loading failed";
                if (_errorPanel != null)
                    _errorPanel.SetActive(true);
                if (_errorText != null)
                    _errorText.text = error;
            }
            else
            {
                if (_statusText != null)
                    _statusText.text = "AI unavailable \u2014 using scripted dialogue";

                ShowStartButton("Enter the Tavern");
            }
        }

        private void ShowStartButton(string label)
        {
            if (_startButton == null) return;

            _startButton.gameObject.SetActive(true);

            if (_startButtonText != null)
                _startButtonText.text = label;

            _startButton.onClick.AddListener(OnStartClicked);
            _startButton.Select();
        }

        private void OnStartClicked()
        {
            _startButton.interactable = false;
            StartCoroutine(FadeOutAndStart());
        }

        private IEnumerator FadeOutAndStart()
        {
            float elapsed = 0f;

            while (elapsed < _fadeOutDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / _fadeOutDuration);
                if (_overlayCanvasGroup != null)
                    _overlayCanvasGroup.alpha = 1f - t;
                yield return null;
            }

            if (_overlayCanvasGroup != null)
            {
                _overlayCanvasGroup.alpha = 0f;
                _overlayCanvasGroup.interactable = false;
                _overlayCanvasGroup.blocksRaycasts = false;
            }

            // Enable gameplay
            if (_playerMovement != null)
                _playerMovement.canMove = true;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            gameObject.SetActive(false);
        }
    }
}
