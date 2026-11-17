using System;
using System.Threading;

namespace Tavern.Social
{
    public enum TicketVerdict
    {
        Accept,
        /// <summary>A newer turn of the same session replaced this one.</summary>
        Superseded,
        /// <summary>The dialogue closed (or another one opened) after the utterance.</summary>
        SessionClosed,
        /// <summary>The judgment arrived too long after the utterance to make sense.</summary>
        Expired,
        /// <summary>The listener no longer exists or left the scene.</summary>
        ListenerGone
    }

    /// <summary>One utterance's claim on the reaction pipeline. Cancelled when superseded or the session closes.</summary>
    public sealed class ReactionTicket
    {
        internal readonly CancellationTokenSource Cts = new CancellationTokenSource();

        public UtteranceEvent Event { get; }
        public double SubmittedAt { get; }
        public CancellationToken Token => Cts.Token;

        internal ReactionTicket(UtteranceEvent e, double now)
        {
            Event = e;
            SubmittedAt = now;
        }
    }

    /// <summary>
    /// Bounded, main-thread bookkeeping for pending judgments: at most one live ticket per table. A new
    /// utterance supersedes (and cancels) the previous ticket; closing the session cancels it; a judgment
    /// older than <see cref="MaxAgeSeconds"/> is expired. Every result and every delayed reaction step is
    /// re-checked here before it touches a character.
    /// </summary>
    public sealed class ReactionGate
    {
        private readonly Func<double> _clock;
        private ReactionTicket _current;
        private int _latestTurn;

        public double MaxAgeSeconds { get; set; }
        public int OpenSessionId { get; private set; }

        /// <summary>Tickets created and not yet released (0 or 1).</summary>
        public int PendingCount => _current != null ? 1 : 0;

        public ReactionGate(Func<double> clock, double maxAgeSeconds)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            MaxAgeSeconds = maxAgeSeconds;
        }

        public void OpenSession(int sessionId)
        {
            CancelCurrent();
            OpenSessionId = sessionId;
            _latestTurn = 0;
        }

        public void CloseSession(int sessionId)
        {
            if (sessionId != OpenSessionId) return;
            CancelCurrent();
            OpenSessionId = 0;
            _latestTurn = 0;
        }

        /// <summary>Creates the ticket for a new utterance, superseding any older one. Null when the session isn't open.</summary>
        public ReactionTicket Submit(UtteranceEvent e, out string rejectReason)
        {
            rejectReason = null;
            if (e == null) { rejectReason = "no event"; return null; }
            if (OpenSessionId == 0 || e.SessionId != OpenSessionId) { rejectReason = "session not open"; return null; }
            if (e.TurnId <= _latestTurn) { rejectReason = "turn " + e.TurnId + " is not newer than " + _latestTurn; return null; }

            CancelCurrent();
            _latestTurn = e.TurnId;
            _current = new ReactionTicket(e, _clock());
            return _current;
        }

        /// <param name="checkAge">True when a judgment arrives; false for later steps of an accepted reaction.</param>
        public TicketVerdict Check(ReactionTicket ticket, bool checkAge = true)
        {
            if (ticket == null || OpenSessionId == 0 || ticket.Event.SessionId != OpenSessionId) return TicketVerdict.SessionClosed;
            if (ticket.Event.TurnId != _latestTurn || ticket.Token.IsCancellationRequested) return TicketVerdict.Superseded;
            if (checkAge && _clock() - ticket.Event.RealtimeSeconds > MaxAgeSeconds) return TicketVerdict.Expired;
            return TicketVerdict.Accept;
        }

        public TicketVerdict CheckListener(ReactionTicket ticket, string listenerId, Func<string, bool> isAlive)
        {
            TicketVerdict verdict = Check(ticket, checkAge: false);
            if (verdict != TicketVerdict.Accept) return verdict;
            return isAlive != null && isAlive(listenerId) ? TicketVerdict.Accept : TicketVerdict.ListenerGone;
        }

        /// <summary>Releases the ticket once its judgment has been handled (accepted or not).</summary>
        public void Release(ReactionTicket ticket)
        {
            if (ticket != null && ReferenceEquals(ticket, _current)) _current = null;
        }

        private void CancelCurrent()
        {
            if (_current == null) return;
            try { _current.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
            _current = null;
        }
    }
}
