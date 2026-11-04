using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class VarLinksTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vmlinks-" + Guid.NewGuid().ToString("N"));
        private readonly string addons;
        private readonly string links;
        private readonly string temp;

        public VarLinksTests()
        {
            addons = Path.Combine(root, "AddonPackages");
            links = Path.Combine(addons, LibraryFolders.VarsLink);
            temp = Path.Combine(addons, LibraryFolders.TempVarLink);
            Directory.CreateDirectory(links);
            Directory.CreateDirectory(temp);
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private string Var(string name, string content = "data")
        {
            string path = Path.Combine(root, "repo", name + ".var");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private VarLinkResult Install(string name, string file, bool temporary = false)
        {
            int error;
            return VarLinks.Install(addons, name, file, temporary, out error);
        }

        [Fact]
        public void Create_links_a_file_and_copies_its_time()
        {
            string target = Var("A.B.1");
            File.SetLastWriteTime(target, new DateTime(2023, 1, 2, 3, 4, 5));
            string link = Path.Combine(links, "A.B.1.var");

            Assert.True(VarLinks.Create(link, target));

            Assert.True(VarLinks.IsLink(link));
            Assert.Equal("data", File.ReadAllText(link));
            Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5), new FileInfo(link).LastWriteTime);
        }

        [Fact]
        public void Create_refuses_a_missing_target()
        {
            string link = Path.Combine(links, "A.B.1.var");

            Assert.False(VarLinks.Create(link, Path.Combine(root, "gone.var")));

            Assert.False(File.Exists(link));
        }

        [Fact]
        public void Install_links_into_the_link_folder()
        {
            Assert.Equal(VarLinkResult.Linked, Install("A.B.1", Var("A.B.1")));

            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
        }

        [Fact]
        public void Install_temporary_links_into_the_temp_folder()
        {
            Assert.Equal(VarLinkResult.Linked, Install("A.B.1", Var("A.B.1"), temporary: true));

            Assert.True(VarLinks.IsLink(Path.Combine(temp, "A.B.1.var")));
            Assert.False(File.Exists(Path.Combine(links, "A.B.1.var")));
        }

        [Fact]
        public void Install_again_reports_already_linked()
        {
            string file = Var("A.B.1");
            Install("A.B.1", file);

            Assert.Equal(VarLinkResult.AlreadyLinked, Install("A.B.1", file));
        }

        [Fact]
        public void Install_clears_a_disabled_marker()
        {
            string marker = Path.Combine(links, "A.B.1.var.disabled");
            File.WriteAllText(marker, "");

            Install("A.B.1", Var("A.B.1"));

            Assert.False(File.Exists(marker));
        }

        [Fact]
        public void Install_reports_the_windows_error_when_the_link_cant_be_made()
        {
            Directory.Delete(links);
            int error;

            VarLinkResult result = VarLinks.Install(addons, "A.B.1", Var("A.B.1"), false, out error);

            Assert.Equal(VarLinkResult.Failed, result);
            Assert.NotEqual(0, error);
        }

        [Fact]
        public void Installed_lists_links_in_the_folder_and_at_the_top()
        {
            string nested = Path.Combine(links, "sub", "A.B.1.var");
            Directory.CreateDirectory(Path.GetDirectoryName(nested));
            VarLinks.Create(nested, Var("A.B.1"));
            string top = Path.Combine(addons, "C.D.1.var");
            VarLinks.Create(top, Var("C.D.1"));
            File.WriteAllText(Path.Combine(addons, "Real.File.1.var"), "not a link");
            VarLinks.Create(Path.Combine(temp, "E.F.1.var"), Var("E.F.1"));

            Dictionary<string, string> installed = VarLinks.Installed(addons);

            Assert.Equal(new[] { "A.B.1", "C.D.1" }, installed.Keys.OrderBy(k => k));
            Assert.Equal(top, installed["C.D.1"]);
        }

        [Fact]
        public void Installed_ignores_case_like_the_file_system()
        {
            VarLinks.Create(Path.Combine(links, "a.b.1.var"), Var("A.B.1"));

            Assert.True(VarLinks.Installed(addons).ContainsKey("A.B.1"));
        }

        [Fact]
        public void Installing_for_good_drops_the_temporary_link()
        {
            VarLinks.Create(Path.Combine(temp, "a.b.1.var"), Var("A.B.1"));
            VarLinks.Create(Path.Combine(temp, "c.d.1.var"), Var("C.D.1"));

            Assert.Equal(VarLinkResult.Linked, VarLinks.InstallForGood(addons, "A.B.1", Var("A.B.1"), out _));

            Assert.True(VarLinks.IsLink(Path.Combine(links, "A.B.1.var")));
            Assert.Equal(new[] { "c.d.1.var" }, VarLinks.TempLinks(addons));
        }

        [Fact]
        public void Installing_for_good_drops_the_temporary_link_when_already_linked()
        {
            VarLinks.Create(Path.Combine(links, "A.B.1.var"), Var("A.B.1"));
            VarLinks.Create(Path.Combine(temp, "a.b.1.var"), Var("A.B.1"));

            Assert.Equal(VarLinkResult.AlreadyLinked, VarLinks.InstallForGood(addons, "A.B.1", Var("A.B.1"), out _));

            Assert.Empty(VarLinks.TempLinks(addons));
        }

        [Fact]
        public void A_failed_link_keeps_the_temporary_one()
        {
            VarLinks.Create(Path.Combine(temp, "a.b.1.var"), Var("A.B.1"));
            Directory.Delete(links);

            Assert.Equal(VarLinkResult.Failed, VarLinks.InstallForGood(addons, "A.B.1", Var("A.B.1"), out _));

            Assert.Equal(new[] { "a.b.1.var" }, VarLinks.TempLinks(addons));
        }

        [Fact]
        public void Reading_what_is_installed_creates_nothing()
        {
            VarLinks.Create(Path.Combine(addons, "A.B.1.var"), Var("A.B.1"));
            Directory.Delete(links);

            Assert.True(VarLinks.ReadInstalled(addons).ContainsKey("A.B.1"));

            Assert.False(Directory.Exists(links));
        }

        [Fact]
        public void Reading_a_missing_folder_gives_nothing()
        {
            string missing = Path.Combine(root, "nowhere");

            Assert.Empty(VarLinks.ReadInstalled(missing));

            Assert.False(Directory.Exists(missing));
        }

        [Fact]
        public void Installed_creates_the_link_folder()
        {
            Directory.Delete(links);

            Assert.Empty(VarLinks.Installed(addons));

            Assert.True(Directory.Exists(links));
        }

        [Fact]
        public void Installed_with_no_addon_packages_creates_nothing()
        {
            string missing = Path.Combine(root, "gone", "AddonPackages");

            Assert.Empty(VarLinks.Installed(missing));

            Assert.False(Directory.Exists(missing));
        }

        [Fact]
        public void Remove_deletes_the_link_and_keeps_the_var()
        {
            string file = Var("A.B.1");
            Install("A.B.1", file);

            Assert.True(VarLinks.Remove(VarLinks.Installed(addons), "A.B.1"));

            Assert.False(File.Exists(Path.Combine(links, "A.B.1.var")));
            Assert.True(File.Exists(file));
        }

        [Fact]
        public void Remove_ignores_a_var_that_is_not_linked()
        {
            Assert.False(VarLinks.Remove(VarLinks.Installed(addons), "A.B.1"));
        }

        [Fact]
        public void Rebuild_points_a_link_at_the_new_file()
        {
            string old = Var("A.B.1", "old");
            string link = Path.Combine(links, "A.B.1.var");
            VarLinks.Create(link, old);
            string moved = Path.Combine(root, "moved", "A.B.1.var");
            Directory.CreateDirectory(Path.GetDirectoryName(moved));
            File.WriteAllText(moved, "moved");

            VarLinks.Rebuild(link, name => name == "A.B.1" ? moved : null);

            Assert.True(VarLinks.IsLink(link));
            Assert.Equal("moved", File.ReadAllText(link));
        }

        [Fact]
        public void Rebuild_removes_a_link_to_a_var_the_library_lost()
        {
            string link = Path.Combine(links, "A.B.1.var");
            VarLinks.Create(link, Var("A.B.1"));

            VarLinks.Rebuild(link, name => null);

            Assert.False(File.Exists(link));
        }

        [Fact]
        public void Temp_links_are_listed_in_lower_case_without_real_files()
        {
            VarLinks.Create(Path.Combine(temp, "A.B.1.var"), Var("A.B.1"));
            File.WriteAllText(Path.Combine(temp, "Real.File.1.var"), "not a link");

            Assert.Equal(new[] { "a.b.1.var" }, VarLinks.TempLinks(addons));
        }

        [Fact]
        public void Remove_temp_links_deletes_only_the_named_ones()
        {
            VarLinks.Create(Path.Combine(temp, "A.B.1.var"), Var("A.B.1"));
            VarLinks.Create(Path.Combine(temp, "C.D.1.var"), Var("C.D.1"));

            VarLinks.RemoveTempLinks(addons, new[] { "a.b.1.var", "gone.var" });

            Assert.Equal(new[] { "c.d.1.var" }, VarLinks.TempLinks(addons));
        }
    }
}
