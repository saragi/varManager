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
            await shell.Profiles!.Work.WaitAsync(Timeout);
            WaitUntil(() => shell.LibraryLoaded != before);
            await Settle(shell);
        }

        // A job's reload starts as it ends, and Work finishes after anything that follows the job,
        // so once Work is done the newest reload is the last one.
        private static async Task Settle(ShellViewModel shell)
        {
            await shell.Profiles!.Work.WaitAsync(Timeout);
            WaitUntil(() => !shell.Jobs.IsRunning);
            Task last;
            do
            {
                last = shell.LibraryLoaded;
                await last.WaitAsync(Timeout);
            }
            while (last != shell.LibraryLoaded);
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

        [Fact]
        public async Task Reading_links_during_a_switch_never_leaves_a_real_addon_packages_folder()
        {
            Directory.CreateDirectory(Links("other"));
            string packs = Path.Combine(vam, "AddonPackages");
            using var stop = new CancellationTokenSource();
            Task reading = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                    VarLinks.ReadInstalled(packs);
            });

            try
            {
                for (int i = 0; i < 4000; i++)
                {
                    Assert.NotEqual(SwitchChange.Failed, VarSwitches.Point(vam, i % 2 == 0 ? "other" : "default", out _));
                    Assert.True(new DirectoryInfo(packs).Attributes.HasFlag(FileAttributes.ReparsePoint), "round " + i);
                }
            }
            finally
            {
                stop.Cancel();
            }
            await reading.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Two_openers_of_a_new_store_both_get_a_working_store()
        {
            for (int round = 0; round < 30; round++)
            {
                string file = Path.Combine(data, "race" + round + ".db");
                using var start = new ManualResetEventSlim();
                List<Task> opening = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    using LibraryStore store = LibraryStore.Open(file);
                    Assert.Equal(0, store.Count("vars"));
                })).ToList();

                start.Set();
                await Task.WhenAll(opening).WaitAsync(Timeout);
            }
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
            WaitUntil(() => profiles.PlanText.Contains("link 1 var,") && profiles.PreviewText.Contains("Links to add: 1, links to remove: 0"));
            Assert.DoesNotContain("replaces", profiles.PlanText);

            profiles.Name = "lean";
            WaitUntil(() => profiles.PlanText.Contains("This replaces the links in lean.") && profiles.PreviewText.Contains("Links to add: 1, links to remove: 2"));
        }

        [Fact]
        public async Task The_plan_uses_vars_added_after_it_first_ran()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");
            WaitUntil(() => shell.Profiles!.PlanText.Contains("link 1 var,"));

            MakeVar("New.Pack.1");
            Update();
            await shell.Profiles!.Reload();
            shell.Picks.Toggle("New.Pack.1");

            WaitUntil(() => shell.Profiles.PlanText.Contains("link 2 vars,"));
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
        public async Task Spaces_around_a_name_are_ignored()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");

            shell.Profiles!.Name = " default ";
            Assert.False(shell.Profiles.CanSave);

            shell.Profiles.Name = "  mine ";
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));

            Assert.Equal("mine", shell.SelectedSwitch);
        }

        [Fact]
        public async Task A_failed_build_keeps_the_picks_and_saves_nothing()
        {
            MakeVar("A.B.1");
            Update();
            Directory.CreateDirectory(Path.Combine(Links("stuck"), "A.B.1.var"));
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");
            shell.Profiles!.Name = "stuck";

            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));

            Assert.Contains("failed", jobs.Note);
            Assert.True(shell.Picks.HasPicks);
            Assert.False(File.Exists(LeanProfiles.ProfilePath(vars, "stuck")));
            Assert.Equal("default", shell.SelectedSwitch);
        }

        [Fact]
        public async Task A_failed_build_of_a_new_profile_leaves_no_folder()
        {
            MakeVar("A.B.1");
            Update();
            File.Delete(Tidied("A.B.1"));
            ShellViewModel shell = await Shell();
            shell.Picks.Toggle("A.B.1");
            shell.Profiles!.Name = "fresh";

            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));

            Assert.Contains("failed", jobs.Note);
            Assert.False(Directory.Exists(Path.GetDirectoryName(Links("fresh"))));
            Assert.True(shell.Picks.HasPicks);
        }

        [Fact]
        public async Task Rebuilding_a_profile_forgets_a_timing_measured_on_other_links()
        {
            ShellViewModel shell = await ShellWithLean();
            ProfileTimings.Record(vars, "lean", TimeSpan.FromSeconds(24), 1000);

            shell.Picks.Toggle("A.B.1");
            shell.Profiles!.Name = "lean";
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));
            Assert.True(ProfileTimings.Load(vars).ContainsKey("lean"));

            shell.Picks.Toggle("Other.Pack.1");
            shell.Profiles.Name = "lean";
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));
            Assert.False(ProfileTimings.Load(vars).ContainsKey("lean"));
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
        public async Task An_exclude_profile_keeps_what_others_share_and_reads_default_as_vam_would()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Keep.Pack.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1");
            MakeVar("Top.Pack.1");
            MakeVar("Dead.Pack.1");
            Update();
            LinkInDefault("A.B.1", "Keep.Pack.1", "Dep.Pack.1");
            string top = Path.GetDirectoryName(Links("default"))!;
            Assert.True(VarLinks.Create(Path.Combine(top, "Top.Pack.1.var"), Tidied("Top.Pack.1")));
            string gone = Path.Combine(root, "gone.var");
            File.WriteAllText(gone, "x");
            Assert.True(VarLinks.Create(Path.Combine(Links("default"), "Dead.Pack.1.var"), gone));
            File.Delete(gone);
            ShellViewModel shell = await Shell();
            Choose(shell, "Leave these out");
            shell.Picks.Toggle("A.B.1");

            WaitUntil(() => shell.Profiles!.PlanText.StartsWith("Links 3 vars from default"));
            await Run(shell, () => shell.Profiles!.SaveCommand.Execute(null));

            Assert.Equal(new[] { "Dep.Pack.1", "Keep.Pack.1", "Top.Pack.1" }, LinkedIn("trimmed"));
        }

        [Fact]
        public async Task An_exclude_profile_can_be_checked()
        {
            MakeVar("A.B.1");
            MakeVar("Keep.Pack.1");
            Update();
            LinkInDefault("A.B.1", "Keep.Pack.1");
            ShellViewModel shell = await Shell();
            Choose(shell, "Leave these out");
            shell.Picks.Toggle("A.B.1");
            await Run(shell, () => shell.Profiles!.SaveCommand.Execute(null));

            await Run(shell, () => shell.Profiles!.CheckCommand.Execute(Card(shell, "trimmed")));

            WaitUntil(() => shell.Profiles!.HasCheck);
            Assert.Contains("This profile is healthy.", shell.Profiles!.CheckText);
        }

        [Fact]
        public async Task The_mode_sets_the_name_and_stays_after_a_save()
        {
            MakeVar("A.B.1");
            MakeVar("Keep.Pack.1");
            Update();
            LinkInDefault("A.B.1", "Keep.Pack.1");
            ShellViewModel shell = await Shell();
            ProfilesViewModel profiles = shell.Profiles!;

            Choose(shell, "Leave these out");
            Assert.True(profiles.ModePills.Single(p => p.Label == "Leave these out").IsActive);
            Assert.False(profiles.ModePills.Single(p => p.Label == "Keep only these").IsActive);
            shell.Picks.Toggle("A.B.1");
            await Run(shell, () => profiles.SaveCommand.Execute(null));
            Assert.Equal("trimmed 2", profiles.Name);

            Choose(shell, "Keep only these");
            Assert.Equal("lean", profiles.Name);
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
        public async Task Default_can_not_be_renamed_or_deleted()
        {
            ShellViewModel shell = await ShellWithLean();
            ProfilesViewModel profiles = shell.Profiles!;

            Assert.False(Card(shell, "default").CanChange);
            Assert.False(Card(shell, "default").CanCheck);
            Assert.True(Card(shell, "lean").CanChange);
            Assert.True(Card(shell, "lean").CanCheck);

            profiles.DeleteCommand.Execute(Card(shell, "default"));
            Assert.False(profiles.IsDeleting);
            profiles.ConfirmDeleteCommand.Execute(null);
            profiles.RenameCommand.Execute(Card(shell, "default"));
            Assert.False(profiles.IsRenaming);

            Assert.True(Directory.Exists(Links("default")));
        }

        [Fact]
        public async Task A_folder_made_by_hand_is_a_plain_profile()
        {
            MakeVar("A.B.1");
            Update();
            Directory.CreateDirectory(Links("foo"));
            Assert.True(VarLinks.Create(Path.Combine(Links("foo"), "A.B.1.var"), Tidied("A.B.1")));

            ShellViewModel shell = await Shell();

            Assert.False(Card(shell, "foo").CanCheck);
            Assert.True(Card(shell, "foo").CanChange);
            Assert.Equal("1 var linked", Card(shell, "foo").CountText);
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
            Assert.DoesNotContain("Could not", jobs.Note);
            Assert.True(File.Exists(LeanProfiles.ProfilePath(vars, "renamed")));
            Assert.False(File.Exists(LeanProfiles.ProfilePath(vars, "lean")));
            Assert.Equal(24, ProfileTimings.Load(vars)["renamed"].StartupSeconds);
            Assert.True(VarLinks.Installed(paths.AddonPackages).ContainsKey("A.B.1"));
        }

        [Fact]
        public async Task A_rename_that_fails_after_the_folder_moved_still_follows_the_folder()
        {
            ShellViewModel shell = await ShellWithLean();
            ProfilesViewModel profiles = shell.Profiles!;
            using (new FileStream(LeanProfiles.ProfilePath(vars, "lean"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                profiles.RenameCommand.Execute(Card(shell, "lean"));
                profiles.NewName = "renamed";
                profiles.ConfirmRenameCommand.Execute(null);
                WaitUntil(() => shell.SelectedSwitch == "renamed");
            }
            await Settle(shell);

            Assert.True(VarLinks.Installed(paths.AddonPackages).ContainsKey("A.B.1"));
            Assert.True(File.Exists(LeanProfiles.ProfilePath(vars, "lean")));
            Assert.Equal("renamed", shell.SelectedSwitch);
            Assert.Contains("renamed", shell.Switches);
        }

        [Fact]
        public async Task A_rename_carries_the_previous_version_so_restore_still_works()
        {
            ShellViewModel shell = await ShellWithLean();
            shell.Picks.Toggle("Other.Pack.1");
            shell.Profiles!.Name = "lean";
            await Run(shell, () => shell.Profiles.SaveCommand.Execute(null));

            shell.Profiles.RenameCommand.Execute(Card(shell, "lean"));
            shell.Profiles.NewName = "renamed";
            await Run(shell, () => shell.Profiles.ConfirmRenameCommand.Execute(null));
            await Run(shell, () => shell.Profiles.CheckCommand.Execute(Card(shell, "renamed")));

            WaitUntil(() => shell.Profiles.HasCheck);
            Assert.True(shell.Profiles.CanRestore);
            await Run(shell, () => shell.Profiles.RestoreCommand.Execute(null));
            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("renamed"));
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
            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("renamed"));
            Assert.True(File.Exists(LeanProfiles.ProfilePath(vars, "renamed")));
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
        public async Task Deleting_another_profile_leaves_the_current_one_alone()
        {
            ShellViewModel shell = await ShellWithLean();
            await Run(shell, () => shell.Profiles!.UseCommand.Execute(Card(shell, "default")));

            shell.Profiles!.DeleteCommand.Execute(Card(shell, "lean"));
            await Run(shell, () => shell.Profiles.ConfirmDeleteCommand.Execute(null));

            Assert.Equal("default", shell.SelectedSwitch);
            Assert.DoesNotContain("lean", shell.Switches);
        }

        [Fact]
        public async Task Deleting_keeps_real_files_in_the_folder_and_says_so()
        {
            ShellViewModel shell = await ShellWithLean();
            string top = Path.GetDirectoryName(Links("lean"))!;
            string real = Path.Combine(top, "Custom", "real.var");
            Directory.CreateDirectory(Path.GetDirectoryName(real)!);
            File.WriteAllText(real, "real");
            File.WriteAllText(Path.Combine(Links("lean"), "A.B.1.var.disabled"), "");
            string notMarker = Path.Combine(top, "real.disabled");
            File.WriteAllText(notMarker, "real");

            shell.Profiles!.DeleteCommand.Execute(Card(shell, "lean"));
            shell.Profiles.ConfirmDeleteCommand.Execute(null);
            WaitUntil(() => jobs.Note.Contains("2 real files"));
            await Settle(shell);

            Assert.True(File.Exists(real));
            Assert.True(File.Exists(notMarker));
            Assert.False(Directory.Exists(Links("lean")));
            Assert.True(File.Exists(Tidied("A.B.1")));
        }

        [Fact]
        public void Deleting_removes_a_folder_link_without_following_it()
        {
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            string keep = Path.Combine(outside, "keep.var");
            File.WriteAllText(keep, "x");
            string top = Path.GetDirectoryName(Links("lean"))!;
            Directory.CreateDirectory(top);
            Assert.True(Comm.CreateSymbolicLink(Path.Combine(top, "linked"), outside, Comm.SYMBOLIC_LINK_FLAG.Directory | Comm.SYMBOLIC_LINK_FLAG.AllowUnprivilegedCreate));

            Assert.Equal(0, ProfileCatalog.Delete(vam, vars, "lean"));

            Assert.True(File.Exists(keep));
            Assert.False(Directory.Exists(top));
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
        public async Task An_empty_profile_can_be_made_and_used()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.Profiles!.Name = "blank";
            Assert.True(shell.Profiles.CanMakeEmpty);

            await Run(shell, () => shell.Profiles.MakeEmptyCommand.Execute(null));

            Assert.Equal("blank", shell.SelectedSwitch);
            Assert.Contains("blank", shell.Switches);
            Assert.Empty(LinkedIn("blank"));

            shell.Profiles.Name = "BLANK";
            Assert.False(shell.Profiles.CanMakeEmpty);
        }

        [Fact]
        public async Task An_empty_profile_needs_no_picks_and_a_good_name()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();

            shell.Profiles!.Name = "default";
            Assert.False(shell.Profiles.CanMakeEmpty);

            shell.Profiles.Name = "blank";
            shell.Picks.Toggle("A.B.1");
            Assert.False(shell.Profiles.CanMakeEmpty);
            shell.Profiles.MakeEmptyCommand.Execute(null);

            Assert.False(Directory.Exists(Links("blank")));
        }

        [Fact]
        public async Task A_list_of_linked_vars_saves_and_turns_back_on_with_dependencies()
        {
            ShellViewModel shell = await ShellWithLean();
            string file = Path.Combine(data, "list.txt");

            shell.Profiles!.ExportLinked(file);
            WaitUntil(() => !jobs.IsRunning);
            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, File.ReadAllLines(file));

            await Run(shell, () => shell.Profiles.UseCommand.Execute(Card(shell, "default")));
            File.WriteAllLines(file, new[] { "A.B.1", "", "Never.Was.1" });
            shell.Profiles.TurnOnFromList(file);
            WaitUntil(() => !jobs.IsRunning);

            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, LinkedIn("default"));
            Assert.Equal("Turned on 2 vars. 1 name isn't in the library.", jobs.Note);

            File.WriteAllLines(file, new[] { "A.B.1", "a.b.1" });
            shell.Profiles.TurnOnFromList(file);
            WaitUntil(() => !jobs.IsRunning);
            Assert.Equal("Turned on 0 vars.", jobs.Note);
        }

        [Fact]
        public void A_list_says_what_could_not_be_linked()
        {
            MakeVar("A.B.1");
            Update();
            Directory.CreateDirectory(Path.Combine(paths.AddonPackages, LibraryFolders.VarsLink, "A.B.1.var"));
            string file = Path.Combine(data, "list.txt");
            File.WriteAllLines(file, new[] { "A.B.1" });
            using LibraryStore store = LibraryStore.Open(storeFile);

            ListResult result = ProfileCatalog.LinkFromList(store, paths, file);

            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Linked);
        }

        [Fact]
        public async Task Profile_actions_before_any_update_create_no_store()
        {
            ShellViewModel shell = await Shell();
            Directory.CreateDirectory(Links("foo"));
            var profile = new LeanProfile();
            profile.Direct.Add("A.B.1");
            LeanProfiles.Save(profile, LeanProfiles.ProfilePath(vars, "foo"));
            await shell.Profiles!.Reload();
            string file = Path.Combine(data, "list.txt");
            File.WriteAllLines(file, new[] { "A.B.1" });

            shell.Profiles.TurnOnFromList(file);
            Assert.Equal("Run Update DB first.", jobs.Note);
            shell.Profiles.CheckCommand.Execute(Card(shell, "foo"));
            await shell.Profiles.Work.WaitAsync(Timeout);

            shell.Picks.Toggle("A.B.1");
            shell.Profiles.Name = "another";
            await shell.Profiles.Reload();
            shell.Profiles.SaveCommand.Execute(null);
            await shell.Profiles.Work.WaitAsync(Timeout);

            Assert.Equal("Run Update DB first.", jobs.Note);
            Assert.False(File.Exists(storeFile));
        }

        [Fact]
        public async Task Settings_open_after_setup_and_can_be_left_unchanged()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();

            shell.OpenSettingsCommand.Execute(null);
            Assert.True(shell.NeedsSetup);
            Assert.True(shell.CanCancelSetup);
            shell.VarsPath = Path.Combine(root, "elsewhere");

            shell.VamPath = Path.Combine(root, "elsewhere2");

            shell.CancelSetupCommand.Execute(null);

            Assert.False(shell.NeedsSetup);
            Assert.Equal(vars, shell.VarsPath);
            Assert.Equal(vam, shell.VamPath);
            await shell.LibraryLoaded.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Settings_wait_for_a_running_job()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            using var gate = new ManualResetEventSlim();
            Task busy = jobs.Start("Busy", "done", _ => gate.Wait(Timeout))!;

            shell.OpenSettingsCommand.Execute(null);

            Assert.False(shell.NeedsSetup);
            Assert.Contains("Wait for the current job", jobs.Note);
            gate.Set();
            await busy.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Saving_settings_waits_for_a_running_job()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            string other = Path.Combine(root, "vars2");
            Directory.CreateDirectory(other);
            shell.OpenSettingsCommand.Execute(null);
            shell.VarsPath = other;
            using var gate = new ManualResetEventSlim();
            Task busy = jobs.Start("Busy", "done", _ => gate.Wait(Timeout))!;

            shell.SaveSetupCommand.Execute(null);

            Assert.True(shell.NeedsSetup);
            Assert.NotEmpty(shell.SetupProblem);
            Assert.Equal(vars, AppSettings.Load(Path.Combine(data, "settings.json")).VarsPath);
            gate.Set();
            await busy.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Leaving_settings_picks_up_a_change_made_while_they_were_open()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            shell.OpenSettingsCommand.Execute(null);
            MakeVar("C.D.1");
            Update();

            Task before = shell.LibraryLoaded;
            shell.CancelSetupCommand.Execute(null);

            Assert.NotSame(before, shell.LibraryLoaded);
            await shell.LibraryLoaded.WaitAsync(Timeout);
        }

        [Fact]
        public async Task New_settings_take_effect_when_saved()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            string other = Path.Combine(root, "vars2");
            Directory.CreateDirectory(other);

            shell.OpenSettingsCommand.Execute(null);
            shell.VarsPath = other;
            shell.SaveSetupCommand.Execute(null);

            Assert.False(shell.NeedsSetup);
            Assert.False(shell.CanCancelSetup);
            Assert.Equal(other, AppSettings.Load(Path.Combine(data, "settings.json")).VarsPath);
            await shell.LibraryLoaded.WaitAsync(Timeout);
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

            shell.Picks.Toggle("a.b.1");
            Assert.True(Tile().IsPicked);
            shell.Picks.Toggle("A.B.1");
            Assert.False(Tile().IsPicked);
        }

        [Fact]
        public async Task Saving_setup_again_leaves_the_old_pages_behind()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();
            LibraryViewModel oldLibrary = shell.Library!;
            ProfilesViewModel oldProfiles = shell.Profiles!;
            oldLibrary.Columns = 10;
            LibraryTile oldTile = oldLibrary.Rows.OfType<TileRow>().SelectMany(row => row.Tiles).Single();

            shell.SaveSetupCommand.Execute(null);
            await Settle(shell);
            Assert.NotSame(oldProfiles, shell.Profiles);
            shell.Picks.Toggle("A.B.1");

            Assert.Empty(oldProfiles.PickRows);
            Assert.False(oldTile.IsPicked);
            Assert.Equal(new[] { "A.B.1" }, shell.Profiles!.PickRows);
        }

        [Fact]
        public async Task Build_profile_opens_the_profiles_page()
        {
            MakeVar("A.B.1");
            Update();
            ShellViewModel shell = await Shell();

            shell.BuildProfileCommand.Execute(null);

            Assert.True(shell.IsProfilesPage);
            Assert.False(shell.IsMissingPage);
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
            Assert.True(reading.Startup >= TimeSpan.FromMilliseconds(50));
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
        public async Task The_probe_never_deletes_a_request_it_did_not_write()
        {
            string request = VamBridge.RequestFile(vam);
            Task<StartupReading> measuring = StartupProbe.Measure(vam, () => 1, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200), default);
            File.WriteAllText(request, "{\"resources\":[]}");

            Assert.Null(await measuring.WaitAsync(Timeout));

            Assert.Contains("resources", File.ReadAllText(request));
        }

        [Fact]
        public async Task The_probe_stops_when_vam_closes_before_taking_the_request()
        {
            int calls = 0;
            StartupReading reading = await StartupProbe.Measure(vam, () => Interlocked.Increment(ref calls) < 3 ? 1 : 0, TimeSpan.FromMilliseconds(10), Timeout, default)
                .WaitAsync(Timeout);

            Assert.Null(reading);
            Assert.False(File.Exists(VamBridge.RequestFile(vam)));
        }

        [Fact]
        public async Task A_cancelled_probe_stops()
        {
            using var cancel = new CancellationTokenSource();
            Task<StartupReading> measuring = StartupProbe.Measure(vam, () => 1, TimeSpan.FromMilliseconds(10), Timeout, cancel.Token);

            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring.WaitAsync(Timeout));
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
            Assert.True(ProfileTimings.Load(vars)["lean"].StartupSeconds > 0);
        }

        [Fact]
        public async Task A_second_launch_replaces_the_first_timer()
        {
            ShellViewModel shell = await ShellWithLean();
            string settingsFile = Path.Combine(data, "settings.json");
            var launching = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 1);
            await launching.LibraryLoaded.WaitAsync(Timeout);
            string request = VamBridge.RequestFile(vam);

            launching.LaunchCommand.Execute(null);
            launching.SelectedSwitch = "default";
            launching.LaunchCommand.Execute(null);
            File.Delete(request);

            WaitUntil(() => ProfileTimings.Load(vars).ContainsKey("default"));
            await Task.Delay(1500);
            Assert.False(ProfileTimings.Load(vars).ContainsKey("lean"));
            WaitUntil(() => launching.Profiles!.Cards.Single(card => card.Name == "default").Ram != "Not measured");
        }

        [Fact]
        public async Task Launching_while_vam_runs_measures_nothing()
        {
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            var shell = new ShellViewModel(settingsFile, jobs, () => true, _ => { }, () => 1);
            await shell.LibraryLoaded.WaitAsync(Timeout);

            shell.LaunchCommand.Execute(null);

            Assert.False(File.Exists(VamBridge.RequestFile(vam)));
        }
    }
}
