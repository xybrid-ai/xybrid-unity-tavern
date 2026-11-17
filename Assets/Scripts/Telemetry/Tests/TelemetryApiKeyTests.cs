using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace Tavern.Telemetry.Tests
{
    /// <summary>
    /// Uses synthetic project roots and dummy values only, so the real project's
    /// saved preference is never read, written or cleared.
    /// </summary>
    public class TelemetryApiKeyTests
    {
        private static readonly string TestRootA = Path.Combine(Path.GetTempPath(), "xybrid-tavern-telemetry-tests", "project-a");
        private static readonly string TestRootB = Path.Combine(Path.GetTempPath(), "xybrid-tavern-telemetry-tests", "project-b");

        [SetUp]
        [TearDown]
        public void ClearTestPreferences()
        {
            TelemetryEditorPreferences.Clear(TestRootA);
            TelemetryEditorPreferences.Clear(TestRootB);
        }

        [Test]
        public void Save_ThenLoad_ReturnsTrimmedKey()
        {
            TelemetryEditorPreferences.Save(TestRootA, "  dummy-saved-value-a  ");

            Assert.AreEqual("dummy-saved-value-a", TelemetryEditorPreferences.Load(TestRootA));
            Assert.IsTrue(TelemetryEditorPreferences.HasKey(TestRootA));
        }

        [Test]
        public void Clear_RemovesSavedKey()
        {
            TelemetryEditorPreferences.Save(TestRootA, "dummy-saved-value-a");
            TelemetryEditorPreferences.Clear(TestRootA);

            Assert.IsNull(TelemetryEditorPreferences.Load(TestRootA));
            Assert.IsFalse(EditorPrefs.HasKey(TelemetryEditorPreferences.PreferenceNameFor(TestRootA)));
        }

        [Test]
        public void SaveBlank_ClearsSavedKey()
        {
            TelemetryEditorPreferences.Save(TestRootA, "dummy-saved-value-a");
            TelemetryEditorPreferences.Save(TestRootA, "   ");

            Assert.IsFalse(TelemetryEditorPreferences.HasKey(TestRootA));
        }

        [Test]
        public void Projects_AreIsolated()
        {
            TelemetryEditorPreferences.Save(TestRootA, "dummy-saved-value-a");

            Assert.IsNull(TelemetryEditorPreferences.Load(TestRootB));
            Assert.AreNotEqual(
                TelemetryEditorPreferences.PreferenceNameFor(TestRootA),
                TelemetryEditorPreferences.PreferenceNameFor(TestRootB));

            TelemetryEditorPreferences.Save(TestRootB, "dummy-saved-value-b");
            TelemetryEditorPreferences.Clear(TestRootA);

            Assert.AreEqual("dummy-saved-value-b", TelemetryEditorPreferences.Load(TestRootB));
        }

        [Test]
        public void PreferenceName_IsStableAndHoldsNoKeyMaterial()
        {
            string name = TelemetryEditorPreferences.PreferenceNameFor(TestRootA);

            Assert.AreEqual(name, TelemetryEditorPreferences.PreferenceNameFor(TestRootA + "/"));
            TelemetryEditorPreferences.Save(TestRootA, "dummy-saved-value-a");
            Assert.AreEqual(name, TelemetryEditorPreferences.PreferenceNameFor(TestRootA));
            StringAssert.DoesNotContain("dummy", name);
        }

        [Test]
        public void Editor_SavedKeyTakesPrecedenceOverEnvironment()
        {
            TelemetryEditorPreferences.Save(TestRootA, "dummy-saved-value-a");

            string key = TelemetryApiKey.ResolveForEditor(
                TelemetryEditorPreferences.Load(TestRootA), _ => "dummy-env-value", out var source);

            Assert.AreEqual("dummy-saved-value-a", key);
            Assert.AreEqual(TelemetryKeySource.EditorPreferences, source);
        }

        [Test]
        public void Editor_FallsBackToDotEnvLookupByVariableName()
        {
            var asked = new List<string>();

            string key = TelemetryApiKey.ResolveForEditor(
                TelemetryEditorPreferences.Load(TestRootA),
                name => { asked.Add(name); return "dummy-env-value"; },
                out var source);

            Assert.AreEqual("dummy-env-value", key);
            Assert.AreEqual(TelemetryKeySource.DotEnvOrEnvironment, source);
            CollectionAssert.AreEqual(new[] { "xybrid_api_key" }, asked);
        }

        [Test]
        public void Editor_MissingEverywhere_DisablesTelemetry()
        {
            string key = TelemetryApiKey.ResolveForEditor("  ", _ => null, out var source);

            Assert.IsNull(key);
            Assert.AreEqual(TelemetryKeySource.None, source);
        }

        [Test]
        public void Player_ReadsOnlyProcessEnvironmentVariable()
        {
            var asked = new List<string>();

            string key = TelemetryApiKey.ResolveForPlayer(name => { asked.Add(name); return " dummy-env-value "; }, out var source);

            Assert.AreEqual("dummy-env-value", key);
            Assert.AreEqual(TelemetryKeySource.ProcessEnvironment, source);
            CollectionAssert.AreEqual(new[] { "xybrid_api_key" }, asked);
        }

        [Test]
        public void Player_MissingVariable_DisablesTelemetry()
        {
            string key = TelemetryApiKey.ResolveForPlayer(_ => "", out var source);

            Assert.IsNull(key);
            Assert.AreEqual(TelemetryKeySource.None, source);
        }

        [Test]
        public void Player_UsesRealProcessEnvironment()
        {
            string original = Environment.GetEnvironmentVariable(TelemetryApiKey.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(TelemetryApiKey.EnvironmentVariable, "dummy-process-value");
                Assert.AreEqual("dummy-process-value",
                    TelemetryApiKey.ResolveForPlayer(Environment.GetEnvironmentVariable, out var source));
                Assert.AreEqual(TelemetryKeySource.ProcessEnvironment, source);

                Environment.SetEnvironmentVariable(TelemetryApiKey.EnvironmentVariable, null);
                Assert.IsNull(TelemetryApiKey.ResolveForPlayer(Environment.GetEnvironmentVariable, out source));
                Assert.AreEqual(TelemetryKeySource.None, source);
            }
            finally
            {
                Environment.SetEnvironmentVariable(TelemetryApiKey.EnvironmentVariable, original);
            }
        }
    }
}
