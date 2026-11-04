using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
