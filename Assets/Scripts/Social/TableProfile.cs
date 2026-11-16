using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Tavern.Social
{
    /// <summary>One table member as the judges and the policy know them. Facts come from the persona files.</summary>
    public sealed class TableMember
    {
        public string Id { get; }
        public string NpcName { get; }
        public string DisplayName { get; }
        public string Pronoun { get; }
        public IReadOnlyList<string> Aliases { get; }
        public IReadOnlyList<string> Facts { get; }

        public TableMember(string id, string npcName, string displayName, string pronoun,
            IList<string> aliases, IList<string> facts)
        {
            Id = id;
            NpcName = npcName;
            DisplayName = displayName;
            Pronoun = pronoun;
            Aliases = new ReadOnlyCollection<string>(new List<string>(aliases ?? new List<string>()));
            Facts = new ReadOnlyCollection<string>(new List<string>(facts ?? new List<string>()));
        }
    }

    public enum GazeKind { None, Player, Addressee, Referent }

    public enum ReactionGesture { None, Nod, Shake, GlanceAway, GlanceToReferent }

    /// <summary>One authored way a listener can react. All fields empty = a deliberate "do nothing".</summary>
    public sealed class ReactionOption
    {
        public string Key;
        public double Weight;
        public GazeKind Gaze;
        public ReactionGesture Gesture;
        /// <summary>FaceEmotion name, or null for no expression change.</summary>
        public string Face;
        public double Intensity;

        public bool IsNothing => Gaze == GazeKind.None && Gesture == ReactionGesture.None && Face == null;
    }

    /// <summary>Authored mapping for (listener, relation, meaning). "*" matches anything.</summary>
    public sealed class PolicyRule
    {
        public string Listener;
        public string Relation;
        public string Meaning;
        public List<ReactionOption> Options = new List<ReactionOption>();

        public string Key => Listener + "/" + Relation + "/" + Meaning;
    }

    public struct SecondsRange
    {
        public double Min;
        public double Max;

        public SecondsRange(double min, double max) { Min = min; Max = max; }

        public double Sample(Random rng) => Min + (Max - Min) * rng.NextDouble();

        public override string ToString() => Min.ToString("0.##", CultureInfo.InvariantCulture) + "-" +
                                             Max.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public sealed class PolicyConfig
    {
        public double CooldownSeconds = 8;
        /// <summary>Chance a listener does nothing even when the judgment warrants a reaction.</summary>
        public double SkipChance = 0.2;
        /// <summary>Same, when the remark is about the listener themself (they almost always notice).</summary>
        public double SelfSkipChance = 0.05;
        public double MinStaggerSeconds = 0.3;
        public SecondsRange FirstDelay = new SecondsRange(0.25, 0.7);
        public SecondsRange Stagger = new SecondsRange(0.35, 0.9);
        public SecondsRange GazeSeconds = new SecondsRange(1.2, 2.0);
        public SecondsRange FaceHoldSeconds = new SecondsRange(1.4, 2.4);
        public List<PolicyRule> Rules = new List<PolicyRule>();
    }

    /// <summary>
    /// Table members, a short setting line and the authored reaction policy, loaded from one JSON file
    /// (Assets/Resources/TableReactions/*.json). Parsing is strict: unknown labels fail the load.
    /// </summary>
    public sealed class TableProfile
    {
        public static readonly string[] Relations = { "self", "peer", "addressee", "none" };

        public string TableId { get; private set; }
        public string Setting { get; private set; }
        public IReadOnlyList<TableMember> Members { get; private set; }
        public PolicyConfig Policy { get; private set; }

        public IReadOnlyList<string> MemberIds
        {
            get
            {
                var ids = new List<string>();
                foreach (TableMember m in Members) ids.Add(m.Id);
                return ids;
            }
        }

        public TableMember Find(string id)
        {
            foreach (TableMember m in Members)
                if (m.Id == id) return m;
            return null;
        }

        public TableMember FindByNpcName(string npcName)
        {
            foreach (TableMember m in Members)
                if (string.Equals(m.NpcName, npcName, StringComparison.Ordinal)) return m;
            return null;
        }

        /// <param name="allowedFaces">Valid FaceEmotion names (the Unity enum lives outside this assembly).</param>
        public static TableProfile Parse(string json, IEnumerable<string> allowedFaces)
        {
            JObject root = JObject.Parse(json);
            var faces = new HashSet<string>(allowedFaces ?? Array.Empty<string>());

            var profile = new TableProfile
            {
                TableId = Required(root, "tableId"),
                Setting = (string)root["setting"] ?? "",
            };

            var members = new List<TableMember>();
            foreach (JToken t in RequiredArray(root, "members"))
            {
                string id = Required(t, "id");
                if (SocialIds.IsReserved(id) || id.Contains("_") || id.ToLowerInvariant() != id)
                    throw new FormatException("Member id '" + id + "' must be lower-case, without '_', and not reserved.");
                foreach (TableMember existing in members)
                    if (existing.Id == id) throw new FormatException("Duplicate member id '" + id + "'.");
                members.Add(new TableMember(id, Required(t, "npcName"), (string)t["displayName"] ?? Required(t, "npcName"),
                    (string)t["pronoun"] ?? "they", Strings(t["aliases"], lower: true), Strings(t["facts"], lower: false)));
            }
            if (members.Count < 2) throw new FormatException("A table needs at least two members.");
            profile.Members = new ReadOnlyCollection<TableMember>(members);

            profile.Policy = ParsePolicy(root["policy"] as JObject, profile, faces);
            return profile;
        }

        private static PolicyConfig ParsePolicy(JObject p, TableProfile profile, HashSet<string> faces)
        {
            var config = new PolicyConfig();
            if (p == null) return config;

            config.CooldownSeconds = Number(p, "cooldownSeconds", config.CooldownSeconds, 0, 120);
            config.SkipChance = Number(p, "skipChance", config.SkipChance, 0, 1);
            config.SelfSkipChance = Number(p, "selfSkipChance", config.SelfSkipChance, 0, 1);
            config.MinStaggerSeconds = Number(p, "minStaggerSeconds", config.MinStaggerSeconds, 0, 5);
            config.FirstDelay = Range(p, "firstDelaySeconds", config.FirstDelay);
            config.Stagger = Range(p, "staggerSeconds", config.Stagger);
            config.GazeSeconds = Range(p, "gazeSeconds", config.GazeSeconds);
            config.FaceHoldSeconds = Range(p, "faceHoldSeconds", config.FaceHoldSeconds);

            foreach (JToken r in (p["rules"] as JArray) ?? new JArray())
            {
                var rule = new PolicyRule
                {
                    Listener = Required(r, "listener"),
                    Relation = Required(r, "relation"),
                    Meaning = Required(r, "meaning"),
                };
                if (rule.Listener != "*" && profile.Find(rule.Listener) == null)
                    throw new FormatException("Rule " + rule.Key + ": unknown listener.");
                if (rule.Relation != "*" && Array.IndexOf(Relations, rule.Relation) < 0)
                    throw new FormatException("Rule " + rule.Key + ": unknown relation.");
                if (rule.Meaning != "*" && !SocialLabels.TryParseMeaning(rule.Meaning, out _))
                    throw new FormatException("Rule " + rule.Key + ": unknown meaning.");

                int index = 0;
                foreach (JToken o in RequiredArray(r, "options"))
                {
                    var option = new ReactionOption
                    {
                        Key = (string)o["key"] ?? ("option" + index),
                        Weight = Number(o, "weight", 1, 0.0001, 1000),
                        Gaze = ParseGaze((string)o["gaze"] ?? "none", rule.Key),
                        Gesture = ParseGesture((string)o["gesture"] ?? "none", rule.Key),
                        Face = (string)o["face"],
                        Intensity = Number(o, "intensity", 0.35, 0, 1),
                    };
                    if (option.Face == "none") option.Face = null;
                    if (option.Face != null && !faces.Contains(option.Face))
                        throw new FormatException("Rule " + rule.Key + ": unknown face '" + option.Face + "'.");
                    rule.Options.Add(option);
                    index++;
                }
                if (rule.Options.Count == 0) throw new FormatException("Rule " + rule.Key + " has no options.");
                config.Rules.Add(rule);
            }
            return config;
        }

        private static GazeKind ParseGaze(string value, string rule)
        {
            switch (value)
            {
                case "none": return GazeKind.None;
                case "player": return GazeKind.Player;
                case "addressee": return GazeKind.Addressee;
                case "referent": return GazeKind.Referent;
                default: throw new FormatException("Rule " + rule + ": unknown gaze '" + value + "'.");
            }
        }

        private static ReactionGesture ParseGesture(string value, string rule)
        {
            switch (value)
            {
                case "none": return ReactionGesture.None;
                case "nod": return ReactionGesture.Nod;
                case "shake": return ReactionGesture.Shake;
                case "glance_away": return ReactionGesture.GlanceAway;
                case "glance_referent": return ReactionGesture.GlanceToReferent;
                default: throw new FormatException("Rule " + rule + ": unknown gesture '" + value + "'.");
            }
        }

        private static string Required(JToken t, string key)
        {
            string value = (string)t[key];
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Missing '" + key + "'.");
            return value;
        }

        private static JArray RequiredArray(JToken t, string key)
        {
            if (!(t[key] is JArray array)) throw new FormatException("Missing array '" + key + "'.");
            return array;
        }

        private static List<string> Strings(JToken t, bool lower)
        {
            var list = new List<string>();
            if (t is JArray array)
                foreach (JToken item in array)
                {
                    string s = ((string)item ?? "").Trim();
                    if (s.Length > 0) list.Add(lower ? s.ToLowerInvariant() : s);
                }
            return list;
        }

        private static double Number(JToken t, string key, double fallback, double min, double max)
        {
            JToken value = t[key];
            if (value == null) return fallback;
            if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)
                throw new FormatException("'" + key + "' must be a number.");
            double d = (double)value;
            if (double.IsNaN(d) || d < min || d > max) throw new FormatException("'" + key + "' is out of range.");
            return d;
        }

        private static SecondsRange Range(JToken t, string key, SecondsRange fallback)
        {
            if (!(t[key] is JArray a)) return fallback;
            if (a.Count != 2) throw new FormatException("'" + key + "' needs [min, max].");
            double min = (double)a[0], max = (double)a[1];
            if (min < 0 || max < min || max > 30) throw new FormatException("'" + key + "' is out of range.");
            return new SecondsRange(min, max);
        }
    }
}
