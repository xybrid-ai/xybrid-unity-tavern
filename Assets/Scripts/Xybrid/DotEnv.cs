using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tavern.Dialogue
{
    /// <summary>
    /// Minimal .env reader. In the Editor, parses the project-root .env file.
    /// In Player builds, falls back to process environment variables.
    /// </summary>
    public static class DotEnv
    {
        private static Dictionary<string, string> _cache;

        public static string Get(string key)
        {
#if UNITY_EDITOR
            EnsureLoaded();
            if (_cache.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
#endif
            return Environment.GetEnvironmentVariable(key);
        }

#if UNITY_EDITOR
        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);

            // Application.dataPath is <project>/Assets
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string envPath = Path.Combine(projectRoot, ".env");
            if (!File.Exists(envPath)) return;

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (v.Length >= 2 && ((v[0] == '"' && v[v.Length - 1] == '"') ||
                                       (v[0] == '\'' && v[v.Length - 1] == '\'')))
                {
                    v = v.Substring(1, v.Length - 2);
                }
                _cache[k] = v;
            }
        }
#endif
    }
}
