using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace varManager
{
    /// <summary>Everything a set of vars needs, as found by DependencyScanner.Closure.</summary>
    public sealed class DependencyClosure
    {
        public List<string> Vars { get; } = new List<string>();
        public SortedSet<string> Missing { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>References satisfied by a different version, mapped to the var used.</summary>
        public SortedDictionary<string, string> Substitutions { get; } =
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds package references in var content. meta.json can be incomplete, so scene and
    /// preset files are read as well. Names can contain spaces but not quotes, colons,
    /// slashes or dots.
    /// </summary>
    public static class DependencyScanner
    {
        private const string Name = @"[^""\r\n:/\\.]{1,80}\.[^""\r\n:/\\.]{1,120}\.(?:\d+|latest|min\d+)";

        private static readonly Regex MetaReference =
            new Regex("\"(" + Name + ")\"\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ContentReference =
            new Regex("\"(?:[^\"\r\n:]*/)?(" + Name + "):/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] ScannedExtensions = { ".json", ".vap", ".vaj", ".vam", ".cslist" };

        public static bool IsScannable(string entryName)
        {
            return ScannedExtensions.Any(extension => entryName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsMeta(string entryName)
        {
            return string.Equals(entryName, "meta.json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Returns the references in one file, using the rules for its entry name.</summary>
        public static HashSet<string> Extract(string entryName, string text)
        {
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(references, ContentReference, text);
            if (IsMeta(entryName))
                Add(references, MetaReference, text);
            return references;
        }

        private static void Add(HashSet<string> references, Regex pattern, string text)
        {
            foreach (Match match in pattern.Matches(text))
                references.Add(match.Groups[1].Value.Trim());
        }

        /// <summary>Lists each file's references and what they resolve to, leaving out the var itself.</summary>
        public static string Describe(IEnumerable<KeyValuePair<string, HashSet<string>>> files, VarIndex index, string varName)
        {
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, HashSet<string>> file in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<string> references = file.Value
                    .Where(reference => !string.Equals(reference, varName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (references.Count == 0)
                    continue;
                text.AppendLine(file.Key);
                foreach (string reference in references)
                {
                    DependencyResolution resolution = index.ResolveReference(reference);
                    text.AppendLine(resolution.Status == DependencyStatus.Missing
                        ? $"    {reference}: Missing"
                        : $"    {reference}: {resolution.Status}, uses {resolution.VarName}");
                }
            }
            return text.Length > 0 ? text.ToString() : "No package references found.";
        }

        /// <summary>
        /// Walks references from the seed vars. referencesOf returns the references inside one var.
        /// </summary>
        public static DependencyClosure Closure(IEnumerable<string> seeds, VarIndex index,
            Func<string, IEnumerable<string>> referencesOf)
        {
            DependencyClosure closure = new DependencyClosure();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new Stack<string>(seeds.Reverse());
            while (pending.Count > 0)
            {
                string reference = pending.Pop();
                DependencyResolution resolution = index.ResolveReference(reference);
                if (resolution.Status == DependencyStatus.Missing)
                {
                    closure.Missing.Add(reference);
                    continue;
                }
                if (resolution.Status == DependencyStatus.Fallback)
                    closure.Substitutions[reference] = resolution.VarName;
                if (!visited.Add(resolution.VarName))
                    continue;
                closure.Vars.Add(resolution.VarName);
                foreach (string next in referencesOf(resolution.VarName))
                    pending.Push(next);
            }
            return closure;
        }
    }
}
