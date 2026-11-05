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

        private void Link(string name)
        {
            Assert.True(VarLinks.Create(Path.Combine(links, name + ".var"), Tidied(name)));
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
            Link("A.B.1");
            Link("C.D.1");
            Link("E.F.1");

            List<MissingNeed> missing = Report().Missing;

            Assert.Equal(new[] { "Gone.Two.1", "Gone.One.1" }, missing.Select(need => need.Reference));
            Assert.Equal(2, missing[0].NeededBy);
        }

        [Fact]
        public void Missing_dependencies_count_only_what_the_selected_profile_links()
        {
            MakeVar("A.B.1", "Gone.One.1:/Custom/a.vap");
            MakeVar("C.D.1", "Gone.One.1:/Custom/a.vap");
            MakeVar("E.F.1", "Gone.Two.1:/Custom/a.vap");
            Update();
            Link("A.B.1");

            MissingNeed need = Assert.Single(Report().Missing);

            Assert.Equal("Gone.One.1", need.Reference);
            Assert.Equal(1, need.NeededBy);
            Assert.Equal("A.B.1", need.Example);
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
        public async Task A_held_file_is_flagged_when_the_library_record_has_no_file()
        {
            MakeVar("A.B.1");
            MakeVar("Keep.Pack.1");
            Update();
            File.Delete(Tidied("A.B.1"));
            Held(LibraryFolders.Redundant, "A.B.1.var");
            HealthViewModel health = await Open();

            Press(health, "Duplicates set aside");

            Assert.Contains("1 var here has no copy", health.Pending!.Note);
        }

        [Fact]
        public async Task Deleting_a_holding_group_keeps_the_log_beside_a_file_that_arrived_since()
        {
            MakeVar("A.B.1");
            Update();
            string reviewed = Held(LibraryFolders.Holding + "\\20260101", "A.B.1.var");
            string log = Held(LibraryFolders.Holding + "\\20260101", "moved.txt");
            HealthViewModel health = await Open();
            Press(health, "Moved out of the library");

            string later = Held(LibraryFolders.Holding + "\\20260101", "Later.Pack.1.var");
            await Confirm(health);

            Assert.False(File.Exists(reviewed));
            Assert.True(File.Exists(later));
            Assert.True(File.Exists(log));
            Assert.NotNull(MoveOut.LatestHolding(vars));
        }

        [Fact]
        public async Task An_emptied_group_folder_stays_and_a_folder_link_is_not_followed()
        {
            MakeVar("A.B.1");
            Update();
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            string keep = Path.Combine(outside, "keep.var");
            File.WriteAllText(keep, "x");
            Held(LibraryFolders.Redundant, "A.B.1.var");
            string redundant = Path.Combine(vars, LibraryFolders.Redundant);
            Assert.True(Comm.CreateSymbolicLink(Path.Combine(redundant, "linked"), outside, Comm.SYMBOLIC_LINK_FLAG.Directory | Comm.SYMBOLIC_LINK_FLAG.AllowUnprivilegedCreate));
            HealthViewModel health = await Open();

            Press(health, "Duplicates set aside");
            Assert.Single(health.Pending!.Lines);
            await Confirm(health);

            Assert.True(File.Exists(keep));
            Assert.True(Directory.Exists(redundant));
        }

        [Fact]
        public async Task A_folder_link_in_a_holding_folder_keeps_what_it_points_at()
        {
            MakeVar("A.B.1");
            Update();
            string outside = Path.Combine(root, "outside");
            string empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(empty);
            string otherLog = Path.Combine(outside, "moved.txt");
            File.WriteAllText(otherLog, "log");
            Held(LibraryFolders.Holding + "\\20260101", "A.B.1.var");
            string batch = Path.Combine(vars, LibraryFolders.Holding, "20260101");
            var link = Comm.SYMBOLIC_LINK_FLAG.Directory | Comm.SYMBOLIC_LINK_FLAG.AllowUnprivilegedCreate;
            Assert.True(Comm.CreateSymbolicLink(Path.Combine(batch, "linked"), outside, link));
            Assert.True(Comm.CreateSymbolicLink(Path.Combine(batch, "linkedEmpty"), empty, link));
            HealthViewModel health = await Open();

            Press(health, "Moved out of the library");
            await Confirm(health);

            Assert.True(File.Exists(otherLog));
            Assert.True(Directory.Exists(Path.Combine(batch, "linkedEmpty")));
        }

        [Fact]
        public void A_held_folder_that_is_a_link_offers_nothing_to_delete()
        {
            MakeVar("A.B.1");
            Update();
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.var"), "x");
            string redundant = Path.Combine(vars, LibraryFolders.Redundant);
            if (Directory.Exists(redundant))
                Directory.Delete(redundant);
            Assert.True(Comm.CreateSymbolicLink(redundant, outside, Comm.SYMBOLIC_LINK_FLAG.Directory | Comm.SYMBOLIC_LINK_FLAG.AllowUnprivilegedCreate));

            Assert.Empty(Report().Held);
        }

        [Fact]
        public void Checking_creates_no_default_switch()
        {
            MakeVar("A.B.1");
            Update();
            string packs = Path.Combine(vam, "AddonPackages");
            new DirectoryInfo(packs).Delete();
            Directory.Delete(Path.Combine(vam, LibraryFolders.AddonPacksSwitch), true);

            Report();

            Assert.False(Directory.Exists(Path.Combine(vam, LibraryFolders.AddonPacksSwitch)));
        }

        [Fact]
        public async Task A_review_goes_when_the_page_checks_again()
        {
            MakeVar("A.B.1");
            Update();
            Held(LibraryFolders.Redundant, "A.B.1.var");
            HealthViewModel health = await Open();
            Press(health, "Duplicates set aside");

            await health.Reload();

            Assert.False(health.HasPending);
        }

        [Fact]
        public async Task Confirming_while_a_job_runs_keeps_the_review_and_deletes_nothing()
        {
            MakeVar("A.B.1");
            Update();
            string duplicate = Held(LibraryFolders.Redundant, "A.B.1.var");
            HealthViewModel health = await Open();
            Press(health, "Duplicates set aside");
            using var gate = new ManualResetEventSlim();
            Task busy = jobs.Start("Busy", "done", _ => gate.Wait(Timeout))!;

            health.ConfirmCommand.Execute(null);
            await health.Work.WaitAsync(Timeout);

            Assert.True(health.HasPending);
            Assert.True(File.Exists(duplicate));
            gate.Set();
            await busy.WaitAsync(Timeout);
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
        public async Task A_linked_stale_var_is_flagged_in_the_review()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            MakeVar("C.D.1");
            MakeVar("C.D.2");
            Update();
            Assert.True(VarLinks.Create(Path.Combine(links, "A.B.1.var"), Tidied("A.B.1")));
            HealthViewModel health = await Open();

            Press(health, "Old versions nothing uses");

            Assert.Contains("1 var of them is linked now", health.Pending!.Note);
            Assert.Contains("A.B.1  (linked now)", health.Pending.Lines);
            Assert.Contains("C.D.1", health.Pending.Lines);
        }

        [Fact]
        public void An_old_version_is_not_stale_when_the_newest_file_is_gone()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            MakeVar("Keep.Pack.1");
            Update();
            File.Delete(Tidied("A.B.2"));

            Assert.Empty(Report().Stale);
        }

        [Fact]
        public void With_a_save_unreadable_nothing_counts_as_stale()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            Update();
            string scene = Path.Combine(vam, "Saves", "scene", "mine.json");
            Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
            File.WriteAllText(scene, "{}");
            var problems = new List<string>();

            using (new FileStream(scene, FileMode.Open, FileAccess.Read, FileShare.None))
            using (LibraryStore store = LibraryStore.Open(storeFile))
            {
                Assert.Empty(LibraryHealth.Load(store, paths, problems.Add).Stale);
                Assert.Null(LibraryHealth.MoveStale(store, paths, new[] { "A.B.1" }));
            }

            Assert.NotEmpty(problems);
            Assert.True(File.Exists(Tidied("A.B.1")));
        }

        [Fact]
        public async Task Stale_vars_stay_if_more_became_stale_since_the_review()
        {
            MakeVar("A.B.1");
            MakeVar("A.B.2");
            Update();
            HealthViewModel health = await Open();
            Press(health, "Old versions nothing uses");

            MakeVar("A.B.3");
            Update();
            await Confirm(health);

            Assert.True(File.Exists(Tidied("A.B.1")));
            Assert.True(File.Exists(Tidied("A.B.2")));
            Assert.Contains("nothing moved", jobs.Note);
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
        public async Task Repair_leaves_a_real_file_or_working_link_that_replaced_a_reviewed_one()
        {
            MakeVar("A.B.1");
            Update();
            string target = Path.Combine(root, "t.var");
            File.WriteAllText(target, "x");
            string becomesReal = Path.Combine(links, "Real.Pack.1.var");
            string becomesGood = Path.Combine(links, "Good.Pack.1.var");
            Assert.True(VarLinks.Create(becomesReal, target));
            Assert.True(VarLinks.Create(becomesGood, target));
            File.Delete(target);
            HealthViewModel health = await Open();
            Press(health, "Broken links in this profile");

            File.Delete(becomesReal);
            File.WriteAllText(becomesReal, "real var");
            File.WriteAllText(target, "x");
            await Confirm(health);

            Assert.Equal("real var", File.ReadAllText(becomesReal));
            Assert.True(VarLinks.IsLink(becomesGood));
            Assert.Equal("Repaired 0 links.", jobs.Note);
        }

        [Fact]
        public async Task Repair_drops_a_link_whose_record_has_no_file_and_its_marker()
        {
            MakeVar("A.B.1");
            MakeVar("Keep.Pack.1");
            Update();
            string link = Path.Combine(links, "A.B.1.var");
            Assert.True(VarLinks.Create(link, Tidied("A.B.1")));
            File.WriteAllText(link + ".disabled", "");
            File.Delete(Tidied("A.B.1"));
            HealthViewModel health = await Open();

            Press(health, "Broken links in this profile");
            await Confirm(health);

            Assert.False(new FileInfo(link).Exists);
            Assert.False(File.Exists(link + ".disabled"));
        }

        [Fact]
        public async Task Repair_keeps_the_marker_of_a_repaired_link_and_a_marker_holding_a_var()
        {
            MakeVar("A.B.1");
            MakeVar("Lost.Pack.1");
            Update();
            string target = Path.Combine(root, "A.B.1.var");
            File.WriteAllText(target, "x");
            string kept = Path.Combine(links, "A.B.1.var");
            Assert.True(VarLinks.Create(kept, target));
            File.Delete(target);
            File.WriteAllText(kept + ".disabled", "");
            string lost = Path.Combine(links, "Lost.Pack.1.var");
            Assert.True(VarLinks.Create(lost, Tidied("Lost.Pack.1")));
            File.WriteAllText(lost + ".disabled", "a whole var");
            File.Delete(Tidied("Lost.Pack.1"));
            HealthViewModel health = await Open();

            Press(health, "Broken links in this profile");
            await Confirm(health);

            Assert.True(LeanProfiles.LinkResolves(kept));
            Assert.True(File.Exists(kept + ".disabled"));
            Assert.Equal("a whole var", File.ReadAllText(lost + ".disabled"));
        }

        [Fact]
        public async Task Repair_finds_a_var_moved_inside_the_tidied_folder()
        {
            MakeVar("A.B.1");
            Update();
            string link = Path.Combine(links, "A.B.1.var");
            Assert.True(VarLinks.Create(link, Tidied("A.B.1")));
            string moved = Path.Combine(vars, LibraryFolders.Tidied, "Elsewhere", "A.B.1.var");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.Move(Tidied("A.B.1"), moved);
            HealthViewModel health = await Open();

            Press(health, "Broken links in this profile");
            await Confirm(health);

            Assert.True(LeanProfiles.LinkResolves(link));
        }

        [Fact]
        public void An_unreachable_vars_folder_says_nothing_is_gone()
        {
            MakeVar("A.B.1");
            Update();
            Directory.Delete(Path.Combine(vars, LibraryFolders.Tidied), true);

            Assert.Empty(Report().GoneRecords);
            using LibraryStore store = LibraryStore.Open(storeFile);
            Assert.Equal(0, LibraryUpdate.RemoveMissing(store, paths, new[] { "A.B.1" }));
            Assert.Equal(1, store.Count("vars"));
        }

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(1, 1, false)]
        [InlineData(4, 4, false)]
        [InlineData(4, 8, false)]
        [InlineData(5, 10, true)]
        [InlineData(5, 11, false)]
        [InlineData(49, 60, true)]
        [InlineData(49, 250, false)]
        [InlineData(50, 250, true)]
        [InlineData(50, 251, false)]
        public void A_removal_looks_like_a_wrong_folder_only_when_large(int gone, int total, bool held)
        {
            Assert.Equal(held, LibraryUpdate.LooksLikeWrongFolder(gone, total));
        }

        [Fact]
        public void Forgetting_counts_only_records_that_are_really_gone()
        {
            MakeVar("A.B.1");
            MakeVar("Keep.Pack.1");
            Update();
            File.Delete(Tidied("A.B.1"));
            using LibraryStore store = LibraryStore.Open(storeFile);

            Assert.Equal(1, LibraryUpdate.RemoveMissing(store, paths, new[] { "A.B.1", "Keep.Pack.1", "Never.Was.1" }));

            Assert.Equal(1, store.Count("vars"));
        }

        [Fact]
        public void A_var_moved_inside_the_tidied_folder_is_not_gone()
        {
            MakeVar("A.B.1");
            Update();
            string moved = Path.Combine(vars, LibraryFolders.Tidied, "Elsewhere", "A.B.1.var");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.Move(Tidied("A.B.1"), moved);

            Assert.Empty(Report().GoneRecords);
        }

        [Fact]
        public void Checking_health_during_a_switch_leaves_addon_packages_a_link()
        {
            MakeVar("A.B.1");
            Update();
            Directory.CreateDirectory(VarSwitches.LinkFolder(vam, "other"));
            string packs = Path.Combine(vam, "AddonPackages");
            using var stop = new CancellationTokenSource();
            Task reading = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        Report();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Losing the race is fine. Only the folder matters here.
                    }
                }
            });

            try
            {
                for (int i = 0; i < 1500; i++)
                {
                    Assert.NotEqual(SwitchChange.Failed, VarSwitches.Point(vam, i % 2 == 0 ? "other" : "default", out _));
                    Assert.True(new DirectoryInfo(packs).Attributes.HasFlag(FileAttributes.ReparsePoint), "round " + i);
                }
            }
            finally
            {
                stop.Cancel();
            }
            Assert.True(reading.Wait(Timeout));
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
        public async Task A_missing_preview_can_be_put_back()
        {
            string file = Path.Combine(vars, "A.B.1.var");
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(file)))
            {
                foreach (string entry in new[] { "meta.json", "Saves/scene/S.json", "Saves/scene/S.jpg" })
                {
                    zip.PutNextEntry(new ZipEntry(entry));
                    byte[] bytes = Encoding.UTF8.GetBytes("{}");
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
            Update();
            string previews = Path.Combine(vars, LibraryFolders.PreviewPics);
            string picture = Directory.GetFiles(previews, "*.jpg", SearchOption.AllDirectories).Single();
            File.Delete(picture);
            HealthViewModel health = await Open();

            Press(health, "Preview pictures missing");
            await Confirm(health);

            Assert.True(File.Exists(picture));
            Assert.Equal("Pictures repaired.", jobs.Note);
            Assert.Equal(0, Report().MissingPreviews);
        }

        [Fact]
        public void A_missing_picture_of_a_var_that_is_gone_is_not_counted()
        {
            string file = Path.Combine(vars, "A.B.1.var");
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(file)))
            {
                foreach (string entry in new[] { "meta.json", "Saves/scene/S.json", "Saves/scene/S.jpg" })
                {
                    zip.PutNextEntry(new ZipEntry(entry));
                    byte[] bytes = Encoding.UTF8.GetBytes("{}");
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
            MakeVar("Keep.Pack.1");
            Update();
            Assert.Equal(0, Report().MissingPreviews);

            File.Delete(Directory.GetFiles(Path.Combine(vars, LibraryFolders.PreviewPics), "*.jpg", SearchOption.AllDirectories).Single());
            File.Delete(Tidied("A.B.1"));

            Assert.Equal(0, Report().MissingPreviews);
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
            Assert.False(shell.IsMissingPage);
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (shell.Health.Cards.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.NotEmpty(shell.Health.Cards);
        }
    }
}
