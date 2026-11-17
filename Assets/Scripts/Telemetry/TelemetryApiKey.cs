using System;

namespace Tavern.Telemetry
{
    /// <summary>Where the telemetry API key came from. Never carries the key itself.</summary>
    public enum TelemetryKeySource
    {
        None,
        EditorPreferences,
        DotEnvOrEnvironment,
        ProcessEnvironment,
    }

    /// <summary>
    /// Resolves the optional Xybrid telemetry API key.
    /// Editor: the key saved in Preferences > Xybrid Tavern > Telemetry, then the
    /// project-root .env, then the process environment.
    /// Player: the process environment only. The Editor preference is never read
    /// by, or copied into, a player build.
    /// </summary>
    public static class TelemetryApiKey
    {
        /// <summary>Variable name looked up in .env and the process environment.</summary>
        public const string EnvironmentVariable = "xybrid_api_key";

        /// <summary>
        /// Returns the key for this runtime, or null when telemetry is not configured.
        /// </summary>
        /// <param name="editorEnvironmentLookup">
        /// Editor-only lookup for <see cref="EnvironmentVariable"/> (.env, then process
        /// environment). Ignored in player builds.
        /// </param>
        public static string Resolve(Func<string, string> editorEnvironmentLookup, out TelemetryKeySource source)
        {
#if UNITY_EDITOR
            return ResolveForEditor(TelemetryEditorPreferences.Load(), editorEnvironmentLookup, out source);
#else
            return ResolveForPlayer(Environment.GetEnvironmentVariable, out source);
#endif
        }

        /// <summary>Editor precedence: saved preference, then the environment lookup.</summary>
        public static string ResolveForEditor(string savedKey, Func<string, string> environmentLookup, out TelemetryKeySource source)
        {
            string key = Normalize(savedKey);
            if (key != null)
            {
                source = TelemetryKeySource.EditorPreferences;
                return key;
            }

            key = Normalize(environmentLookup?.Invoke(EnvironmentVariable));
            source = key != null ? TelemetryKeySource.DotEnvOrEnvironment : TelemetryKeySource.None;
            return key;
        }

        /// <summary>Player builds: process environment only.</summary>
        public static string ResolveForPlayer(Func<string, string> processEnvironmentLookup, out TelemetryKeySource source)
        {
            string key = Normalize(processEnvironmentLookup?.Invoke(EnvironmentVariable));
            source = key != null ? TelemetryKeySource.ProcessEnvironment : TelemetryKeySource.None;
            return key;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }
    }
}
