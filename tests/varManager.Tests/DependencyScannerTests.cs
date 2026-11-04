using System.Collections.Generic;
using System.Linq;
using varManager;
using Xunit;

namespace varManagerTests
{
    public class DependencyScannerTests
    {
        private static VarIndex Index(params string[] varNames)
        {
            return new VarIndex(varNames.Select(name =>
            {
                string[] parts = name.Split('.');
                return new VarEntry(name, parts[0], parts[1], int.Parse(parts[2]));
            }));
        }

        [Fact]
        public void Finds_references_in_scene_values()
        {
            string scene = "{\"id\":\"Skippy.Pigtail.latest:/Custom/Hair/Pig.vam\",\"plugin#0\":\"HAL9001.LogicBricks2.16:/Custom/Scripts/a.cslist\"}";
            Assert.Equal(new[] { "HAL9001.LogicBricks2.16", "Skippy.Pigtail.latest" },
                DependencyScanner.Extract("Saves/scene/a.json", scene).OrderBy(x => x));
        }

        [Fact]
        public void Keeps_creator_names_with_spaces()
        {
            string scene = "{\"id\":\"s p l i n e  VR.Hair_Mega_Pack.latest:/Custom/Hair/Female/P10/Pony.vam\"}";
            Assert.Equal(new[] { "s p l i n e  VR.Hair_Mega_Pack.latest" }, DependencyScanner.Extract("a.json", scene));
        }

        [Fact]
        public void Finds_min_references()
        {
            Assert.Equal(new[] { "A.B.min3" }, DependencyScanner.Extract("a.vap", "{\"url\":\"A.B.min3:/Custom/x.vap\"}"));
        }

        [Fact]
        public void Reference_after_a_path_prefix_is_found()
        {
            Assert.Equal(new[] { "A.B.2" }, DependencyScanner.Extract("a.json", "{\"url\":\"Custom/Scripts/A.B.2:/x.cs\"}"));
        }

        [Fact]
        public void Self_paths_and_plain_values_are_not_references()
        {
            string scene = "{\"a\":\"SELF:/Custom/x.vap\",\"b\":\"Custom/Atom/Person/x.1.vap\",\"version\":\"1.20.1\"}";
            Assert.Empty(DependencyScanner.Extract("a.json", scene));
        }

        [Fact]
        public void Dependency_keys_count_only_in_meta_json()
        {
            string text = "{\"dependencies\":{\"A.B.2\":{\"licenseType\":\"CC BY\",\"dependencies\":{\"C.D.latest\":{}}}}}";
            Assert.Equal(new[] { "A.B.2", "C.D.latest" }, DependencyScanner.Extract("META.JSON", text).OrderBy(x => x));
            Assert.Empty(DependencyScanner.Extract("Saves/scene/a.json", text));
        }

        [Theory]
        [InlineData("Saves/scene/a.json", true)]
        [InlineData("Custom/x.VAP", true)]
        [InlineData("Custom/Hair/a.vam", true)]
        [InlineData("Custom/Scripts/a.cslist", true)]
        [InlineData("Custom/a.jpg", false)]
        [InlineData("Custom/Scripts/a.cs", false)]
        public void Only_text_formats_are_scanned(string name, bool expected)
        {
            Assert.Equal(expected, DependencyScanner.IsScannable(name));
        }

        [Fact]
        public void Closure_walks_nested_references_once_despite_cycles()
        {
            Dictionary<string, string[]> refs = new Dictionary<string, string[]>
            {
                { "A.A.1", new[] { "B.B.latest", "C.C.1" } },
                { "B.B.2", new[] { "A.A.1", "C.C.1" } },
                { "C.C.1", new string[0] },
            };
            DependencyClosure closure = DependencyScanner.Closure(new[] { "A.A.1" }, Index("A.A.1", "B.B.1", "B.B.2", "C.C.1"), v => refs[v]);
            Assert.Equal(new[] { "A.A.1", "B.B.2", "C.C.1" }, closure.Vars.OrderBy(x => x));
            Assert.Empty(closure.Missing);
        }

        [Fact]
        public void Closure_reports_missing_references()
        {
            DependencyClosure closure = DependencyScanner.Closure(new[] { "A.A.1" }, Index("A.A.1"), v => new[] { "X.Y.latest" });
            Assert.Equal(new[] { "X.Y.latest" }, closure.Missing);
        }

        [Fact]
        public void Closure_records_substitute_versions()
        {
            DependencyClosure closure = DependencyScanner.Closure(new[] { "A.A.1" }, Index("A.A.1", "P.Q.19"),
                v => v == "A.A.1" ? new[] { "P.Q.16" } : new string[0]);
            Assert.Equal("P.Q.19", closure.Substitutions["P.Q.16"]);
            Assert.Contains("P.Q.19", closure.Vars);
        }
    }
}
