using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using SimpleJSON;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class SceneAnalysisTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vmscene-" + Guid.NewGuid().ToString("N"));
        private readonly string startFolder = Directory.GetCurrentDirectory();

        public SceneAnalysisTests()
        {
            Directory.CreateDirectory(root);
            Directory.SetCurrentDirectory(root);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(startFolder);
            Directory.Delete(root, true);
        }

        private string Var(string fileName, params string[] namesAndContents)
        {
            string path = Path.Combine(root, fileName);
            using (ZipOutputStream zip = new ZipOutputStream(File.Create(path)))
            {
                for (int i = 0; i < namesAndContents.Length; i += 2)
                {
                    zip.PutNextEntry(new ZipEntry(namesAndContents[i]));
                    byte[] bytes = Encoding.UTF8.GetBytes(namesAndContents[i + 1]);
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
            return path;
        }

        private static string Atom(string id, string type, string extra = "")
        {
            return "{\"id\":\"" + id + "\",\"type\":\"" + type + "\"" + extra + "}";
        }

        private static string Person(string id, string character)
        {
            return Atom(id, "Person", ",\"storables\":[{\"id\":\"geometry\",\"character\":\"" + character + "\"}]");
        }

        private string Analyse(string sceneJson, string gender = "female")
        {
            string file = Var("A.B.1.var", "Saves/scene/a.json", sceneJson);
            SceneAnalysis.Prepare("A.B.1:/Saves/scene/a.json", file, root, gender, true);
            return SceneAnalysis.CacheFolder("A.B.1", "Saves/scene/a.json");
        }

        [Theory]
        [InlineData("A.B.1:/Saves/scene/a.json", "A.B.1", "Saves/scene/a.json")]
        [InlineData("Saves/scene/a.json", "save", "Saves/scene/a.json")]
        [InlineData("A:/x", "save", "A:/x")]
        public void Splits_a_save_name(string saveName, string varName, string entryName)
        {
            Assert.Equal((varName, entryName), SceneAnalysis.SplitSaveName(saveName));
        }

        [Fact]
        public void Cache_folder_flattens_the_entry_and_drops_bad_characters()
        {
            Assert.Equal(Path.Combine(root, "Cache", "AB.1", "Saves_scene_a.json"),
                SceneAnalysis.CacheFolder("A|B.1", @"Saves/scene\a.json"));
        }

        [Theory]
        [InlineData("Female1", "Female")]
        [InlineData("MaleBase", "Male")]
        [InlineData("Lee", "Male")]
        [InlineData("JarLee", "Male")]
        [InlineData("Julian", "Male")]
        [InlineData("JarJulian", "Male")]
        [InlineData("Futa1", "Futa")]
        [InlineData("", "Female")]
        public void Reads_gender_from_the_character(string character, string gender)
        {
            Assert.Equal(gender, SceneAnalysis.CharacterGender(character));
        }

        [Fact]
        public void Prepare_writes_the_var_and_its_references_as_depends()
        {
            string folder = Analyse("{\"x\":\"Other.Pack.2:/Custom/a.vap\",\"atoms\":[]}");

            Assert.Equal(new[] { "A.B.1", "Other.Pack.2" }, SceneAnalysis.ReadDepends(folder));
            Assert.Equal("female", SceneAnalysis.ReadGender(folder));
        }

        [Theory]
        [InlineData("{\"x\":\"Custom/Atom/Person/Female/a.vap\",\"atoms\":[]}", "female")]
        [InlineData("{\"atoms\":[]}", "male")]
        public void An_unknown_gender_comes_from_the_scene_text(string json, string gender)
        {
            Assert.Equal(gender, SceneAnalysis.ReadGender(Analyse(json, "unknown")));
        }

        [Fact]
        public void An_unknown_gender_can_come_from_the_save_name()
        {
            string file = Var("A.B.1.var", "Custom/Female/a.json", "{}");

            SceneAnalysis.Prepare("A.B.1:/Custom/Female/a.json", file, root, "unknown", false);

            Assert.Equal("female", SceneAnalysis.ReadGender(SceneAnalysis.CacheFolder("A.B.1", "Custom/Female/a.json")));
        }

        [Fact]
        public void Prepare_reads_a_save_from_the_vam_folder()
        {
            string save = Path.Combine(root, "Saves", "scene", "own.json");
            Directory.CreateDirectory(Path.GetDirectoryName(save));
            File.WriteAllText(save, "{\"x\":\"Other.Pack.2:/Custom/a.vap\"}");

            SceneAnalysis.Prepare(@"Saves\scene\own.json", null, root, "female", false);

            Assert.Equal(new[] { "Other.Pack.2" }, SceneAnalysis.ReadDepends(SceneAnalysis.CacheFolder("save", @"Saves\scene\own.json")));
        }

        [Fact]
        public void Prepare_without_analysis_writes_no_atoms()
        {
            string file = Var("A.B.1.var", "Saves/scene/a.json", "{\"atoms\":[" + Atom("x", "Empty") + "]}");

            SceneAnalysis.Prepare("A.B.1:/Saves/scene/a.json", file, root, "female", false);

            Assert.False(Directory.Exists(Path.Combine(SceneAnalysis.CacheFolder("A.B.1", "Saves/scene/a.json"), "atoms")));
        }

        [Fact]
        public void Atoms_are_split_by_type_and_the_rest_goes_to_posinfo()
        {
            string folder = Analyse("{\"playerHeight\":\"1.8\",\"atoms\":[" + Atom("Ball", "Sphere") + "," + Atom("Core", "CoreControl") + "]}");

            Assert.Equal("Ball", JSON.Parse(ReadBin(Path.Combine(folder, "atoms", "Sphere", "Ball.bin")))["id"].Value);
            Assert.True(File.Exists(Path.Combine(folder, "atoms", "(base)CoreControl", "Core.bin")));
            Assert.Equal("1.8", JSONNode.LoadFromFile(Path.Combine(folder, "posinfo.bin"))["playerHeight"].Value);
            Assert.False(((JSONClass)JSONNode.LoadFromFile(Path.Combine(folder, "posinfo.bin"))).HasKey("atoms"));
        }

        private static string ReadBin(string path)
        {
            return JSONNode.LoadFromFile(path).ToString();
        }

        [Fact]
        public void Person_atoms_are_named_with_their_gender()
        {
            string folder = Analyse("{\"atoms\":[" + Person("Anna", "Female1") + "," + Person("Bob", "Lee") + "]}");

            Assert.Equal(new[] { "(Female)Anna.bin", "(Male)Bob.bin" },
                Directory.GetFiles(Path.Combine(folder, "atoms", "Person")).Select(Path.GetFileName).OrderBy(n => n));
        }

        [Fact]
        public void A_person_without_geometry_has_an_unknown_gender()
        {
            string folder = Analyse("{\"atoms\":[" + Atom("Anna", "Person", ",\"storables\":[]") + "]}");

            Assert.True(File.Exists(Path.Combine(folder, "atoms", "Person", "(unknown)Anna.bin")));
        }

        [Fact]
        public void A_single_look_goes_to_the_person_folder()
        {
            string folder = Analyse("{\"id\":\"Look\",\"storables\":[{\"id\":\"geometry\",\"character\":\"Male\"}]}");

            Assert.True(File.Exists(Path.Combine(folder, "atoms", "Person", "(Male)Look.bin")));
        }

        [Fact]
        public void A_sub_scene_is_split_into_its_own_folder()
        {
            string sub = "{\"id\":\"Sub\",\"type\":\"SubScene\",\"atoms\":[" + Atom("Inner", "Sphere") + "]}";

            string folder = Analyse("{\"atoms\":[" + sub + "]}");

            Assert.True(File.Exists(Path.Combine(folder, "atoms", "SubScene", "atoms", "Sphere", "Inner.bin")));
            Assert.True(File.Exists(Path.Combine(folder, "atoms", "SubScene", "posinfo.bin")));
        }

        [Fact]
        public void A_sub_scene_without_atoms_is_saved_under_its_id()
        {
            string folder = Analyse("{\"atoms\":[" + Atom("Sub", "SubScene") + "]}");

            Assert.True(File.Exists(Path.Combine(folder, "atoms", "SubScene", "Sub.bin")));
        }

        [Fact]
        public void Parents_are_listed_with_their_children()
        {
            string folder = Analyse("{\"atoms\":[" +
                Atom("Kid1", "Sphere", ",\"parentAtom\":\"Boss\"") + "," +
                Atom("Kid2", "Sphere", ",\"parentAtom\":\"Boss\"") + "," +
                Atom("Solo", "Sphere", ",\"parentAtom\":\"\"") + "]}");

            Assert.Equal(new[] { "Boss\tKid1,Kid2" }, File.ReadAllLines(Path.Combine(folder, "parentAtom.txt")));
        }

        [Fact]
        public void An_atom_with_no_type_is_skipped()
        {
            string folder = Analyse("{\"atoms\":[{\"id\":\"x\"}," + Atom("Ball", "Sphere") + "]}");

            Assert.Equal(new[] { "Sphere" }, Directory.GetDirectories(Path.Combine(folder, "atoms")).Select(Path.GetFileName));
        }

        [Fact]
        public void Self_references_become_the_var_name_in_saved_atoms()
        {
            string folder = Analyse("{\"atoms\":[" + Atom("Ball", "Sphere", ",\"url\":\"SELF:/Custom/a.png\"") + "]}");

            Assert.Equal("A.B.1:/Custom/a.png", JSONNode.LoadFromFile(Path.Combine(folder, "atoms", "Sphere", "Ball.bin"))["url"].Value);
        }

        [Fact]
        public void Previews_restore_from_the_var()
        {
            string file = Var("A.B.1.var", "Saves/scene/a.json", "{}", "Saves/scene/a.jpg", "picture");
            string picture = ScenePreviews.PicturePath(root, "scenes", "A.B.1", "scenes001_a.jpg");

            Assert.True(ScenePreviews.Restore(file, "Saves/scene/a.json", picture));

            Assert.Equal(Path.Combine(root, LibraryFolders.PreviewPics, "scenes", "A.B.1", "scenes001_a.jpg"), picture);
            Assert.Equal("picture", File.ReadAllText(picture));
        }

        [Fact]
        public void Previews_keep_an_existing_file()
        {
            string file = Var("A.B.1.var", "Saves/scene/a.jpg", "new");
            string picture = Path.Combine(root, "pic.jpg");
            File.WriteAllText(picture, "kept");

            Assert.True(ScenePreviews.Restore(file, "Saves/scene/a.json", picture));

            Assert.Equal("kept", File.ReadAllText(picture));
        }

        [Fact]
        public void Previews_cant_restore_without_the_var_or_the_picture()
        {
            string file = Var("A.B.1.var", "Saves/scene/a.json", "{}");
            string picture = Path.Combine(root, "pics", "pic.jpg");

            Assert.False(ScenePreviews.Restore(file, "Saves/scene/a.json", picture));
            Assert.False(ScenePreviews.Restore(Path.Combine(root, "gone.var"), "Saves/scene/a.json", picture));
            Assert.False(File.Exists(picture));
        }

        [Fact]
        public void Deleting_previews_removes_every_type_for_that_var_only()
        {
            string scene = ScenePreviews.PicturePath(root, "scenes", "A.B.1", "a.jpg");
            string pose = ScenePreviews.PicturePath(root, "pose", "A.B.1", "b.jpg");
            string other = ScenePreviews.PicturePath(root, "scenes", "C.D.1", "c.jpg");
            foreach (string path in new[] { scene, pose, other })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "x");
            }

            ScenePreviews.DeleteAll(root, "A.B.1");

            Assert.False(Directory.Exists(Path.GetDirectoryName(scene)));
            Assert.False(Directory.Exists(Path.GetDirectoryName(pose)));
            Assert.True(File.Exists(other));
        }

        private static JSONClass Request(params string[] resources)
        {
            return (JSONClass)JSON.Parse("{\"resources\":[" + string.Join(",", resources) + "]}");
        }

        [Fact]
        public void Load_request_fills_defaults_and_keeps_given_values()
        {
            JSONClass request = Request("{\"saveName\":\"A.B.1:/x\",\"merge\":\"true\"}");
            bool hasScene;

            JSONClass prepared = VamBridge.PrepareLoad(request, false, "male", true, 2, out hasScene);

            JSONNode resource = prepared["resources"][0];
            Assert.Equal("true", resource["merge"].Value);
            Assert.Equal("male", resource["characterGender"].Value);
            Assert.Equal("true", resource["ignoreGender"].Value);
            Assert.Equal("2", resource["personOrder"].Value);
            Assert.False(hasScene);
            Assert.False(((JSONClass)request["resources"][0]).HasKey("characterGender"));
        }

        [Fact]
        public void Load_request_stops_filling_defaults_at_the_first_scene()
        {
            JSONClass request = Request("{\"type\":\"scenes\",\"saveName\":\"A.B.1:/x\"}", "{\"saveName\":\"C.D.1:/y\"}");
            bool hasScene;

            JSONClass prepared = VamBridge.PrepareLoad(request, false, "female", false, 1, out hasScene);

            Assert.True(hasScene);
            Assert.Equal("false", prepared["resources"][0]["merge"].Value);
            Assert.False(((JSONClass)prepared["resources"][1]).HasKey("merge"));
        }

        [Fact]
        public void Resource_vars_come_from_the_save_names()
        {
            JSONClass request = Request("{\"saveName\":\"A.B.1:/x\"}", "{\"saveName\":\"C.D.1:/y/z\"}");

            Assert.Equal(new List<string> { "A.B.1", "C.D.1" }, VamBridge.ResourceVars(request));
        }

        [Fact]
        public void Written_request_can_be_read_back_and_replaces_an_old_one()
        {
            string file = VamBridge.RequestFile(root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, new string('x', 5000));
            JSONClass request = Request("{\"saveName\":\"A.B.1:/x\"}");
            request["rescan"] = "True";

            VamBridge.WriteRequest(root, request);

            JSONNode written = JSON.Parse(File.ReadAllText(file));
            Assert.Equal("True", written["rescan"].Value);
            Assert.Equal("A.B.1:/x", written["resources"][0]["saveName"].Value);
        }
    }
}
