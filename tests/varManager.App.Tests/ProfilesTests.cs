using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public sealed class ProfilesTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmprofiles-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string data;
        private readonly string storeFile;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs = new JobRunner(null);

        public ProfilesTests()
        {
            vam = Path.Combine(root, "VaM");
            vars = Path.Combine(root, "vars");
            data = Path.Combine(root, "app");
            Directory.CreateDirectory(vam);
            Directory.CreateDirectory(vars);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(vam, "VaM.exe"), "");
            VarSwitches.Names(vam);
            VarSwitches.EnsureLink(vam);
            Directory.CreateDirectory(Links("default"));
            paths = new LibraryPaths(vars, vam, data);
            storeFile = Path.Combine(data, "library.db");
        }

        public void Dispose()
        {
            string packs = Path.Combine(vam, "AddonPackages");
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                try
                {
                    if (new DirectoryInfo(packs).Exists)
                        new DirectoryInfo(packs).Delete();
                    Directory.Delete(root, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private string Links(string profile)
        {
            return VarSwitches.LinkFolder(vam, profile);
        }

        private string Tidied(string name)
        {
            return Path.Combine(vars, LibraryFolders.Tidied, name.Split('.')[0], name + ".var");
        }

        private void MakeVar(string name, string references = "")
        {
            using ZipOutputStream zip = new ZipOutputStream(File.Create(Path.Combine(vars, name + ".var")));
            zip.PutNextEntry(new ZipEntry("meta.json"));
            byte[] bytes = Encoding.UTF8.GetBytes("{\"x\":\"" + references + "\"}");
            zip.Write(bytes, 0, bytes.Length);
            zip.CloseEntry();
        }

        private void Update()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            LibraryUpdate.Run(store, paths, (done, total) => { }, default);
        }

        private void LinkInDefault(params string[] names)
        {
            foreach (string name in names)
                Assert.True(VarLinks.Create(Path.Combine(Links("default"), name + ".var"), Tidied(name)));
        }

        private List<string> LinkedIn(string profile)
        {
            return LeanProfiles.Links(Links(profile)).Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()!;
        }

        private async Task<ShellViewModel> Shell(Action<string>? start = null, Func<long>? memory = null)
        {
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            var shell = new ShellViewModel(settingsFile, jobs, () => false, start ?? (_ => { }), memory);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            return shell;
        }

        private static void WaitUntil(Func<bool> done)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!done() && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.True(done());
        }

        // A job ends with a reload, so wait for that as well as for the job.
        private static async Task Run(ShellViewModel shell, Action action)
        {
            Task before = shell.LibraryLoaded;
            action();
            WaitUntil(() => shell.LibraryLoaded != before && !shell.Jobs.IsRunning);
            await shell.LibraryLoaded.WaitAsync(Timeout);
        }

        private static ProfileTile Card(ShellViewModel shell, string name)
        {
            return shell.Profiles!.Cards.Single(card => card.Name == name);
        }

        private async Task<ShellViewModel> ShellWithLean()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1");
            MakeVar("Other.Pack.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");
            shell.Profiles!.Name = "lean";
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));
            return shell;
        }

        private static void Choose(ShellViewModel shell, string pill)
        {
            shell.Profiles!.ModePills.Single(p => p.Label == pill).Command.Execute(null);
        }

        [Theory]
        [InlineData("lean", true)]
        [InlineData("default", false)]
        [InlineData("Default", false)]
        [InlineData("", false)]
        [InlineData("bad.", false)]
        [InlineData("a/b", false)]
        [InlineData("con", false)]
        [InlineData("com1.txt", false)]
        public void Names_must_work_as_a_folder_and_not_be_default(string name, bool valid)
        {
            Assert.Equal(valid, LeanProfiles.IsValidName(name));
        }

        [Fact]
        public void The_base_set_comes_from_session_plugins_the_default_scene_and_base_txt()
        {
            string presets = Path.Combine(vam, "Custom", "PluginPresets");
            string scene = Path.Combine(vam, "Saves", "scene", "MeshedVR");
            Directory.CreateDirectory(presets);
            Directory.CreateDirectory(scene);
            Directory.CreateDirectory(Path.Combine(vars, LibraryFolders.Profiles));
            File.WriteAllText(Path.Combine(presets, "Plugins_UserDefaults.vap"), "{\"a\":\"Plug.In.1:/Custom/a.cs\"}");
            File.WriteAllText(Path.Combine(scene, "default.json"), "{\"a\":\"Scene.Pack.2:/Custom/a.vap\"}");
            File.WriteAllLines(Path.Combine(vars, LibraryFolders.Profiles, "base.txt"), new[] { " Listed.Pack.3 ", "" });

            Assert.Equal(new[] { "Listed.Pack.3", "Plug.In.1", "Scene.Pack.2" }, LeanProfiles.BaseReferences(vam, vars));
        }

        [Fact]
        public async Task Saving_builds_the_links_and_switches_to_the_new_profile()
        {
            ShellViewModel shell = await ShellWithLean();

            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("lean"));
            Assert.Equal("lean", shell.SelectedSwitch);
            Assert.Contains("lean", shell.Switches);
            Assert.False(shell.Picks.HasPicks);
            Assert.Equal(new[] { "A.B.1" }, LeanProfiles.Load(LeanProfiles.ProfilePath(vars, "lean")).Direct);
            Assert.EndsWith("lean", new DirectoryInfo(Comm.ReparsePoint(Path.Combine(vam, "AddonPackages"))).Name);
            Assert.True(Card(shell, "lean").IsActive);
            Assert.False(Card(shell, "default").IsActive);
            Assert.Equal("2 vars linked from 1 pick", Card(shell, "lean").CountText);
            Assert.Equal("In use", Card(shell, "lean").UseLabel);
            Assert.Equal("Switch to this profile", Card(shell, "default").UseLabel);
        }

        [Fact]
        public async Task A_profile_links_the_base_set_too()
        {
            MakeVar("A.B.1");
            MakeVar("Base.Pack.1");
            Update();
            Directory.CreateDirectory(Path.Combine(vars, LibraryFolders.Profiles));
            File.WriteAllLines(Path.Combine(vars, LibraryFolders.Profiles, "base.txt"), new[] { "Base.Pack.1" });
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");

            await Run(shell, () => shell.Profiles!.SaveCommand.Execute(null));

            Assert.Equal(new[] { "A.B.1", "Base.Pack.1" }, LinkedIn("lean"));
        }

        [Fact]
        public async Task Saving_under_a_name_in_another_case_reuses_that_profile()
        {
            ShellViewModel shell = await ShellWithLean();
            shell.Picks.Toggle("Other.Pack.1");
            shell.Profiles!.Name = "LEAN";

            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));

            Assert.Equal(new[] { "Other.Pack.1" }, LinkedIn("lean"));
            Assert.Equal("lean", shell.SelectedSwitch);
            Assert.Equal(2, shell.Switches.Count);
        }

        [Fact]
        public async Task The_plan_shows_before_saving_and_warns_about_replacing()
        {
            ShellViewModel shell = await ShellWithLean();
            ProfilesViewModel profiles = shell.Profiles!;

            shell.Picks.Toggle("Other.Pack.1");
            profiles.Name = "fresh";
            WaitUntil(() => profiles.PlanText.Contains("link 1 var,"));
            Assert.DoesNotContain("replaces", profiles.PlanText);
            Assert.Contains("Links to add: 1, links to remove: 0", profiles.PreviewText);

            profiles.Name = "lean";
            WaitUntil(() => profiles.PlanText.Contains("This replaces the links in lean."));
            Assert.Contains("Links to add: 1, links to remove: 2", profiles.PreviewText);
        }

        [Fact]
        public async Task A_bad_name_stops_the_save_and_says_why()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");

            shell.Profiles!.Name = "default";

            Assert.False(shell.Profiles.CanSave);
            Assert.NotEmpty(shell.Profiles.NameProblem);
            shell.Profiles.SaveCommand.Execute(null);
            Assert.False(jobs.IsRunning);
            Assert.Empty(LinkedIn("default"));
        }

        [Fact]
        public async Task With_no_picks_nothing_can_be_saved()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();

            Assert.True(shell.Profiles!.NoPicks);
            Assert.False(shell.Profiles.CanSave);
        }

        [Fact]
        public async Task Removing_a_pick_on_the_profiles_page_takes_it_off_the_list()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");
            Assert.Equal(new[] { "A.B.1" }, shell.Profiles!.PickRows);

            shell.Profiles.RemovePickCommand.Execute("A.B.1");

            Assert.Empty(shell.Profiles.PickRows);
            Assert.False(shell.Picks.HasPicks);
        }

        [Fact]
        public async Task An_exclude_profile_is_default_minus_the_picks_and_what_only_they_use()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1");
            MakeVar("Keep.Pack.1");
            MakeVar("Off.Pack.1");
            Update();
            LinkInDefault("A.B.1", "Dep.Pack.1", "Keep.Pack.1");
            ShellViewModel shell = await Shell();

            Choose(shell, "Leave these out");
            Assert.Equal("trimmed", shell.Profiles!.Name);
            shell.Picks.Toggle("A.B.1");
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));

            Assert.Equal(new[] { "Keep.Pack.1" }, LinkedIn("trimmed"));
            Assert.Equal(new[] { "A.B.1" }, LeanProfiles.Load(LeanProfiles.ProfilePath(vars, "trimmed")).Exclude);
            Assert.Equal("1 var linked, leaving out 1", Card(shell, "trimmed").CountText);
        }

        [Fact]
        public async Task Choosing_leave_out_on_an_exclude_profile_edits_it()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            MakeVar("Keep.Pack.1");
            Update();
            LinkInDefault("A.B.1", "C.D.1", "Keep.Pack.1");
            ShellViewModel shell = await Shell();
            Choose(shell, "Leave these out");
            shell.Picks.Toggle("A.B.1");
            await Run(shell, () => shell.Profiles!.SaveCommand.Execute(null));

            Choose(shell, "Keep only these");
            Choose(shell, "Leave these out");

            Assert.Equal("trimmed", shell.Profiles!.Name);
            Assert.Equal(new[] { "A.B.1" }, shell.Profiles.PickRows);
            shell.Picks.Toggle("C.D.1");
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));
            Assert.Equal(new[] { "Keep.Pack.1" }, LinkedIn("trimmed"));
        }

        [Fact]
        public async Task A_card_switches_profile()
        {
            ShellViewModel shell = await ShellWithLean();

            await Run(shell, () => shell.Profiles!.UseCommand.Execute(Card(shell, "default")));

            Assert.Equal("default", shell.SelectedSwitch);
            Assert.True(Card(shell, "default").IsActive);
        }

        [Fact]
        public async Task Default_and_plain_profiles_can_only_change_as_far_as_they_are_ours()
        {
            ShellViewModel shell = await ShellWithLean();

            Assert.False(Card(shell, "default").CanChange);
            Assert.False(Card(shell, "default").CanCheck);
            Assert.True(Card(shell, "lean").CanChange);
            Assert.True(Card(shell, "lean").CanCheck);
        }

        [Fact]
        public async Task Check_finds_nothing_wrong_with_a_fresh_profile()
        {
            ShellViewModel shell = await ShellWithLean();

            shell.Profiles!.CheckCommand.Execute(Card(shell, "lean"));
            WaitUntil(() => shell.Profiles.HasCheck);

            Assert.Contains("This profile is healthy.", shell.Profiles.CheckText);
            Assert.False(shell.Profiles.CanRepair);
            Assert.False(shell.Profiles.CanRestore);
        }

        [Fact]
        public async Task Repair_puts_back_a_link_and_drops_one_added_by_hand()
        {
            ShellViewModel shell = await ShellWithLean();
            File.Delete(Path.Combine(Links("lean"), "Dep.Pack.1.var"));
            Assert.True(VarLinks.Create(Path.Combine(Links("lean"), "Other.Pack.1.var"), Tidied("Other.Pack.1")));

            shell.Profiles!.CheckCommand.Execute(Card(shell, "lean"));
            WaitUntil(() => shell.Profiles.HasCheck);
            Assert.True(shell.Profiles.CanRepair);
            Assert.Contains("Dep.Pack.1", shell.Profiles.CheckText);

            await Run(shell, () => shell.Profiles.RepairCommand.Execute(null));

            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("lean"));
            Assert.False(shell.Profiles.HasCheck);
        }

        [Fact]
        public async Task The_check_stays_on_screen_after_the_reload_its_job_causes()
        {
            ShellViewModel shell = await ShellWithLean();

            await Run(shell, () => shell.Profiles!.CheckCommand.Execute(Card(shell, "lean")));

            WaitUntil(() => shell.Profiles!.HasCheck);
            Assert.Equal("Profile check: lean", shell.Profiles!.CheckTitle);
        }

        [Fact]
        public async Task Restore_goes_back_to_the_picks_before_the_last_save()
        {
            ShellViewModel shell = await ShellWithLean();
            shell.Picks.Toggle("Other.Pack.1");
            shell.Profiles!.Name = "lean";
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));
            Assert.Equal(new[] { "Other.Pack.1" }, LinkedIn("lean"));

            shell.Profiles.CheckCommand.Execute(Card(shell, "lean"));
            WaitUntil(() => shell.Profiles.HasCheck);
            Assert.True(shell.Profiles.CanRestore);
            await Run(shell, () => shell.Profiles.RestoreCommand.Execute(null));

            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("lean"));
            Assert.Equal(new[] { "A.B.1" }, LeanProfiles.Load(LeanProfiles.ProfilePath(vars, "lean")).Direct);
        }

        [Fact]
        public async Task Renaming_the_profile_in_use_keeps_it_in_use()
        {
            ShellViewModel shell = await ShellWithLean();
            ProfileTimings.Record(vars, "lean", TimeSpan.FromSeconds(24), 1000);
            ProfilesViewModel profiles = shell.Profiles!;

            profiles.RenameCommand.Execute(Card(shell, "lean"));
            Assert.False(profiles.CanRename);
            profiles.NewName = "default";
            Assert.False(profiles.CanRename);
            profiles.NewName = "renamed";
            Assert.True(profiles.CanRename);
            await Run(shell, () => profiles.ConfirmRenameCommand.Execute(null));
            WaitUntil(() => shell.SelectedSwitch == "renamed");
            await shell.LibraryLoaded.WaitAsync(Timeout);

            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("renamed"));
            Assert.Equal(new[] { "default", "renamed" }, shell.Switches.OrderBy(n => n));
            Assert.True(File.Exists(LeanProfiles.ProfilePath(vars, "renamed")));
            Assert.False(File.Exists(LeanProfiles.ProfilePath(vars, "lean")));
            Assert.Equal(24, ProfileTimings.Load(vars)["renamed"].StartupSeconds);
            Assert.True(VarLinks.Installed(paths.AddonPackages).ContainsKey("A.B.1"));
        }

        [Fact]
        public async Task Renaming_another_profile_leaves_the_current_one_alone()
        {
            ShellViewModel shell = await ShellWithLean();
            await Run(shell, () => shell.Profiles!.UseCommand.Execute(Card(shell, "default")));

            shell.Profiles!.RenameCommand.Execute(Card(shell, "lean"));
            shell.Profiles.NewName = "renamed";
            await Run(shell, () => shell.Profiles.ConfirmRenameCommand.Execute(null));
            WaitUntil(() => shell.Switches.Contains("renamed"));

            Assert.Equal("default", shell.SelectedSwitch);
            Assert.DoesNotContain("lean", shell.Switches);
        }

        [Fact]
        public async Task Deleting_the_profile_in_use_goes_to_default_and_keeps_the_var_files()
        {
            ShellViewModel shell = await ShellWithLean();
            ProfileTimings.Record(vars, "lean", TimeSpan.FromSeconds(24), 1000);
            ProfilesViewModel profiles = shell.Profiles!;

            profiles.DeleteCommand.Execute(Card(shell, "lean"));
            Assert.True(profiles.IsDeleting);
            Assert.Contains("Your var files stay", profiles.DeleteText);
            Assert.True(Directory.Exists(Links("lean")));

            profiles.ConfirmDeleteCommand.Execute(null);
            WaitUntil(() => !shell.Switches.Contains("lean") && !jobs.IsRunning);
            await shell.LibraryLoaded.WaitAsync(Timeout);

            Assert.Equal("default", shell.SelectedSwitch);
            Assert.False(Directory.Exists(Path.GetDirectoryName(Links("lean"))));
            Assert.False(File.Exists(LeanProfiles.ProfilePath(vars, "lean")));
            Assert.False(ProfileTimings.Load(vars).ContainsKey("lean"));
            Assert.True(File.Exists(Tidied("A.B.1")));
            Assert.True(File.Exists(Tidied("Dep.Pack.1")));
        }

        [Fact]
        public async Task Changing_your_mind_about_a_delete_does_nothing()
        {
            ShellViewModel shell = await ShellWithLean();

            shell.Profiles!.DeleteCommand.Execute(Card(shell, "lean"));
            shell.Profiles.CancelChangeCommand.Execute(null);

            Assert.False(shell.Profiles.IsDeleting);
            Assert.True(Directory.Exists(Links("lean")));
        }

        [Fact]
        public async Task Picks_show_on_tiles_and_survive_a_reload()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            LibraryViewModel library = shell.Library!;
            library.Columns = 10;
            LibraryTile Tile() => library.Rows.OfType<TileRow>().SelectMany(row => row.Tiles).Single();

            library.SelectCommand.Execute(Tile());
            Assert.Equal("Add to profile picks", library.PickLabel);
            library.TogglePickCommand.Execute(null);

            Assert.True(Tile().IsPicked);
            Assert.Equal("Remove from picks", library.PickLabel);
            Assert.Equal("1 picked", shell.Picks.CountText);

            await library.Reload();
            Assert.True(Tile().IsPicked);

            library.TogglePickCommand.Execute(null);
            Assert.False(Tile().IsPicked);
            Assert.False(shell.Picks.HasPicks);
        }

        [Fact]
        public async Task Build_profile_opens_the_profiles_page()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();

            shell.BuildProfileCommand.Execute(null);

            Assert.True(shell.IsProfilesPage);
            Assert.False(shell.IsOtherPage);
        }

        [Fact]
        public void Timings_round_trip_and_ignore_a_bad_line()
        {
            ProfileTimings.Record(vars, "lean", TimeSpan.FromSeconds(24.5), 5_000_000_000);
            File.AppendAllLines(Path.Combine(vars, LibraryFolders.Profiles, "timings.txt"), new[] { "broken line" });

            ProfileTiming timing = ProfileTimings.Load(vars)["LEAN"];

            Assert.Equal(24.5, timing.StartupSeconds);
            Assert.Equal(5_000_000_000, timing.RamBytes);
        }

        [Fact]
        public void A_card_shows_what_was_measured()
        {
            var measured = new ProfileTile(new ProfileSummary
            {
                Name = "lean",
                Timing = new ProfileTiming { StartupSeconds = 24.4, RamBytes = 5046586573 },
            }, false);
            var unmeasured = new ProfileTile(new ProfileSummary { Name = "fresh" }, false);

            Assert.Equal("24 s", measured.Startup);
            Assert.Equal("4.7 GB", measured.Ram);
            Assert.Equal("Not measured", unmeasured.Startup);
            Assert.Equal("Not measured", unmeasured.Ram);
            Assert.Equal("Not measured", measured.SceneLoad);
        }

        [Fact]
        public async Task The_probe_reads_startup_when_the_plugin_takes_the_request()
        {
            Task<StartupReading> measuring = StartupProbe.Measure(vam, () => 123, TimeSpan.FromMilliseconds(10), Timeout, default);
            string request = VamBridge.RequestFile(vam);
            Assert.Contains("\"rescan\" : \"false\"", File.ReadAllText(request));

            await Task.Delay(100);
            File.Delete(request);
            StartupReading reading = await measuring.WaitAsync(Timeout);

            Assert.Equal(123, reading.RamBytes);
            Assert.True(reading.Startup >= TimeSpan.FromMilliseconds(100));
        }

        [Fact]
        public async Task The_probe_leaves_a_request_already_waiting()
        {
            string request = VamBridge.RequestFile(vam);
            Directory.CreateDirectory(Path.GetDirectoryName(request)!);
            File.WriteAllText(request, "{\"waiting\":\"yes\"}");

            Task<StartupReading> measuring = StartupProbe.Measure(vam, () => 1, TimeSpan.FromMilliseconds(10), Timeout, default);

            Assert.Contains("waiting", File.ReadAllText(request));
            File.Delete(request);
            await measuring.WaitAsync(Timeout);
        }

        [Fact]
        public async Task With_no_plugin_the_probe_gives_up_and_cleans_up()
        {
            StartupReading reading = await StartupProbe.Measure(vam, () => 1, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), default)
                .WaitAsync(Timeout);

            Assert.Null(reading);
            Assert.False(File.Exists(VamBridge.RequestFile(vam)));
        }

        [Fact]
        public async Task A_vam_that_is_gone_gives_no_reading()
        {
            Task<StartupReading> measuring = StartupProbe.Measure(vam, () => 0, TimeSpan.FromMilliseconds(10), Timeout, default);
            File.Delete(VamBridge.RequestFile(vam));

            Assert.Null(await measuring.WaitAsync(Timeout));
        }

        [Fact]
        public async Task Launching_records_the_timing_on_the_profile_in_use()
        {
            ShellViewModel shell = await ShellWithLean();
            string settingsFile = Path.Combine(data, "settings.json");
            var launching = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 2147483648);
            await launching.LibraryLoaded.WaitAsync(Timeout);

            launching.LaunchCommand.Execute(null);
            string request = VamBridge.RequestFile(vam);
            Assert.True(File.Exists(request));
            File.Delete(request);

            WaitUntil(() => launching.Profiles!.Cards.Single(card => card.Name == "lean").Ram == "2.0 GB");
            Assert.Equal("Not measured", launching.Profiles!.Cards.Single(card => card.Name == "default").Ram);
            Assert.Equal("lean", shell.SelectedSwitch);
        }
    }
}
