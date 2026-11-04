using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class VarScannerTests : IDisposable
    {
        private const string Meta = "{\"dependencies\":{}}";

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmscan-" + Guid.NewGuid().ToString("N"));
        private readonly string previews;
        private readonly List<string> warnings = new List<string>();
        private readonly List<string> problems = new List<string>();

        public VarScannerTests()
        {
            Directory.CreateDirectory(root);
            previews = Path.Combine(root, "previews");
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private string Var(string fileName, params string[] namesAndContents)
        {
            string path = Path.Combine(root, fileName);
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(path)))
            {
                for (int i = 0; i < namesAndContents.Length; i += 2)
                {
                    zip.PutNextEntry(new ZipEntry(namesAndContents[i]) { DateTime = new DateTime(2024, 2, 3, 4, 5, 6) });
                    byte[] bytes = Encoding.UTF8.GetBytes(namesAndContents[i + 1]);
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
            return path;
        }

        private VarScan Read(string path)
        {
            return VarScanner.Read(path, previews, warnings.Add, problems.Add);
        }

        private VarScan ReadEntries(params string[] entryNames)
        {
            var entries = new List<string> { "meta.json", Meta };
            foreach (string name in entryNames)
            {
                entries.Add(name);
                entries.Add("{}");
            }
            return Read(Var("A.B.1.var", entries.ToArray()));
        }

        [Fact]
        public void Reads_name_size_and_dates()
        {
            string path = Var("Deep Creator.Some Pack.12.var", "meta.json", Meta);
            File.SetLastWriteTime(path, new DateTime(2023, 1, 2, 3, 4, 5));

            VarScan scan = Read(path);

            Assert.Equal(VarScanOutcome.Scanned, scan.Outcome);
            Assert.Equal("Deep Creator", scan.CreatorName);
            Assert.Equal("Some Pack", scan.PackageName);
            Assert.Equal(12, scan.Version);
            Assert.Equal(new FileInfo(path).Length, scan.FileSize);
            Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5), scan.VarDate);
            Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6), scan.MetaDate);
        }

        [Fact]
        public void A_version_that_is_not_a_number_becomes_one()
        {
            Assert.Equal(1, Read(Var("A.B.x.var", "meta.json", Meta)).Version);
        }

        [Theory]
        [InlineData("Saves/scene/a.json", "scenes", false)]
        [InlineData("Saves/Person/Appearance/a.json", "looks", true)]
        [InlineData("Saves/Person/Appearance/a.vac", "looks", false)]
        [InlineData("Custom/Atom/Person/General/a.vap", "looks", true)]
        [InlineData("Custom/Atom/Person/Appearance/a.json", "looks", true)]
        [InlineData("Custom/Clothing/a.vam", "clothing", false)]
        [InlineData("Custom/Clothing/a.vap", "clothing", false)]
        [InlineData("Custom/Atom/Person/Clothing/a.vap", "clothing", true)]
        [InlineData("Custom/Atom/Person/Clothing/a.vam", "clothing", false)]
        [InlineData("Custom/Hair/a.vam", "hairstyle", false)]
        [InlineData("Custom/Atom/Person/Hair/a.vap", "hairstyle", true)]
        [InlineData("Custom/Atom/Person/Morphs/a.vmi", "morphs", false)]
        [InlineData("Custom/Atom/Person/Morphs/a.vap", "morphs", true)]
        [InlineData("Custom/Atom/Person/Pose/a.vap", "pose", true)]
        [InlineData("Saves/Person/Pose/a.json", "pose", true)]
        [InlineData("Saves/Person/Pose/a.vac", "pose", false)]
        [InlineData("Custom/Atom/Person/Skin/a.vap", "skin", true)]
        public void Sorts_an_entry_into_its_type(string entryName, string type, bool isPreset)
        {
            VarItem item = Assert.Single(ReadEntries(entryName).Items);

            Assert.Equal(type, item.Type);
            Assert.Equal(isPreset, item.IsPreset);
            Assert.Equal(entryName, item.EntryName);
            Assert.Equal("", item.Preview);
        }

        [Fact]
        public void Preset_check_is_case_sensitive_as_the_old_scan_was()
        {
            Assert.False(Assert.Single(ReadEntries("Saves/Person/Pose/a.JSON").Items).IsPreset);
        }

        [Fact]
        public void Counts_each_type()
        {
            VarScan scan = ReadEntries(
                "Saves/scene/a.json", "Saves/scene/b.json", "Saves/Person/Appearance/a.vac", "Custom/Clothing/a.vam",
                "Custom/Hair/a.vam", "Custom/Atom/Person/Morphs/a.vmi", "Custom/Atom/Person/Pose/a.vap",
                "Custom/Atom/Person/Skin/a.vap", "Custom/Assets/a.assetbundle", "readme.txt");

            Assert.Equal(new[] { 2, 1, 1, 1, 1, 1, 1, 1 },
                new[] { scan.Scenes, scan.Looks, scan.Clothing, scan.Hairstyle, scan.Morphs, scan.Pose, scan.Skin, scan.Assets });
        }

        [Fact]
        public void Plugins_count_scripts_when_there_is_no_list()
        {
            Assert.Equal(2, ReadEntries("Custom/Scripts/a.cs", "Custom/Atom/Person/Scripts/b.cs").Plugins);
        }

        [Fact]
        public void Plugin_lists_replace_the_script_count()
        {
            Assert.Equal(1, ReadEntries("Custom/Scripts/a.cs", "Custom/Scripts/b.cs", "Custom/Scripts/c.cslist").Plugins);
        }

        [Fact]
        public void Assets_are_counted_but_are_not_items()
        {
            VarScan scan = ReadEntries("Custom/Assets/a.assetbundle");

            Assert.Equal(1, scan.Assets);
            Assert.Empty(scan.Items);
        }

        [Fact]
        public void Extracts_a_preview_numbered_by_type()
        {
            VarScan scan = Read(Var("A.B.1.var", "meta.json", Meta,
                "Saves/scene/one.json", "{}", "Saves/scene/Two.json", "{}", "Saves/scene/Two.jpg", "picture"));

            Assert.Equal("", scan.Items[0].Preview);
            Assert.Equal("scenes002_two.jpg", scan.Items[1].Preview);
            Assert.Equal("picture", File.ReadAllText(Path.Combine(previews, "scenes", "A.B.1", "scenes002_two.jpg")));
        }

        [Fact]
        public void Keeps_a_preview_that_already_exists()
        {
            string existing = Path.Combine(previews, "scenes", "A.B.1", "scenes001_a.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(existing));
            File.WriteAllText(existing, "kept");

            VarScan scan = Read(Var("A.B.1.var", "meta.json", Meta, "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "new"));

            Assert.Equal("scenes001_a.jpg", scan.Items[0].Preview);
            Assert.Equal("kept", File.ReadAllText(existing));
        }

        [Fact]
        public void Extracts_a_preview_for_assets()
        {
            Read(Var("A.B.1.var", "meta.json", Meta, "Custom/Assets/a.assetbundle", "x", "Custom/Assets/a.jpg", "picture"));

            Assert.True(File.Exists(Path.Combine(previews, "assets", "A.B.1", "assets001_a.jpg")));
        }

        [Fact]
        public void Dependencies_come_from_meta_and_scenes_without_the_var_itself()
        {
            VarScan scan = Read(Var("A.B.1.var",
                "meta.json", "{\"dependencies\":{\"Meta.Pack.1\":{}}}",
                "Saves/scene/a.json", "{\"x\":\"Scene.Pack.2:/Custom/a.vap\",\"y\":\"A.B.1:/Custom/b.vap\"}"));

            Assert.Equal(new[] { "Meta.Pack.1", "Scene.Pack.2" }, scan.Dependencies.OrderBy(d => d));
            Assert.Empty(warnings);
            Assert.Empty(problems);
        }

        [Fact]
        public void File_references_are_listed_per_file()
        {
            string path = Var("A.B.1.var", "meta.json", Meta, "Saves/scene/a.json", "{\"x\":\"Scene.Pack.2:/Custom/a.vap\"}");

            var files = VarScanner.FileReferences(path, warnings.Add);

            Assert.Equal(new[] { "Scene.Pack.2" }, files.Single(f => f.Key == "Saves/scene/a.json").Value);
        }

        [Fact]
        public void A_file_that_is_not_a_zip_is_invalid_and_stays_unlocked()
        {
            string path = Path.Combine(root, "A.B.1.var");
            File.WriteAllText(path, "not a zip");

            Assert.Equal(VarScanOutcome.Invalid, Read(path).Outcome);

            File.Delete(path);
        }

        [Fact]
        public void A_var_without_meta_is_invalid_and_stays_unlocked()
        {
            string path = Var("A.B.1.var", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");

            Assert.Equal(VarScanOutcome.Invalid, Read(path).Outcome);

            File.Delete(path);
            Assert.False(Directory.Exists(previews));
        }

        [Fact]
        public void A_name_without_three_parts_is_left_alone()
        {
            string path = Var("Broken.var", "meta.json", Meta, "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");

            Assert.Equal(VarScanOutcome.BadName, Read(path).Outcome);

            Assert.False(Directory.Exists(previews));
        }

        [Fact]
        public void Scanning_leaves_the_var_unchanged()
        {
            string path = Var("A.B.1.var", "meta.json", Meta, "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            byte[] before = File.ReadAllBytes(path);

            Read(path);

            Assert.Equal(before, File.ReadAllBytes(path));
            File.Delete(path);
        }
    }
}
