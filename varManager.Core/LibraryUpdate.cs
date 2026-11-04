using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace varManager
{
    public sealed class LibraryPaths
    {
        public LibraryPaths(string varsPath, string vamPath, string dataFolder)
        {
            VarsPath = varsPath;
            VamPath = vamPath;
            DataFolder = dataFolder;
        }

        public string VarsPath { get; }
        public string VamPath { get; }

        // Holds the lists of vars still to link, which survive a restart.
        public string DataFolder { get; }

        public string AddonPackages => VarSwitches.AddonPackages(VamPath);
        public string MigratedList => Path.Combine(DataFolder, "varsMigrated.txt");
        public string InstallList => Path.Combine(DataFolder, "varsForInstall.txt");
    }

    public sealed class UpdateReport
    {
        public bool FoundVars { get; set; }
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
        public int NotLinked { get; set; }

        /// <summary>Vars that look gone, in numbers too large to forget without asking.</summary>
        public int HeldBack { get; set; }

        public List<string> Problems { get; } = new List<string>();

        public string Summary
        {
            get
            {
                if (!FoundVars)
                    return "No vars found. Check the vars folder.";
                string text = $"Update DB finished: {Added} new, {Changed} changed, {Removed} removed.";
                if (HeldBack > 0)
                    text += $" {HeldBack} more look gone, so they were kept. See Health.";
                if (NotLinked > 0)
                    text += $" {NotLinked} not linked yet.";
                if (Problems.Count > 0)
                    text += $" {Problems.Count} problems, listed in update-problems.txt.";
                return text;
            }
        }
    }

    /// <summary>Brings the store up to date with the repository: tidy, scan what's new, drop what's gone, link what's waiting.</summary>
    public static class LibraryUpdate
    {
        /// <summary>
        /// A wrong vars folder looks like a mass removal, and rebuilding the records took 106 minutes on the
        /// real library. A handful gone from a tiny library is normal use.
        /// </summary>
        public static bool LooksLikeWrongFolder(int gone, int total)
        {
            return (gone >= 5 && gone * 2 >= total) || (gone >= 50 && gone * 5 >= total);
        }

        /// <summary>The vars in the tidied folder by name, wherever they sit in it.</summary>
        public static Dictionary<string, string> TidiedVars(LibraryPaths paths)
        {
            string tidied = Path.Combine(paths.VarsPath, LibraryFolders.Tidied);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(tidied))
            {
                foreach (string file in Directory.GetFiles(tidied, "*.var", SearchOption.AllDirectories))
                    files[Path.GetFileNameWithoutExtension(file)] = file;
            }
            return files;
        }

        public static UpdateReport Run(LibraryStore store, LibraryPaths paths, Action<int, int> progress, CancellationToken token)
        {
            var report = new UpdateReport();
            Tidy(paths, report);

            string tidied = Path.Combine(paths.VarsPath, LibraryFolders.Tidied);
            string[] files = Directory.GetFiles(tidied, "*.var", SearchOption.AllDirectories);
            report.FoundVars = files.Length > 0;
            if (report.FoundVars)
            {
                Scan(store, paths, files, report, progress, token);
                RemoveGone(store, paths, files, report);
                LinkWaiting(store, paths, report);
            }
            RefreshInstallStatus(store, paths);
            VamBridge.RequestRescan(paths.VamPath);
            WriteProblems(paths, report);
            return report;
        }

        private static void Tidy(LibraryPaths paths, UpdateReport report)
        {
            List<string> vars = VarTidier.RepositoryVars(paths.VarsPath);
            List<string> loose = VarTidier.LooseAddonVars(paths.AddonPackages).ToList();
            IEnumerable<string> saved = File.Exists(paths.InstallList) ? File.ReadAllLines(paths.InstallList) : new string[0];
            File.WriteAllLines(paths.InstallList, VarTidier.MergeInstallList(saved, loose));

            vars.AddRange(loose);
            VarTidier.Tidy(vars, paths.VarsPath, report.Problems.Add, (current, total) => { });
        }

        private static void Scan(LibraryStore store, LibraryPaths paths, string[] files, UpdateReport report,
            Action<int, int> progress, CancellationToken token)
        {
            Dictionary<string, VarRecord> known = store.LoadVars().ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            string previews = Path.Combine(paths.VarsPath, LibraryFolders.PreviewPics);
            for (int i = 0; i < files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    ScanOne(store, paths, files[i], known, previews, report);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // One bad var must not stop the rest. A damaged zip entry throws SharpZipLib's own types.
                    report.Problems.Add(files[i] + " " + ex.Message);
                }
                progress(i + 1, files.Length);
            }
        }

        private static void ScanOne(LibraryStore store, LibraryPaths paths, string file,
            Dictionary<string, VarRecord> known, string previews, UpdateReport report)
        {
            string varName = Path.GetFileNameWithoutExtension(file);
            string varPath = Comm.MakeRelativePath(paths.VarsPath, Path.GetDirectoryName(file));

            VarRecord record;
            bool isKnown = known.TryGetValue(varName, out record);
            if (isKnown)
            {
                // File times shift with daylight saving, so only size marks a changed var.
                if (record.FileSize == null || record.FileSize == new FileInfo(file).Length)
                {
                    if (record.VarPath != varPath)
                        store.UpdateVarPath(varName, varPath);
                    return;
                }
                ScenePreviews.DeleteAll(paths.VarsPath, varName);
            }

            VarScan scan = VarScanner.Read(file, previews, report.Problems.Add, report.Problems.Add);
            if (scan.Outcome == VarScanOutcome.Invalid)
            {
                report.Problems.Add($"{varName}, Invalid var package structure, move into {LibraryFolders.NotComplyRule} directory");
                if (isKnown)
                    store.DeleteVars(new[] { varName });
                File.Move(file, Path.Combine(paths.VarsPath, LibraryFolders.NotComplyRule, Path.GetFileName(file)));
                return;
            }
            if (scan.Outcome != VarScanOutcome.Scanned)
                return;

            store.SaveVar(varName, varPath, scan);
            if (isKnown)
                report.Changed++;
            else
                report.Added++;
        }

        private static void RemoveGone(LibraryStore store, LibraryPaths paths, string[] files, UpdateReport report)
        {
            var present = new HashSet<string>(files.Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<VarRecord> records = store.LoadVars();
            List<string> gone = records.Select(v => v.VarName).Where(name => !present.Contains(name)).ToList();
            if (gone.Count == 0)
                return;
            if (LooksLikeWrongFolder(gone.Count, records.Count))
            {
                report.HeldBack = gone.Count;
                return;
            }
            Forget(store, paths, gone, report);
            report.Removed = gone.Count;
        }

        private static void Forget(LibraryStore store, LibraryPaths paths, List<string> gone, UpdateReport report)
        {
            store.DeleteVars(gone);
            foreach (string varName in gone)
                ScenePreviews.DeleteAll(paths.VarsPath, varName);
            RestorePreviews(store, paths, report);
        }

        /// <summary>Forgets records the user reviewed, skipping any whose file came back.</summary>
        /// <returns>How many went.</returns>
        public static int RemoveMissing(LibraryStore store, LibraryPaths paths, IEnumerable<string> reviewed)
        {
            var known = new HashSet<string>(store.LoadVars().Select(v => v.VarName), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> present = TidiedVars(paths);
            // No vars at all means the folder can't be reached, which says nothing about any one var.
            if (present.Count == 0)
                return 0;
            List<string> gone = reviewed.Where(name => known.Contains(name) && !present.ContainsKey(name)).ToList();
            if (gone.Count > 0)
                Forget(store, paths, gone, new UpdateReport());
            return gone.Count;
        }

        /// <summary>Extracts any preview that's missing again. Two vars can share a preview folder entry, so removing one may cost the other.</summary>
        public static void RestorePreviews(LibraryStore store, LibraryPaths paths, UpdateReport report)
        {
            Dictionary<string, string> files = VarFiles(store, paths);
            foreach (ScenePreview preview in store.LoadPreviews())
            {
                string picture = ScenePreviews.PicturePath(paths.VarsPath, preview.Type, preview.VarName, preview.Picture);
                string file;
                if (File.Exists(picture) || !files.TryGetValue(preview.VarName, out file))
                    continue;
                try
                {
                    if (!ScenePreviews.Restore(file, preview.ScenePath, picture))
                        report.Problems.Add(picture + " is missing and the repair failed");
                }
                catch (Exception ex)
                {
                    report.Problems.Add(picture + " " + ex.Message);
                }
            }
        }

        private static void LinkWaiting(LibraryStore store, LibraryPaths paths, UpdateReport report)
        {
            Directory.CreateDirectory(Path.Combine(paths.AddonPackages, LibraryFolders.VarsLink));
            Dictionary<string, string> files = VarFiles(store, paths);
            if (File.Exists(paths.MigratedList))
            {
                List<string> notLinked = File.ReadAllLines(paths.MigratedList)
                    .Where(name => name.Length > 0 && Link(paths, files, name) == VarLinkResult.Failed)
                    .ToList();
                report.NotLinked = notLinked.Count;
                if (notLinked.Count == 0)
                    File.Delete(paths.MigratedList);
                else
                    File.WriteAllLines(paths.MigratedList, notLinked);
            }

            if (File.Exists(paths.InstallList))
            {
                string[] waiting = File.ReadAllLines(paths.InstallList).Where(name => name.Length > 0).ToArray();
                var vars = new VarIndex(store.LoadVars().Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
                foreach (ResolvedReference gone in DependencyAnalysis.Resolve(waiting, vars).Where(r => r.VarName == null))
                    report.Problems.Add(gone.Reference + " was waiting to be linked, but the library doesn't have it");

                List<string> notLinked = DependencyAnalysis.WithDependencies(waiting, vars, new DependencyIndex(store.LoadDependencies()))
                    .Where(varName => Link(paths, files, varName) == VarLinkResult.Failed)
                    .ToList();
                report.NotLinked += notLinked.Count;

                // Only now, so a crash or a failed link can't lose a var the user had enabled.
                if (notLinked.Count == 0)
                    File.Delete(paths.InstallList);
                else
                    File.WriteAllLines(paths.InstallList, notLinked);
            }
        }

        private static VarLinkResult Link(LibraryPaths paths, Dictionary<string, string> files, string varName)
        {
            string file;
            if (!files.TryGetValue(varName, out file))
                return VarLinkResult.Failed;
            int errorCode;
            return VarLinks.Install(paths.AddonPackages, varName, file, false, out errorCode);
        }

        private static void RefreshInstallStatus(LibraryStore store, LibraryPaths paths)
        {
            var known = new HashSet<string>(store.LoadVars().Select(v => v.VarName), StringComparer.OrdinalIgnoreCase);
            store.SetInstallStatus(VarLinks.Installed(paths.AddonPackages)
                .Where(link => known.Contains(link.Key))
                .Select(link => new KeyValuePair<string, bool>(link.Key, File.Exists(link.Value + ".disabled"))));
        }

        private static Dictionary<string, string> VarFiles(LibraryStore store, LibraryPaths paths)
        {
            return store.LoadVars().ToDictionary(v => v.VarName,
                v => Path.Combine(paths.VarsPath, v.VarPath ?? "", v.VarName + ".var"), StringComparer.OrdinalIgnoreCase);
        }

        private static void WriteProblems(LibraryPaths paths, UpdateReport report)
        {
            string file = Path.Combine(paths.DataFolder, "update-problems.txt");
            if (report.Problems.Count > 0)
                File.WriteAllLines(file, report.Problems);
            else if (File.Exists(file))
                File.Delete(file);
        }
    }
}
