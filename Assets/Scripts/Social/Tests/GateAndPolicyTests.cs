using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using static Tavern.Social.Tests.SocialTestSupport;

namespace Tavern.Social.Tests
{
    public class ReactionGateTests
    {
        private double _now;
        private ReactionGate Gate(double maxAge = 6) => new ReactionGate(() => _now, maxAge);

        [SetUp]
        public void Reset() => _now = 100;

        [Test]
        public void FreshTicket_IsAccepted()
        {
            ReactionGate gate = Gate();
            gate.OpenSession(4);
            ReactionTicket t = gate.Submit(Event("Hi", session: 4, turn: 1, realtime: 100), out _);
            _now = 100.4;
            Assert.AreEqual(TicketVerdict.Accept, gate.Check(t));
            Assert.AreEqual(1, gate.PendingCount);
            gate.Release(t);
            Assert.AreEqual(0, gate.PendingCount);
        }

        [Test]
        public void NewerTurn_SupersedesAndCancelsTheOlderTicket()
        {
            ReactionGate gate = Gate();
            gate.OpenSession(4);
            ReactionTicket first = gate.Submit(Event("one", session: 4, turn: 1, realtime: 100), out _);
            ReactionTicket second = gate.Submit(Event("two", session: 4, turn: 2, realtime: 100.5), out _);

            Assert.IsTrue(first.Token.IsCancellationRequested);
            Assert.AreEqual(TicketVerdict.Superseded, gate.Check(first));
            Assert.AreEqual(TicketVerdict.Accept, gate.Check(second));
            Assert.AreEqual(1, gate.PendingCount, "bounded: one live ticket per table");
        }

        [Test]
        public void ReleasedTicket_IsStillSupersededByALaterTurn()
        {
            ReactionGate gate = Gate();
            gate.OpenSession(4);
            ReactionTicket first = gate.Submit(Event("one", session: 4, turn: 1, realtime: 100), out _);
            gate.Release(first);
            gate.Submit(Event("two", session: 4, turn: 2, realtime: 101), out _);
            Assert.AreEqual(TicketVerdict.Superseded, gate.Check(first, checkAge: false));
        }

        [Test]
        public void ClosingTheSession_DiscardsResults()
        {
            ReactionGate gate = Gate();
            gate.OpenSession(4);
            ReactionTicket t = gate.Submit(Event("Hi", session: 4, turn: 1, realtime: 100), out _);
            gate.CloseSession(4);

            Assert.IsTrue(t.Token.IsCancellationRequested);
            Assert.AreEqual(TicketVerdict.SessionClosed, gate.Check(t));
            Assert.AreEqual(TicketVerdict.SessionClosed, gate.CheckListener(t, "shara", _ => true));
        }

        [Test]
        public void ResultsFromAPreviousSession_AreDiscarded()
        {
            ReactionGate gate = Gate();
            gate.OpenSession(4);
            ReactionTicket old = gate.Submit(Event("Hi", session: 4, turn: 1, realtime: 100), out _);
            gate.CloseSession(4);
            gate.OpenSession(6);
            Assert.AreEqual(TicketVerdict.SessionClosed, gate.Check(old));
            Assert.IsNull(gate.Submit(Event("late", session: 4, turn: 2, realtime: 101), out string reason));
            StringAssert.Contains("session", reason);
        }

        [Test]
        public void LateJudgment_Expires_ButAcceptedStepsDoNotAgeOut()
        {
            ReactionGate gate = Gate(maxAge: 6);
            gate.OpenSession(4);
            ReactionTicket t = gate.Submit(Event("Hi", session: 4, turn: 1, realtime: 100), out _);
            _now = 106.5;
            Assert.AreEqual(TicketVerdict.Expired, gate.Check(t));
            Assert.AreEqual(TicketVerdict.Accept, gate.Check(t, checkAge: false));
        }

        [Test]
        public void DestroyedListener_IsRejected()
        {
            ReactionGate gate = Gate();
            gate.OpenSession(4);
            ReactionTicket t = gate.Submit(Event("Hi", session: 4, turn: 1, realtime: 100), out _);
            Assert.AreEqual(TicketVerdict.ListenerGone, gate.CheckListener(t, "shara", id => id != "shara"));
            Assert.AreEqual(TicketVerdict.Accept, gate.CheckListener(t, "varien", id => id != "shara"));
        }

