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
    public sealed class HealthTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmhealth-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string data;
        private readonly string storeFile;
        private readonly string links;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs = new JobRunner(null);

        public HealthTests()
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
            links = VarSwitches.LinkFolder(vam, "default");
            Directory.CreateDirectory(links);
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

        private UpdateReport Update()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            return LibraryUpdate.Run(store, paths, (done, total) => { }, default);
        }

        private HealthReport Report()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            return LibraryHealth.Load(store, paths, _ => { });
        }

        private string Held(string folder, string name, string text = "x")
        {
            string file = Path.Combine(vars, folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            return file;
        }

        private async Task<HealthViewModel> Open()
        {
            var health = new HealthViewModel(storeFile, paths, jobs, () => { });
            await health.Reload();
            return health;
        }

        private static void Press(HealthViewModel health, string cardText)
        {
            health.Cards.Single(card => card.Text.StartsWith(cardText)).Action!.Execute(null);
        }

        private async Task Confirm(HealthViewModel health)
        {
            health.ConfirmCommand.Execute(null);
            await health.Work.WaitAsync(Timeout);
        }

        [Fact]
        public void The_report_counts_what_is_stored_and_not_linked()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            Update();
            Assert.True(VarLinks.Create(Path.Combine(links, "A.B.1.var"), Tidied("A.B.1")));

            HealthReport report = Report();

            Assert.Equal(2, report.VarCount);
            Assert.Equal(1, report.NotLinked);
            Assert.True(report.LibraryBytes > 0);
            Assert.NotNull(report.FreeBytes);
            Assert.Equal(Path.GetPathRoot(root)!.TrimEnd('\\'), report.Drive, ignoreCase: true);
        }

        [Fact]
        public void A_stale_var_is_old_and_used_by_nothing()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            MakeVar("Used.Pack.1");
            MakeVar("Used.Pack.2");
            MakeVar("User.Pack.1", "Used.Pack.1:/Custom/a.vap");
            Update();

            Assert.Equal(new[] { "A.B.1" }, Report().Stale);
        }

        [Fact]
        public void A_var_a_save_or_profile_uses_is_not_stale()
        {
            MakeVar("Saved.Pack.1");
            MakeVar("Saved.Pack.2");
            MakeVar("Picked.Pack.1");
            MakeVar("Picked.Pack.2");
            Update();
            string scene = Path.Combine(vam, "Saves", "scene", "mine.json");
            Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
            File.WriteAllText(scene, "{\"a\":\"Saved.Pack.1:/Custom/a.vap\"}");
            var profile = new LeanProfile();
            profile.Direct.Add("Picked.Pack.1");
            LeanProfiles.Save(profile, LeanProfiles.ProfilePath(vars, "lean"));

            Assert.Empty(Report().Stale);
        }

        [Fact]
        public void Missing_dependencies_are_listed_most_needed_first()
        {
            MakeVar("A.B.1", "Gone.One.1:/Custom/a.vap");
            MakeVar("C.D.1", "Gone.Two.1:/Custom/a.vap");
            MakeVar("E.F.1", "Gone.Two.1:/Custom/a.vap");
            Update();

            List<MissingNeed> missing = Report().Missing;

            Assert.Equal(new[] { "Gone.Two.1", "Gone.One.1" }, missing.Select(need => need.Reference));
            Assert.Equal(2, missing[0].NeededBy);
        }

        [Fact]
        public void Held_files_are_grouped_and_say_if_the_library_has_a_copy()
        {
            MakeVar("A.B.1");
            Update();
            Held(LibraryFolders.Redundant, "A.B.1.var", "12345");
            Held(LibraryFolders.Redundant, "Only.Here.1.var", "123");
            Held(LibraryFolders.Holding + "\\batch", "moved.txt");

            HeldGroup group = Assert.Single(Report().Held);

            Assert.Equal("Duplicates set aside", group.Title);
            Assert.Equal(2, group.Files.Count);
            Assert.Equal(8, group.Bytes);
            Assert.Equal(1, group.NotInLibrary);
        }

        [Fact]
        public async Task Nothing_is_deleted_until_confirmed_and_then_only_what_was_reviewed()
        {
            MakeVar("A.B.1");
            Update();
            string duplicate = Held(LibraryFolders.Redundant, "A.B.1.var");
            HealthViewModel health = await Open();

            Press(health, "Duplicates set aside");
            Assert.True(health.HasPending);
            Assert.Contains("for good", health.Pending!.Note);
            Assert.True(File.Exists(duplicate));

            string later = Held(LibraryFolders.Redundant, "Later.Pack.1.var");
            await Confirm(health);

            Assert.False(File.Exists(duplicate));
            Assert.True(File.Exists(later));
            Assert.False(health.HasPending);
            Assert.Equal("Deleted 1 file.", jobs.Note);
        }

        [Fact]
        public async Task Leaving_it_changes_nothing()
        {
            MakeVar("A.B.1");
            Update();
            string duplicate = Held(LibraryFolders.Redundant, "A.B.1.var");
            HealthViewModel health = await Open();

            Press(health, "Duplicates set aside");
            health.CancelCommand.Execute(null);

            Assert.False(health.HasPending);
            Assert.True(File.Exists(duplicate));
        }

        [Fact]
        public async Task The_review_warns_when_the_library_has_no_copy()
        {
            MakeVar("A.B.1");
            Update();
            Held(LibraryFolders.Redundant, "Only.Here.1.var");
            HealthViewModel health = await Open();

            Press(health, "Duplicates set aside");

            Assert.Contains("1 var here has no copy", health.Pending!.Note);
            Assert.Contains("no copy in the library", health.Pending.Lines[0]);
        }

        [Fact]
        public async Task A_deleted_holding_folder_goes_whole_and_undo_no_longer_offers_it()
        {
            MakeVar("A.B.1");
            Update();
            Held(LibraryFolders.Holding + "\\20260101", "A.B.1.var");
            string log = Held(LibraryFolders.Holding + "\\20260101", "moved.txt");
            Assert.NotNull(MoveOut.LatestHolding(vars));
            HealthViewModel health = await Open();

            Press(health, "Moved out of the library");
            await Confirm(health);

            Assert.False(File.Exists(log));
            Assert.False(Directory.Exists(Path.GetDirectoryName(log)));
            Assert.Null(MoveOut.LatestHolding(vars));
        }

        [Fact]
        public async Task Stale_vars_move_to_holding_and_can_come_back()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            Update();
            Assert.True(VarLinks.Create(Path.Combine(links, "A.B.1.var"), Tidied("A.B.1")));
            HealthViewModel health = await Open();

            Press(health, "Old versions nothing uses");
            await Confirm(health);

            Assert.False(File.Exists(Tidied("A.B.1")));
            Assert.True(File.Exists(Tidied("A.B.2")));
            Assert.False(File.Exists(Path.Combine(links, "A.B.1.var")));
            string holding = MoveOut.LatestHolding(vars)!;
            Assert.True(File.Exists(Path.Combine(holding, "A.B.1.var")));
            using (LibraryStore store = LibraryStore.Open(storeFile))
                Assert.Equal(1, store.Count("vars"));

            Assert.Empty(MoveOut.Undo(holding));
            Assert.True(File.Exists(Tidied("A.B.1")));
            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
        }

        [Fact]
        public async Task Stale_vars_stay_if_something_came_to_need_them_since_the_review()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            Update();
            HealthViewModel health = await Open();
            Press(health, "Old versions nothing uses");

            string scene = Path.Combine(vam, "Saves", "scene", "mine.json");
            Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
            File.WriteAllText(scene, "{\"a\":\"A.B.1:/Custom/a.vap\"}");
            await Confirm(health);

            Assert.True(File.Exists(Tidied("A.B.1")));
            Assert.Contains("nothing moved", jobs.Note);
        }

        [Fact]
        public async Task Repair_points_a_broken_link_at_the_var_and_drops_one_to_a_lost_var()
        {
            MakeVar("A.B.1");
            Update();
            string gone = Path.Combine(root, "A.B.1.var");
            File.WriteAllText(gone, "x");
            string good = Path.Combine(links, "A.B.1.var");
            Assert.True(VarLinks.Create(good, gone));
            string lostTarget = Path.Combine(root, "Lost.Pack.1.var");
            File.WriteAllText(lostTarget, "x");
            string lost = Path.Combine(links, "Lost.Pack.1.var");
            Assert.True(VarLinks.Create(lost, lostTarget));
            File.Delete(gone);
            File.Delete(lostTarget);
            HealthViewModel health = await Open();

            Press(health, "Broken links in this profile");
            await Confirm(health);

            Assert.True(LeanProfiles.LinkResolves(good));
            Assert.False(File.Exists(lost));
            Assert.False(new FileInfo(lost).Exists);
            Assert.Equal("Repaired 2 links.", jobs.Note);
        }

        [Fact]
        public void Update_db_removes_a_few_gone_vars_as_before()
        {
            for (int i = 0; i < 3; i++)
                MakeVar("A.B." + (i + 1));
            MakeVar("Keep.Pack.1");
            Update();
            File.Delete(Tidied("A.B.1"));

            UpdateReport report = Update();

            Assert.Equal(1, report.Removed);
            Assert.Equal(0, report.HeldBack);
        }

        [Fact]
        public async Task A_mass_disappearance_is_held_back_until_confirmed()
        {
            for (int i = 0; i < 60; i++)
                MakeVar("Many.Pack" + i + ".1");
            MakeVar("Keep.Pack.1");
            Update();
            for (int i = 0; i < 60; i++)
                File.Delete(Tidied("Many.Pack" + i + ".1"));

            UpdateReport report = Update();

            Assert.Equal(0, report.Removed);
            Assert.Equal(60, report.HeldBack);
            Assert.Contains("60 more look gone", report.Summary);
            using (LibraryStore store = LibraryStore.Open(storeFile))
                Assert.Equal(61, store.Count("vars"));

            HealthViewModel health = await Open();
            Press(health, "Records whose var file is gone");
            Assert.Contains("Check the vars folder first", health.Pending!.Note);
            await Confirm(health);

            using (LibraryStore store = LibraryStore.Open(storeFile))
                Assert.Equal(1, store.Count("vars"));
        }

        [Fact]
        public async Task A_record_whose_file_came_back_is_not_forgotten()
        {
            MakeVar("A.B.1");
            MakeVar("Keep.Pack.1");
            Update();
            string file = Tidied("A.B.1");
            string aside = file + ".aside";
            File.Move(file, aside);
            HealthViewModel health = await Open();
            Press(health, "Records whose var file is gone");

            File.Move(aside, file);
            await Confirm(health);

            using LibraryStore store = LibraryStore.Open(storeFile);
            Assert.Equal(2, store.Count("vars"));
        }

        [Fact]
        public async Task A_healthy_library_offers_nothing_to_do()
        {
            MakeVar("A.B.1");
            Update();

            HealthViewModel health = await Open();

            Assert.DoesNotContain(health.Cards, card => card.HasAction);
            Assert.False(health.HasMissing);
        }

        [Fact]
        public async Task Opening_the_page_checks_the_library_and_the_page_flag_follows()
        {
            MakeVar("A.B.1");
            Update();
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            var shell = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 0);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            Assert.Empty(shell.Health!.Cards);

            shell.SelectedPage = shell.Pages[3];

            Assert.True(shell.IsHealthPage);
            Assert.False(shell.IsOtherPage);
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (shell.Health.Cards.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.NotEmpty(shell.Health.Cards);
        }
    }
}
