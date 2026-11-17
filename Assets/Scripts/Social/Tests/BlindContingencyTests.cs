using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Tavern.Social.Tests
{
    public class BlindContingencyTests
    {
        private static readonly string[] Conditions = { "off", "d1" };

        private static List<BlindLine> Lines(int about, int smallTalk)
        {
            var lines = new List<BlindLine>();
            for (int i = 0; i < about; i++) lines.Add(new BlindLine("about " + i, BlindLineKind.AboutSomeone));
            for (int i = 0; i < smallTalk; i++) lines.Add(new BlindLine("small " + i, BlindLineKind.SmallTalk));
            return lines;
        }

        [Test]
        public void Plan_UsesEveryLineOnce_AndBalancesConditionsWithinEachKind()
        {
            List<BlindLine> lines = Lines(8, 8);
            List<BlindTrial> trials = BlindContingency.Plan(lines, Conditions, new Random(3));

            Assert.AreEqual(16, trials.Count);
            CollectionAssert.AreEquivalent(lines, trials.Select(t => t.Line));
            CollectionAssert.AreEqual(Enumerable.Range(1, 16), trials.Select(t => t.Index));
            foreach (string c in Conditions)
                foreach (BlindLineKind kind in new[] { BlindLineKind.AboutSomeone, BlindLineKind.SmallTalk })
                    Assert.AreEqual(4, trials.Count(t => t.Condition == c && t.Line.Kind == kind), c + " " + kind);
        }

        [Test]
        public void Plan_IsDeterministicForASeed_AndVariesAcrossSeeds()
        {
            List<BlindLine> lines = Lines(8, 8);
            string Signature(int seed) => string.Join("|",
                BlindContingency.Plan(lines, Conditions, new Random(seed)).Select(t => t.Line.Text + "@" + t.Condition));

            Assert.AreEqual(Signature(7), Signature(7));
            Assert.IsTrue(Enumerable.Range(8, 5).Any(s => Signature(s) != Signature(7)));
        }

        [Test]
        public void Plan_RejectsUnevenSplitsAndDuplicateConditions()
        {
            Assert.Throws<ArgumentException>(() => BlindContingency.Plan(Lines(3, 4), Conditions, new Random(1)));
            Assert.Throws<ArgumentException>(() => BlindContingency.Plan(Lines(4, 4), new[] { "off", "off" }, new Random(1)));
            Assert.Throws<ArgumentException>(() => new BlindLine(" ", BlindLineKind.SmallTalk));
        }

        [Test]
        public void Score_CountsHitsAndFalseAlarms_AndSkipsUnanswered()
        {
            var about = new BlindLine("Varien is disciplined.", BlindLineKind.AboutSomeone);
            var small = new BlindLine("What's good to drink?", BlindLineKind.SmallTalk);
            var trials = new List<BlindTrial>
            {
                new BlindTrial { Line = about, Condition = "d1", SaidYes = true },
                new BlindTrial { Line = about, Condition = "d1", SaidYes = true },
                new BlindTrial { Line = about, Condition = "d1", SaidYes = false },
                new BlindTrial { Line = about, Condition = "d1", SaidYes = null },
                new BlindTrial { Line = small, Condition = "d1", SaidYes = false },
                new BlindTrial { Line = small, Condition = "d1", SaidYes = true },
                new BlindTrial { Line = about, Condition = "off", SaidYes = true },
                new BlindTrial { Line = small, Condition = "off", SaidYes = true },
            };

            List<ConditionScore> scores = BlindContingency.Score(trials, Conditions);

            Assert.AreEqual(new[] { "off", "d1" }, scores.Select(s => s.Condition).ToArray());
            ConditionScore d1 = scores[1];
            Assert.AreEqual(3, d1.AboutTrials);
            Assert.AreEqual(2, d1.Hits);
            Assert.AreEqual(2, d1.SmallTalkTrials);
            Assert.AreEqual(1, d1.FalseAlarms);
            Assert.AreEqual(2.0 / 3 - 0.5, d1.Contingency, 1e-9);
            Assert.AreEqual(0.0, scores[0].Contingency, 1e-9);
        }

        [Test]
        public void Score_IsNaN_UntilBothKindsHaveAnswers()
        {
            var trials = new[]
            {
                new BlindTrial { Line = new BlindLine("a", BlindLineKind.AboutSomeone), Condition = "d1", SaidYes = true },
            };
            ConditionScore s = BlindContingency.Score(trials).Single();
            Assert.AreEqual(1.0, s.HitRate);
            Assert.IsTrue(double.IsNaN(s.Contingency));
        }
    }
}
