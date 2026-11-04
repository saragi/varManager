using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace varManager
{
    /// <summary>Who needs what, with every reference resolved to a var in the library.</summary>
    public sealed class LibraryUsage
    {
        private static readonly string[] Nothing = new string[0];

        private readonly Dictionary<string, HashSet<string>> needers = NewMap();
        private readonly Dictionary<string, HashSet<string>> needs = NewMap();

        public LibraryUsage(VarIndex vars, IEnumerable<KeyValuePair<string, string>> dependencies)
        {
            foreach (KeyValuePair<string, string> pair in dependencies)
            {
                string resolved = DependencyAnalysis.Resolve(new[] { pair.Value }, vars)[0].VarName;
                if (resolved == null || string.Equals(resolved, pair.Key, StringComparison.OrdinalIgnoreCase))
                    continue;
                Add(needers, resolved, pair.Key);
                Add(needs, pair.Key, resolved);
            }
        }

        public IEnumerable<string> NeedersOf(string varName)
        {
            HashSet<string> found;
            return needers.TryGetValue(varName, out found) ? (IEnumerable<string>)found : Nothing;
        }

        public IEnumerable<string> NeedsOf(string varName)
        {
            HashSet<string> found;
            return needs.TryGetValue(varName, out found) ? (IEnumerable<string>)found : Nothing;
        }

        private static Dictionary<string, HashSet<string>> NewMap()
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
        {
            HashSet<string> set;
            if (!map.TryGetValue(key, out set))
                map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(value);
        }
    }

    public sealed class MovePlan
    {
        public string Target { get; set; }
        public List<string> NeededBy { get; } = new List<string>();
        public List<string> Moves { get; } = new List<string>();
        public SortedDictionary<string, string> Stays { get; } = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A profile, base.txt or a scene in Saves uses the package itself. It can still move, but the user should know.
        public bool TargetInUseOutsideVars { get; set; }

        public bool IsBlocked => NeededBy.Count > 0;
    }

    /// <summary>
    /// Takes a package and the dependencies only it uses out of the library, into a holding folder.
    /// Nothing is deleted, and the log beside the files puts everything back.
    /// </summary>
    public static class MoveOut
    {
        public const string UsedByOthers = "Stays, used by others";
        public const string UsedOutsideVars = "Stays, used by a profile or scene";

        private const string LogName = "moved.txt";
        private const string FileEntry = "file";
        private const string LinkEntry = "link";
        private const string MarkerEntry = "marker";

        /// <summary>The vars that profiles, base.txt and files in Saves and Custom refer to.</summary>
        public static HashSet<string> ReferencedOutsideVars(LibraryPaths paths, VarIndex vars, Action<string> problem)
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string profiles = Path.Combine(paths.VarsPath, LibraryFolders.Profiles);
            if (Directory.Exists(profiles))
            {
                foreach (string file in Directory.GetFiles(profiles, "*.profile"))
                {
                    LeanProfile profile = LeanProfiles.Load(file);
                    references.UnionWith(profile.Direct);
                    references.UnionWith(profile.Base);
                }
                string baseList = Path.Combine(profiles, "base.txt");
                if (File.Exists(baseList))
                    references.UnionWith(File.ReadAllLines(baseList).Select(line => line.Trim()).Where(line => line.Length > 0));
            }

            foreach (string file in SaveFiles(paths.VamPath))
            {
                try
                {
                    references.UnionWith(DependencyScanner.Extract("meta.json", File.ReadAllText(file)));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    problem(file + " " + ex.Message);
                }
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            found.UnionWith(DependencyAnalysis.Resolve(references, vars).Where(r => r.VarName != null).Select(r => r.VarName));
            return found;
        }

        public static MovePlan Plan(string target, LibraryUsage usage, ISet<string> referencedOutsideVars)
        {
            var plan = new MovePlan { Target = target };
            plan.TargetInUseOutsideVars = referencedOutsideVars.Contains(target);
            plan.NeededBy.AddRange(usage.NeedersOf(target).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            if (plan.IsBlocked)
                return plan;

            var moving = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
            plan.Moves.Add(target);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (string varName in plan.Moves.ToList())
                {
                    foreach (string dependency in usage.NeedsOf(varName).Where(name => !moving.Contains(name)))
                    {
                        if (referencedOutsideVars.Contains(dependency))
                        {
                            plan.Stays[dependency] = UsedOutsideVars;
                        }
                        else if (usage.NeedersOf(dependency).All(moving.Contains))
                        {
                            moving.Add(dependency);
                            plan.Moves.Add(dependency);
                            plan.Stays.Remove(dependency);
                            grew = true;
                        }
                        else
                        {
                            plan.Stays[dependency] = UsedByOthers;
                        }
                    }
                }
            }
            return plan;
        }

        /// <summary>Moves the plan's vars and takes down their links in every switch. Returns the holding folder.</summary>
        /// <param name="files">Gives the file of each var in the plan.</param>
        public static string Apply(MovePlan plan, Func<string, string> files, LibraryPaths paths)
        {
            if (plan.IsBlocked)
                throw new InvalidOperationException(plan.Target + " can't move while other vars need it.");

            string holding = NewHoldingFolder(paths.VarsPath);
            var log = new List<string>();
            var moves = new List<KeyValuePair<string, string>>();
            foreach (string varName in plan.Moves)
            {
                string file = files(varName);
                string folder = Path.GetDirectoryName(file);
                foreach (string source in Directory.GetFiles(folder, varName + ".*"))
                {
                    string destination = Path.Combine(holding, Path.GetFileName(source));
                    moves.Add(new KeyValuePair<string, string>(source, destination));
                    log.Add(string.Join("\t", FileEntry, source, destination));
                }
            }

            var links = new List<string>();
            foreach (string switchName in VarSwitches.Names(paths.VamPath))
            {
                string folder = Path.GetDirectoryName(VarSwitches.LinkFolder(paths.VamPath, switchName));
                Dictionary<string, string> installed = VarLinks.Installed(folder);
                foreach (string varName in plan.Moves)
                {
                    string link;
                    if (!installed.TryGetValue(varName, out link))
                        continue;
                    links.Add(link);
                    log.Add(string.Join("\t", LinkEntry, link, files(varName)));
                    if (File.Exists(link + ".disabled"))
                    {
                        links.Add(link + ".disabled");
                        log.Add(string.Join("\t", MarkerEntry, link + ".disabled", ""));
                    }
                }
            }

            // The log comes first, so a failure part way can still be undone.
            File.WriteAllLines(Path.Combine(holding, LogName), log);
            foreach (string link in links)
                File.Delete(link);
            foreach (KeyValuePair<string, string> move in moves)
                File.Move(move.Key, move.Value);
            return holding;
        }

        /// <summary>The newest move that can still be undone, or null.</summary>
        public static string LatestHolding(string varsPath)
        {
            string root = Path.Combine(varsPath, LibraryFolders.Holding);
            if (!Directory.Exists(root))
                return null;
            return Directory.GetDirectories(root)
                .Where(folder => File.Exists(Path.Combine(folder, LogName)))
                .OrderByDescending(folder => Path.GetFileName(folder), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>Puts everything back. Returns what it couldn't, and keeps those files in the holding folder.</summary>
        public static List<string> Undo(string holding)
        {
            var problems = new List<string>();
            string[] log = File.ReadAllLines(Path.Combine(holding, LogName));
            foreach (string[] entry in log.Select(line => line.Split('\t')).Where(entry => entry[0] == FileEntry))
            {
                string original = entry[1], held = entry[2];
                if (!File.Exists(held))
                    continue;
                if (File.Exists(original))
                {
                    problems.Add(original + " already exists, so its copy stays in " + holding);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                File.Move(held, original);
            }

            foreach (string[] entry in log.Select(line => line.Split('\t')))
            {
                if (entry[0] == LinkEntry && !File.Exists(entry[1]) && !VarLinks.Create(entry[1], entry[2]))
                    problems.Add("Could not link " + entry[1]);
                else if (entry[0] == MarkerEntry && !File.Exists(entry[1]))
                    File.WriteAllText(entry[1], "");
            }

            if (problems.Count == 0)
            {
                File.Delete(Path.Combine(holding, LogName));
                if (!Directory.EnumerateFileSystemEntries(holding).Any())
                    Directory.Delete(holding);
            }
            return problems;
        }

        private static IEnumerable<string> SaveFiles(string vamPath)
        {
            bool hasSaves = Directory.Exists(Path.Combine(vamPath, "Saves"));
            bool hasCustom = Directory.Exists(Path.Combine(vamPath, "Custom"));
            if (hasSaves && hasCustom)
                return DependencyAnalysis.SaveFiles(vamPath);
            string folder = hasSaves ? "Saves" : hasCustom ? "Custom" : null;
            if (folder == null)
                return new string[0];
            return Directory.GetFiles(Path.Combine(vamPath, folder), hasSaves ? "*.json" : "*.vap", SearchOption.AllDirectories);
        }

        private static string NewHoldingFolder(string varsPath)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string root = Path.Combine(varsPath, LibraryFolders.Holding);
            string folder = Path.Combine(root, stamp);
            for (int number = 2; Directory.Exists(folder); number++)
                folder = Path.Combine(root, stamp + "-" + number.ToString("00", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
