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
    public sealed class LibraryUpdateTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmupdate-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string data;
        private readonly string links;
        private readonly LibraryPaths paths;
        private readonly LibraryStore store;

        public LibraryUpdateTests()
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
            links = Path.Combine(vam, "AddonPackages", LibraryFolders.VarsLink);
            Directory.CreateDirectory(links);
            paths = new LibraryPaths(vars, vam, data);
            store = LibraryStore.Open(Path.Combine(data, "library.db"));
        }

        public void Dispose()
        {
            store.Dispose();
            new DirectoryInfo(Path.Combine(vam, "AddonPackages")).Delete();
            Directory.Delete(root, true);
        }

        private string MakeVar(string folder, string name, string depends = "", params string[] namesAndContents)
        {
            string path = Path.Combine(folder, name + ".var");
            Directory.CreateDirectory(folder);
            var entries = new List<string> { "meta.json", "{\"x\":\"" + depends + "\"}" };
            entries.AddRange(namesAndContents);
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(path)))
            {
                for (int i = 0; i < entries.Count; i += 2)
                {
                    zip.PutNextEntry(new ZipEntry(entries[i]));
                    byte[] bytes = Encoding.UTF8.GetBytes(entries[i + 1]);
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
            return path;
        }

        private string Tidied(string name)
        {
            return Path.Combine(vars, LibraryFolders.Tidied, name.Split('.')[0], name + ".var");
        }

        private UpdateReport Run(CancellationToken token = default)
        {
            return LibraryUpdate.Run(store, paths, (done, total) => { }, token);
        }

        private List<string> Names()
        {
            return store.LoadVars().Select(v => v.VarName).ToList();
        }

        [Fact]
        public void A_dropped_var_is_tidied_scanned_and_recorded()
        {
            MakeVar(vars, "A.B.1", "Other.Pack.2:/Custom/a.vap", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");

            UpdateReport report = Run();

            Assert.Equal(1, report.Added);
            Assert.True(File.Exists(Tidied("A.B.1")));
            VarRecord record = Assert.Single(store.LoadVars());
            Assert.Equal("A", record.CreatorName);
            Assert.Equal(Path.Combine(LibraryFolders.Tidied, "A"), record.VarPath);
            Assert.Equal(1, record.Scenes);
            Assert.Equal(new[] { "Other.Pack.2" }, store.LoadDependencies().Select(d => d.Value));
            ScenePreview preview = Assert.Single(store.LoadPreviews());
            Assert.Equal("scenes001_a.jpg", preview.Picture);
            Assert.True(File.Exists(ScenePreviews.PicturePath(vars, "scenes", "A.B.1", preview.Picture)));
        }

        [Fact]
        public void A_second_run_changes_nothing()
        {
            MakeVar(vars, "A.B.1");
            Run();

            UpdateReport report = Run();

            Assert.Equal(0, report.Added);
            Assert.Equal(0, report.Changed);
            Assert.Equal(0, report.Removed);
            Assert.Single(store.LoadVars());
        }

        [Fact]
        public void No_vars_at_all_is_reported_and_keeps_links_alone()
        {
            UpdateReport report = Run();

            Assert.False(report.FoundVars);
            Assert.Contains("No vars found", report.Summary);
        }

        [Fact]
        public void No_vars_on_disk_does_not_wipe_the_records()
        {
            MakeVar(vars, "A.B.1");
            Run();
            File.Delete(Tidied("A.B.1"));

            Run();

            Assert.Equal(new[] { "A.B.1" }, Names());
        }

        [Fact]
        public void A_replaced_var_is_read_again()
        {
            MakeVar(vars, "A.B.1", "", "Saves/scene/a.json", "{}");
            Run();
            File.Delete(Tidied("A.B.1"));
            MakeVar(Path.GetDirectoryName(Tidied("A.B.1"))!, "A.B.1", "", "Saves/scene/a.json", "{}", "Saves/scene/b.json", "{}");

            UpdateReport report = Run();

            Assert.Equal(1, report.Changed);
            Assert.Equal(2, Assert.Single(store.LoadVars()).Scenes);
            Assert.Equal(2, store.Count("scenes"));
        }

        [Fact]
        public void A_changed_var_loses_its_old_previews()
        {
            MakeVar(vars, "A.B.1", "", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            Run();
            string oldPicture = ScenePreviews.PicturePath(vars, "scenes", "A.B.1", "scenes001_a.jpg");
            File.Delete(Tidied("A.B.1"));
            MakeVar(Path.GetDirectoryName(Tidied("A.B.1"))!, "A.B.1", "", "Saves/scene/c.json", "{}", "Saves/scene/d.json", "{}");

            Run();

            Assert.False(File.Exists(oldPicture));
        }

        [Fact]
        public void A_moved_var_gets_its_new_path()
        {
            MakeVar(vars, "A.B.1");
            MakeVar(vars, "C.D.1");
            Run();
            string moved = Path.Combine(vars, LibraryFolders.Tidied, "elsewhere", "A.B.1.var");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.Move(Tidied("A.B.1"), moved);

            UpdateReport report = Run();

            Assert.Equal(0, report.Changed);
            Assert.Equal(Path.Combine(LibraryFolders.Tidied, "elsewhere"), store.LoadVars().Single(v => v.VarName == "A.B.1").VarPath);
        }

        [Fact]
        public void A_var_that_left_is_removed_with_its_previews()
        {
            MakeVar(vars, "A.B.1", "", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            MakeVar(vars, "C.D.1");
            Run();
            File.Delete(Tidied("A.B.1"));

            UpdateReport report = Run();

            Assert.Equal(1, report.Removed);
            Assert.Equal(new[] { "C.D.1" }, Names());
            Assert.Equal(0, store.Count("scenes"));
            Assert.False(Directory.Exists(Path.Combine(vars, LibraryFolders.PreviewPics, "scenes", "A.B.1")));
        }

        [Fact]
        public void Removing_a_var_restores_previews_missing_from_others()
        {
            MakeVar(vars, "A.B.1", "", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            MakeVar(vars, "C.D.1");
            Run();
            File.Delete(ScenePreviews.PicturePath(vars, "scenes", "A.B.1", "scenes001_a.jpg"));
            File.Delete(Tidied("C.D.1"));

            Run();

            Assert.True(File.Exists(ScenePreviews.PicturePath(vars, "scenes", "A.B.1", "scenes001_a.jpg")));
        }

        [Fact]
        public void An_invalid_var_moves_aside_and_is_reported()
        {
            MakeVar(vars, "Good.Pack.1");
            string tidied = Tidied("Bad.Pack.1");
            Directory.CreateDirectory(Path.GetDirectoryName(tidied)!);
            File.WriteAllText(tidied, "not a zip");

            UpdateReport report = Run();

            Assert.False(File.Exists(tidied));
            Assert.True(File.Exists(Path.Combine(vars, LibraryFolders.NotComplyRule, "Bad.Pack.1.var")));
            Assert.Contains(report.Problems, p => p.Contains("Invalid var"));
            Assert.Equal(new[] { "Good.Pack.1" }, Names());
        }

        [Fact]
        public void A_var_with_a_damaged_entry_is_reported_and_the_rest_carry_on()
        {
            string bad = MakeVar(vars, "Bad.Pack.1", "", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            BreakEntry(bad, "Saves/scene/a.jpg");
            MakeVar(vars, "Good.Pack.1");

            UpdateReport report = Run();

            Assert.Contains("Good.Pack.1", Names());
            Assert.Contains(report.Problems, p => p.Contains("Bad.Pack.1"));
            Assert.DoesNotContain(report.Problems, p => p.Contains("Invalid var"));
            Assert.True(File.Exists(Tidied("Bad.Pack.1")));
        }

        // Spoils an entry's local header. The zip still opens, and only reading that entry throws ZipException.
        private static void BreakEntry(string path, string entryName)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(entryName)) - 30] = 0;
            File.WriteAllBytes(path, bytes);
        }

        [Fact]
        public void A_known_var_that_turned_invalid_loses_its_record()
        {
            MakeVar(vars, "A.B.1");
            MakeVar(vars, "C.D.1");
            Run();
            File.WriteAllText(Tidied("A.B.1"), "now not a zip at all");

            Run();

            Assert.Equal(new[] { "C.D.1" }, Names());
        }

        [Fact]
        public void A_badly_named_var_goes_aside_during_tidy()
        {
            MakeVar(vars, "Good.Pack.1");
            File.WriteAllText(Path.Combine(vars, "Broken.var"), "x");

            UpdateReport report = Run();

            Assert.True(File.Exists(Path.Combine(vars, LibraryFolders.NotComplyRule, "Broken.var")));
            Assert.Contains(report.Problems, p => p.Contains("not comply"));
        }

        [Fact]
        public void A_loose_addon_var_moves_in_and_gets_linked_with_its_dependencies()
        {
            MakeVar(vars, "Dep.Pack.1");
            MakeVar(Path.Combine(vam, "AddonPackages"), "A.B.1", "Dep.Pack.latest:/Custom/a.vap");

            Run();

            Assert.True(File.Exists(Tidied("A.B.1")));
            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
            Assert.True(VarLinks.IsLink(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.False(File.Exists(paths.InstallList));
            Assert.Equal(2, store.Count("installStatus"));
        }

        [Fact]
        public void The_migrated_list_links_exactly_those_vars_and_is_used_up()
        {
            MakeVar(vars, "A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar(vars, "Dep.Pack.1");
            File.WriteAllLines(paths.MigratedList, new[] { "A.B.1", "" });

            Run();

            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
            Assert.False(File.Exists(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.False(File.Exists(paths.MigratedList));
        }

        [Fact]
        public void A_migrated_var_that_cant_link_stays_listed()
        {
            MakeVar(vars, "A.B.1");
            File.WriteAllLines(paths.MigratedList, new[] { "Ghost.Pack.1", "A.B.1" });

            UpdateReport report = Run();

            Assert.Equal(new[] { "Ghost.Pack.1" }, File.ReadAllLines(paths.MigratedList));
            Assert.Equal(1, report.NotLinked);
            Assert.Contains("1 not linked", report.Summary);
        }

        [Fact]
        public void Install_status_lists_links_of_known_vars_and_their_disabled_state()
        {
            MakeVar(vars, "A.B.1");
            MakeVar(vars, "C.D.1");
            Run();
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Tidied("A.B.1"));
            VarLinks.Create(Path.Combine(links, "C.D.1.var"), Tidied("C.D.1"));
            File.WriteAllText(Path.Combine(links, "C.D.1.var.disabled"), "");
            VarLinks.Create(Path.Combine(links, "Ghost.Pack.1.var"), Tidied("A.B.1"));

            Run();

            List<VarRecord> records = store.LoadVars().ToList();
            Assert.True(records.Single(v => v.VarName == "A.B.1").Installed);
            Assert.False(records.Single(v => v.VarName == "A.B.1").Disabled);
            Assert.True(records.Single(v => v.VarName == "C.D.1").Disabled);
            Assert.Equal(2, store.Count("installStatus"));
        }

        [Fact]
        public void Progress_counts_every_var()
        {
            MakeVar(vars, "A.B.1");
            MakeVar(vars, "C.D.1");
            var seen = new List<string>();

            LibraryUpdate.Run(store, paths, (done, total) => seen.Add(done + "/" + total), default);

            Assert.Equal(new[] { "1/2", "2/2" }, seen);
        }

        [Fact]
        public void Cancelling_keeps_what_was_scanned_and_removes_nothing()
        {
            MakeVar(vars, "A.B.1");
            MakeVar(vars, "C.D.1");
            Run();
            File.Delete(Tidied("A.B.1"));
            MakeVar(vars, "E.F.1");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            Assert.Throws<OperationCanceledException>(() => Run(cancel.Token));

            Assert.Contains("A.B.1", Names());
        }

        [Fact]
        public void Problems_are_written_down_and_cleared_when_gone()
        {
            MakeVar(vars, "Good.Pack.1");
            File.WriteAllText(Path.Combine(vars, "Broken.var"), "x");
            string problems = Path.Combine(data, "update-problems.txt");

            UpdateReport report = Run();

            Assert.True(File.Exists(problems));
            Assert.Contains("1 problems", report.Summary);

            Run();

            Assert.False(File.Exists(problems));
        }

        [Fact]
        public void Vars_are_never_rewritten()
        {
            string var = MakeVar(vars, "A.B.1", "", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            byte[] before = File.ReadAllBytes(var);

            Run();
            Run();

            Assert.Equal(before, File.ReadAllBytes(Tidied("A.B.1")));
        }

        [Fact]
        public void Save_replaces_a_records_rows_and_delete_clears_them()
        {
            var scan = new VarScan { CreatorName = "A", PackageName = "B", Version = 1, Scenes = 1 };
            scan.Items.Add(new VarItem { Type = "scenes", EntryName = "Saves/scene/a.json", Preview = "" });
            scan.Dependencies.Add("Dep.Pack.1");

            store.SaveVar("A.B.1", "x", scan);
            store.SaveVar("a.b.1", "y", scan);

            Assert.Equal("y", Assert.Single(store.LoadVars()).VarPath);
            Assert.Equal(1, store.Count("scenes"));
            Assert.Equal(1, store.Count("dependencies"));

            store.DeleteVars(new[] { "A.B.1" });

            Assert.Equal(0, store.Count("vars"));
            Assert.Equal(0, store.Count("scenes"));
            Assert.Equal(0, store.Count("dependencies"));
        }

        [Fact]
        public void The_shell_runs_update_db_and_shows_the_summary()
        {
            string app = Path.Combine(root, "shellapp");
            Directory.CreateDirectory(app);
            string settings = Path.Combine(app, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settings);
            MakeVar(vars, "A.B.1");
            var jobs = new JobRunner(null);
            var shell = new ShellViewModel(settings, jobs, () => false, _ => { });

            shell.UpdateCommand.Execute(null);
            WaitForJob(jobs);

            Assert.Contains("1 new", jobs.Note);
            Assert.True(File.Exists(Path.Combine(app, "library.db")));
        }

        [Fact]
        public void A_failed_import_turns_update_db_off_and_creates_no_store()
        {
            string app = Path.Combine(root, "shellapp");
            Directory.CreateDirectory(app);
            string settings = Path.Combine(app, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settings);
            File.WriteAllText(Path.Combine(app, "varManager.mdb"), "not a database");
            var jobs = new JobRunner(null);
            var shell = new ShellViewModel(settings, jobs, () => false, _ => { });
            WaitForJob(jobs);
            Assert.Contains("failed", jobs.Note);

            shell.UpdateCommand.Execute(null);

            Assert.Contains("Update DB is off", jobs.Note);
            Assert.False(jobs.IsRunning);
            Assert.False(File.Exists(Path.Combine(app, "library.db")));
        }

        [Fact]
        public void The_old_database_is_imported_on_first_open_only()
        {
            string app = Path.Combine(root, "shellapp");
            Directory.CreateDirectory(app);
            string settings = Path.Combine(app, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settings);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "template.mdb"), Path.Combine(app, "varManager.mdb"));
            var jobs = new JobRunner(null);

            _ = new ShellViewModel(settings, jobs, () => false, _ => { });
            WaitForJob(jobs);

            Assert.Equal("Library imported.", jobs.Note);
            Assert.True(File.Exists(Path.Combine(app, "library.db")));

            var again = new JobRunner(null);
            _ = new ShellViewModel(settings, again, () => false, _ => { });
            Assert.False(again.IsRunning);
            Assert.Equal("Ready.", again.Note);
        }

        private static void WaitForJob(JobRunner jobs)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (jobs.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            Assert.False(jobs.IsRunning);
        }
    }
}
