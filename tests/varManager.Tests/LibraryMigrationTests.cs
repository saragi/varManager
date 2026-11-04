using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class LibraryMigrationTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vmtest-" + Guid.NewGuid().ToString("N"));
        private readonly string addon;
        private readonly string repo;
        private readonly string log;
        private readonly string installList;

        public LibraryMigrationTests()
        {
            addon = Path.Combine(root, "AddonPackages");
            repo = Path.Combine(root, "repo");
            log = Path.Combine(root, "logs", "move.tsv");
            installList = Path.Combine(root, "varsForInstall.txt");
            Directory.CreateDirectory(addon);
            Directory.CreateDirectory(repo);
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private string Add(string relative, string content = "x")
        {
            string path = Path.Combine(addon, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private string Tidied(string relative)
        {
            return Path.Combine(repo, "tidied", relative);
        }

        private MigrationPlan Plan()
        {
            return LibraryMigration.Plan(addon, repo, "tidied", "redundant", new[] { "___VarsLink___" });
        }

        private IEnumerable<string> AddonFiles()
        {
            return Directory.GetFiles(addon, "*", SearchOption.AllDirectories);
        }

        [Fact]
        public void Plan_changes_nothing_on_disk()
        {
            Add(@"A\A.B.1.var");
            Add(@"A\A.C.1.var.disabled");
            Plan();
            Assert.Equal(2, AddonFiles().Count());
            Assert.Empty(Directory.GetFileSystemEntries(repo));
        }

        [Fact]
        public void Enabled_var_moves_and_is_marked_for_linking()
        {
            Add(@"Sub\Deep\A.B.1.var");
            MigrationPlan plan = Plan();
            LibraryMigration.Apply(plan, log, installList);
            Assert.True(File.Exists(Tidied(@"A\A.B.1.var")));
            Assert.Equal(new[] { "A.B.1" }, File.ReadAllLines(installList));
        }

        [Fact]
        public void Disabled_only_content_is_stored_as_a_var_and_not_linked()
        {
            Add(@"A\A.B.1.var.disabled", "content");
            MigrationPlan plan = Plan();
            LibraryMigration.Apply(plan, log, installList);
            Assert.Equal("content", File.ReadAllText(Tidied(@"A\A.B.1.var")));
            Assert.Empty(plan.VarsToInstall);
        }

        [Fact]
        public void Var_with_disabled_copy_stays_disabled_and_copy_is_set_aside()
        {
            Add(@"A\A.B.1.var", "same");
            Add(@"A\A.B.1.var.disabled", "same");
            MigrationPlan plan = Plan();
            Assert.Empty(plan.VarsToInstall);
            Assert.Equal(1, plan.Count(MigrationActionKind.Var));
            Assert.Equal(1, plan.Count(MigrationActionKind.Redundant));
        }

        [Fact]
        public void Empty_marker_keeps_the_var_disabled_and_is_not_stored_as_a_var()
        {
            Add(@"A\A.B.1.var", "real");
            Add(@"A\A.B.1.var.disabled", "");
            MigrationPlan plan = Plan();
            LibraryMigration.Apply(plan, log, installList);
            Assert.Empty(plan.VarsToInstall);
            Assert.Equal("real", File.ReadAllText(Tidied(@"A\A.B.1.var")));
        }

        [Fact]
        public void Uppercase_disabled_rename_is_recognised()
        {
            Add(@"A\A.B.1.DISABLED", "content");
            Assert.Equal(1, Plan().Count(MigrationActionKind.DisabledVar));
        }

        [Fact]
        public void Duplicate_prefers_creator_folder_copy_over_top_level()
        {
            string nested = Add(@"A\A.B.1.var", "nested");
            string top = Add("A.B.1.var", "top");
            MigrationPlan plan = Plan();
            Assert.Equal(nested, plan.Actions.Single(a => a.Kind == MigrationActionKind.Var).Source);
            Assert.Equal(top, plan.Actions.Single(a => a.Kind == MigrationActionKind.Redundant).Source);
            Assert.Contains(plan.Warnings, w => w.Contains("Duplicate"));
        }

        [Fact]
        public void Duplicates_in_redundant_folder_get_unique_names()
        {
            Add(@"A\A.B.1.var");
            Add(@"X\A.B.1.var");
            Add(@"Y\A.B.1.var");
            MigrationPlan plan = Plan();
            LibraryMigration.Apply(plan, log, installList);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(repo, "redundant")).Length);
        }

        [Fact]
        public void Var_already_in_repository_is_not_overwritten_but_is_still_linked()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Tidied(@"A\A.B.1.var")));
            File.WriteAllText(Tidied(@"A\A.B.1.var"), "old");
            Add(@"A\A.B.1.var", "new");
            MigrationPlan plan = Plan();
            LibraryMigration.Apply(plan, log, installList);
            Assert.Equal("old", File.ReadAllText(Tidied(@"A\A.B.1.var")));
            Assert.Contains("A.B.1", plan.VarsToInstall);
        }

        [Fact]
        public void Companions_follow_their_var_including_for_disabled_content()
        {
            Add(@"A\A.B.1.var");
            Add(@"A\A.B.1.jpg");
            Add(@"A\A.B.1.var.depend.txt");
            Add(@"A\A.C.1.var.disabled");
            Add(@"A\A.C.1.jpg");
            LibraryMigration.Apply(Plan(), log, installList);
            Assert.True(File.Exists(Tidied(@"A\A.B.1.jpg")));
            Assert.True(File.Exists(Tidied(@"A\A.B.1.var.depend.txt")));
            Assert.True(File.Exists(Tidied(@"A\A.C.1.jpg")));
        }

        [Fact]
        public void Unrelated_files_and_badly_named_vars_are_left_and_reported()
        {
            string note = Add("Dependencies_ReadMe.txt");
            string bad = Add("not-a-package.var");
            MigrationPlan plan = Plan();
            Assert.Contains(note, plan.LeftInPlace);
            Assert.Contains(bad, plan.LeftInPlace);
            Assert.Empty(plan.Actions);
        }

        [Fact]
        public void Creator_names_with_spaces_work()
        {
            Add(@"s p l i n e  VR\s p l i n e  VR.Hair_Mega_Pack.1.var");
            LibraryMigration.Apply(Plan(), log, installList);
            Assert.True(File.Exists(Tidied(@"s p l i n e  VR\s p l i n e  VR.Hair_Mega_Pack.1.var")));
        }

        [Fact]
        public void Link_folders_are_skipped()
        {
            Add(@"___VarsLink___\A.B.1.var");
            Assert.Empty(Plan().Actions);
        }

        [Fact]
        public void Existing_install_list_is_kept()
        {
            File.WriteAllLines(installList, new[] { "X.Y.1" });
            Add(@"A\A.B.1.var");
            LibraryMigration.Apply(Plan(), log, installList);
            Assert.Equal(new[] { "X.Y.1", "A.B.1" }, File.ReadAllLines(installList));
        }

        [Fact]
        public void Apply_moves_nothing_if_a_source_has_vanished()
        {
            string gone = Add(@"A\A.B.1.var");
            string kept = Add(@"A\A.C.1.var");
            MigrationPlan plan = Plan();
            File.Delete(gone);
            Assert.Throws<FileNotFoundException>(() => LibraryMigration.Apply(plan, log, installList));
            Assert.True(File.Exists(kept));
            Assert.False(File.Exists(log));
        }

        [Fact]
        public void Apply_moves_nothing_if_a_destination_appeared()
        {
            string kept = Add(@"A\A.B.1.var");
            MigrationPlan plan = Plan();
            Directory.CreateDirectory(Path.GetDirectoryName(Tidied(@"A\A.B.1.var")));
            File.WriteAllText(Tidied(@"A\A.B.1.var"), "new");
            Assert.Throws<IOException>(() => LibraryMigration.Apply(plan, log, installList));
            Assert.True(File.Exists(kept));
        }

        [Fact]
        public void Apply_refuses_a_different_drive()
        {
            MigrationPlan plan = new MigrationPlan { SameDrive = false };
            Assert.Throws<InvalidOperationException>(() => LibraryMigration.Apply(plan, log, installList));
        }

        [Fact]
        public void Undo_restores_every_file_with_its_content()
        {
            Add(@"A\A.B.1.var", "one");
            Add(@"A\A.B.1.jpg", "image");
            Add(@"A\A.C.1.var.disabled", "two");
            Add("A.B.1.var", "dupe");
            LibraryMigration.Apply(Plan(), log, installList);
            Assert.Empty(AddonFiles());

            MigrationUndoResult result = LibraryMigration.Undo(log);

            Assert.Equal(4, result.Restored);
            Assert.Empty(result.Failed);
            Assert.Equal("one", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.var")));
            Assert.Equal("image", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.jpg")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(addon, @"A\A.C.1.var.disabled")));
            Assert.Equal("dupe", File.ReadAllText(Path.Combine(addon, "A.B.1.var")));
        }

        [Fact]
        public void Undo_skips_a_logged_move_that_never_happened()
        {
            string source = Add(@"A\A.B.1.var");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            File.WriteAllText(log, source + "\t" + Tidied(@"A\A.B.1.var") + Environment.NewLine);
            MigrationUndoResult result = LibraryMigration.Undo(log);
            Assert.Equal(0, result.Restored);
            Assert.Empty(result.Failed);
            Assert.True(File.Exists(source));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static void Link(string link, string target, int flags)
        {
            if (!CreateSymbolicLink(link, target, flags | 2))
                throw new InvalidOperationException("These tests need Windows Developer Mode to create symlinks.");
        }

        /// <summary>Puts the folders where the app leaves them after migration and Update DB.</summary>
        private string SimulateFirstRun()
        {
            string defaultSwitch = Path.Combine(root, "switches", "default");
            Directory.CreateDirectory(Path.GetDirectoryName(defaultSwitch));
            Directory.Move(addon, defaultSwitch);
            Link(addon, defaultSwitch, 1);
            string links = Path.Combine(defaultSwitch, "___VarsLink___");
            Directory.CreateDirectory(Path.Combine(links, "sub"));
            Link(Path.Combine(links, "A.B.1.var"), Tidied(@"A\A.B.1.var"), 0);
            Link(Path.Combine(links, "sub", "A.D.1.var"), Tidied(@"A\A.B.1.var"), 0);
            string temp = Path.Combine(defaultSwitch, "___TempVarLink___");
            Directory.CreateDirectory(temp);
            Link(Path.Combine(temp, "A.B.1.var"), Tidied(@"A\A.B.1.var"), 0);
            string dangling = Path.Combine(defaultSwitch, "___MissingVarLink___");
            Directory.CreateDirectory(dangling);
            Link(Path.Combine(dangling, "Gone.Var.1.var"), Path.Combine(root, "gone.var"), 0);
            return defaultSwitch;
        }

        private static readonly string[] LinkDirs = { "___VarsLink___", "___MissingVarLink___", "___TempVarLink___" };

        [Fact]
        public void Rollback_returns_a_real_folder_with_every_file()
        {
            Add(@"A\A.B.1.var", "one");
            Add(@"A\A.C.1.var.disabled", "two");
            Add("Dependencies_ReadMe.txt", "note");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();

            MigrationUndoResult result = LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.Empty(result.Failed);
            Assert.False(new DirectoryInfo(addon).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.False(Directory.Exists(defaultSwitch));
            Assert.Equal("one", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.var")));
            Assert.Equal("two", File.ReadAllText(Path.Combine(addon, @"A\A.C.1.var.disabled")));
            Assert.Equal("note", File.ReadAllText(Path.Combine(addon, "Dependencies_ReadMe.txt")));
            Assert.False(Directory.Exists(Path.Combine(addon, "___VarsLink___")));
            Assert.False(Directory.Exists(Path.Combine(addon, "___TempVarLink___")));
            Assert.False(Directory.Exists(Path.Combine(addon, "___MissingVarLink___")));
            Assert.False(File.Exists(Tidied(@"A\A.B.1.var")));
        }

        [Fact]
        public void Rollback_finishes_after_being_interrupted_between_steps()
        {
            Add(@"A\A.B.1.var", "one");
            Add("Dependencies_ReadMe.txt", "note");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();
            Directory.Delete(addon);

            MigrationUndoResult result = LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.Empty(result.Failed);
            Assert.False(Directory.Exists(defaultSwitch));
            Assert.Equal("note", File.ReadAllText(Path.Combine(addon, "Dependencies_ReadMe.txt")));
            Assert.Equal("one", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.var")));
            foreach (string linkDir in LinkDirs)
                Assert.False(Directory.Exists(Path.Combine(addon, linkDir)));
        }

        [Fact]
        public void Rollback_finishes_if_an_empty_AddonPackages_appeared_in_between()
        {
            Add("Dependencies_ReadMe.txt", "note");
            Add(@"A\A.B.1.var", "one");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();
            Directory.Delete(addon);
            Directory.CreateDirectory(addon);

            LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.False(Directory.Exists(defaultSwitch));
            Assert.Equal("note", File.ReadAllText(Path.Combine(addon, "Dependencies_ReadMe.txt")));
        }

        [Fact]
        public void Rollback_leaves_links_alone_when_nothing_was_migrated()
        {
            string var = Add(@"A\A.B.1.var", "one");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            File.WriteAllText(log, "");
            string otherLinks = Path.Combine(root, "switches", "other", "___VarsLink___");
            Directory.CreateDirectory(otherLinks);
            string link = Path.Combine(otherLinks, "A.B.1.var");
            Link(link, var, 0);

            MigrationUndoResult result = LibraryMigration.Rollback(log, addon, Path.Combine(root, "switches", "default"), LinkDirs);

            Assert.Equal(0, result.Restored);
            Assert.True(File.Exists(var));
            Assert.True(new FileInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        [Fact]
        public void Rollback_refuses_a_missing_log_before_changing_anything()
        {
            Add(@"A\A.B.1.var");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();

            Assert.Throws<FileNotFoundException>(() => LibraryMigration.Rollback(log + ".wrong", addon, defaultSwitch, LinkDirs));

            Assert.True(new DirectoryInfo(addon).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.True(new FileInfo(Path.Combine(defaultSwitch, "___VarsLink___", "A.B.1.var")).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Directory.Delete(addon);
        }

        [Fact]
        public void Rollback_clears_links_in_other_switches_when_one_is_active()
        {
            Add(@"A\A.B.1.var", "one");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();
            string lean = Path.Combine(root, "switches", "lean");
            string leanLinks = Path.Combine(lean, "___VarsLink___");
            Directory.CreateDirectory(leanLinks);
            Link(Path.Combine(leanLinks, "A.B.1.var"), Tidied(@"A\A.B.1.var"), 0);
            Directory.Delete(addon);
            Link(addon, lean, 1);

            MigrationUndoResult result = LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.Empty(result.Failed);
            Assert.False(Directory.Exists(leanLinks));
            Assert.False(new DirectoryInfo(addon).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("one", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.var")));
        }

        [Fact]
        public void Rollback_reports_a_file_it_cannot_find_and_restores_the_rest()
        {
            Add(@"A\A.B.1.var", "one");
            Add(@"A\A.C.1.var", "two");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();
            File.Delete(Tidied(@"A\A.C.1.var"));
            File.AppendAllText(log, Path.Combine(addon, "Lost.Var.1.var") + "\t" + Tidied(@"Lost\Lost.Var.1.var") + Environment.NewLine);

            MigrationUndoResult result = LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.Equal(2, result.Failed.Count);
            Assert.Equal(1, result.Restored);
            Assert.Contains(result.Failed, f => f.Contains("A.C.1.var"));
            Assert.Contains(result.Failed, f => f.Contains("Lost.Var.1.var"));
            Assert.False(Directory.Exists(defaultSwitch));
            Assert.Equal("one", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.var")));
        }

        [Fact]
        public void Rollback_can_run_again_after_finishing()
        {
            Add(@"A\A.B.1.var", "one");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();
            LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            MigrationUndoResult again = LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.Equal(0, again.Restored);
            Assert.Empty(again.Failed);
            Assert.Equal("one", File.ReadAllText(Path.Combine(addon, @"A\A.B.1.var")));
        }

        [Fact]
        public void Rollback_keeps_a_real_var_found_in_a_link_folder()
        {
            Add(@"A\A.B.1.var", "one");
            LibraryMigration.Apply(Plan(), log, installList);
            string defaultSwitch = SimulateFirstRun();
            File.WriteAllText(Path.Combine(defaultSwitch, "___VarsLink___", "Real.Var.1.var"), "real");

            LibraryMigration.Rollback(log, addon, defaultSwitch, LinkDirs);

            Assert.Equal("real", File.ReadAllText(Path.Combine(addon, "___VarsLink___", "Real.Var.1.var")));
        }

        [Fact]
        public void Rollback_stops_before_changing_anything_if_the_default_switch_is_gone()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            File.WriteAllText(log, "");
            string elsewhere = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            Directory.Delete(addon);
            Link(addon, elsewhere, 1);

            Assert.Throws<DirectoryNotFoundException>(() => LibraryMigration.Rollback(log, addon, Path.Combine(root, "missing"), LinkDirs));

            Assert.True(new DirectoryInfo(addon).Attributes.HasFlag(FileAttributes.ReparsePoint));
            Directory.Delete(addon);
        }

        [Theory]
        [InlineData("A.B.1", true)]
        [InlineData("abc tits.Alice.2", true)]
        [InlineData("A.B", false)]
        [InlineData("A.B.latest", false)]
        [InlineData("A.B.C.1", false)]
        [InlineData(".B.1", false)]
        public void IsVarName_matches_the_package_naming_rule(string name, bool expected)
        {
            Assert.Equal(expected, LibraryMigration.IsVarName(name));
        }
    }
}
