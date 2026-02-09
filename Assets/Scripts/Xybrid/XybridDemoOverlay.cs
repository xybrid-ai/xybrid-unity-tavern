using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Demo overlay that shows Xybrid inference stats.
    /// Press Tab to toggle visibility.
    /// </summary>
    public class XybridDemoOverlay : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private TMP_Text providerText;
        [SerializeField] private TMP_Text modelText;
        [SerializeField] private TMP_Text locationText;
        [SerializeField] private TMP_Text latencyText;
        [SerializeField] private TMP_Text statusText;

        [Header("Settings")]
        [SerializeField] private Key toggleKey = Key.Tab;

        private CanvasGroup _canvasGroup;
        private DialogueManagerV2 _dialogueManager;

        private void Awake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
            {
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            // Start hidden
            SetVisible(false);
        }

        private void Start()
        {
            _dialogueManager = FindFirstObjectByType<DialogueManagerV2>();
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
            {
                SetVisible(_canvasGroup.alpha < 0.5f);
            }
        }

        public void SetVisible(bool visible)
        {
            _canvasGroup.alpha = visible ? 1f : 0f;
            _canvasGroup.interactable = visible;
            _canvasGroup.blocksRaycasts = visible;
        }

        public void UpdateStats(string provider, string model, string location, uint latencyMs, string status)
        {
            if (providerText != null)
                providerText.text = $"Provider: {provider}";

            if (modelText != null)
                modelText.text = $"Model: {model}";

            if (locationText != null)
            {
                locationText.text = $"Inference: {location}";
                // Color code by location
                locationText.color = location switch
                {
                    "device" => Color.green,
                    "edge" => Color.yellow,
                    "cloud" => Color.cyan,
                    _ => Color.white
                };
            }

            if (latencyText != null)
            {
                latencyText.text = $"Latency: {latencyMs}ms";
                // Color code by latency
                latencyText.color = latencyMs switch
                {
                    < 100 => Color.green,
                    < 500 => Color.yellow,
                    _ => Color.red
                };
            }

            if (statusText != null)
                statusText.text = status;
        }
    }
}