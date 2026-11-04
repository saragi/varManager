using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimpleJSON;

namespace varManager
{
    public enum SceneFit
    {
        Ready,
        NotInProfile,
        MissingVars
    }

    /// <summary>One scene, and whether it can load in the current profile.</summary>
    public sealed class SceneEntry
    {
        public string VarName { get; set; }
        public string Creator { get; set; }
        public string Package { get; set; }
        public string EntryName { get; set; }
        public string Title { get; set; }
        public string PreviewPath { get; set; }
        public SceneFit Fit { get; set; }
        public int MissingCount { get; set; }
        public int OffCount { get; set; }
    }

    public static class SceneCatalog
    {
        private const string SceneType = "scenes";

        /// <summary>Every scene, checked against what's linked. Vars are read once, so this opens no var files.</summary>
        public static List<SceneEntry> Load(LibraryStore store, string varsPath, ISet<string> linked)
        {
            IReadOnlyList<VarRecord> records = store.LoadVars();
            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            var vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
            var dependencies = new DependencyIndex(store.LoadDependencies());
            var closures = new Dictionary<string, DependencyClosure>(StringComparer.OrdinalIgnoreCase);

            var scenes = new List<SceneEntry>();
            foreach (ScenePreview scene in store.LoadItems().Where(item => item.Type == SceneType))
            {
                VarRecord record;
                if (!byName.TryGetValue(scene.VarName, out record))
                    continue;

                DependencyClosure closure;
                if (!closures.TryGetValue(scene.VarName, out closure))
                {
                    closure = DependencyScanner.Closure(new[] { scene.VarName }, vars, name => dependencies.DependenciesOf(name));
                    closures[scene.VarName] = closure;
                }

                int off = closure.Vars.Count(name => !linked.Contains(name));
                scenes.Add(new SceneEntry
                {
                    VarName = record.VarName,
                    Creator = record.CreatorName,
                    Package = record.PackageName,
                    EntryName = scene.ScenePath,
                    Title = Path.GetFileNameWithoutExtension(scene.ScenePath),
                    PreviewPath = string.IsNullOrEmpty(scene.Picture)
                        ? null
                        : ScenePreviews.PicturePath(varsPath, scene.Type, scene.VarName, scene.Picture),
                    MissingCount = closure.Missing.Count,
                    OffCount = off,
                    Fit = closure.Missing.Count > 0 ? SceneFit.MissingVars : off > 0 ? SceneFit.NotInProfile : SceneFit.Ready,
                });
            }
            return scenes;
        }

        public static List<SceneEntry> Filter(IEnumerable<SceneEntry> scenes, string text, SceneFit? fit)
        {
            string typed = text.Trim();
            return scenes.Where(scene =>
                    (fit == null || scene.Fit == fit)
                    && (typed.Length == 0
                        || scene.Title.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0
                        || scene.VarName.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
        }
    }

    /// <summary>Asks VaM to load something, using temporary links for whatever the current profile lacks.</summary>
    public static class SceneLauncher
    {
        /// <param name="type">The item's type, such as "scenes" or "looks".</param>
        /// <returns>Temporary links left from earlier loads, to remove once VaM has taken this request.</returns>
        /// <param name="merge">Adds to the open scene instead of replacing it.</param>
        public static List<string> Load(LibraryStore store, LibraryPaths paths, string varName, string entryName, string type, bool merge = false)
        {
            IReadOnlyList<VarRecord> records = store.LoadVars();
            VarRecord record = records.Single(v => string.Equals(v.VarName, varName, StringComparison.OrdinalIgnoreCase));
            string varFile = FileOf(paths, record);

            string saveName = record.VarName + ":/" + entryName.Replace('\\', '/');
            SceneAnalysis.Prepare(saveName, varFile, paths.VamPath, "unknown", false);
            string cache = SceneAnalysis.CacheFolder(record.VarName, saveName.Substring(saveName.IndexOf(":/", StringComparison.Ordinal) + 2));

            JSONClass request = new JSONClass();
            request["resources"] = new JSONArray();
            JSONClass resource = new JSONClass();
            resource["type"] = type;
            resource["saveName"] = saveName;
            request["resources"].Add(resource);

            bool hasScene;
            JSONClass prepared = VamBridge.PrepareLoad(request, merge, SceneAnalysis.ReadGender(cache), false, 1, out hasScene);
            List<string> stale = hasScene ? VarLinks.TempLinks(paths.AddonPackages) : new List<string>();

            var vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
            var dependencies = new DependencyIndex(store.LoadDependencies());
            Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> installed = VarLinks.Installed(paths.AddonPackages);

            Directory.CreateDirectory(Path.Combine(paths.AddonPackages, LibraryFolders.TempVarLink));
            bool rescan = false;
            foreach (string needed in DependencyAnalysis.WithDependencies(SceneAnalysis.ReadDepends(cache), vars, dependencies))
            {
                // A var the profile links needs no temporary link, so an old one can go.
                if (installed.ContainsKey(needed))
                    continue;
                stale.Remove(needed.ToLower() + ".var");
                int errorCode;
                if (VarLinks.Install(paths.AddonPackages, needed, FileOf(paths, byName[needed]), true, out errorCode) == VarLinkResult.Linked)
                    rescan = true;
            }

            prepared["rescan"] = rescan.ToString();
            VamBridge.WriteRequest(paths.VamPath, prepared);
            return stale;
        }

        private static string FileOf(LibraryPaths paths, VarRecord record)
        {
            return Path.Combine(paths.VarsPath, record.VarPath ?? "", record.VarName + ".var");
        }
    }
}
