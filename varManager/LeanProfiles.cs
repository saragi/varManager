using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace varManager
{
    /// <summary>What the user chose. Everything else in a profile is worked out from this.</summary>
    public sealed class LeanProfile
    {
        public List<string> Direct { get; } = new List<string>();
        public List<string> Base { get; } = new List<string>();
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

        public static ProfilePlan Plan(LeanProfile profile, VarIndex index,
            Func<string, IEnumerable<string>> referencesOf, Func<string, long> sizeOf)
        {
            DependencyClosure closure = DependencyScanner.Closure(profile.Direct.Concat(profile.Base), index, referencesOf);
            return new ProfilePlan(closure, closure.Vars.Sum(sizeOf));
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
        private static bool LinkResolves(string link)
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
                .Concat(profile.Base.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Select(name => BasePrefix + name)).ToArray();
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
            AppendList(text, "Missing", plan.Closure.Missing);
            AppendList(text, "Different version", plan.Closure.Substitutions.Select(s => s.Key + " uses " + s.Value));
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
