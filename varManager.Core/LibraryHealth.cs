using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace varManager
{
    public sealed class HeldFile
    {
        public string Path { get; set; }
        public long Bytes { get; set; }

        /// <summary>Whether the library still has a var of this name, so this file is a spare copy.</summary>
        public bool InLibrary { get; set; }
    }

    /// <summary>Files set aside in one folder, which only ever go after the user has reviewed them.</summary>
    public sealed class HeldGroup
    {
        public string Title { get; set; }
        public string Folder { get; set; }
        public List<HeldFile> Files { get; } = new List<HeldFile>();

        public long Bytes => Files.Sum(file => file.Bytes);
        public int NotInLibrary => Files.Count(file => IsVar(file) && !file.InLibrary);

        private static bool IsVar(HeldFile file)
        {
            return file.Path.EndsWith(".var", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class MissingNeed
    {
        public string Reference { get; set; }
        public int NeededBy { get; set; }
        public string Example { get; set; }
    }

    /// <summary>What's worth knowing about the library. Reading this changes nothing.</summary>
    public sealed class HealthReport
    {
        public int VarCount { get; set; }
        public long LibraryBytes { get; set; }
        public string Drive { get; set; }
        public long? FreeBytes { get; set; }
        public int NotLinked { get; set; }
        public List<string> Stale { get; } = new List<string>();
        public long StaleBytes { get; set; }

        /// <summary>Stale vars some profile links now, which moving turns off.</summary>
        public HashSet<string> StaleLinked { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<HeldGroup> Held { get; } = new List<HeldGroup>();
        public List<string> BrokenLinks { get; } = new List<string>();
        public List<string> GoneRecords { get; } = new List<string>();
        public List<MissingNeed> Missing { get; } = new List<MissingNeed>();
        public int MissingPreviews { get; set; }
        public long CacheBytes { get; set; }
    }

    public static class LibraryHealth
    {
        private const string HoldingLog = "moved.txt";

        private static readonly KeyValuePair<string, string>[] HeldFolders =
        {
            new KeyValuePair<string, string>(LibraryFolders.Redundant, "Duplicates set aside"),
            new KeyValuePair<string, string>(LibraryFolders.Holding, "Moved out of the library"),
            new KeyValuePair<string, string>(LibraryFolders.StaleVars, "Stale vars set aside"),
            new KeyValuePair<string, string>(LibraryFolders.OldVersionVars, "Old versions set aside"),
            new KeyValuePair<string, string>(LibraryFolders.DeletedVars, "Deleted vars"),
        };

        public static HealthReport Load(LibraryStore store, LibraryPaths paths, Action<string> problem)
        {
            IReadOnlyList<VarRecord> records = store.LoadVars();
            List<KeyValuePair<string, string>> dependencies = store.LoadDependencies().ToList();
            var vars = new VarIndex(records.Select(Entry));
            var report = new HealthReport
            {
                VarCount = records.Count,
                LibraryBytes = records.Sum(v => (long)(v.FileSize ?? 0)),
            };

            ReadDrive(paths.VarsPath, report);
            var linked = new HashSet<string>(LeanProfiles.SwitchVars(paths.VamPath, VarSwitches.Default), StringComparer.OrdinalIgnoreCase);
            report.NotLinked = records.Count(v => !linked.Contains(v.VarName));

            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> present = LibraryUpdate.TidiedVars(paths);
            report.Stale.AddRange(StaleVars(records, vars, dependencies, paths, present, problem));
            report.StaleBytes = report.Stale.Sum(name => (long)(byName[name].FileSize ?? 0));
            var linkedSomewhere = new HashSet<string>(SwitchFolders(paths.VamPath).SelectMany(folder => VarLinks.ReadInstalled(folder).Keys), StringComparer.OrdinalIgnoreCase);
            report.StaleLinked.UnionWith(report.Stale.Where(linkedSomewhere.Contains));

            foreach (KeyValuePair<string, string> folder in HeldFolders)
            {
                HeldGroup group = ReadHeld(Path.Combine(paths.VarsPath, folder.Key), folder.Value, paths, byName, present);
                if (group.Files.Count > 0)
                    report.Held.Add(group);
            }

            report.BrokenLinks.AddRange(VarLinks.ReadInstalled(paths.AddonPackages).Values.Where(link => !LeanProfiles.LinkResolves(link)).OrderBy(link => link, StringComparer.OrdinalIgnoreCase));
            // No vars at all means the folder can't be reached, which says nothing about any one var.
            if (present.Count > 0)
                report.GoneRecords.AddRange(records.Select(v => v.VarName).Where(name => !present.ContainsKey(name)));
            report.Missing.AddRange(MissingNeeds(vars, dependencies));
            // Only what a repair can bring back: a var that's gone has nothing to take the picture from.
            report.MissingPreviews = store.LoadPreviews()
                .Where(preview => present.ContainsKey(preview.VarName))
                .Select(preview => ScenePreviews.PicturePath(paths.VarsPath, preview.Type, preview.VarName, preview.Picture))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(picture => !File.Exists(picture));
            report.CacheBytes = CacheSize();
            return report;
        }

        // A load or a clear can be changing the cache, and a size isn't worth failing the whole report.
        private static long CacheSize()
        {
            try
            {
                return Directory.Exists(SceneAnalysis.CacheRoot)
                    ? new DirectoryInfo(SceneAnalysis.CacheRoot).GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                    : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        // VarSwitches.Names would create the default switch, and reading must change nothing.
        private static IEnumerable<string> SwitchFolders(string vamPath)
        {
            string root = Path.Combine(vamPath, LibraryFolders.AddonPacksSwitch);
            return Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0];
        }

        /// <summary>
        /// Old versions that no var, profile, base.txt or save needs, in packages whose newest file is really
        /// there. With a save unreadable nobody can say what's needed, so nothing counts as stale.
        /// </summary>
        private static IEnumerable<string> StaleVars(IReadOnlyList<VarRecord> records, VarIndex vars,
            List<KeyValuePair<string, string>> dependencies, LibraryPaths paths, Dictionary<string, string> present, Action<string> problem)
        {
            List<VarEntry> entries = records.Select(Entry).ToList();
            List<string> old = DependencyAnalysis.OldVersions(entries, DependencyAnalysis.LatestVersions(entries));
            var usage = new LibraryUsage(vars, dependencies);
            bool unreadable = false;
            HashSet<string> referenced = MoveOut.ReferencedOutsideVars(paths, vars, text =>
            {
                unreadable = true;
                problem(text);
            });
            if (unreadable)
                return new string[0];

            Dictionary<string, int> latest = DependencyAnalysis.LatestVersions(entries);
            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            Func<string, bool> newestIsThere = name =>
            {
                string package = name.Substring(0, name.LastIndexOf('.'));
                VarRecord newest;
                return byName.TryGetValue(package + "." + latest[package], out newest) && Locate(paths, newest, present) != null;
            };
            return old.Where(name => !usage.NeedersOf(name).Any() && !referenced.Contains(name) && newestIsThere(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<MissingNeed> MissingNeeds(VarIndex vars, List<KeyValuePair<string, string>> dependencies)
        {
            return dependencies
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .Where(group => DependencyAnalysis.Resolve(new[] { group.Key }, vars)[0].VarName == null)
                .Select(group => new MissingNeed
                {
                    Reference = group.Key,
                    NeededBy = group.Select(pair => pair.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Example = group.First().Key,
                })
                .OrderByDescending(need => need.NeededBy)
                .ThenBy(need => need.Reference, StringComparer.OrdinalIgnoreCase);
        }

        private static void ReadDrive(string varsPath, HealthReport report)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(varsPath)));
                report.Drive = drive.Name.TrimEnd('\\');
                report.FreeBytes = drive.AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                // A network path has no drive to ask, and the rest of the report still stands.
            }
        }

        private static HeldGroup ReadHeld(string folder, string title, LibraryPaths paths, Dictionary<string, VarRecord> byName, Dictionary<string, string> present)
        {
            var group = new HeldGroup { Title = title, Folder = folder };
            // A link here leads somewhere else, and what's there isn't this app's to delete.
            if (!Directory.Exists(folder) || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return group;
            foreach (FileInfo file in RealFiles(new DirectoryInfo(folder)))
            {
                if (string.Equals(file.Name, HoldingLog, StringComparison.OrdinalIgnoreCase))
                    continue;
                VarRecord record;
                group.Files.Add(new HeldFile
                {
                    Path = file.FullName,
                    Bytes = file.Length,
                    // A record alone isn't a copy: its file may be the one that's gone.
                    InLibrary = byName.TryGetValue(Path.GetFileNameWithoutExtension(file.Name), out record) && Locate(paths, record, present) != null,
                });
            }
            group.Files.Sort((first, second) => string.Compare(first.Path, second.Path, StringComparison.OrdinalIgnoreCase));
            return group;
        }

        // Walks by hand, so a folder link can't lead to files outside the group.
        private static IEnumerable<FileInfo> RealFiles(DirectoryInfo folder)
        {
            IEnumerable<FileInfo> files = folder.GetFiles();
            foreach (DirectoryInfo child in folder.GetDirectories().Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                files = files.Concat(RealFiles(child));
            return files;
        }

        /// <summary>Deletes exactly the files reviewed, for good. Anything that arrived since then stays.</summary>
        /// <returns>How many went.</returns>
        public static int DeleteHeld(HeldGroup group)
        {
            int deleted = 0;
            foreach (HeldFile file in group.Files)
            {
                if (File.Exists(file.Path))
                {
                    File.Delete(file.Path);
                    deleted++;
                }
            }
            if (Directory.Exists(group.Folder))
                RemoveEmpty(group.Folder, group.Folder);
            return deleted;
        }

        // A holding folder's log only means something beside its files.
        private static void RemoveEmpty(string folder, string root)
        {
            foreach (DirectoryInfo child in new DirectoryInfo(folder).GetDirectories().Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                RemoveEmpty(child.FullName, root);
            string log = Path.Combine(folder, HoldingLog);
            if (File.Exists(log) && Directory.GetFileSystemEntries(folder).Length == 1)
                File.Delete(log);
            if (folder != root && Directory.GetFileSystemEntries(folder).Length == 0)
                Directory.Delete(folder);
        }

        /// <summary>Moves stale vars to a holding folder that can be undone, if the list still holds.</summary>
        /// <returns>The holding folder, or null if the library changed since the list was shown.</returns>
        public static string MoveStale(LibraryStore store, LibraryPaths paths, IReadOnlyCollection<string> reviewed)
        {
            IReadOnlyList<VarRecord> records = store.LoadVars();
            var vars = new VarIndex(records.Select(Entry));
            Dictionary<string, string> present = LibraryUpdate.TidiedVars(paths);
            List<string> now = StaleVars(records, vars, store.LoadDependencies().ToList(), paths, present, text => { }).ToList();
            if (!new HashSet<string>(now, StringComparer.OrdinalIgnoreCase).SetEquals(reviewed))
                return null;

            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            var plan = new MovePlan { Target = "stale vars" };
            plan.Moves.AddRange(now);
            string holding = MoveOut.Apply(plan, name => Locate(paths, byName[name], present) ?? FileOf(paths, byName[name]), paths);
            store.DeleteVars(plan.Moves);
            return holding;
        }

        /// <summary>The scene cache only saves time, and each scene rebuilds its own on the next load.</summary>
        public static void ClearSceneCache()
        {
            try
            {
                Directory.Delete(SceneAnalysis.CacheRoot, true);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone is what was asked for.
            }
        }

        /// <summary>Points broken links at where their var is now. A link to a var the library lost goes.</summary>
        /// <param name="links">The links reviewed. Any that isn't a broken link by now is left alone.</param>
        /// <returns>How many links were dealt with.</returns>
        public static int RepairLinks(LibraryStore store, LibraryPaths paths, IEnumerable<string> links)
        {
            Dictionary<string, VarRecord> byName = store.LoadVars().ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> present = LibraryUpdate.TidiedVars(paths);
            int repaired = 0;
            foreach (string link in links.Where(path => File.Exists(path) && VarLinks.IsLink(path) && !LeanProfiles.LinkResolves(path)))
            {
                VarLinks.Rebuild(link, name =>
                {
                    VarRecord record;
                    return byName.TryGetValue(name, out record) ? Locate(paths, record, present) : null;
                });
                // Only an empty marker: a .disabled file can hold a whole var.
                var marker = new FileInfo(link + ".disabled");
                if (!File.Exists(link) && marker.Exists && marker.Length == 0)
                    marker.Delete();
                repaired++;
            }
            return repaired;
        }

        // A var can sit elsewhere in the tidied folder than its record says, and Update DB only notices a changed size.
        private static string Locate(LibraryPaths paths, VarRecord record, Dictionary<string, string> present)
        {
            string file = FileOf(paths, record);
            if (File.Exists(file))
                return file;
            return present.TryGetValue(record.VarName, out file) ? file : null;
        }

        private static VarEntry Entry(VarRecord record)
        {
            return new VarEntry(record.VarName, record.CreatorName, record.PackageName, record.Version ?? 0);
        }

        private static string FileOf(LibraryPaths paths, VarRecord record)
        {
            return Path.Combine(paths.VarsPath, record.VarPath ?? "", record.VarName + ".var");
        }
    }
}
