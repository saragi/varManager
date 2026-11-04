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
    public sealed class MoveOutTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmmove-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string data;
        private readonly string links;
        private readonly string storeFile;
        private readonly LibraryPaths paths;

        public MoveOutTests()
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
            storeFile = Path.Combine(data, "library.db");
        }

        public void Dispose()
        {
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

        private static VarIndex Index(params string[] names)
        {
            return new VarIndex(names.Select(name =>
            {
                string[] parts = name.Split('.');
                return new VarEntry(name, parts[0], parts[1], int.Parse(parts[2]));
            }));
        }

        private static LibraryUsage Usage(string[] names, params string[] pairs)
        {
            return new LibraryUsage(Index(names), pairs.Select(pair =>
            {
                string[] parts = pair.Split('>');
                return new KeyValuePair<string, string>(parts[0], parts[1]);
            }));
        }

        private static readonly HashSet<string> NothingOutside = new HashSet<string>();

        private string MakeVar(string name, string depends = "")
        {
            string path = Path.Combine(vars, name + ".var");
            using ZipOutputStream zip = new ZipOutputStream(File.Create(path));
            zip.PutNextEntry(new ZipEntry("meta.json"));
            byte[] bytes = Encoding.UTF8.GetBytes("{\"x\":\"" + depends + "\"}");
            zip.Write(bytes, 0, bytes.Length);
            zip.CloseEntry();
            return path;
        }

        private string Tidied(string name)
        {
            return Path.Combine(vars, LibraryFolders.Tidied, name.Split('.')[0], name + ".var");
        }

        private void Update()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            LibraryUpdate.Run(store, paths, (done, total) => { }, default);
        }

        private List<string> Names()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            return store.LoadVars().Select(v => v.VarName).OrderBy(n => n).ToList();
        }

        private async Task<(LibraryViewModel library, JobRunner jobs)> Open(string select)
        {
            var jobs = new JobRunner(null);
            var library = new LibraryViewModel(storeFile, paths, jobs, () => VarLinks.Installed(paths.AddonPackages).Keys, null);
            await library.Reload();
            library.Columns = 10;
            library.SelectCommand.Execute(library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).Single(t => t.Item.VarName == select));
            return (library, jobs);
        }

        private static void WaitForJob(JobRunner jobs)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (jobs.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.False(jobs.IsRunning);
        }

        [Fact]
        public void A_package_other_vars_need_is_blocked()
        {
            LibraryUsage usage = Usage(new[] { "A.B.1", "C.D.1" }, "C.D.1>A.B.latest");

            MovePlan plan = MoveOut.Plan("A.B.1", usage, NothingOutside);

            Assert.True(plan.IsBlocked);
            Assert.Equal(new[] { "C.D.1" }, plan.NeededBy);
            Assert.Empty(plan.Moves);
        }

        [Fact]
        public void A_self_reference_does_not_block()
        {
            Assert.False(MoveOut.Plan("A.B.1", Usage(new[] { "A.B.1" }, "A.B.1>A.B.latest"), NothingOutside).IsBlocked);
        }

        [Fact]
        public void Dependencies_only_this_package_uses_move_with_it_all_the_way_down()
        {
            LibraryUsage usage = Usage(new[] { "A.B.1", "C.D.1", "E.F.1" }, "A.B.1>C.D.1", "C.D.1>E.F.1");

            MovePlan plan = MoveOut.Plan("A.B.1", usage, NothingOutside);

            Assert.Equal(new[] { "A.B.1", "C.D.1", "E.F.1" }, plan.Moves);
            Assert.Empty(plan.Stays);
        }

        [Fact]
        public void A_dependency_another_var_needs_stays()
        {
            LibraryUsage usage = Usage(new[] { "A.B.1", "C.D.1", "X.Y.1" }, "A.B.1>C.D.1", "X.Y.1>C.D.1");

            MovePlan plan = MoveOut.Plan("A.B.1", usage, NothingOutside);

            Assert.Equal(new[] { "A.B.1" }, plan.Moves);
            Assert.Equal(MoveOut.UsedByOthers, plan.Stays["C.D.1"]);
        }

        [Fact]
        public void A_dependency_used_only_by_vars_that_move_also_moves()
        {
            LibraryUsage usage = Usage(new[] { "A.B.1", "C.D.1", "E.F.1" }, "A.B.1>C.D.1", "A.B.1>E.F.1", "C.D.1>E.F.1");

            Assert.Equal(new[] { "A.B.1", "C.D.1", "E.F.1" }, MoveOut.Plan("A.B.1", usage, NothingOutside).Moves.OrderBy(n => n));
        }

        [Fact]
        public void A_dependency_a_profile_or_scene_uses_stays_and_keeps_what_it_needs()
        {
            LibraryUsage usage = Usage(new[] { "A.B.1", "C.D.1", "E.F.1" }, "A.B.1>C.D.1", "C.D.1>E.F.1");

            MovePlan plan = MoveOut.Plan("A.B.1", usage, new HashSet<string> { "C.D.1" });

            Assert.Equal(new[] { "A.B.1" }, plan.Moves);
            Assert.Equal(MoveOut.UsedOutsideVars, plan.Stays["C.D.1"]);
            Assert.False(plan.Stays.ContainsKey("E.F.1"));
        }

        [Fact]
        public void A_target_a_profile_uses_can_move_but_says_so()
        {
            MovePlan plan = MoveOut.Plan("A.B.1", Usage(new[] { "A.B.1" }), new HashSet<string> { "A.B.1" });

            Assert.False(plan.IsBlocked);
            Assert.True(plan.TargetInUseOutsideVars);
        }

        [Fact]
        public void A_later_dependency_can_turn_a_stay_into_a_move()
        {
            LibraryUsage usage = Usage(new[] { "A.B.1", "C.D.1", "E.F.1" }, "A.B.1>E.F.1", "A.B.1>C.D.1", "C.D.1>E.F.1");

            MovePlan plan = MoveOut.Plan("A.B.1", usage, NothingOutside);

            Assert.Empty(plan.Stays);
        }

        [Fact]
        public void References_from_profiles_base_list_and_saves_are_found()
        {
            string profiles = Path.Combine(vars, LibraryFolders.Profiles);
            Directory.CreateDirectory(profiles);
            var profile = new LeanProfile();
            profile.Direct.Add("Picked.Pack.1");
            profile.Base.Add("Based.Pack.latest");
            LeanProfiles.Save(profile, Path.Combine(profiles, "lean.profile"));
            File.WriteAllLines(Path.Combine(profiles, "base.txt"), new[] { " Listed.Pack.1 ", "" });
            string scene = Path.Combine(vam, "Saves", "scene", "a.json");
            Directory.CreateDirectory(Path.GetDirectoryName(scene)!);
            File.WriteAllText(scene, "{\"x\":\"Scene.Pack.1:/Custom/a.vap\"}");
            string preset = Path.Combine(vam, "Custom", "Atom", "b.vap");
            Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
            File.WriteAllText(preset, "{\"x\":\"Preset.Pack.1:/Custom/a.vap\"}");
            VarIndex index = Index("Picked.Pack.1", "Based.Pack.2", "Listed.Pack.1", "Scene.Pack.1", "Preset.Pack.1", "Free.Pack.1");

            HashSet<string> found = MoveOut.ReferencedOutsideVars(paths, index, _ => { });

            Assert.Equal(new[] { "Based.Pack.2", "Listed.Pack.1", "Picked.Pack.1", "Preset.Pack.1", "Scene.Pack.1" }, found.OrderBy(n => n));
        }

        [Fact]
        public void No_saves_folder_is_fine()
        {
            Assert.Empty(MoveOut.ReferencedOutsideVars(paths, Index("A.B.1"), _ => { }));
        }

        [Fact]
        public void Apply_moves_files_removes_links_everywhere_and_undo_puts_it_all_back()
        {
            MakeVar("A.B.1");
            Update();
            string var = Tidied("A.B.1");
            string companion = Path.ChangeExtension(var, "jpg");
            File.WriteAllText(companion, "picture");
            byte[] before = File.ReadAllBytes(var);
            string defaultLink = Path.Combine(links, "A.B.1.var");
            VarLinks.Create(defaultLink, var);
            File.WriteAllText(defaultLink + ".disabled", "");
            string leanLinks = VarSwitches.LinkFolder(vam, "lean");
            Directory.CreateDirectory(leanLinks);
            string leanLink = Path.Combine(leanLinks, "A.B.1.var");
            VarLinks.Create(leanLink, var);
            var plan = new MovePlan { Target = "A.B.1" };
            plan.Moves.Add("A.B.1");

            string holding = MoveOut.Apply(plan, _ => var, paths);

            Assert.False(File.Exists(var));
            Assert.False(File.Exists(companion));
            Assert.False(File.Exists(defaultLink));
            Assert.False(File.Exists(defaultLink + ".disabled"));
            Assert.False(File.Exists(leanLink));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(holding, "A.B.1.var")));
            Assert.Equal(holding, MoveOut.LatestHolding(vars));

            Assert.Empty(MoveOut.Undo(holding));

            Assert.Equal(before, File.ReadAllBytes(var));
            Assert.Equal("picture", File.ReadAllText(companion));
            Assert.True(VarLinks.IsLink(defaultLink));
            Assert.True(File.Exists(defaultLink + ".disabled"));
            Assert.True(VarLinks.IsLink(leanLink));
            Assert.False(Directory.Exists(holding));
            Assert.Null(MoveOut.LatestHolding(vars));
        }

        [Fact]
        public void Apply_refuses_a_blocked_plan()
        {
            var plan = new MovePlan { Target = "A.B.1" };
            plan.NeededBy.Add("C.D.1");

            Assert.Throws<InvalidOperationException>(() => MoveOut.Apply(plan, _ => "x", paths));
        }

        [Fact]
        public void Undo_keeps_the_held_copy_if_a_new_file_took_the_place()
        {
            MakeVar("A.B.1");
            Update();
            string var = Tidied("A.B.1");
            var plan = new MovePlan { Target = "A.B.1" };
            plan.Moves.Add("A.B.1");
            string holding = MoveOut.Apply(plan, _ => var, paths);
            File.WriteAllText(var, "newer");

            List<string> problems = MoveOut.Undo(holding);

            Assert.Contains("already exists", Assert.Single(problems));
            Assert.Equal("newer", File.ReadAllText(var));
            Assert.True(File.Exists(Path.Combine(holding, "A.B.1.var")));
        }

        [Fact]
        public void Undo_finishes_a_move_that_failed_part_way()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            Update();
            string a = Tidied("A.B.1"), c = Tidied("C.D.1");
            var plan = new MovePlan { Target = "A.B.1" };
            plan.Moves.Add("A.B.1");
            plan.Moves.Add("C.D.1");
            string? holding = null;
            using (File.Open(c, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => MoveOut.Apply(plan, name => name == "A.B.1" ? a : c, paths));
            holding = MoveOut.LatestHolding(vars);
            Assert.NotNull(holding);

            Assert.Empty(MoveOut.Undo(holding!));

            Assert.True(File.Exists(a));
            Assert.True(File.Exists(c));
        }

        [Fact]
        public void Update_db_does_not_tidy_held_vars_back_in()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            Update();
            string var = Tidied("A.B.1");
            var plan = new MovePlan { Target = "A.B.1" };
            plan.Moves.Add("A.B.1");
            string holding = MoveOut.Apply(plan, _ => var, paths);

            Update();

            Assert.True(File.Exists(Path.Combine(holding, "A.B.1.var")));
            Assert.False(File.Exists(var));
            Assert.Equal(new[] { "C.D.1" }, Names());
        }

        [Fact]
        public async Task The_screen_turns_a_var_on_with_its_dependencies_then_off_with_its_users()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1");
            Update();
            (LibraryViewModel library, JobRunner jobs) = await Open("A.B.1");
            Assert.Equal("Turn on, with 1 dependency", library.ToggleLabel);

            library.ToggleCommand.Execute(null);
            WaitForJob(jobs);

            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
            Assert.True(VarLinks.IsLink(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.Equal("Turned on 2 vars.", jobs.Note);

            (library, jobs) = await Open("Dep.Pack.1");
            Assert.Equal("Turn off, with 1 var that needs it", library.ToggleLabel);

            library.ToggleCommand.Execute(null);
            WaitForJob(jobs);

            Assert.False(File.Exists(Path.Combine(links, "A.B.1.var")));
            Assert.False(File.Exists(Path.Combine(links, "Dep.Pack.1.var")));
            Assert.True(File.Exists(Tidied("Dep.Pack.1")));
        }

        [Fact]
        public async Task The_screen_blocks_a_needed_package()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1");
            Update();

            (LibraryViewModel library, _) = await Open("Dep.Pack.1");

            Assert.True(library.IsBlocked);
            Assert.False(library.CanMove);
            Assert.Equal("Can't move out: 1 other var needs this package.", library.BlockedText);
        }

        [Fact]
        public async Task The_screen_plans_moves_and_undoes()
        {
            MakeVar("A.B.1", "Dep.Pack.1:/Custom/a.vap");
            MakeVar("Dep.Pack.1");
            MakeVar("Other.Pack.1");
            Update();
            (LibraryViewModel library, JobRunner jobs) = await Open("A.B.1");
            Assert.True(library.CanMove);
            Assert.False(library.CanUndo);

            library.PlanMoveCommand.Execute(null);
            WaitForJob(jobs);

            Assert.True(library.HasMovePlan);
            Assert.False(library.CanMove);
            Assert.Equal("Move 2 vars", library.ConfirmMoveLabel);
            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1" }, library.MoveLines.Select(l => l.Name));
            Assert.True(File.Exists(Tidied("A.B.1")));

            library.ConfirmMoveCommand.Execute(null);
            WaitForJob(jobs);

            Assert.False(File.Exists(Tidied("A.B.1")));
            Assert.False(File.Exists(Tidied("Dep.Pack.1")));
            Assert.Equal(new[] { "Other.Pack.1" }, Names());
            Assert.True(library.CanUndo);
            Assert.Contains("holding folder", jobs.Note);

            library.UndoMoveCommand.Execute(null);
            WaitForJob(jobs);

            Assert.True(File.Exists(Tidied("A.B.1")));
            Assert.Equal(new[] { "A.B.1", "Dep.Pack.1", "Other.Pack.1" }, Names());
            Assert.False(library.CanUndo);
            Assert.Equal("Move undone.", jobs.Note);
        }

        [Fact]
        public async Task A_plan_survives_the_reload_that_follows_its_job()
        {
            MakeVar("A.B.1");
            Update();
            string settings = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settings);
            var jobs = new JobRunner(null);
            var shell = new ShellViewModel(settings, jobs, () => false, _ => { });
            await shell.LibraryLoaded;
            LibraryViewModel library = shell.Library!;
            library.Columns = 4;
            library.SelectCommand.Execute(library.Rows.OfType<TileRow>().Single().Tiles[0]);

            library.PlanMoveCommand.Execute(null);
            WaitForJob(jobs);
            await shell.LibraryLoaded;

            Assert.True(library.HasMovePlan);

            library.ConfirmMoveCommand.Execute(null);
            WaitForJob(jobs);
            await shell.LibraryLoaded;

            Assert.False(File.Exists(Tidied("A.B.1")));
            Assert.False(library.HasSelection);
        }

        [Fact]
        public async Task Choosing_another_package_drops_the_plan()
        {
            MakeVar("A.B.1");
            MakeVar("C.D.1");
            Update();
            (LibraryViewModel library, JobRunner jobs) = await Open("A.B.1");
            library.PlanMoveCommand.Execute(null);
            WaitForJob(jobs);

            library.SelectCommand.Execute(library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).Single(t => t.Item.VarName == "C.D.1"));

            Assert.False(library.HasMovePlan);
            Assert.Empty(library.MoveLines);
            Assert.True(library.CanMove);
        }

        [Fact]
        public async Task A_plan_survives_confirming_while_another_job_runs()
        {
            MakeVar("A.B.1");
            Update();
            (LibraryViewModel library, JobRunner jobs) = await Open("A.B.1");
            library.PlanMoveCommand.Execute(null);
            WaitForJob(jobs);
            using var release = new ManualResetEventSlim();
            Task other = jobs.Start("Other", "Done.", _ => release.Wait(Timeout))!;

            library.ConfirmMoveCommand.Execute(null);

            Assert.True(library.HasMovePlan);
            Assert.True(File.Exists(Tidied("A.B.1")));
            release.Set();
            Assert.True(other.Wait(Timeout));
        }

        [Fact]
        public async Task Cancelling_a_plan_moves_nothing()
        {
            MakeVar("A.B.1");
            Update();
            (LibraryViewModel library, JobRunner jobs) = await Open("A.B.1");
            library.PlanMoveCommand.Execute(null);
            WaitForJob(jobs);

            library.CancelMoveCommand.Execute(null);

            Assert.False(library.HasMovePlan);
            Assert.True(library.CanMove);
            Assert.True(File.Exists(Tidied("A.B.1")));
        }

        [Fact]
        public async Task Undo_is_offered_after_a_restart()
        {
            MakeVar("A.B.1");
            Update();
            string var = Tidied("A.B.1");
            var plan = new MovePlan { Target = "A.B.1" };
            plan.Moves.Add("A.B.1");
            MoveOut.Apply(plan, _ => var, paths);

            var library = new LibraryViewModel(storeFile, paths, new JobRunner(null), () => new string[0], null);
            await library.Reload();

            Assert.True(library.CanUndo);
        }
    }
}
