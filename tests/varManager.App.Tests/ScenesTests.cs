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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
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

        private void MakeZip(string name, params string[] namesAndTexts)
        {
            using ZipOutputStream zip = new ZipOutputStream(File.Create(Path.Combine(vars, name + ".var")));
            for (int i = 0; i < namesAndTexts.Length; i += 2)
            {
                zip.PutNextEntry(new ZipEntry(namesAndTexts[i]));
                byte[] bytes = Encoding.UTF8.GetBytes(namesAndTexts[i + 1]);
                zip.Write(bytes, 0, bytes.Length);
                zip.CloseEntry();
            }
        }

        private ShellViewModel Shell(JobRunner jobs)
        {
            string settingsFile = Path.Combine(Path.GetDirectoryName(storeFile)!, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            return new ShellViewModel(settingsFile, jobs, () => false, _ => { });
        }

        private static void WaitUntil(Func<bool> done)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!done() && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.True(done());
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

        private async Task<(ScenesViewModel scenes, JobRunner jobs, List<string> wentToMissing)> Open(List<string>? stale = null)
        {
            var jobs = new JobRunner(null);
            var went = new List<string>();
            var scenes = new ScenesViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys,
                () => went.Add("missing"), names => stale?.AddRange(names), null);
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
        public void A_latest_reference_is_ready_when_the_newest_version_is_linked()
        {
            MakeVar("A.B.1", "Dep.Pack.latest:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            MakeVar("Dep.Pack.2", scene: false);
            Update();

            Assert.Equal(SceneFit.Ready, Assert.Single(Load("A.B.1", "Dep.Pack.2")).Fit);
            Assert.Equal(SceneFit.NotInProfile, Assert.Single(Load("A.B.1", "Dep.Pack.1")).Fit);
        }

        [Fact]
        public void A_var_with_two_scenes_lists_both_and_a_scene_may_have_no_picture()
        {
            MakeZip("A.B.1", "meta.json", "{}", "Saves/scene/Two.json", "{}", "Saves/scene/One.json", "{}");
            Update();

            List<SceneEntry> scenes = Load("A.B.1");

            Assert.Equal(new[] { "One", "Two" }, scenes.Select(s => s.Title).OrderBy(t => t));
            Assert.All(scenes, s => Assert.Equal(SceneFit.Ready, s.Fit));
            Assert.All(scenes, s => Assert.Null(s.PreviewPath));
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
        public void A_temporary_link_the_profile_now_covers_is_stale()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            string tidied = Path.Combine(vars, LibraryFolders.Tidied);
            VarLinks.Create(Path.Combine(links, "Dep.Pack.1.var"), Path.Combine(tidied, "Dep", "Dep.Pack.1.var"));
            VarLinks.Create(Path.Combine(temp, "Dep.Pack.1.var"), Path.Combine(tidied, "Dep", "Dep.Pack.1.var"));
            using LibraryStore store = LibraryStore.Open(storeFile);

            List<string> stale = SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "scenes");

            Assert.Equal(new[] { "dep.pack.1.var" }, stale);
        }

        [Fact]
        public void A_link_named_in_another_case_counts_as_linked()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            string tidied = Path.Combine(vars, LibraryFolders.Tidied);
            VarLinks.Create(Path.Combine(links, "a.b.1.var"), Path.Combine(tidied, "A", "A.B.1.var"));
            VarLinks.Create(Path.Combine(links, "dep.pack.1.var"), Path.Combine(tidied, "Dep", "Dep.Pack.1.var"));
            using LibraryStore store = LibraryStore.Open(storeFile);

            SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "scenes");

            Assert.Empty(VarLinks.TempLinks(paths.AddonPackages));
        }

        [Fact]
        public void Load_makes_the_temporary_folder_for_any_type()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            Directory.Delete(temp);
            using LibraryStore store = LibraryStore.Open(storeFile);

            List<string> stale = SceneLauncher.Load(store, paths, "A.B.1", "Saves/scene/My Scene.json", "looks");

            Assert.Empty(stale);
            Assert.True(VarLinks.IsLink(Path.Combine(temp, "Dep.Pack.1.var")));
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

            VarLinks.Create(Path.Combine(temp, "Other.Pack.1.var"), target);

            Task waiting = VamBridge.RemoveTempLinksAfterLoad(vam, new[] { "old.pack.1.var" }, TimeSpan.FromSeconds(2), default);
            await Task.Delay(200);
            Assert.True(File.Exists(Path.Combine(temp, "Old.Pack.1.var")));

            // It sees the request gone at 2 s, then pauses for 2 s more.
            File.Delete(request);
            await Task.Delay(2300);
            Assert.True(File.Exists(Path.Combine(temp, "Old.Pack.1.var")));
            await waiting.WaitAsync(Timeout);

            Assert.False(File.Exists(Path.Combine(temp, "Old.Pack.1.var")));
            Assert.True(File.Exists(Path.Combine(temp, "Other.Pack.1.var")));
        }

        [Fact]
        public async Task A_newer_load_keeps_links_an_older_removal_held()
        {
            string target = Path.Combine(vars, "x.var");
            File.WriteAllText(target, "x");
            VarLinks.Create(Path.Combine(temp, "Needed.Pack.1.var"), target);
            VarLinks.Create(Path.Combine(temp, "Old.Pack.1.var"), target);
            var removal = new TempLinkRemoval();

            Task older = removal.Schedule(vam, new[] { "needed.pack.1.var", "old.pack.1.var" }, TimeSpan.FromMilliseconds(100));
            Task newer = removal.Schedule(vam, new[] { "old.pack.1.var" }, TimeSpan.FromMilliseconds(100));
            await Task.WhenAll(older, newer).WaitAsync(Timeout);

            Assert.True(File.Exists(Path.Combine(temp, "Needed.Pack.1.var")));
            Assert.False(File.Exists(Path.Combine(temp, "Old.Pack.1.var")));
        }

        [Fact]
        public async Task A_load_with_no_old_links_still_stops_the_older_removal()
        {
            string target = Path.Combine(vars, "x.var");
            File.WriteAllText(target, "x");
            VarLinks.Create(Path.Combine(temp, "Needed.Pack.1.var"), target);
            var removal = new TempLinkRemoval();

            Task older = removal.Schedule(vam, new[] { "needed.pack.1.var" }, TimeSpan.FromMilliseconds(100));
            await removal.Schedule(vam, new string[0], TimeSpan.FromMilliseconds(100));
            await older.WaitAsync(Timeout);

            Assert.True(File.Exists(Path.Combine(temp, "Needed.Pack.1.var")));
        }

        [Fact]
        public async Task Merge_reaches_the_request()
        {
            MakeVar("A.B.1");
            Update();
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.1.var"));
            (ScenesViewModel scenes, JobRunner jobs, _) = await Open();

            scenes.Merge = true;
            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.Equal("true", JSON.Parse(File.ReadAllText(VamBridge.RequestFile(vam)))["resources"][0]["merge"].Value);

            scenes.Merge = false;
            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);
            Assert.Equal("false", JSON.Parse(File.ReadAllText(VamBridge.RequestFile(vam)))["resources"][0]["merge"].Value);
        }

        [Fact]
        public async Task The_cache_card_clears_the_cache_and_goes()
        {
            string cached = Path.Combine(SceneAnalysis.CacheRoot, "A.B.1", "scene", "depend.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllText(cached, "x");
            MakeVar("A.B.1");
            Update();
            var jobs = new JobRunner(null);
            var health = new HealthViewModel(storeFile, paths, jobs, () => { });
            await health.Reload();

            health.Cards.Single(card => card.Text == "Scene cache").Action!.Execute(null);
            health.ConfirmCommand.Execute(null);
            await health.Work.WaitAsync(Timeout);

            Assert.False(Directory.Exists(SceneAnalysis.CacheRoot));
            Assert.Equal("Scene cache cleared.", jobs.Note);
            await health.Reload();
            Assert.DoesNotContain(health.Cards, card => card.Text == "Scene cache");
        }

        [Fact]
        public void Clearing_the_cache_ignores_a_moved_working_directory()
        {
            string home = Path.Combine(root, "home");
            string cached = Path.Combine(home, "Cache", "a.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllText(cached, "x");
            string keep = Path.Combine(Directory.GetCurrentDirectory(), "Cache", "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.WriteAllText(keep, "someone else's");
            SceneAnalysis.CacheBase = home;
            try
            {
                LibraryHealth.ClearSceneCache();
            }
            finally
            {
                SceneAnalysis.CacheBase = null;
            }

            Assert.False(File.Exists(cached));
            Assert.True(File.Exists(keep));
        }

        [Fact]
        public void Clearing_a_cache_that_is_gone_is_fine()
        {
            LibraryHealth.ClearSceneCache();
            LibraryHealth.ClearSceneCache();
        }

        [Fact]
        public void Clearing_the_scene_cache_takes_only_the_cache()
        {
            string cached = Path.Combine(SceneAnalysis.CacheRoot, "A.B.1", "scene", "depend.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllText(cached, "x");
            MakeVar("A.B.1");
            Update();
            using (LibraryStore store = LibraryStore.Open(storeFile))
                Assert.True(LibraryHealth.Load(store, paths, _ => { }).CacheBytes > 0);

            LibraryHealth.ClearSceneCache();

            Assert.False(Directory.Exists(SceneAnalysis.CacheRoot));
            Assert.True(File.Exists(Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.1.var")));
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
        public async Task Add_to_this_profile_drops_the_temporary_link_it_replaces()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            VarLinks.Create(Path.Combine(temp, "dep.pack.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "Dep", "Dep.Pack.1.var"));
            (ScenesViewModel scenes, JobRunner jobs, _) = await Open();

            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.True(VarLinks.IsLink(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.Empty(VarLinks.TempLinks(paths.AddonPackages));
        }

        [Fact]
        public async Task Fix_says_when_a_link_failed()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            Directory.CreateDirectory(Path.Combine(links, "Dep.Pack.1.var"));
            (ScenesViewModel scenes, JobRunner jobs, _) = await Open();

            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.Contains("1 could not be linked", jobs.Note);
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
        public async Task Loading_a_scene_passes_on_the_old_temporary_links()
        {
            MakeVar("A.B.1");
            MakeVar("Old.Pack.1", scene: false);
            Update();
            string tidied = Path.Combine(vars, LibraryFolders.Tidied);
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Path.Combine(tidied, "A", "A.B.1.var"));
            VarLinks.Create(Path.Combine(temp, "Old.Pack.1.var"), Path.Combine(tidied, "Old", "Old.Pack.1.var"));
            var stale = new List<string>();
            (ScenesViewModel scenes, JobRunner jobs, _) = await Open(stale);

            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));
            WaitForJob(jobs);

            Assert.Equal(new[] { "old.pack.1.var" }, stale);
        }

        [Fact]
        public async Task The_shell_opens_scenes_and_a_fix_shows_after_the_job()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1", scene: false);
            Update();
            var jobs = new JobRunner(null);
            ShellViewModel shell = Shell(jobs);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            ScenesViewModel scenes = shell.Scenes!;
            scenes.Columns = 10;

            shell.SelectedPage = shell.Pages[1];
            Assert.True(shell.IsScenesPage);
            Assert.False(shell.IsLibraryPage);
            Assert.False(shell.IsMissingPage);

            Assert.Equal(SceneFit.NotInProfile, Tile(scenes, "A.B.1").Scene.Fit);
            Task before = shell.LibraryLoaded;
            scenes.ActCommand.Execute(Tile(scenes, "A.B.1"));

            WaitUntil(() => shell.LibraryLoaded != before);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            Assert.Equal(SceneFit.Ready, Tile(scenes, "A.B.1").Scene.Fit);
        }

        [Fact]
        public async Task Fix_in_the_shell_opens_the_missing_page()
        {
            MakeVar("A.B.1", "Gone.Pack.1:/Custom/a.vap");
            Update();
            var jobs = new JobRunner(null);
            ShellViewModel shell = Shell(jobs);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            shell.Scenes!.Columns = 10;
            Task before = shell.LibraryLoaded;

            shell.Scenes.ActCommand.Execute(Tile(shell.Scenes, "A.B.1"));
            WaitUntil(() => shell.SelectedPage.Name == "Missing");

            Assert.True(shell.IsMissingPage);
            WaitUntil(() => shell.LibraryLoaded != before);
            await shell.LibraryLoaded.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Turning_a_package_on_drops_its_temporary_link()
        {
            MakeVar("A.B.1");
            Update();
            VarLinks.Create(Path.Combine(temp, "a.b.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.1.var"));
            var jobs = new JobRunner(null);
            var library = new LibraryViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys, _ => { }, null);
            await library.Reload();
            library.Columns = 10;

            library.SelectCommand.Execute(library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).Single());
            library.ToggleCommand.Execute(null);
            WaitForJob(jobs);

            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
            Assert.Empty(VarLinks.TempLinks(paths.AddonPackages));
        }

        [Fact]
        public async Task The_library_loads_the_first_scene_even_with_no_picture()
        {
            MakeZip("A.B.1", "meta.json", "{}", "Saves/scene/Two.json", "{}", "Saves/scene/One.json", "{}");
            Update();
            var jobs = new JobRunner(null);
            var library = new LibraryViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys, _ => { }, null);
            await library.Reload();
            library.Columns = 10;

            LibraryTile tile = library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).Single();
            library.SelectCommand.Execute(tile);

            Assert.True(library.CanLoad);
            Assert.Equal("Saves/scene/Two.json", tile.Item.FirstScene);
        }

        [Fact]
        public async Task The_library_can_load_a_scene_package_and_not_others()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1", scene: false);
            MakeVar("Old.Pack.1", scene: false);
            Update();
            VarLinks.Create(Path.Combine(temp, "Old.Pack.1.var"), Path.Combine(vars, LibraryFolders.Tidied, "Old", "Old.Pack.1.var"));
            var jobs = new JobRunner(null);
            var stale = new List<string>();
            var library = new LibraryViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys, stale.AddRange, null);
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
            Assert.Equal(new[] { "old.pack.1.var" }, stale);
        }

        [Fact]
        public void A_row_makes_its_tiles_once_and_only_when_read()
        {
            int made = 0;
            var row = new SceneRow(() =>
            {
                made++;
                return new List<SceneTile>();
            });

            Assert.Equal(0, made);
            Assert.Same(row.Tiles, row.Tiles);
            Assert.Equal(1, made);
        }

        [Fact]
        public async Task An_old_row_keeps_its_width_after_the_columns_change()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            MakeVar("E.F.1");
            Update();
            (ScenesViewModel scenes, _, _) = await Open();
            scenes.Columns = 1;
            SceneRow old = scenes.Rows[0];

            scenes.Columns = 3;

            Assert.Single(old.Tiles);
        }

        [Fact]
        public async Task Rows_make_their_tiles_only_when_shown()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            Update();
            (ScenesViewModel scenes, _, _) = await Open();

            scenes.Columns = 1;

            Assert.Equal(2, scenes.Rows.Count);
            Assert.Equal("A.B.1", Assert.Single(scenes.Rows[0].Tiles).Scene.VarName);
            Assert.Same(scenes.Rows[0].Tiles, scenes.Rows[0].Tiles);
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
            try
            {
                await scenes.Reload();
            }
            finally
            {
                gate.Set();
            }
            await older;

            Assert.Equal("Ready in this profile", Tile(scenes, "A.B.1").FitText);
        }
    }
}
