using System;
using System.Collections.Generic;

namespace varManager
{
    /// <summary>One package version known to the database.</summary>
    public struct VarEntry
    {
        public VarEntry(string varName, string creatorName, string packageName, int version)
        {
            VarName = varName;
            CreatorName = creatorName;
            PackageName = packageName;
            Version = version;
        }

        public string VarName { get; }
        public string CreatorName { get; }
        public string PackageName { get; }
        public int Version { get; }
    }

    /// <summary>
    /// In-memory lookup of package versions, replacing per-call scans of the vars table.
    /// Resolve keeps the result format callers rely on: a var name, "missing", or a
    /// substitute version with a trailing "$".
    /// </summary>
    public sealed class VarIndex
    {
        public const string Missing = "missing";

        private static readonly List<VarEntry> NoVersions = new List<VarEntry>();

        private readonly Dictionary<string, List<VarEntry>> packages =
            new Dictionary<string, List<VarEntry>>(StringComparer.Ordinal);

        // The vars table primary key ignores case, so exact lookups do too.
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public VarIndex(IEnumerable<VarEntry> entries)
        {
            foreach (VarEntry entry in entries)
            {
                names.Add(entry.VarName);
                string key = PackageKey(entry.CreatorName, entry.PackageName);
                List<VarEntry> versions;
                if (!packages.TryGetValue(key, out versions))
                {
                    versions = new List<VarEntry>();
                    packages.Add(key, versions);
                }
                versions.Add(entry);
            }
            foreach (List<VarEntry> versions in packages.Values)
            {
                versions.Sort((a, b) => a.Version.CompareTo(b.Version));
            }
        }

        private static string PackageKey(string creatorName, string packageName)
        {
            return creatorName + "\0" + packageName;
        }

        private List<VarEntry> Versions(string creatorName, string packageName)
        {
            List<VarEntry> versions;
            return packages.TryGetValue(PackageKey(creatorName, packageName), out versions) ? versions : NoVersions;
        }

        public bool Contains(string varName)
        {
            return names.Contains(varName);
        }

        public int VersionCount(string creatorName, string packageName)
        {
            return Versions(creatorName, packageName).Count;
        }

        /// <summary>True unless a higher version of the same package exists.</summary>
        public bool IsLatest(string varName)
        {
            string[] parts = varName.Split('.');
            int version;
            if (parts.Length != 3 || !int.TryParse(parts[2], out version))
            {
                return true;
            }
            List<VarEntry> versions = Versions(parts[0], parts[1]);
            return versions.Count == 0 || version >= versions[versions.Count - 1].Version;
        }

        /// <summary>First version at or above the request, otherwise the highest available.</summary>
        public string Closest(string creatorName, string packageName, int requestVersion)
        {
            List<VarEntry> versions = Versions(creatorName, packageName);
            if (versions.Count == 0)
            {
                return Missing;
            }
            foreach (VarEntry entry in versions)
            {
                if (entry.Version >= requestVersion)
                {
                    return entry.VarName;
                }
            }
            return versions[versions.Count - 1].VarName;
        }

        /// <summary>
        /// Resolves a dependency reference. Returns the var name, "missing", or a substitute
        /// version followed by "$" when the exact version is not present.
        /// </summary>
        public string Resolve(string varName)
        {
            string[] parts = varName.Split('.');
            if (parts.Length != 3)
            {
                return Missing;
            }
            if (parts[2].ToLower() == "latest")
            {
                List<VarEntry> versions = Versions(parts[0], parts[1]);
                return versions.Count > 0 ? versions[versions.Count - 1].VarName : Missing;
            }
            if (names.Contains(varName))
            {
                return varName;
            }
            int version;
            if (!int.TryParse(parts[2], out version))
            {
                return Missing;
            }
            string closest = Closest(parts[0], parts[1], version);
            return closest == Missing ? Missing : closest + "$";
        }
    }

    /// <summary>Two-way lookup over the dependencies table.</summary>
    public sealed class DependencyIndex
    {
        private static readonly List<string> Empty = new List<string>();

        private readonly Dictionary<string, List<string>> byVar =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        private readonly Dictionary<string, List<string>> byDependency =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public DependencyIndex(IEnumerable<KeyValuePair<string, string>> varAndDependency)
        {
            foreach (KeyValuePair<string, string> pair in varAndDependency)
            {
                Add(byVar, pair.Key, pair.Value);
                Add(byDependency, pair.Value, pair.Key);
            }
        }

        private static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            List<string> values;
            if (!map.TryGetValue(key, out values))
            {
                values = new List<string>();
                map.Add(key, values);
            }
            values.Add(value);
        }

        /// <summary>What this var depends on.</summary>
        public IReadOnlyList<string> DependenciesOf(string varName)
        {
            List<string> values;
            return byVar.TryGetValue(varName, out values) ? values : Empty;
        }

        /// <summary>Which vars depend on this reference.</summary>
        public IReadOnlyList<string> DependentsOf(string dependency)
        {
            List<string> values;
            return byDependency.TryGetValue(dependency, out values) ? values : Empty;
        }
    }
}
