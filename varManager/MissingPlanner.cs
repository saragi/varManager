using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace varManager
{
    /// <summary>A package some var needs and no owned version satisfies.</summary>
    public sealed class MissingPackage
    {
        public MissingPackage(string package)
        {
            Package = package;
        }

        /// <summary>creator.package, without a version.</summary>
        public string Package { get; }

        /// <summary>The reference asking for the highest version.</summary>
        public string Reference { get; set; }
        public int WantedVersion { get; set; }
        public SortedSet<string> NeededBy { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when something linked needs it, so a download should be linked too.</summary>
        public bool NeededByLinkedVar { get; set; }
    }

    public sealed class MissingReport
    {
        public List<MissingPackage> Needed { get; } = new List<MissingPackage>();

        /// <summary>Stored vars that linked vars need, each with what needs it.</summary>
        public SortedDictionary<string, SortedSet<string>> TurnedOff { get; } =
            new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>References to a version you lack, where a newer owned version serves.</summary>
        public SortedSet<string> Covered { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get { return Needed.Count + TurnedOff.Count; }
        }
    }

    /// <summary>Where each missing package can come from.</summary>
    public sealed class MissingSources
    {
        public List<MissingPackage> OnHub { get; } = new List<MissingPackage>();
        public List<MissingPackage> Listed { get; } = new List<MissingPackage>();
        public List<MissingPackage> NotFound { get; } = new List<MissingPackage>();
    }

    /// <summary>Works out what the library needs, from the database alone.</summary>
    public static class MissingPlanner
    {
        private static readonly Regex Reference =
            new Regex(@"^[^.]+\.[^.]+\.(\d+|latest|min\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static MissingReport Plan(IEnumerable<string> vars, Func<string, IEnumerable<string>> referencesOf,
            VarIndex library, IEnumerable<string> linkedVars)
        {
            HashSet<string> linked = new HashSet<string>(linkedVars, StringComparer.OrdinalIgnoreCase);
            VarIndex visible = new VarIndex(linked.SelectMany(LeanProfiles.ToEntry));
            Dictionary<string, MissingPackage> needed = new Dictionary<string, MissingPackage>(StringComparer.OrdinalIgnoreCase);
            MissingReport report = new MissingReport();

            foreach (string varName in vars)
            {
                bool isLinked = linked.Contains(varName);
                foreach (string reference in referencesOf(varName))
                {
                    if (!Reference.IsMatch(reference))
                        continue;
                    int wanted = WantedVersion(reference);
                    DependencyResolution owned = library.ResolveReference(reference);
                    if (!Satisfies(owned, wanted))
                    {
                        Add(needed, reference, wanted, varName, isLinked);
                        continue;
                    }
                    if (owned.Status == DependencyStatus.Fallback)
                        report.Covered.Add(reference);
                    if (isLinked && !Satisfies(visible.ResolveReference(reference), wanted))
                    {
                        SortedSet<string> users;
                        if (!report.TurnedOff.TryGetValue(owned.VarName, out users))
                        {
                            users = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                            report.TurnedOff.Add(owned.VarName, users);
                        }
                        users.Add(varName);
                    }
                }
            }
            report.Needed.AddRange(needed.Values.OrderBy(item => item.Package, StringComparer.OrdinalIgnoreCase));
            return report;
        }

        /// <summary>A newer version serves a reference, but an older one doesn't.</summary>
        private static bool Satisfies(DependencyResolution resolution, int wanted)
        {
            switch (resolution.Status)
            {
                case DependencyStatus.Missing:
                    return false;
                case DependencyStatus.Fallback:
                    return HubParser.VersionOf(resolution.VarName) >= wanted;
                default:
                    return true;
            }
        }

        private static void Add(Dictionary<string, MissingPackage> needed, string reference, int wanted, string varName, bool isLinked)
        {
            string package = PackageOf(reference);
            MissingPackage item;
            if (!needed.TryGetValue(package, out item))
            {
                item = new MissingPackage(package) { Reference = reference, WantedVersion = wanted };
                needed.Add(package, item);
            }
            if (wanted > item.WantedVersion)
            {
                item.WantedVersion = wanted;
                item.Reference = reference;
            }
            item.NeededBy.Add(varName);
            item.NeededByLinkedVar |= isLinked;
        }

        /// <summary>0 for .latest, otherwise the version number in an exact or .minN reference.</summary>
        public static int WantedVersion(string reference)
        {
            string version = reference.Substring(reference.LastIndexOf('.') + 1);
            if (version.StartsWith("min", StringComparison.OrdinalIgnoreCase))
                version = version.Substring(3);
            int number;
            return int.TryParse(version, out number) ? number : 0;
        }

        /// <summary>The creator.package part of a var name or reference.</summary>
        public static string PackageOf(string name)
        {
            int versionDot = name.LastIndexOf('.');
            return versionDot < 0 ? name : name.Substring(0, versionDot);
        }

        /// <summary>References for owned packages the Hub has a newer version of.</summary>
        public static List<string> UpdateRequests(IEnumerable<string> ownedVars, IDictionary<string, int> hubVersions)
        {
            Dictionary<string, int> newest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string varName in ownedVars)
            {
                string package = PackageOf(varName);
                int known;
                if (!newest.TryGetValue(package, out known) || HubParser.VersionOf(varName) > known)
                    newest[package] = HubParser.VersionOf(varName);
            }
            List<string> requests = new List<string>();
            foreach (KeyValuePair<string, int> owned in newest.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                int version;
                if (hubVersions.TryGetValue(owned.Key, out version) && version > owned.Value)
                    requests.Add(owned.Key + ".latest");
            }
            return requests;
        }

        /// <summary>Sorts packages by where they can come from, given what the Hub offered links for.</summary>
        public static MissingSources Sort(IEnumerable<MissingPackage> needed, IEnumerable<HubPackage> offered, ISet<string> listedPackages)
        {
            Dictionary<string, int> links = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (HubPackage package in offered)
            {
                string name = PackageOf(package.Name);
                int known;
                if (!links.TryGetValue(name, out known) || HubParser.VersionOf(package.Name) > known)
                    links[name] = HubParser.VersionOf(package.Name);
            }

            MissingSources sources = new MissingSources();
            foreach (MissingPackage item in needed)
            {
                int version;
                if (links.TryGetValue(item.Package, out version) && version >= item.WantedVersion)
                    sources.OnHub.Add(item);
                else if (listedPackages.Contains(item.Package))
                    sources.Listed.Add(item);
                else
                    sources.NotFound.Add(item);
            }
            return sources;
        }
    }

    /// <summary>Reads package names from a local SharpVaMTools install. Names only, and never its settings.</summary>
    public static class SharpVaMToolsData
    {
        private const string LinksFolder = "_Download_Links";
        private const string DatabaseFile = "_Database.txt";
        private static readonly Regex LinkLine =
            new Regex(@"^\s*(.+?)\.var(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Finds an install beside the given folder, or null.</summary>
        public static string FindBeside(string folder)
        {
            string parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return null;
            return Directory.GetDirectories(parent, "SharpVaMTools*")
                .FirstOrDefault(path => Directory.Exists(Path.Combine(path, LinksFolder)));
        }

        public static HashSet<string> Packages(string install)
        {
            HashSet<string> packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string links = Path.Combine(install, LinksFolder);
            if (Directory.Exists(links))
            {
                foreach (string file in Directory.GetFiles(links, "*.txt"))
                {
                    foreach (string line in File.ReadLines(file))
                    {
                        Match match = LinkLine.Match(line);
                        if (match.Success)
                            AddName(packages, match.Groups[1].Value);
                    }
                }
            }
            string database = Path.Combine(install, DatabaseFile);
            if (File.Exists(database))
            {
                foreach (string line in File.ReadLines(database))
                {
                    int pathStart = line.LastIndexOf(":/", StringComparison.Ordinal);
                    if (pathStart <= 0)
                        continue;
                    string head = line.Substring(0, pathStart);
                    int gap = head.LastIndexOf("   ", StringComparison.Ordinal);
                    AddName(packages, gap < 0 ? head.Trim() : head.Substring(gap + 3).Trim());
                }
            }
            return packages;
        }

        private static void AddName(HashSet<string> packages, string varName)
        {
            if (varName.Count(character => character == '.') == 2 && HubParser.VersionOf(varName) >= 0)
                packages.Add(MissingPlanner.PackageOf(varName));
        }
    }
}
