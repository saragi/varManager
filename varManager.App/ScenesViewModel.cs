using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace varManager.Ui
{
    public sealed class SceneTile : PictureTile
    {
        public SceneTile(SceneEntry scene, ThumbnailLoader thumbnails, SynchronizationContext? ui)
            : base(scene.PreviewPath, thumbnails, ui)
        {
            Scene = scene;
        }

        public SceneEntry Scene { get; }
        public string Title => Scene.Title;
        public string Caption => Scene.Package + " · " + Scene.Creator;
        public string FitKey => Scene.Fit.ToString();
        public bool IsReady => Scene.Fit == SceneFit.Ready;
        public bool IsNotReady => !IsReady;

        public string FitText => Scene.Fit switch
        {
            SceneFit.Ready => "Ready in this profile",
            SceneFit.NotInProfile => Scene.OffCount == 1 ? "1 var is off" : $"{Scene.OffCount} vars are off",
            _ => Scene.MissingCount == 1 ? "1 missing var" : $"{Scene.MissingCount} missing vars",
        };

        public string ActionLabel => Scene.Fit switch
        {
            SceneFit.Ready => "Load in VaM",
            SceneFit.NotInProfile => "Add to this profile",
            _ => "Fix",
        };
    }

    // Tiles are made when a row first shows, because a library has far more scenes than fit on screen.
    public sealed class SceneRow
    {
        private readonly Lazy<IReadOnlyList<SceneTile>> tiles;

        public SceneRow(Func<IReadOnlyList<SceneTile>> makeTiles)
        {
            tiles = new Lazy<IReadOnlyList<SceneTile>>(makeTiles);
        }

        public IReadOnlyList<SceneTile> Tiles => tiles.Value;
    }

    /// <summary>The Scenes screen: whether each scene is ready in the current profile, with one button for the next step.</summary>
    public sealed class ScenesViewModel : Observable
    {
        private const string All = "All";

        private readonly ThumbnailLoader thumbnails = new ThumbnailLoader();
        private readonly string storeFile;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs;
        private readonly Func<IReadOnlyCollection<string>> linkedVars;
        private readonly Action goToMissing;
        private readonly Action<IReadOnlyList<string>> tempLinksStale;
        private readonly SynchronizationContext? ui;
        private List<SceneEntry> scenes = new List<SceneEntry>();
        private IReadOnlyList<SceneRow> rows = new SceneRow[0];
        private string query = "";
        private SceneFit? fitFilter;
        private int columns = 1;
        private int reloads;
        private string shownText = "";

        public ScenesViewModel(string storeFile, LibraryPaths paths, JobRunner jobs, Func<IReadOnlyCollection<string>> linkedVars,
            Action goToMissing, Action<IReadOnlyList<string>> tempLinksStale, SynchronizationContext? ui)
        {
            this.storeFile = storeFile;
            this.paths = paths;
            this.jobs = jobs;
            this.linkedVars = linkedVars;
            this.goToMissing = goToMissing;
            this.tempLinksStale = tempLinksStale;
            this.ui = ui;
            ActCommand = new ArgCommand<SceneTile>(Act);
            FitPills.Add(new Pill(All, new Command(() => SetFit(null))) { IsActive = true });
            FitPills.Add(new Pill("Ready", new Command(() => SetFit(SceneFit.Ready))));
            FitPills.Add(new Pill("Not in profile", new Command(() => SetFit(SceneFit.NotInProfile))));
            FitPills.Add(new Pill("Missing vars", new Command(() => SetFit(SceneFit.MissingVars))));
        }

        public ObservableCollection<Pill> FitPills { get; } = new ObservableCollection<Pill>();
        public ICommand ActCommand { get; }

        public IReadOnlyList<SceneRow> Rows
        {
            get => rows;
            private set
            {
                rows = value;
                Changed();
            }
        }

        public string ShownText
        {
            get => shownText;
            private set
            {
                shownText = value;
                Changed();
            }
        }

        public string Query
        {
            get => query;
            set
            {
                query = value;
                Changed();
                Apply();
            }
        }

        public int Columns
        {
            get => columns;
            set
            {
                if (value == columns || value < 1)
                    return;
                columns = value;
                Apply();
            }
        }

        public async Task Reload()
        {
            int reload = ++reloads;
            List<SceneEntry> loaded = await Task.Run(() =>
            {
                if (!File.Exists(storeFile))
                    return new List<SceneEntry>();
                ISet<string> linked = new HashSet<string>(linkedVars(), StringComparer.OrdinalIgnoreCase);
                using LibraryStore store = LibraryStore.Open(storeFile);
                return SceneCatalog.Load(store, paths.VarsPath, linked);
            }).ConfigureAwait(true);

            if (reload != reloads)
                return;
            scenes = loaded;
            Apply();
        }

        /// <summary>Adds a loaded scene to the open one instead of replacing it.</summary>
        public bool Merge { get; set; }

        private void SetFit(SceneFit? fit)
        {
            fitFilter = fit;
            string label = fit switch
            {
                SceneFit.Ready => "Ready",
                SceneFit.NotInProfile => "Not in profile",
                SceneFit.MissingVars => "Missing vars",
                _ => All,
            };
            foreach (Pill pill in FitPills)
                pill.IsActive = pill.Label == label;
            Apply();
        }

        private void Apply()
        {
            List<SceneEntry> shown = SceneCatalog.Filter(scenes, query, fitFilter);
            ShownText = $"{shown.Count:N0} of {scenes.Count:N0} scenes";
            var built = new List<SceneRow>();
            int width = columns;
            for (int start = 0; start < shown.Count; start += width)
            {
                int first = start;
                built.Add(new SceneRow(() => shown.GetRange(first, Math.Min(width, shown.Count - first))
                    .Select(scene => new SceneTile(scene, thumbnails, ui))
                    .ToList()));
            }
            Rows = built;
        }

        private void Act(SceneTile tile)
        {
            if (tile.Scene.Fit == SceneFit.Ready)
                Load(tile.Scene);
            else
                Fix(tile.Scene);
        }

        private void Load(SceneEntry scene)
        {
            bool merge = Merge;
            jobs.Start("Loading in VaM", context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                List<string> stale = SceneLauncher.Load(store, paths, scene.VarName, scene.EntryName, "scenes", merge);
                tempLinksStale(stale);
                return $"Asked VaM to load {scene.Title}.";
            });
        }

        // Turns on the stored vars a scene needs. Only what's left over needs the Missing screen.
        private void Fix(SceneEntry scene)
        {
            jobs.Start("Fixing " + scene.Title, context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                IReadOnlyList<VarRecord> records = store.LoadVars();
                var vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
                var dependencies = new DependencyIndex(store.LoadDependencies());
                Dictionary<string, VarRecord> byName = records.ToDictionary(v => v.VarName, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> installed = VarLinks.Installed(paths.AddonPackages);
                Directory.CreateDirectory(Path.Combine(paths.AddonPackages, LibraryFolders.VarsLink));

                int linked = 0;
                int failed = 0;
                foreach (string name in DependencyAnalysis.WithDependencies(new[] { scene.VarName }, vars, dependencies))
                {
                    if (installed.ContainsKey(name))
                        continue;
                    string file = Path.Combine(paths.VarsPath, byName[name].VarPath ?? "", name + ".var");
                    VarLinkResult result = VarLinks.InstallForGood(paths.AddonPackages, name, file, out _);
                    if (result == VarLinkResult.Linked)
                        linked++;
                    else if (result == VarLinkResult.Failed)
                        failed++;
                }
                VamBridge.RequestRescan(paths.VamPath);

                string text = linked == 1 ? "Turned on 1 var." : $"Turned on {linked} vars.";
                if (failed > 0)
                    text += $" {failed} could not be linked.";
                if (scene.MissingCount == 0)
                    return text;
                if (ui == null)
                    goToMissing();
                else
                    ui.Post(_ => goToMissing(), null);
                return $"{text} {scene.MissingCount} still missing.";
            });
        }
    }
}
