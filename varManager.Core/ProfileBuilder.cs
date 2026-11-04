using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimpleJSON;

namespace varManager
{
    public enum ProfileKind
    {
        Plain,
        Lean,
        Exclude
    }

    /// <summary>What the last launch from this app measured for a profile.</summary>
    public sealed class ProfileTiming
    {
        public double StartupSeconds { get; set; }
        public long RamBytes { get; set; }
    }

    /// <summary>One profile, as the Profiles screen shows it.</summary>
    public sealed class ProfileSummary
    {
        public string Name { get; set; }
        public int LinkCount { get; set; }
        public ProfileKind Kind { get; set; }

        /// <summary>Direct picks of a lean profile, or exclusions of an exclude profile.</summary>
        public int PickCount { get; set; }

        public ProfileTiming Timing { get; set; }
    }

    public sealed class ProfileCheck
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public bool CanRepair { get; set; }
        public bool CanRestore { get; set; }
    }

    public static class ProfileCatalog
    {
        public static List<ProfileSummary> Load(string vamPath, string varsPath)
        {
            Dictionary<string, ProfileTiming> timings = ProfileTimings.Load(varsPath);
            var profiles = new List<ProfileSummary>();
            foreach (string name in VarSwitches.Names(vamPath))
            {
                string file = LeanProfiles.ProfilePath(varsPath, name);
                LeanProfile saved = File.Exists(file) ? LeanProfiles.Load(file) : new LeanProfile();
                ProfileTiming timing;
                timings.TryGetValue(name, out timing);
                profiles.Add(new ProfileSummary
                {
                    Name = name,
                    LinkCount = CountLinks(vamPath, name),
                    Kind = name == VarSwitches.Default ? ProfileKind.Plain
                        : saved.Exclude.Count > 0 ? ProfileKind.Exclude
                        : saved.Direct.Count > 0 ? ProfileKind.Lean
                        : ProfileKind.Plain,
                    PickCount = saved.Exclude.Count > 0 ? saved.Exclude.Count : saved.Direct.Count,
                    Timing = timing,
                });
            }
            return profiles;
        }

        private static int CountLinks(string vamPath, string name)
        {
            string links = VarSwitches.LinkFolder(vamPath, name);
            string top = Path.GetDirectoryName(links);
            IEnumerable<string> files = Directory.GetFiles(top, "*.var", SearchOption.TopDirectoryOnly);
            if (Directory.Exists(links))
                files = files.Concat(Directory.GetFiles(links, "*.var", SearchOption.AllDirectories));
            return files.Count(file => new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        /// <summary>Removes a profile's links and saved picks. Anything real in its folder stays, and so does the folder then.</summary>
        /// <returns>How many real files stayed.</returns>
        public static int Delete(string vamPath, string varsPath, string name)
        {
            int kept = RemoveLinks(new DirectoryInfo(Path.GetDirectoryName(VarSwitches.LinkFolder(vamPath, name))));
            LeanProfiles.DeleteFiles(LeanProfiles.ProfilePath(varsPath, name));
            ProfileTimings.Remove(varsPath, name);
            return kept;
        }

        // Walks by hand, so a folder link is removed and never followed.
        private static int RemoveLinks(DirectoryInfo folder)
        {
            int kept = 0;
            foreach (FileInfo file in folder.GetFiles())
            {
                bool marker = file.Length == 0 && file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (marker || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    file.Delete();
                else
                    kept++;
            }
            foreach (DirectoryInfo child in folder.GetDirectories())
            {
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    child.Delete();
                else
                    kept += RemoveLinks(child);
            }
            if (kept == 0 && !folder.EnumerateFileSystemInfos().Any())
                folder.Delete();
            return kept;
        }

        public static void Rename(string vamPath, string varsPath, string oldName, string newName)
        {
            VarSwitches.Rename(vamPath, oldName, newName);
            LeanProfiles.RenameFiles(LeanProfiles.ProfilePath(varsPath, oldName), LeanProfiles.ProfilePath(varsPath, newName));
            ProfileTimings.Rename(varsPath, oldName, newName);
        }
    }

    /// <summary>Plans profiles from the database, without opening var files, and applies them as links.</summary>
    public sealed class ProfileBuilder
    {
        private readonly LibraryPaths paths;
        private readonly Dictionary<string, VarRecord> byName;
        private readonly VarIndex vars;
        private readonly DependencyIndex dependencies;

        public ProfileBuilder(LibraryStore store, LibraryPaths paths)
        {
            this.paths = paths;
            IReadOnlyList<VarRecord> records = store.LoadVars();
            byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
            dependencies = new DependencyIndex(store.LoadDependencies());
        }

        public LeanProfile Lean(IEnumerable<string> picks)
        {
            LeanProfile profile = new LeanProfile();
            profile.Direct.AddRange(picks);
            profile.Base.AddRange(LeanProfiles.BaseReferences(paths.VamPath, paths.VarsPath));
            return profile;
        }

        public LeanProfile Excluding(IEnumerable<string> picks)
        {
            LeanProfile profile = new LeanProfile();
            profile.Exclude.AddRange(picks);
            profile.Base.AddRange(LeanProfiles.BaseReferences(paths.VamPath, paths.VarsPath));
            return profile;
        }

        public ProfilePlan Plan(LeanProfile profile)
        {
            Func<string, IEnumerable<string>> referencesOf = name => dependencies.DependenciesOf(name);
            if (profile.Exclude.Count == 0)
                return LeanProfiles.Plan(profile, vars, referencesOf, SizeOf);
            List<string> linked = LeanProfiles.SwitchVars(paths.VamPath, VarSwitches.Default).Where(vars.Contains).ToList();
            return LeanProfiles.PlanExcluding(profile, linked, vars, referencesOf, SizeOf);
        }

        public string Preview(string name, ProfilePlan plan)
        {
            return LeanProfiles.Describe(plan, LeanProfiles.Diff(plan.Closure.Vars, LinkedIn(name)));
        }

        /// <summary>Links first, so a failure leaves the saved profile describing the last good state.</summary>
        /// <returns>The profile's name as it is on disk, because names ignore case.</returns>
        public string Apply(string name, ProfilePlan plan, LeanProfile toSave)
        {
            string existing = VarSwitches.Names(paths.VamPath)
                .FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            string switchName = existing ?? name;
            string linkFolder = VarSwitches.LinkFolder(paths.VamPath, switchName);
            ProfileDiff change = LeanProfiles.Diff(plan.Closure.Vars, LinkedIn(switchName));
            try
            {
                LeanProfiles.ApplyLinks(plan.Closure.Vars, linkFolder, FileOf, VarLinks.Create);
            }
            catch (Exception)
            {
                if (existing == null && !LeanProfiles.Links(linkFolder).Any())
                    VarSwitches.Delete(paths.VamPath, switchName);
                throw;
            }
            if (toSave != null)
                LeanProfiles.Save(toSave, LeanProfiles.ProfilePath(paths.VarsPath, switchName));
            // A timing describes the links it was measured with.
            if (change.Add.Count + change.Remove.Count > 0)
                ProfileTimings.Remove(paths.VarsPath, switchName);
            return switchName;
        }

        public ProfileCheck Check(string name)
        {
            string file = LeanProfiles.ProfilePath(paths.VarsPath, name);
            ProfileHealth health = LeanProfiles.Check(Plan(LeanProfiles.Load(file)), VarSwitches.LinkFolder(paths.VamPath, name));
            return new ProfileCheck
            {
                Name = name,
                Text = LeanProfiles.Describe(health),
                CanRepair = health.CanRepair,
                CanRestore = File.Exists(LeanProfiles.PreviousPath(file)),
            };
        }

        /// <summary>Makes the links match the saved profile again.</summary>
        public void Repair(string name)
        {
            Apply(name, Plan(LeanProfiles.Load(LeanProfiles.ProfilePath(paths.VarsPath, name))), null);
        }

        /// <summary>Goes back to the version saved before the last change.</summary>
        public void Restore(string name)
        {
            LeanProfile previous = LeanProfiles.Load(LeanProfiles.PreviousPath(LeanProfiles.ProfilePath(paths.VarsPath, name)));
            Apply(name, Plan(previous), previous);
        }

        private IEnumerable<string> LinkedIn(string name)
        {
            return LeanProfiles.Links(VarSwitches.LinkFolder(paths.VamPath, name)).Select(Path.GetFileNameWithoutExtension);
        }

        private long SizeOf(string name)
        {
            VarRecord record;
            return byName.TryGetValue(name, out record) ? (long)(record.FileSize ?? 0) : 0;
        }

        private string FileOf(string name)
        {
            VarRecord record = byName[name];
            return Path.Combine(paths.VarsPath, record.VarPath ?? "", record.VarName + ".var");
        }
    }

    /// <summary>Keeps the last measured startup of each profile, beside the saved profiles.</summary>
    public static class ProfileTimings
    {
        private const string FileName = "timings.txt";

        public static Dictionary<string, ProfileTiming> Load(string varsPath)
        {
            var timings = new Dictionary<string, ProfileTiming>(StringComparer.OrdinalIgnoreCase);
            string file = PathOf(varsPath);
            if (!File.Exists(file))
                return timings;
            foreach (string line in File.ReadAllLines(file))
            {
                string[] parts = line.Split('\t');
                double seconds;
                long bytes;
                if (parts.Length == 3
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                    && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes))
                    timings[parts[0]] = new ProfileTiming { StartupSeconds = seconds, RamBytes = bytes };
            }
            return timings;
        }

        public static void Record(string varsPath, string name, TimeSpan startup, long ramBytes)
        {
            Dictionary<string, ProfileTiming> timings = Load(varsPath);
            timings[name] = new ProfileTiming { StartupSeconds = startup.TotalSeconds, RamBytes = ramBytes };
            Save(varsPath, timings);
        }

        public static void Remove(string varsPath, string name)
        {
            Dictionary<string, ProfileTiming> timings = Load(varsPath);
            if (timings.Remove(name))
                Save(varsPath, timings);
        }

        public static void Rename(string varsPath, string oldName, string newName)
        {
            Dictionary<string, ProfileTiming> timings = Load(varsPath);
            ProfileTiming timing;
            if (!timings.TryGetValue(oldName, out timing))
                return;
            timings.Remove(oldName);
            timings[newName] = timing;
            Save(varsPath, timings);
        }

        private static string PathOf(string varsPath)
        {
            return Path.Combine(varsPath, LibraryFolders.Profiles, FileName);
        }

        private static void Save(string varsPath, Dictionary<string, ProfileTiming> timings)
        {
            string file = PathOf(varsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllLines(file, timings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => string.Join("\t",
                pair.Key,
                pair.Value.StartupSeconds.ToString("R", CultureInfo.InvariantCulture),
                pair.Value.RamBytes.ToString(CultureInfo.InvariantCulture))));
        }
    }

    public sealed class StartupReading
    {
        public TimeSpan Startup { get; set; }
        public long RamBytes { get; set; }
    }

    /// <summary>
    /// Times a VaM launch. The plugin takes a request as soon as VaM has started and its session
    /// plugins run, so an empty request marks that moment. Without the plugin nothing is measured.
    /// </summary>
    public static class StartupProbe
    {
        /// <summary>Call this right after starting VaM.</summary>
        /// <param name="memory">VaM's memory now, or 0 if it isn't running.</param>
        /// <returns>The reading, or null if VaM never took the request.</returns>
        public static async Task<StartupReading> Measure(string vamPath, Func<long> memory, TimeSpan poll, TimeSpan limit, CancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            string request = VamBridge.RequestFile(vamPath);
            string written = null;
            if (!File.Exists(request))
            {
                JSONClass empty = new JSONClass();
                empty["rescan"] = "false";
                VamBridge.WriteRequest(vamPath, empty);
                written = File.ReadAllText(request);
            }

            bool seenRunning = false;
            while (File.Exists(request))
            {
                bool running = memory() > 0;
                seenRunning |= running;
                if (clock.Elapsed >= limit || (seenRunning && !running))
                {
                    RemoveOwn(request, written);
                    return null;
                }
                await Task.Delay(poll, token).ConfigureAwait(false);
            }

            TimeSpan startup = clock.Elapsed;
            long ram = memory();
            return ram > 0 ? new StartupReading { Startup = startup, RamBytes = ram } : null;
        }

        // Something else may have replaced the request since, and that one isn't ours to delete.
        private static void RemoveOwn(string request, string written)
        {
            if (written != null && File.Exists(request) && File.ReadAllText(request) == written)
                File.Delete(request);
        }

        /// <summary>The memory VaM holds now, or 0 if it isn't running.</summary>
        public static long VamMemory()
        {
            long total = 0;
            foreach (Process process in Process.GetProcessesByName("vam"))
            {
                using (process)
                {
                    try
                    {
                        total += process.WorkingSet64;
                    }
                    catch (InvalidOperationException)
                    {
                        // It closed between listing and reading.
                    }
                }
            }
            return total;
        }
    }
}
