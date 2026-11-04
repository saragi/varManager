using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using SimpleJSON;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    [Collection("Working directory")]
    public sealed class ScenesTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmscenes-" + Guid.NewGuid().ToString("N"));
        private readonly string startFolder = Directory.GetCurrentDirectory();
        private readonly string vam;
        private readonly string vars;
        private readonly string links;
        private readonly string temp;
        private readonly string storeFile;
        private readonly LibraryPaths paths;

        public ScenesTests()
        {
            vam = Path.Combine(root, "VaM");
            vars = Path.Combine(root, "vars");
            string data = Path.Combine(root, "app");
            Directory.CreateDirectory(vam);
            Directory.CreateDirectory(vars);
            Directory.CreateDirectory(data);
            Directory.SetCurrentDirectory(data);
            File.WriteAllText(Path.Combine(vam, "VaM.exe"), "");
            VarSwitches.Names(vam);
            VarSwitches.EnsureLink(vam);
            links = Path.Combine(vam, "AddonPackages", LibraryFolders.VarsLink);
            temp = Path.Combine(vam, "AddonPackages", LibraryFolders.TempVarLink);
            Directory.CreateDirectory(links);
            Directory.CreateDirectory(temp);
            paths = new LibraryPaths(vars, vam, data);
            storeFile = Path.Combine(data, "library.db");
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(startFolder);
            new DirectoryInfo(Path.Combine(vam, "AddonPackages")).Delete();
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                try
                {
                    Directory.Delete(root, true);
                    return;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private void MakeVar(string name, string references = "", bool scene = true)
        {
            using ZipOutputStream zip = new ZipOutputStream(File.Create(Path.Combine(vars, name + ".var")));
            var entries = new List<string> { "meta.json", "{}" };
            if (scene)
                entries.AddRange(new[] { "Saves/scene/My Scene.json", "{\"x\":\"" + references + "\",\"f\":\"/Female/\"}", "Saves/scene/My Scene.jpg", "picture" });
            for (int i = 0; i < entries.Count; i += 2)
            {
                zip.PutNextEntry(new ZipEntry(entries[i]));
                byte[] bytes = Encoding.UTF8.GetBytes(entries[i + 1]);
                zip.Write(bytes, 0, bytes.Length);
                zip.CloseEntry();
            }
        }

        private void Update()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            LibraryUpdate.Run(store, paths, (done, total) => { }, default);
        }

        private List<SceneEntry> Load(params string[] linked)
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            return SceneCatalog.Load(store, vars, new HashSet<string>(linked, StringComparer.OrdinalIgnoreCase));
        }

        private async Task<(ScenesViewModel scenes, JobRunner jobs, List<string> wentToMissing)> Open()
        {
            var jobs = new JobRunner(null);
            var went = new List<string>();
            var scenes = new ScenesViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys,
                () => went.Add("missing"), _ => { }, null);
            await scenes.Reload();
            scenes.Columns = 10;
            return (scenes, jobs, went);
        }

        private static void WaitForJob(JobRunner jobs)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (jobs.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.False(jobs.IsRunning);
        }

        private static SceneTile Tile(ScenesViewModel scenes, string varName)
        {
            return scenes.Rows.SelectMany(row => row.Tiles).Single(tile => tile.Scene.VarName == varName);
        }

        [Fact]
        public void A_scene_with_everything_linked_is_ready()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();

            SceneEntry scene = Assert.Single(Load("A.B.1", "Dep.Pack.1"));

            Assert.Equal(SceneFit.Ready, scene.Fit);
            Assert.Equal("My Scene", scene.Title);
            Assert.Equal("Saves/scene/My Scene.json", scene.EntryName);
            Assert.EndsWith("scenes001_my scene.jpg", scene.PreviewPath);
        }

        [Fact]
        public void A_scene_with_a_var_off_is_not_in_the_profile()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();

            SceneEntry scene = Assert.Single(Load("A.B.1"));

            Assert.Equal(SceneFit.NotInProfile, scene.Fit);
            Assert.Equal(1, scene.OffCount);
        }

        [Fact]
        public void A_missing_var_outranks_vars_that_are_off()
        {
            MakeVar("A.B.1", "Gone.Pack.1:/Custom/a.vap");
            Update();

            SceneEntry scene = Assert.Single(Load());

            Assert.Equal(SceneFit.MissingVars, scene.Fit);
            Assert.Equal(1, scene.MissingCount);
        }

        [Fact]
        public void Readiness_follows_dependencies_of_dependencies()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(Path.Combine(vars, "Dep.Pack.1.var"))))
            {
                zip.PutNextEntry(new ZipEntry("meta.json"));
                byte[] bytes = Encoding.UTF8.GetBytes("{\"x\":\"Deep.Gone.1:/Custom/a.vap\"}");
                zip.Write(bytes, 0, bytes.Length);
                zip.CloseEntry();
            }
            Update();

            Assert.Equal(SceneFit.MissingVars, Assert.Single(Load("A.B.1", "Dep.Pack.1")).Fit);
        }

        [Fact]
        public void Only_scenes_are_listed()
        {
            string path = Path.Combine(vars, "Look.Pack.1.var");
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(path)))
            {
                foreach (string entry in new[] { "meta.json", "Saves/Person/Appearance/a.vac" })
                {
                    zip.PutNextEntry(new ZipEntry(entry));
                    byte[] bytes = Encoding.UTF8.GetBytes("{}");
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
            Update();

            Assert.Empty(Load());
        }

        [Fact]
        public void Filter_matches_title_or_package_and_fit()
        {
            var scenes = new[]
            {
                new SceneEntry { VarName = "A.Beach.1", Title = "Sunset", Fit = SceneFit.Ready },
                new SceneEntry { VarName = "C.Forest.1", Title = "Beach day", Fit = SceneFit.MissingVars },
                new SceneEntry { VarName = "E.City.1", Title = "Night", Fit = SceneFit.Ready },
            };

            Assert.Equal(2, SceneCatalog.Filter(scenes, "beach", null).Count);
            Assert.Equal("A.Beach.1", Assert.Single(SceneCatalog.Filter(scenes, "beach", SceneFit.Ready)).VarName);
            Assert.Equal(2, SceneCatalog.Filter(scenes, " ", SceneFit.Ready).Count);
        }

        [Fact]
        public void Load_writes_a_request_the_plugin_understands()
        {
            MakeVar("A.B.1");
            Update();
            using LibraryStore store = LibraryStore.Open(storeFile);

            SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "scenes");

            JSONNode request = JSON.Parse(File.ReadAllText(VamBridge.RequestFile(vam)));
            JSONNode resource = request["resources"][0];
            Assert.Equal("scenes", resource["type"].Value);
            Assert.Equal("A.B.1:/Saves/scene/My Scene.json", resource["saveName"].Value);
            Assert.Equal("female", resource["characterGender"].Value);
            Assert.Equal("false", resource["merge"].Value);
        }

        [Fact]
        public void Load_adds_temporary_links_for_what_the_profile_lacks_and_asks_for_a_rescan()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.1.var"));
            using LibraryStore store = LibraryStore.Open(storeFile);

            SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "scenes");

            Assert.True(VarLinks.IsLink(Path.Combine(temp, "Dep.Pack.1.var")));
            Assert.False(File.Exists(Path.Combine(temp, "A.B.1.var")));
            Assert.False(File.Exists(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.Equal("True", JSON.Parse(File.ReadAllText(VamBridge.RequestFile(vam)))["rescan"].Value);
        }

        [Fact]
        public void Load_with_everything_linked_needs_no_rescan()
        {
            MakeVar("A.B.1");
            Update();
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.1.var"));
            using LibraryStore store = LibraryStore.Open(storeFile);

            SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "scenes");

            Assert.Equal("False", JSON.Parse(File.ReadAllText(VamBridge.RequestFile(vam)))["rescan"].Value);
        }

        [Fact]
        public void Load_reports_old_temporary_links_but_keeps_ones_it_still_needs()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            MakeVar("Old.Pack.1", scene: false);
            Update();
            string tidied = Path.Combine(vars, LibraryFolders.Tidied);
            VarLinks.Create(Path.Combine(temp, "Old.Pack.1.var"), Path.Combine(tidied, "Old", "Old.Pack.1.var"));
            VarLinks.Create(Path.Combine(temp, "Dep.Pack.1.var"), Path.Combine(tidied, "Dep", "Dep.Pack.1.var"));
            using LibraryStore store = LibraryStore.Open(storeFile);

            List<string> stale = SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "scenes");

            Assert.Equal(new[] { "old.pack.1.var" }, stale);
        }

        [Fact]
        public async Task Old_temporary_links_go_once_vam_takes_the_request()
        {
            string target = Path.Combine(vars, "x.var");
            File.WriteAllText(target, "x");
            VarLinks.Create(Path.Combine(temp, "Old.Pack.1.var"), target);
            string request = VamBridge.RequestFile(vam);
            Directory.CreateDirectory(Path.GetDirectoryName(request)!);
            File.WriteAllText(request, "{}");

            Task waiting = VamBridge.RemoveTempLinksAfterLoad(vam, new[] { "old.pack.1.var" }, TimeSpan.FromMilliseconds(50));
            await Task.Delay(200);
            Assert.True(File.Exists(Path.Combine(temp, "Old.Pack.1.var")));

            File.Delete(request);
            await waiting.WaitAsync(Timeout);

            Assert.False(File.Exists(Path.Combine(temp, "Old.Pack.1.var")));
        }

        [Fact]
        public async Task The_screen_shows_each_state_and_filters_by_it()
        {
            MakeVar("Ready.Pack.1");
            MakeVar("Off.Pack.1");
            MakeVar("Gone.Pack.1", "Nowhere.Pack.1:/Custom/a.vap");
            Update();
            VarLinks.Create(Path.Combine(links, "Ready.Pack.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "Ready", "Ready.Pack.1.var"));

            (ScenesViewModel scenes, _, _) = await Open();

            Assert.Equal("3 of 3 scenes", scenes.ShownText);
            Assert.Equal("Load in VaM", Tile(scenes, "Ready.Pack.1").ActionLabel);
            Assert.Equal("Ready in this profile", Tile(scenes, "Ready.Pack.1").FitText);
            Assert.Equal("Add to this profile", Tile(scenes, "Off.Pack.1").ActionLabel);
            Assert.Equal("1 var is off", Tile(scenes, "Off.Pack.1").FitText);
            Assert.Equal("Fix", Tile(scenes, "Gone.Pack.1").ActionLabel);
            Assert.Equal("1 missing var", Tile(scenes, "Gone.Pack.1").FitText);

            scenes.FitPills.Single(p => p.Label == "Missing vars").Command.Execute(null);

            Assert.Equal("1 of 3 scenes", scenes.ShownText);
            Assert.Equal("Missing vars", scenes.FitPills.Single(p => p.IsActive).Label);
        }

        [Fact]
        public async Task Add_to_this_profile_links_what_the_scene_needs_for_good()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            (ScenesViewModel scenes, JobRunner jobs, List<string> went) = await Open();

            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
            Assert.True(VarLinks.IsLink(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.Equal("Turned on 2 vars.", jobs.Note);
            Assert.Empty(went);
        }

        [Fact]
        public async Task Fix_links_what_it_can_and_opens_missing_only_for_what_is_left()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap\",\"y\":\"Gone.Pack.1:/Custom/b.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            (ScenesViewModel scenes, JobRunner jobs, List<string> went) = await Open();

            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.True(VarLinks.IsLink(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.Equal(new[] { "missing" }, went);
            Assert.Contains("still missing", jobs.Note);
        }

        [Fact]
        public async Task A_ready_scene_loads()
        {
            MakeVar("A.B.1");
            Update();
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.1.var"));
            (ScenesViewModel scenes, JobRunner jobs, _) = await Open();

            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.True(File.Exists(VamBridge.RequestFile(vam)));
            Assert.Contains("Asked VaM to load My Scene", jobs.Note);
        }

        [Fact]
        public async Task The_library_can_load_a_scene_package_and_not_others()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1", scene: false);
            Update();
            var jobs = new JobRunner(null);
            var library = new LibraryViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys, _ => { }, null);
            await library.Reload();
            library.Columns = 10;
            List<LibraryTile> tiles = library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).ToList();

            library.SelectCommand.Execute(tiles.Single(t => t.Item.VarName == "C.D.1"));
            Assert.False(library.CanLoad);

            library.SelectCommand.Execute(tiles.Single(t => t.Item.VarName == "A.B.1"));
            Assert.True(library.CanLoad);
            library.LoadCommand.Execute(null);
            WaitForJob(jobs);

            Assert.True(File.Exists(VamBridge.RequestFile(vam)));
        }

        [Fact]
        public async Task An_older_reload_finishing_late_is_ignored()
        {
            MakeVar("A.B.1");
            Update();
            using var gate = new ManualResetEventSlim();
            int calls = 0;
            var scenes = new ScenesViewModel(storeFile, paths, new JobRunner(null), () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    gate.Wait(Timeout);
                    return new string[0];
                }
                return new[] { "A.B.1" };
            }, () => { }, _ => { }, null);
            scenes.Columns = 4;

            Task older = scenes.Reload();
            while (Volatile.Read(ref calls) == 0)
                await Task.Delay(5);
            await scenes.Reload();
            gate.Set();
            await older;

            Assert.Equal("Ready in this profile", Tile(scenes, "A.B.1").FitText);
        }
    }
}
