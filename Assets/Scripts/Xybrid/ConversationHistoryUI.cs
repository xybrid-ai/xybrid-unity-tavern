using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Displays a scrolling conversation history sidebar.
    /// Shows the last N messages with a fade effect — older entries are more transparent.
    /// Attach to a UI GameObject; child text elements are created automatically.
    /// A VerticalLayoutGroup is added at runtime if not already present.
    /// </summary>
    public class ConversationHistoryUI : MonoBehaviour
    {
        [SerializeField] private int maxMessages = 5;
        [SerializeField] private float oldestAlpha = 0.3f;
        [SerializeField] private Color playerColor = new Color(0.8f, 0.85f, 0.9f);
        [SerializeField] private Color npcColor = new Color(0.91f, 0.86f, 0.77f); // parchment #E8DCC4
        [SerializeField] private float fontSize = 14f;
        [SerializeField] private float messageSpacing = 4f;

        private readonly List<MessageEntry> _messages = new List<MessageEntry>();
        private readonly List<TMP_Text> _textElements = new List<TMP_Text>();

        private struct MessageEntry
        {
            public string Speaker;
            public string Text;
            public bool IsPlayer;
        }

        private void Awake()
        {
            // Ensure a VerticalLayoutGroup exists for auto-layout
            var layoutGroup = GetComponent<VerticalLayoutGroup>();
            if (layoutGroup == null)
            {
                layoutGroup = gameObject.AddComponent<VerticalLayoutGroup>();
                layoutGroup.childAlignment = TextAnchor.LowerLeft;
                layoutGroup.childControlWidth = true;
                layoutGroup.childControlHeight = true;
                layoutGroup.childForceExpandWidth = true;
                layoutGroup.childForceExpandHeight = false;
                layoutGroup.spacing = messageSpacing;
            }

            // Create text elements up front
            for (int i = 0; i < maxMessages; i++)
            {
                var go = new GameObject($"HistoryMsg_{i}");
                go.transform.SetParent(transform, false);

                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.fontSize = fontSize;
                tmp.enableWordWrapping = true;
                tmp.overflowMode = TextOverflowModes.Ellipsis;
                tmp.text = "";

                // Auto-size height to fit wrapped text
                var fitter = go.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                go.SetActive(false);
                _textElements.Add(tmp);
            }
        }

        public void AddMessage(string speaker, string text, bool isPlayer)
        {
            _messages.Add(new MessageEntry
            {
                Speaker = speaker,
                Text = text,
                IsPlayer = isPlayer
            });

            while (_messages.Count > maxMessages)
                _messages.RemoveAt(0);

            RefreshDisplay();
        }

        public void Clear()
        {
            _messages.Clear();
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            for (int i = 0; i < _textElements.Count; i++)
            {
                if (i < _messages.Count)
                {
                    var msg = _messages[i];
                    var tmp = _textElements[i];

                    tmp.gameObject.SetActive(true);
                    tmp.text = $"<b>{msg.Speaker}:</b> {msg.Text}";

                    // Fade: oldest = oldestAlpha, newest = 1.0
                    float t = (float)(i + 1) / _messages.Count;
                    float alpha = Mathf.Lerp(oldestAlpha, 1f, t);

                    Color baseColor = msg.IsPlayer ? playerColor : npcColor;
                    tmp.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
                }
                else
                {
                    _textElements[i].gameObject.SetActive(false);
                }
            }
        }
    }
}
