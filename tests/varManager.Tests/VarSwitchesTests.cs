using System;
using System.IO;
using System.Linq;
using System.Threading;
using SimpleJSON;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class VarSwitchesTests : IDisposable
    {
        private readonly string vam = Path.Combine(Path.GetTempPath(), "vmswitch-" + Guid.NewGuid().ToString("N"));
        private readonly string packs;

        public VarSwitchesTests()
        {
            packs = Path.Combine(vam, "AddonPackages");
            Directory.CreateDirectory(vam);
        }

        public void Dispose()
        {
            if (Directory.Exists(packs) && new DirectoryInfo(packs).Attributes.HasFlag(FileAttributes.ReparsePoint))
                new DirectoryInfo(packs).Delete();
            Directory.Delete(vam, true);
        }

        private string SwitchFolder(string name)
        {
            return Path.Combine(vam, LibraryFolders.AddonPacksSwitch, name);
        }

        private SwitchChange Point(string name)
        {
            int error;
            return VarSwitches.Point(vam, name, out error);
        }

        private void Marker(string name)
        {
            Directory.CreateDirectory(SwitchFolder(name));
            File.WriteAllText(Path.Combine(SwitchFolder(name), "marker.txt"), name);
        }

        private string CurrentMarker()
        {
            return File.ReadAllText(Path.Combine(packs, "marker.txt"));
        }

        [Fact]
        public void Names_create_default_and_list_it_first()
        {
            Directory.CreateDirectory(SwitchFolder("alpha"));

            Assert.Equal(new[] { "default", "alpha" }, VarSwitches.Names(vam));
            Assert.True(Directory.Exists(SwitchFolder("default")));
        }

        [Fact]
        public void Ensure_link_creates_a_missing_link_to_default()
        {
            Marker("default");

            Assert.Equal("default", VarSwitches.EnsureLink(vam));

            Assert.Equal("default", CurrentMarker());
        }

        [Fact]
        public void Ensure_link_moves_a_real_folder_into_default()
        {
            Marker("default");
            Directory.CreateDirectory(packs);
            File.WriteAllText(Path.Combine(packs, "loose.txt"), "kept");

            Assert.Equal("default", VarSwitches.EnsureLink(vam));

            Assert.Equal("kept", File.ReadAllText(Path.Combine(SwitchFolder("default"), "loose.txt")));
            Assert.True(new DirectoryInfo(packs).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }

        [Fact]
        public void Ensure_link_keeps_a_file_that_default_already_has_in_a_dated_folder()
        {
            Marker("default");
            Directory.CreateDirectory(packs);
            File.WriteAllText(Path.Combine(packs, "marker.txt"), "clash");

            VarSwitches.EnsureLink(vam);

            string dated = Assert.Single(Directory.GetDirectories(vam, "AddonPackages_*"));
            Assert.Equal("clash", File.ReadAllText(Path.Combine(dated, "marker.txt")));
            Assert.Equal("default", CurrentMarker());
        }

        [Fact]
        public void Ensure_link_returns_the_switch_already_in_use()
        {
            Marker("default");
            Marker("lean");
            Point("lean");

            Assert.Equal("lean", VarSwitches.EnsureLink(vam));
        }

        [Fact]
        public void Ensure_link_repairs_a_link_to_a_lost_switch()
        {
            Marker("default");
            Marker("lean");
            Point("lean");
            Directory.Delete(SwitchFolder("lean"), true);

            Assert.Equal("default", VarSwitches.EnsureLink(vam));

            Assert.Equal("default", CurrentMarker());
        }

        [Fact]
        public void Point_switches_and_reports_it()
        {
            Marker("default");
            Marker("lean");
            VarSwitches.EnsureLink(vam);

            Assert.Equal(SwitchChange.Switched, Point("lean"));

            Assert.Equal("lean", CurrentMarker());
        }

        [Fact]
        public void Point_at_the_current_switch_changes_nothing()
        {
            Marker("default");
            VarSwitches.EnsureLink(vam);
            DateTime written = new DirectoryInfo(packs).LastWriteTimeUtc;
            Thread.Sleep(30);

            Assert.Equal(SwitchChange.Unchanged, Point("default"));

            Assert.Equal(written, new DirectoryInfo(packs).LastWriteTimeUtc);
        }

        [Fact]
        public void Point_ignores_case_when_comparing_with_the_current_switch()
        {
            Marker("lean");
            Point("Lean");

            Assert.Equal(SwitchChange.Unchanged, Point("LEAN"));
        }

        [Fact]
        public void Point_replaces_an_empty_real_folder()
        {
            Marker("lean");
            Directory.CreateDirectory(packs);

            Assert.Equal(SwitchChange.Switched, Point("lean"));

            Assert.Equal("lean", CurrentMarker());
        }

        [Fact]
        public void Point_reports_the_windows_error_when_the_link_cant_be_made()
        {
            File.WriteAllText(packs, "a file in the way");
            int error;

            Assert.Equal(SwitchChange.Failed, VarSwitches.Point(vam, "lean", out error));

            Assert.NotEqual(0, error);
            File.Delete(packs);
        }

        [Fact]
        public void Point_creates_a_new_switch()
        {
            Marker("default");
            VarSwitches.EnsureLink(vam);

            Assert.Equal(SwitchChange.Switched, Point("brand new"));

            Assert.True(Directory.Exists(SwitchFolder("brand new")));
        }

        [Fact]
        public void Point_creates_the_link_when_it_is_missing()
        {
            Marker("lean");

            Assert.Equal(SwitchChange.Switched, Point("lean"));

            Assert.Equal("lean", CurrentMarker());
        }

        [Fact]
        public void Point_refuses_a_folder_with_files_in_it_and_leaves_them()
        {
            Directory.CreateDirectory(packs);
            File.WriteAllText(Path.Combine(packs, "loose.txt"), "kept");

            Assert.ThrowsAny<Exception>(() => Point("lean"));

            Assert.Equal("kept", File.ReadAllText(Path.Combine(packs, "loose.txt")));
        }

        [Fact]
        public void Rename_then_point_keeps_the_switch_in_use()
        {
            Marker("default");
            Marker("lean");
            Point("lean");

            VarSwitches.Rename(vam, "lean", "slim");
            Assert.Equal(SwitchChange.Switched, Point("slim"));

            Assert.Equal("lean", CurrentMarker());
            Assert.False(Directory.Exists(SwitchFolder("lean")));
        }

        [Fact]
        public void Delete_removes_a_switch_and_its_contents()
        {
            Marker("lean");

            VarSwitches.Delete(vam, "lean");

            Assert.False(Directory.Exists(SwitchFolder("lean")));
        }

        [Fact]
        public void Link_folder_is_inside_the_switch()
        {
            Assert.Equal(Path.Combine(SwitchFolder("lean"), LibraryFolders.VarsLink), VarSwitches.LinkFolder(vam, "lean"));
        }

        [Fact]
        public void Unused_name_adds_a_number_and_ignores_case()
        {
            Assert.Equal("lean", VarSwitches.UnusedName(new[] { "default" }, "lean"));
            Assert.Equal("lean 3", VarSwitches.UnusedName(new[] { "LEAN", "Lean 2" }, "lean"));
        }

        [Fact]
        public void Rescan_request_replaces_an_old_request()
        {
            string file = VamBridge.RequestFile(vam);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "old");

            VamBridge.WriteRescan(vam);

            Assert.Equal("true", JSONNode.Parse(File.ReadAllText(file))["rescan"].Value);
        }

        [Fact]
        public void Rescan_request_creates_the_plugin_folder()
        {
            VamBridge.WriteRescan(vam);

            Assert.Equal(Path.Combine(vam, "Custom", "PluginData", "feelfar", "loadscene.json"),
                Directory.GetFiles(vam, "loadscene.json", SearchOption.AllDirectories).Single());
        }
    }
}
