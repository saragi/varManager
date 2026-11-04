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
        public List<HeldGroup> Held { get; } = new List<HeldGroup>();
        public List<string> BrokenLinks { get; } = new List<string>();
        public List<string> GoneRecords { get; } = new List<string>();
        public List<MissingNeed> Missing { get; } = new List<MissingNeed>();
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
            var names = new HashSet<string>(records.Select(v => v.VarName), StringComparer.OrdinalIgnoreCase);
            var report = new HealthReport
            {
                VarCount = records.Count,
                LibraryBytes = records.Sum(v => (long)(v.FileSize ?? 0)),
            };

            ReadDrive(paths.VarsPath, report);
            var linked = new HashSet<string>(LeanProfiles.SwitchVars(paths.VamPath, VarSwitches.Default), StringComparer.OrdinalIgnoreCase);
            report.NotLinked = records.Count(v => !linked.Contains(v.VarName));

            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            report.Stale.AddRange(StaleVars(records, vars, dependencies, paths, problem));
            report.StaleBytes = report.Stale.Sum(name => (long)(byName[name].FileSize ?? 0));

            foreach (KeyValuePair<string, string> folder in HeldFolders)
            {
                HeldGroup group = ReadHeld(Path.Combine(paths.VarsPath, folder.Key), folder.Value, names);
                if (group.Files.Count > 0)
                    report.Held.Add(group);
            }

            report.BrokenLinks.AddRange(VarLinks.ReadInstalled(paths.AddonPackages).Values.Where(link => !LeanProfiles.LinkResolves(link)).OrderBy(link => link, StringComparer.OrdinalIgnoreCase));
            report.GoneRecords.AddRange(records.Where(v => !File.Exists(FileOf(paths, v))).Select(v => v.VarName));
            report.Missing.AddRange(MissingNeeds(vars, dependencies));
            return report;
        }

        /// <summary>Old versions that no var, profile, base.txt or save needs.</summary>
        private static IEnumerable<string> StaleVars(IReadOnlyList<VarRecord> records, VarIndex vars,
            List<KeyValuePair<string, string>> dependencies, LibraryPaths paths, Action<string> problem)
        {
            List<VarEntry> entries = records.Select(Entry).ToList();
            List<string> old = DependencyAnalysis.OldVersions(entries, DependencyAnalysis.LatestVersions(entries));
            var usage = new LibraryUsage(vars, dependencies);
            HashSet<string> referenced = MoveOut.ReferencedOutsideVars(paths, vars, problem);
            return old.Where(name => !usage.NeedersOf(name).Any() && !referenced.Contains(name))
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

        private static HeldGroup ReadHeld(string folder, string title, ISet<string> names)
        {
            var group = new HeldGroup { Title = title, Folder = folder };
            if (!Directory.Exists(folder))
                return group;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), HoldingLog, StringComparison.OrdinalIgnoreCase))
                    continue;
                group.Files.Add(new HeldFile
                {
                    Path = file,
                    Bytes = new FileInfo(file).Length,
                    InLibrary = names.Contains(Path.GetFileNameWithoutExtension(file)),
                });
            }
            group.Files.Sort((first, second) => string.Compare(first.Path, second.Path, StringComparison.OrdinalIgnoreCase));
            return group;
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
            RemoveEmpty(group.Folder, group.Folder);
            return deleted;
        }

        // A holding folder's log only means something beside its files.
        private static void RemoveEmpty(string folder, string root)
        {
            foreach (string child in Directory.GetDirectories(folder))
                RemoveEmpty(child, root);
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
            List<string> now = StaleVars(records, vars, store.LoadDependencies().ToList(), paths, _ => { }).ToList();
            if (!new HashSet<string>(now, StringComparer.OrdinalIgnoreCase).SetEquals(reviewed))
                return null;

            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            var plan = new MovePlan { Target = "stale vars" };
            plan.Moves.AddRange(now);
            string holding = MoveOut.Apply(plan, name => FileOf(paths, byName[name]), paths);
            store.DeleteVars(plan.Moves);
            return holding;
        }

        /// <summary>Points broken links at where their var is now. A link to a var the library lost goes.</summary>
        /// <returns>How many links were dealt with.</returns>
        public static int RepairLinks(LibraryStore store, LibraryPaths paths, IEnumerable<string> links)
        {
            Dictionary<string, VarRecord> byName = store.LoadVars().ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            int repaired = 0;
            foreach (string link in links.Where(File.Exists))
            {
                VarLinks.Rebuild(link, name =>
                {
                    VarRecord record;
                    return byName.TryGetValue(name, out record) ? FileOf(paths, record) : null;
                });
                repaired++;
            }
            return repaired;
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
