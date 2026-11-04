using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class VarTidierTests : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmtidy-" + Guid.NewGuid().ToString("N"));
        private readonly string repo;
        private readonly string addons;
        private readonly List<string> problems = new List<string>();
        private readonly List<string> progress = new List<string>();

        public VarTidierTests()
        {
            repo = Path.Combine(root, "repo");
            addons = Path.Combine(root, "vam", "AddonPackages");
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(addons);
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private string Touch(string folder, string relative, string content = "x")
        {
            string path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private void Tidy(params string[] vars)
        {
            VarTidier.Tidy(vars, repo, problems.Add, (current, total) => progress.Add(current + "/" + total));
        }

        private void Link(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            if (!CreateSymbolicLink(link, target, 2))
                throw new InvalidOperationException("These tests need Windows Developer Mode to create symlinks.");
        }

        [Theory]
        [InlineData("A.B.1", true)]
        [InlineData("Deep Creator.Some Pack.12", true)]
        [InlineData("A.B.latest", false)]
        [InlineData("A.B", false)]
        [InlineData("A.B.C.1", false)]
        [InlineData("A.B.1x", false)]
        public void Var_names_need_three_parts_and_a_number(string name, bool valid)
        {
            Assert.Equal(valid, VarTidier.IsVarName(name));
        }

        [Fact]
        public void Var_file_ignores_folder_and_extension()
        {
            Assert.True(VarTidier.IsVarFile(@"C:\some.folder.2\A.B.1.var"));
            Assert.False(VarTidier.IsVarFile(@"C:\A.B.1\Broken.var"));
        }

        [Fact]
        public void Repository_vars_skip_sorted_folders()
        {
            string dropped = Touch(repo, "A.B.1.var");
            string nested = Touch(repo, @"incoming\C.D.2.var");
            Touch(repo, "readme.txt");
            foreach (string folder in new[]
            {
                LibraryFolders.Tidied, LibraryFolders.Redundant, LibraryFolders.NotComplyRule,
                LibraryFolders.StaleVars, LibraryFolders.OldVersionVars, LibraryFolders.DeletedVars
            })
                Touch(repo, folder + @"\E.F.1.var");

            Assert.Equal(new[] { dropped, nested }.OrderBy(p => p), VarTidier.RepositoryVars(repo).OrderBy(p => p));
        }

        [Fact]
        public void Repository_vars_skip_links()
        {
            string target = Touch(root, "elsewhere.var");
            Link(Path.Combine(repo, "A.B.1.var"), target);

            Assert.Empty(VarTidier.RepositoryVars(repo));
        }

        [Fact]
        public void Loose_addon_vars_skip_link_folders_and_links()
        {
            string loose = Touch(addons, @"sub\A.B.1.var");
            Touch(addons, LibraryFolders.VarsLink + @"\C.D.1.var");
            Touch(addons, LibraryFolders.MissingVarLink + @"\E.F.1.var");
            Touch(addons, LibraryFolders.TempVarLink + @"\G.H.1.var");
            Link(Path.Combine(addons, "I.J.1.var"), Touch(root, "elsewhere.var"));

            Assert.Equal(new[] { loose }, VarTidier.LooseAddonVars(addons));
        }

        [Fact]
        public void Install_list_keeps_saved_names_and_adds_valid_loose_vars_once()
        {
            List<string> list = VarTidier.MergeInstallList(
                new[] { "Saved.Pack.1", "A.B.1" },
                new[] { @"C:\x\A.B.1.var", @"C:\x\C.D.2.var", @"C:\x\Broken.var" });

            Assert.Equal(new[] { "Saved.Pack.1", "A.B.1", "C.D.2" }, list);
        }

        [Fact]
        public void Tidy_sorts_by_creator()
        {
            string var = Touch(repo, "Deep Creator.Pack.1.var", "first");

            Tidy(var);

            Assert.False(File.Exists(var));
            string moved = Path.Combine(repo, LibraryFolders.Tidied, "Deep Creator", "Deep Creator.Pack.1.var");
            Assert.Equal("first", File.ReadAllText(moved));
            Assert.Empty(problems);
        }

        [Fact]
        public void Tidy_moves_a_same_named_var_to_redundant_and_keeps_both()
        {
            string tidied = Touch(repo, LibraryFolders.Tidied + @"\A\A.B.1.var", "kept");
            Touch(repo, LibraryFolders.Redundant + @"\A.B.1.var", "older");
            Touch(repo, LibraryFolders.Redundant + @"\A.B.1(1).var", "oldest");
            string var = Touch(repo, "A.B.1.var", "new");

            Tidy(var);

            Assert.Equal("kept", File.ReadAllText(tidied));
            Assert.Equal("older", File.ReadAllText(Path.Combine(repo, LibraryFolders.Redundant, "A.B.1.var")));
            Assert.Equal("oldest", File.ReadAllText(Path.Combine(repo, LibraryFolders.Redundant, "A.B.1(1).var")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(repo, LibraryFolders.Redundant, "A.B.1(2).var")));
            Assert.Contains("has same filename", Assert.Single(problems));
        }

        [Fact]
        public void Tidy_moves_a_badly_named_var_aside()
        {
            Touch(repo, LibraryFolders.NotComplyRule + @"\Broken.var", "older");
            string var = Touch(repo, "Broken.var", "new");

            Tidy(var);

            Assert.Equal("new", File.ReadAllText(Path.Combine(repo, LibraryFolders.NotComplyRule, "Broken(1).var")));
            Assert.Contains("not comply", Assert.Single(problems));
        }

        [Fact]
        public void Tidy_reports_a_failed_move_and_carries_on()
        {
            string locked = Touch(repo, "A.B.1.var");
            string next = Touch(repo, "C.D.1.var");

            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                Tidy(locked, next);

            Assert.True(File.Exists(locked));
            Assert.Contains("failed", Assert.Single(problems));
            Assert.True(File.Exists(Path.Combine(repo, LibraryFolders.Tidied, "C", "C.D.1.var")));
            Assert.Equal(new[] { "0/2", "1/2" }, progress);
        }

        [Fact]
        public void Tidy_reports_progress_for_each_var()
        {
            Tidy(Touch(repo, "A.B.1.var"), Touch(repo, "C.D.1.var"));

            Assert.Equal(new[] { "0/2", "1/2" }, progress);
        }

        [Fact]
        public void Tidy_creates_its_folders_with_nothing_to_do()
        {
            Tidy();

            Assert.True(Directory.Exists(Path.Combine(repo, LibraryFolders.Tidied)));
            Assert.True(Directory.Exists(Path.Combine(repo, LibraryFolders.Redundant)));
            Assert.True(Directory.Exists(Path.Combine(repo, LibraryFolders.NotComplyRule)));
        }
    }
}
