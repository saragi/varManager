using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace varManager
{
    /// <summary>What the user chose. Everything else in a profile is worked out from this.</summary>
    public sealed class LeanProfile
    {
        public List<string> Direct { get; } = new List<string>();
        public List<string> Base { get; } = new List<string>();

        /// <summary>When not empty, the profile is the default switch minus these vars.</summary>
        public List<string> Exclude { get; } = new List<string>();
    }

    public sealed class ProfilePlan
    {
        public ProfilePlan(DependencyClosure closure, long bytes)
        {
            Closure = closure;
            Bytes = bytes;
        }

        public DependencyClosure Closure { get; }
        public long Bytes { get; }

        public List<string> LeftOut { get; } = new List<string>();

        /// <summary>Dependencies dropped because only left-out vars used them.</summary>
        public List<string> AlsoLeftOut { get; } = new List<string>();

        /// <summary>Exclusions that stay linked, each with what needs it.</summary>
        public List<string> StillNeeded { get; } = new List<string>();

        /// <summary>Exclusions the default switch doesn't link anyway.</summary>
        public List<string> AlreadyOff { get; } = new List<string>();
    }

    public sealed class ProfileDiff
    {
        public List<string> Add { get; } = new List<string>();
        public List<string> Remove { get; } = new List<string>();
    }

    public sealed class ProfileHealth
    {
        public List<string> BrokenLinks { get; } = new List<string>();
        public List<string> NeededLinks { get; } = new List<string>();
        public List<string> UnusedLinks { get; } = new List<string>();
        public List<string> Missing { get; } = new List<string>();

        public bool CanRepair
        {
            get { return BrokenLinks.Count + NeededLinks.Count + UnusedLinks.Count > 0; }
        }
    }

    /// <summary>Plans, stores and applies lean profiles. A profile's switch folder holds only links.</summary>
    public static class LeanProfiles
    {
        private const string DirectPrefix = "direct\t";
        private const string BasePrefix = "base\t";
        private const string ExcludePrefix = "exclude\t";

        public static string ProfilePath(string varsPath, string name)
        {
            return Path.Combine(varsPath, LibraryFolders.Profiles, name + ".profile");
        }

        /// <summary>False for default, and for anything Windows won't take as a folder name.</summary>
        public static bool IsValidName(string name)
        {
            return name.Length > 0 && !name.EndsWith(".")
                && !string.Equals(name, VarSwitches.Default, StringComparison.OrdinalIgnoreCase)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !Regex.IsMatch(name, @"^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$", RegexOptions.IgnoreCase);
        }

        /// <summary>Packages VaM needs in every profile: session plugins, the default scene and base.txt.</summary>
        public static List<string> BaseReferences(string vamPath, string varsPath)
        {
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] sources =
            {
                Path.Combine(vamPath, "Custom", "PluginPresets", "Plugins_UserDefaults.vap"),
                Path.Combine(vamPath, "Saves", "scene", "MeshedVR", "default.json"),
            };
            foreach (string source in sources.Where(File.Exists))
                references.UnionWith(DependencyScanner.Extract(source, File.ReadAllText(source)));
            string baseList = Path.Combine(varsPath, LibraryFolders.Profiles, "base.txt");
            if (File.Exists(baseList))
                references.UnionWith(File.ReadAllLines(baseList).Select(line => line.Trim()).Where(line => line.Length > 0));
            return references.OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>What VaM loads on a switch, from the same places the installed scan reads.</summary>
        public static List<string> SwitchVars(string vamPath, string name)
        {
            string links = VarSwitches.LinkFolder(vamPath, name);
            return ActiveLinks(links, SearchOption.AllDirectories)
                .Concat(ActiveLinks(Path.GetDirectoryName(links), SearchOption.TopDirectoryOnly))
                .ToList();
        }

        public static ProfilePlan Plan(LeanProfile profile, VarIndex index,
            Func<string, IEnumerable<string>> referencesOf, Func<string, long> sizeOf)
        {
            DependencyClosure closure = DependencyScanner.Closure(profile.Direct.Concat(profile.Base), index, referencesOf);
            return new ProfilePlan(closure, closure.Vars.Sum(sizeOf));
        }

        /// <summary>
        /// Plans a profile as what the default switch links, minus the exclusions and the
        /// dependencies only they use. A var something else needs stays. References resolve
        /// among linked vars only, as VaM would see them, so disabled vars stay off.
        /// </summary>
        public static ProfilePlan PlanExcluding(LeanProfile profile, IEnumerable<string> linkedVars, VarIndex library,
            Func<string, IEnumerable<string>> referencesOf, Func<string, long> sizeOf)
        {
            HashSet<string> linked = new HashSet<string>(linkedVars, StringComparer.OrdinalIgnoreCase);
            VarIndex visible = new VarIndex(linked.SelectMany(ToEntry));
            HashSet<string> excluded = new HashSet<string>(profile.Exclude.Where(linked.Contains), StringComparer.OrdinalIgnoreCase);
            VarIndex remaining = new VarIndex(linked.Except(excluded, StringComparer.OrdinalIgnoreCase).SelectMany(ToEntry));
            // A reference moves off an exclusion only to a version that really satisfies it.
            Func<string, DependencyResolution> resolve = reference =>
            {
                DependencyResolution found = visible.ResolveReference(reference);
                if (found.Status != DependencyStatus.Latest || !excluded.Contains(found.VarName))
                    return found;
                DependencyResolution other = remaining.ResolveReference(reference);
                return other.Status == DependencyStatus.Latest ? other : found;
            };
            Func<IEnumerable<string>, IEnumerable<string>> resolved = references => references
                .Select(resolve)
                .Where(resolution => resolution.Status != DependencyStatus.Missing)
                .Select(resolution => resolution.VarName);
            Func<string, IEnumerable<string>> uses = name => resolved(referencesOf(name));

            // Leaving out one version says nothing about its package's other versions, and a
            // var naming its own package doesn't make the other versions its dependencies.
            HashSet<string> excludedPackages = new HashSet<string>(excluded.Select(PackageOf), StringComparer.OrdinalIgnoreCase);
            Func<string, IEnumerable<string>> usesOtherPackages = name => uses(name).Where(next => !excludedPackages.Contains(PackageOf(next))
                && !string.Equals(PackageOf(next), PackageOf(name), StringComparison.OrdinalIgnoreCase));
            List<string> related = Walk(excluded, usesOtherPackages).Keys.Where(name => !excluded.Contains(name)).ToList();
            IEnumerable<string> roots = linked.Except(excluded, StringComparer.OrdinalIgnoreCase)
                .Except(related, StringComparer.OrdinalIgnoreCase)
                .Concat(resolved(profile.Base));
            Dictionary<string, string> wanted = Walk(roots, uses);

            DependencyClosure closure = new DependencyClosure();
            closure.Vars.AddRange(wanted.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            foreach (string reference in profile.Base.Concat(closure.Vars.SelectMany(referencesOf)))
            {
                DependencyResolution resolution = resolve(reference);
                if (resolution.Status == DependencyStatus.Fallback)
                    closure.Substitutions[reference] = resolution.VarName;
                else if (resolution.Status == DependencyStatus.Missing && library.ResolveReference(reference).Status == DependencyStatus.Missing)
                    closure.Missing.Add(reference);
            }

            ProfilePlan plan = new ProfilePlan(closure, closure.Vars.Sum(sizeOf));
            plan.LeftOut.AddRange(excluded.Where(name => !wanted.ContainsKey(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            plan.AlsoLeftOut.AddRange(related.Where(name => !wanted.ContainsKey(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            plan.StillNeeded.AddRange(excluded.Where(wanted.ContainsKey).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => name + " needed by " + (wanted[name] ?? "the base set")));
            plan.AlreadyOff.AddRange(profile.Exclude.Where(name => !linked.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return plan;
        }

        private static string PackageOf(string varName)
        {
            int versionDot = varName.LastIndexOf('.');
            return versionDot < 0 ? varName : varName.Substring(0, versionDot);
        }

        internal static IEnumerable<VarEntry> ToEntry(string varName)
        {
            string[] parts = varName.Split('.');
            int version;
            if (parts.Length == 3 && int.TryParse(parts[2], out version))
                yield return new VarEntry(varName, parts[0], parts[1], version);
        }

        /// <summary>Everything reachable from the starts, mapped to the var that first reached it.</summary>
        private static Dictionary<string, string> Walk(IEnumerable<string> starts, Func<string, IEnumerable<string>> uses)
        {
            Dictionary<string, string> reachedFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new Stack<string>();
            foreach (string start in starts)
            {
                if (!reachedFrom.ContainsKey(start))
                {
                    reachedFrom.Add(start, null);
                    pending.Push(start);
                }
            }
            while (pending.Count > 0)
            {
                string name = pending.Pop();
                foreach (string next in uses(name))
                {
                    if (!reachedFrom.ContainsKey(next))
                    {
                        reachedFrom.Add(next, name);
                        pending.Push(next);
                    }
                }
            }
            return reachedFrom;
        }

        public static ProfileDiff Diff(IEnumerable<string> wanted, IEnumerable<string> linked)
        {
            HashSet<string> wantedSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            HashSet<string> linkedSet = new HashSet<string>(linked, StringComparer.OrdinalIgnoreCase);
            ProfileDiff diff = new ProfileDiff();
            diff.Add.AddRange(wantedSet.Where(name => !linkedSet.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            diff.Remove.AddRange(linkedSet.Where(name => !wantedSet.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return diff;
        }

        /// <param name="linkDirectory">The profile's ___VarsLink___ folder.</param>
        public static ProfileHealth Check(ProfilePlan plan, string linkDirectory)
        {
            ProfileHealth health = new ProfileHealth();
            List<string> linked = new List<string>();
            foreach (string link in Links(linkDirectory))
            {
                string name = Path.GetFileNameWithoutExtension(link);
                linked.Add(name);
                if (!LinkResolves(link))
                    health.BrokenLinks.Add(name);
            }
            ProfileDiff diff = Diff(plan.Closure.Vars, linked);
            health.NeededLinks.AddRange(diff.Add);
            health.UnusedLinks.AddRange(diff.Remove);
            health.Missing.AddRange(plan.Closure.Missing);
            return health;
        }

        /// <summary>File.Exists reports a broken link as present, so open it to reach the target.</summary>
        public static bool LinkResolves(string link)
        {
            try
            {
                using (new FileStream(link, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return false;
            }
        }

        /// <summary>Lists var links, including broken ones, and ignores real files.</summary>
        public static IEnumerable<string> Links(string linkDirectory)
        {
            if (!Directory.Exists(linkDirectory))
                return Enumerable.Empty<string>();
            return Directory.GetFiles(linkDirectory, "*.var")
                .Where(path => new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        /// <summary>Var names linked here that VaM would load: not broken, and not turned off with a .disabled file.</summary>
        public static IEnumerable<string> ActiveLinks(string directory, SearchOption scope)
        {
            if (!Directory.Exists(directory))
                return Enumerable.Empty<string>();
            return Directory.GetFiles(directory, "*.var", scope)
                .Where(path => new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && LinkResolves(path) && !File.Exists(path + ".disabled"))
                .Select(Path.GetFileNameWithoutExtension);
        }

        /// <summary>
        /// Makes the folder hold exactly the wanted links. Broken links are replaced. Only links
        /// are ever deleted, so var files can't be lost.
        /// </summary>
        public static void ApplyLinks(IEnumerable<string> wanted, string linkDirectory,
            Func<string, string> targetOf, Func<string, string, bool> createLink)
        {
            Directory.CreateDirectory(linkDirectory);
            foreach (string link in Links(linkDirectory).Where(path => !LinkResolves(path)).ToList())
                File.Delete(link);

            ProfileDiff diff = Diff(wanted, Links(linkDirectory).Select(Path.GetFileNameWithoutExtension));
            foreach (string name in diff.Add)
            {
                string link = Path.Combine(linkDirectory, name + ".var");
                if (!createLink(link, targetOf(name)))
                    throw new IOException("Could not create link: " + link);
            }
            foreach (string name in diff.Remove)
                File.Delete(Path.Combine(linkDirectory, name + ".var"));
        }

        /// <summary>Saves a profile, keeping the prior version for Restore.</summary>
        public static void Save(LeanProfile profile, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Sorted, because selection order says nothing about the profile.
            string[] lines = profile.Direct.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => DirectPrefix + name)
                .Concat(profile.Base.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => BasePrefix + name))
                .Concat(profile.Exclude.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => ExcludePrefix + name)).ToArray();
            if (File.Exists(path))
            {
                // Saving the same picks again must not overwrite the real restore point.
                if (File.ReadAllLines(path).SequenceEqual(lines))
                    return;
                File.Copy(path, PreviousPath(path), true);
            }
            File.WriteAllLines(path, lines);
        }

        /// <summary>Deletes a profile and its prior version, if present.</summary>
        public static void DeleteFiles(string path)
        {
            foreach (string file in new[] { path, PreviousPath(path) })
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
        }

        /// <summary>Moves a profile and its prior version, replacing leftovers at the new name.</summary>
        public static void RenameFiles(string oldPath, string newPath)
        {
            DeleteFiles(newPath);
            if (File.Exists(oldPath))
                File.Move(oldPath, newPath);
            if (File.Exists(PreviousPath(oldPath)))
                File.Move(PreviousPath(oldPath), PreviousPath(newPath));
        }

        public static string PreviousPath(string path)
        {
            return path + ".previous";
        }

        public static LeanProfile Load(string path)
        {
            LeanProfile profile = new LeanProfile();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith(DirectPrefix, StringComparison.Ordinal))
                    profile.Direct.Add(line.Substring(DirectPrefix.Length));
                else if (line.StartsWith(BasePrefix, StringComparison.Ordinal))
                    profile.Base.Add(line.Substring(BasePrefix.Length));
                else if (line.StartsWith(ExcludePrefix, StringComparison.Ordinal))
                    profile.Exclude.Add(line.Substring(ExcludePrefix.Length));
            }
            return profile;
        }

        public static string Describe(ProfilePlan plan, ProfileDiff diff)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine($"Vars in this profile: {plan.Closure.Vars.Count} ({plan.Bytes / 1073741824.0:F2} GB)");
            text.AppendLine($"Links to add: {diff.Add.Count}, links to remove: {diff.Remove.Count}");
            text.AppendLine($"Missing packages: {plan.Closure.Missing.Count}");
            text.AppendLine($"Using a different version: {plan.Closure.Substitutions.Count}");
            text.AppendLine();
            AppendList(text, "Left out", plan.LeftOut);
            AppendList(text, "Also left out, only used by what you left out", plan.AlsoLeftOut);
            AppendList(text, "Staying, because something needs them", plan.StillNeeded);
            AppendList(text, "Already off in default", plan.AlreadyOff);
            AppendList(text, "Missing", plan.Closure.Missing);
            AppendList(text, "Different version", plan.Closure.Substitutions.Select(s => s.Key + " uses " + s.Value));
            if (plan.LeftOut.Count + plan.StillNeeded.Count + plan.AlreadyOff.Count == 0)
                AppendList(text, "Vars", plan.Closure.Vars.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return text.ToString();
        }

        public static string Describe(ProfileHealth health)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(health.CanRepair || health.Missing.Count > 0 ? "Problems found." : "This profile is healthy.");
            if (health.CanRepair)
                text.AppendLine("Repair makes the links match the saved profile. It undoes links you installed or removed by hand.");
            text.AppendLine();
            AppendList(text, "Broken links", health.BrokenLinks);
            AppendList(text, "Needed but not linked", health.NeededLinks);
            AppendList(text, "Linked but no longer needed", health.UnusedLinks);
            AppendList(text, "Missing packages (download these)", health.Missing);
            return text.ToString();
        }

        private static void AppendList(StringBuilder text, string title, IEnumerable<string> items)
        {
            List<string> values = items.ToList();
            if (values.Count == 0)
                return;
            text.AppendLine(title + ":");
            foreach (string value in values)
                text.AppendLine("  " + value);
            text.AppendLine();
        }
    }
}
