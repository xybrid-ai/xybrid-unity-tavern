using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Tavern.Social
{
    /// <summary>Reserved ids shared by events, judges and policy.</summary>
    public static class SocialIds
    {
        public const string Player = "player";
        public const string None = "none";
        public const string Unclear = "unclear";

        public static bool IsReserved(string id) => id == Player || id == None || id == Unclear;
    }

    /// <summary>Who produced an utterance. Only <see cref="Player"/> is published in this experiment.</summary>
    public enum SpeakerKind
    {
        Player,
        Npc
    }

    /// <summary>One line of recent dialogue, as heard at the table.</summary>
    public sealed class DialogueLine
    {
        public string SpeakerId { get; }
        public string Text { get; }

        public DialogueLine(string speakerId, string text)
        {
            if (string.IsNullOrWhiteSpace(speakerId)) throw new ArgumentException("Speaker id is required.", nameof(speakerId));
            SpeakerId = speakerId;
            Text = text ?? "";
        }
    }

    /// <summary>
    /// Immutable record of one utterance spoken within earshot of a table. Unity decides who could hear
    /// it (<see cref="EligibleListenerIds"/>); judges only interpret meaning for those listeners.
    /// </summary>
    public sealed class UtteranceEvent
    {
        public const int MaxTextLength = 480;
        public const int MaxRecentLines = 4;
        public const int MaxLineLength = 240;

        public int SessionId { get; }
        public int TurnId { get; }
        public SpeakerKind SpeakerKind { get; }
        public string SpeakerId { get; }
        public string AddressedId { get; }
        public string Text { get; }
        public IReadOnlyList<DialogueLine> RecentDialogue { get; }
        /// <summary>Game clock (Unity realtimeSinceStartup) when the utterance was submitted.</summary>
        public double RealtimeSeconds { get; }
        public DateTime UtcTimestamp { get; }
        public IReadOnlyList<string> EligibleListenerIds { get; }

        /// <summary>Unique within a play session: "session:turn".</summary>
        public string Key => SessionId + ":" + TurnId;

        private UtteranceEvent(int sessionId, int turnId, SpeakerKind kind, string speakerId, string addressedId,
            string text, IReadOnlyList<DialogueLine> recent, double realtime, DateTime utc, IReadOnlyList<string> listeners)
        {
            SessionId = sessionId;
            TurnId = turnId;
            SpeakerKind = kind;
            SpeakerId = speakerId;
            AddressedId = addressedId;
            Text = text;
            RecentDialogue = recent;
            RealtimeSeconds = realtime;
            UtcTimestamp = utc;
            EligibleListenerIds = listeners;
        }

        /// <summary>
        /// Builds a validated event. Copies every collection, so later changes to the caller's lists can't
        /// leak in. Throws <see cref="ArgumentException"/> on invalid input.
        /// </summary>
        public static UtteranceEvent Create(int sessionId, int turnId, SpeakerKind kind, string speakerId,
            string addressedId, string text, IEnumerable<DialogueLine> recentDialogue, double realtimeSeconds,
            DateTime utcTimestamp, IEnumerable<string> eligibleListenerIds)
        {
            if (sessionId <= 0) throw new ArgumentException("Session id must be positive.", nameof(sessionId));
            if (turnId <= 0) throw new ArgumentException("Turn id must be positive.", nameof(turnId));
            if (string.IsNullOrWhiteSpace(speakerId)) throw new ArgumentException("Speaker id is required.", nameof(speakerId));
            if (kind == SpeakerKind.Player && speakerId != SocialIds.Player)
                throw new ArgumentException("A player utterance must use the player speaker id.", nameof(speakerId));
            if (kind == SpeakerKind.Npc && SocialIds.IsReserved(speakerId))
                throw new ArgumentException("An NPC utterance needs an NPC speaker id.", nameof(speakerId));
            if (string.IsNullOrWhiteSpace(addressedId) || SocialIds.IsReserved(addressedId))
                throw new ArgumentException("Addressed NPC id is required.", nameof(addressedId));
            if (addressedId == speakerId) throw new ArgumentException("A speaker can't address themselves.", nameof(addressedId));

            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) throw new ArgumentException("Utterance text is empty.", nameof(text));
            if (trimmed.Length > MaxTextLength) throw new ArgumentException("Utterance text is too long.", nameof(text));

            var listeners = new List<string>();
            foreach (string id in eligibleListenerIds ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(id) || SocialIds.IsReserved(id))
                    throw new ArgumentException("Invalid listener id '" + id + "'.", nameof(eligibleListenerIds));
                if (id == speakerId || id == addressedId)
                    throw new ArgumentException("The speaker and the addressee are not listeners.", nameof(eligibleListenerIds));
                if (listeners.Contains(id))
                    throw new ArgumentException("Duplicate listener id '" + id + "'.", nameof(eligibleListenerIds));
                listeners.Add(id);
            }

            var lines = new List<DialogueLine>();
            foreach (DialogueLine line in recentDialogue ?? Array.Empty<DialogueLine>())
            {
                if (line == null) continue;
                string lineText = line.Text.Trim();
                if (lineText.Length > MaxLineLength) lineText = lineText.Substring(0, MaxLineLength);
                lines.Add(new DialogueLine(line.SpeakerId, lineText));
            }
            if (lines.Count > MaxRecentLines) lines.RemoveRange(0, lines.Count - MaxRecentLines);

            return new UtteranceEvent(sessionId, turnId, kind, speakerId, addressedId, trimmed,
                new ReadOnlyCollection<DialogueLine>(lines), realtimeSeconds, utcTimestamp,
                new ReadOnlyCollection<string>(listeners));
        }
    }

    /// <summary>Bounded record of what was said in the current dialogue session.</summary>
    public sealed class ConversationTranscript
    {
        private readonly int _capacity;
        private readonly List<DialogueLine> _lines = new List<DialogueLine>();

        public ConversationTranscript(int capacity = 8)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Count => _lines.Count;

        public void Add(string speakerId, string text)
        {
            if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(text)) return;
            _lines.Add(new DialogueLine(speakerId, text.Trim()));
            if (_lines.Count > _capacity) _lines.RemoveAt(0);
        }

        /// <summary>Copy of the last <paramref name="maxLines"/> lines, oldest first.</summary>
        public List<DialogueLine> Snapshot(int maxLines)
        {
            int take = Math.Max(0, Math.Min(maxLines, _lines.Count));
            return _lines.GetRange(_lines.Count - take, take);
        }

        public void Clear() => _lines.Clear();
    }

    /// <summary>A potential listener as Unity sees it at the moment of the utterance.</summary>
    public struct ListenerCandidate
    {
        public string Id;
        public bool IsTableMember;
        public bool IsAlive;
        public bool IsSpeaking;
        /// <summary>Metres between the listener's head and the speaker.</summary>
        public float DistanceToSpeaker;
    }

    /// <summary>
    /// Hearing is decided here, by explicit table membership and distance, never by a model.
    /// </summary>
    public static class HearingEligibility
    {
        public static List<string> Eligible(IEnumerable<ListenerCandidate> candidates, string speakerId,
            string addressedId, float hearingRadius)
        {
            var result = new List<string>();
            if (candidates == null) return result;
            foreach (ListenerCandidate c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c.Id) || SocialIds.IsReserved(c.Id)) continue;
                if (!c.IsTableMember || !c.IsAlive || c.IsSpeaking) continue;
                if (c.Id == speakerId || c.Id == addressedId) continue;
                if (float.IsNaN(c.DistanceToSpeaker) || c.DistanceToSpeaker > hearingRadius) continue;
                if (!result.Contains(c.Id)) result.Add(c.Id);
            }
            return result;
        }
    }
}