        [Test]
        public void StaleOrDuplicateTurns_AreRefused()
        {
            ReactionGate gate = Gate();
            Assert.IsNull(gate.Submit(Event("Hi", session: 4, turn: 1), out _), "session not open");
            gate.OpenSession(4);
            Assert.NotNull(gate.Submit(Event("Hi", session: 4, turn: 2), out _));
            Assert.IsNull(gate.Submit(Event("Hi", session: 4, turn: 2), out _));
            Assert.IsNull(gate.Submit(Event("Hi", session: 4, turn: 1), out _));
        }
    }

    public class ReactionPolicyTests
    {
        private static ReactionPolicy Policy(int seed = 1, double? skip = 0) => new ReactionPolicy(Table, new Random(seed)) { SkipChanceOverride = skip };

        [Test]
        public void Profile_LoadsWithEveryMemberAndRule()
        {
            CollectionAssert.AreEqual(new[] { "wu", "shara", "varien" }, Table.MemberIds);
            Assert.Greater(Table.Policy.Rules.Count, 20);
            Assert.AreEqual(8, Table.Policy.CooldownSeconds);
            Assert.IsTrue(Table.Policy.Rules.Any(r => r.Options.Any(o => o.IsNothing)), "doing nothing is an authored option");
        }

        [TestCase("\"face\": \"Happy\"", "\"face\": \"Ecstatic\"")]
        [TestCase("\"gesture\": \"nod\"", "\"gesture\": \"backflip\"")]
        [TestCase("\"relation\": \"self\"", "\"relation\": \"rival\"")]
        [TestCase("\"listener\": \"shara\"", "\"listener\": \"greta\"")]
        [TestCase("\"meaning\": \"praise\"", "\"meaning\": \"love\"")]
        public void Profile_RejectsUnknownLabels(string from, string to)
        {
            string json = ReadFile("Assets/Resources/TableReactions/wu_table.json");
            Assert.That(json.Contains(from));
            Assert.Throws<FormatException>(() => TableProfile.Parse(ReplaceFirst(json, from, to), FaceNames));
        }

        [Test]
        public void NoAttention_MeansNoReaction()
        {
            EventJudgment j = Judgment("varien", "praise", ("shara", false), ("varien", false));
            List<ReactionPlan> plans = Policy().Plan(Event("Varien is great."), j, 0);
            Assert.IsTrue(plans.All(p => p.IsNone));
            Assert.IsTrue(plans.All(p => p.Reason == "attention=no"));
        }

        [TestCase("neutral")]
        [TestCase("unclear")]
        public void NeutralOrUnclearAboutSomeoneElse_HasNoRule(string meaning)
        {
            EventJudgment j = Judgment("wu", meaning, ("shara", true), ("varien", true));
            Assert.IsTrue(Policy().Plan(Event("How long have you been coming here?"), j, 0).All(p => p.IsNone));
        }

        [Test]
        public void AtMostOnePlanPerListener_InEventOrder()
        {
            EventJudgment j = Judgment("varien", "praise", ("shara", true), ("varien", true));
            List<ReactionPlan> plans = Policy().Plan(Event("Varien is great."), j, 0);
            CollectionAssert.AreEqual(new[] { "shara", "varien" }, plans.Select(p => p.ListenerId));
        }

        [Test]
        public void Reactions_AreStaggeredNotSynchronized()
        {
            for (int seed = 0; seed < 50; seed++)
            {
                ReactionPolicy policy = Policy(seed);
                var reacting = new List<ReactionPlan>();
                // Pick seeds where both listeners react, then check their start gap
                List<ReactionPlan> plans = policy.Plan(Event("Varien is great."),
                    Judgment("varien", "praise", ("shara", true), ("varien", true)), 0);
                reacting.AddRange(plans.Where(p => !p.IsNone));
                if (reacting.Count < 2) continue;
                double gap = Math.Abs(reacting[0].DelaySeconds - reacting[1].DelaySeconds);
                Assert.GreaterOrEqual(gap, Table.Policy.MinStaggerSeconds - 1e-9, "seed " + seed);
            }
        }

        [Test]
        public void Cooldown_BlocksAQuickSecondReaction()
        {
            ReactionPolicy policy = Policy(seed: 3);
            EventJudgment j = Judgment("shara", "criticism", ("shara", true));
            ReactionPlan first = null;
            double now = 0;
            // Advance until Shara actually reacts (an authored "nothing" option may come up first)
            for (; now < 200 && (first == null || first.IsNone); now += 20)
                first = policy.Plan(Event("Shara is so loud.", listeners: new[] { "shara" }), j, now)[0];
            Assert.IsFalse(first.IsNone);
            double reactedAt = now - 20;

            ReactionPlan second = policy.Plan(Event("Shara is so loud.", listeners: new[] { "shara" }), j, reactedAt + 3)[0];
            Assert.AreEqual("cooldown", second.Reason);
        }

