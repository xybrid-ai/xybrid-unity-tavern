using System;
using System.Collections.Generic;
using NUnit.Framework;
using static Tavern.Social.Tests.SocialTestSupport;

namespace Tavern.Social.Tests
{
    public class UtteranceEventTests
    {
        [Test]
        public void Create_CopiesCollections_SoCallerChangesDoNotLeakIn()
        {
            var listeners = new List<string> { "shara", "varien" };
            var history = new List<DialogueLine> { new DialogueLine("wu", "Sit.") };
            UtteranceEvent e = UtteranceEvent.Create(3, 2, SpeakerKind.Player, SocialIds.Player, "wu", "  Hello there.  ",
                history, 12.5, DateTime.UtcNow, listeners);

            listeners.Add("intruder");
            history.Add(new DialogueLine("wu", "Later line"));

            Assert.AreEqual(2, e.EligibleListenerIds.Count);
            Assert.AreEqual(1, e.RecentDialogue.Count);
            Assert.AreEqual("Hello there.", e.Text);
            Assert.AreEqual("3:2", e.Key);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)e.EligibleListenerIds).Add("x"));
        }

        [Test]
        public void Create_KeepsOnlyTheLastFourBoundedLines()
        {
            var history = new List<DialogueLine>();
            for (int i = 0; i < 7; i++) history.Add(new DialogueLine("wu", "line " + i + new string('x', 300)));
            UtteranceEvent e = UtteranceEvent.Create(1, 1, SpeakerKind.Player, SocialIds.Player, "wu", "Hi", history, 0,
                DateTime.UtcNow, new[] { "shara" });

            Assert.AreEqual(UtteranceEvent.MaxRecentLines, e.RecentDialogue.Count);
            StringAssert.StartsWith("line 3", e.RecentDialogue[0].Text);
            Assert.LessOrEqual(e.RecentDialogue[0].Text.Length, UtteranceEvent.MaxLineLength);
        }

        [Test]
        public void Create_RejectsInvalidEvents()
        {
            Assert.Throws<ArgumentException>(() => Event("x", listeners: new[] { "wu" }), "addressee as listener");
            Assert.Throws<ArgumentException>(() => Event("x", listeners: new[] { "shara", "shara" }), "duplicate listener");
            Assert.Throws<ArgumentException>(() => Event("x", listeners: new[] { "unclear" }), "reserved id");
            Assert.Throws<ArgumentException>(() => Event("   "), "empty text");
            Assert.Throws<ArgumentException>(() => Event(new string('a', UtteranceEvent.MaxTextLength + 1)), "too long");
            Assert.Throws<ArgumentException>(() => Event("x", session: 0), "no session");
            Assert.Throws<ArgumentException>(() => UtteranceEvent.Create(1, 1, SpeakerKind.Player, "shara", "wu", "x", null, 0,
                DateTime.UtcNow, null), "player kind needs the player id");
        }

        [Test]
        public void Create_AllowsFutureNpcSpeakerKind()
        {
            UtteranceEvent e = UtteranceEvent.Create(1, 1, SpeakerKind.Npc, "shara", "wu", "Master!", null, 0, DateTime.UtcNow,
                new[] { "varien" });
            Assert.AreEqual(SpeakerKind.Npc, e.SpeakerKind);
            Assert.Throws<ArgumentException>(() => UtteranceEvent.Create(1, 1, SpeakerKind.Npc, SocialIds.Player, "wu", "x",
                null, 0, DateTime.UtcNow, null));
        }

        [Test]
        public void Transcript_IsBoundedAndSnapshotsAreCopies()
        {
            var t = new ConversationTranscript(3);
            for (int i = 0; i < 5; i++) t.Add("wu", "line " + i);
            List<DialogueLine> snapshot = t.Snapshot(10);
            t.Add("player", "after");

            Assert.AreEqual(3, snapshot.Count);
            Assert.AreEqual("line 2", snapshot[0].Text);
            Assert.AreEqual("after", t.Snapshot(1)[0].Text);
        }
    }

    public class HearingEligibilityTests
    {
        private static ListenerCandidate C(string id, float distance, bool member = true, bool alive = true, bool speaking = false)
            => new ListenerCandidate { Id = id, DistanceToSpeaker = distance, IsTableMember = member, IsAlive = alive, IsSpeaking = speaking };

        [Test]
        public void Eligible_UsesMembershipDistanceAndExcludesAddressee()
        {
            List<string> ids = HearingEligibility.Eligible(new[]
            {
                C("wu", 1f),                       // addressee
                C("shara", 2f),
                C("varien", 5f),                   // too far
                C("greta", 1f, member: false),     // not at the table
                C("sable", 1f, alive: false),
                C("jiro", 1f, speaking: true),
            }, SocialIds.Player, "wu", 3.5f);

            CollectionAssert.AreEqual(new[] { "shara" }, ids);
        }

        [Test]
        public void Eligible_RejectsNaNDistance() =>
            CollectionAssert.IsEmpty(HearingEligibility.Eligible(new[] { C("shara", float.NaN) }, SocialIds.Player, "wu", 3.5f));
    }

    public class JudgmentValidatorTests
    {
        private static bool Validate(RawJudgment raw, out string error, params string[] listeners)
        {
            if (listeners.Length == 0) listeners = new[] { "shara", "varien" };
            return JudgmentValidator.TryValidate(raw, Table.MemberIds, listeners, out _, out error);
        }

        private static RawJudgment Raw(string referent = "varien", string meaning = "praise")
        {
            var raw = new RawJudgment { Referent = referent, Meaning = meaning };
            raw.Attention["shara"] = true;
            raw.Attention["varien"] = false;
            return raw;
        }

        [Test]
        public void ValidJudgment_IsCopiedPerListener()
        {
            Assert.IsTrue(JudgmentValidator.TryValidate(Raw(), Table.MemberIds, new[] { "shara", "varien" }, out EventJudgment j, out _));
            Assert.AreEqual("varien", j.Listeners["shara"].Referent);
            Assert.AreEqual(SocialMeaning.Praise, j.Listeners["varien"].Meaning);
            Assert.IsFalse(j.Listeners["varien"].AttentionWarranted);
        }

        [TestCase("greta")]
        [TestCase("player")]
        [TestCase("Varien")]
        [TestCase(null)]
        public void UnknownReferent_IsRejected(string referent) => Assert.IsFalse(Validate(Raw(referent), out _));

        [TestCase("Praise")]
        [TestCase("angry")]
        [TestCase("")]
        [TestCase(null)]
        public void InvalidMeaning_IsRejected(string meaning) => Assert.IsFalse(Validate(Raw(meaning: meaning), out _));

        [Test]
        public void ListenerOutsideTheSuppliedSet_IsRejected()
        {
            RawJudgment raw = Raw();
            raw.Attention["wu"] = true;
            Assert.IsFalse(Validate(raw, out string error));
            StringAssert.Contains("outside", error);
        }

        [Test]
        public void MissingListener_IsRejected()
        {
            RawJudgment raw = Raw();
            raw.Attention.Remove("varien");
            Assert.IsFalse(Validate(raw, out _));
            raw.Attention["varien"] = null;
            Assert.IsFalse(Validate(raw, out _));
        }

        [TestCase(double.NaN)]
        [TestCase(1.5)]
        [TestCase(-0.1)]
        [TestCase(double.PositiveInfinity)]
        public void BadProbability_IsRejected(double p)
        {
            RawJudgment raw = Raw();
            raw.Probabilities["referent"] = p;
            Assert.IsFalse(Validate(raw, out _));
        }

        [TestCase("none")]
        [TestCase("unclear")]
        public void NoneAndUnclear_AreAllowedReferents(string referent) => Assert.IsTrue(Validate(Raw(referent), out _));
    }

    public class ScenarioFixtureTests
    {
        [Test]
        public void Fixtures_UseOnlyKnownIdsAndLabels()
        {
            ScenarioSet set = Scenarios;
            var ids = new HashSet<string>();
            var members = new HashSet<string>(Table.MemberIds);
            Assert.GreaterOrEqual(set.Cases.Count, 20);
            foreach (ScenarioCase c in set.Cases)
            {
                Assert.IsTrue(ids.Add(c.Id), "duplicate " + c.Id);
                Assert.Contains(c.Addressee, new List<string>(members), c.Id);
                foreach (string l in c.Listeners) Assert.IsTrue(members.Contains(l) && l != c.Addressee, c.Id);
                foreach (string r in c.ExpectReferent) Assert.IsTrue(members.Contains(r) || r == "none" || r == "unclear", c.Id);
                foreach (string m in c.ExpectMeaning) Assert.IsTrue(SocialLabels.TryParseMeaning(m, out _), c.Id);
                foreach (var kv in c.ExpectReact)
                {
                    Assert.Contains(kv.Key, c.Listeners, c.Id);
                    Assert.Contains(kv.Value, new[] { "yes", "no", "any" }, c.Id);
                }
                Assert.DoesNotThrow(() => ScenarioEvaluator.ToEvent(c, 1), c.Id);
            }
        }
    }
}