        [Test]
        public void SameSeed_GivesTheSamePlans()
        {
            EventJudgment j = Judgment("varien", "praise", ("shara", true), ("varien", true));
            string a = string.Join(";", Policy(42).Plan(Event("x"), j, 0).Select(p => p.Describe()));
            string b = string.Join(";", Policy(42).Plan(Event("x"), j, 0).Select(p => p.Describe()));
            Assert.AreEqual(a, b);
        }

        [Test]
        public void SkipChance_MakesNoReactionCommon()
        {
            EventJudgment j = Judgment("varien", "praise", ("varien", true));
            Assert.IsTrue(Policy(skip: 1).Plan(Event("x", listeners: new[] { "varien" }), j, 0)[0].IsNone);

            // With the authored skip chance and "nothing" options, a warranted reaction is still often skipped
            int none = 0;
            for (int seed = 0; seed < 400; seed++)
                if (Policy(seed, skip: null).Plan(Event("x", listeners: new[] { "varien" }), j, 0)[0].IsNone) none++;
            Assert.That(none, Is.InRange(40, 120), "remark about Varien: 0.05 + 0.95 * 0.15 = 19% no-reaction expected");

            // A remark about someone else is skipped more often (0.2 + 0.8 * 0.15 = 32% for Shara here)
            int otherNone = 0;
            for (int seed = 0; seed < 400; seed++)
                if (Policy(seed, skip: null).Plan(Event("x", listeners: new[] { "shara" }),
                        Judgment("varien", "praise", ("shara", true)), 0)[0].IsNone) otherNone++;
            Assert.That(otherNone, Is.GreaterThan(none), "listeners notice remarks about themselves more often");
        }

        [Test]
        public void SameOption_IsNotRepeatedBackToBack()
        {
            ReactionPolicy policy = Policy(seed: 9);
            EventJudgment j = Judgment("varien", "praise", ("varien", true));
            string previous = null;
            for (int i = 0; i < 30; i++)
            {
                ReactionPlan p = policy.Plan(Event("x", listeners: new[] { "varien" }), j, i * 100)[0];
                string key = p.IsNone ? "nothing" : p.OptionKey;
                Assert.AreNotEqual(previous, key, "iteration " + i);
                previous = key;
            }
        }

        [Test]
        public void Relations()
        {
            Assert.AreEqual("self", ReactionPolicy.RelationOf("varien", "varien", "wu", Table));
            Assert.AreEqual("peer", ReactionPolicy.RelationOf("shara", "varien", "wu", Table));
            Assert.AreEqual("addressee", ReactionPolicy.RelationOf("shara", "wu", "wu", Table));
            Assert.AreEqual("none", ReactionPolicy.RelationOf("shara", "unclear", "wu", Table));
            Assert.AreEqual("none", ReactionPolicy.RelationOf("shara", "none", "wu", Table));
        }

        [Test]
        public void TargetsResolveToTheRightPeople()
        {
            // Shara hears Varien praised: her "agree" option looks at Varien, never at herself
            for (int seed = 0; seed < 30; seed++)
            {
                foreach (ReactionPlan p in Policy(seed).Plan(Event("Varien is great."),
                             Judgment("varien", "praise", ("shara", true), ("varien", true)), 0))
                {
                    if (p.IsNone || p.Gaze == GazeKind.None) continue;
                    Assert.AreNotEqual(p.ListenerId, p.GazeTargetId);
                    Assert.IsTrue(p.GazeTargetId == "player" || Table.Find(p.GazeTargetId) != null);
                }
            }
        }

        [Test]
        public void CouldReact_IsDeterministic()
        {
            ReactionPolicy policy = Policy();
            UtteranceEvent e = Event("x");
            Assert.IsTrue(policy.CouldReact(e, Judgment("varien", "praise", ("shara", true), ("varien", true)), "varien", out string rule));
            Assert.AreEqual("varien/self/praise", rule);
            Assert.IsFalse(policy.CouldReact(e, Judgment("none", "neutral", ("shara", true), ("varien", true)), "varien", out _));
            Assert.IsFalse(policy.CouldReact(e, Judgment("varien", "praise", ("shara", true), ("varien", false)), "varien", out _));
        }

        private static string ReplaceFirst(string s, string from, string to)
        {
            int i = s.IndexOf(from, StringComparison.Ordinal);
            return s.Substring(0, i) + to + s.Substring(i + from.Length);
        }
    }
}
