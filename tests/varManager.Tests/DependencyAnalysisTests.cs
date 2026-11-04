using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using varManager;
using Xunit;

namespace varManagerTests
{
    public class DependencyAnalysisTests
    {
        private static VarEntry Entry(string name)
        {
            string[] parts = name.Split('.');
            return new VarEntry(name, parts[0], parts[1], int.Parse(parts[2]));
        }

        private static VarIndex Vars(params string[] names)
        {
            return new VarIndex(names.Select(Entry));
        }

        // Each pair is "var>dependency".
        private static DependencyIndex Deps(params string[] pairs)
        {
            return new DependencyIndex(pairs.Select(pair =>
            {
                string[] parts = pair.Split('>');
                return new KeyValuePair<string, string>(parts[0], parts[1]);
            }));
        }

        [Fact]
        public void Dependents_of_an_old_version_are_only_direct_ones()
        {
            VarIndex vars = Vars("A.B.1", "A.B.2", "X.Y.1", "Z.W.1");
            DependencyIndex deps = Deps("X.Y.1>A.B.1", "Z.W.1>A.B.latest");

            Assert.Equal(new[] { "X.Y.1" }, DependencyAnalysis.DependentVars("A.B.1", vars, deps));
        }

        [Fact]
        public void Dependents_of_the_latest_version_include_latest_and_minimum_references()
        {
            VarIndex vars = Vars("A.B.1", "A.B.5", "X.Y.1", "Z.W.1", "M.N.1", "T.U.1");
            DependencyIndex deps = Deps("X.Y.1>A.B.5", "Z.W.1>A.B.latest", "M.N.1>A.B.min3", "T.U.1>A.B.min9");

            Assert.Equal(new[] { "M.N.1", "X.Y.1", "Z.W.1" },
                DependencyAnalysis.DependentVars("A.B.5", vars, deps).OrderBy(n => n));
        }

        [Fact]
        public void Dependents_are_listed_once()
        {
            VarIndex vars = Vars("A.B.1", "X.Y.1");
            DependencyIndex deps = Deps("X.Y.1>A.B.1", "X.Y.1>A.B.latest");

            Assert.Equal(new[] { "X.Y.1" }, DependencyAnalysis.DependentVars("A.B.1", vars, deps));
        }

        [Fact]
        public void Implicated_vars_follow_dependents_all_the_way()
        {
            VarIndex vars = Vars("A.B.1", "C.D.1", "E.F.1", "G.H.1");
            DependencyIndex deps = Deps("C.D.1>A.B.1", "E.F.1>C.D.1");

            Assert.Equal(new[] { "A.B.1", "C.D.1", "E.F.1" },
                DependencyAnalysis.ImplicatedVars(new[] { "A.B.1" }, vars, deps));
        }

        [Fact]
        public void Implicated_vars_stop_at_a_package_with_another_version()
        {
            VarIndex vars = Vars("A.B.1", "A.B.2", "C.D.1");
            DependencyIndex deps = Deps("C.D.1>A.B.1");

            Assert.Equal(new[] { "A.B.1" }, DependencyAnalysis.ImplicatedVars(new[] { "A.B.1" }, vars, deps));
        }

        [Fact]
        public void Implicated_vars_survive_a_dependency_loop()
        {
            VarIndex vars = Vars("A.B.1", "C.D.1");
            DependencyIndex deps = Deps("C.D.1>A.B.1", "A.B.1>C.D.1");

            Assert.Equal(new[] { "A.B.1", "C.D.1" }, DependencyAnalysis.ImplicatedVars(new[] { "A.B.1" }, vars, deps));
        }

        [Fact]
        public void Implicated_vars_of_nothing_is_nothing()
        {
            Assert.Empty(DependencyAnalysis.ImplicatedVars(new string[0], Vars("A.B.1"), Deps()));
        }

        [Fact]
        public void With_dependencies_follows_references_all_the_way()
        {
            VarIndex vars = Vars("A.B.1", "C.D.2", "E.F.1", "G.H.1");
            DependencyIndex deps = Deps("A.B.1>C.D.latest", "C.D.2>E.F.1");

            Assert.Equal(new[] { "A.B.1", "C.D.2", "E.F.1" },
                DependencyAnalysis.WithDependencies(new[] { "A.B.1" }, vars, deps));
        }

        [Fact]
        public void With_dependencies_drops_missing_and_uses_substitutes()
        {
            VarIndex vars = Vars("A.B.1", "C.D.3");
            DependencyIndex deps = Deps("A.B.1>C.D.2", "A.B.1>Gone.Pack.1");

            Assert.Equal(new[] { "A.B.1", "C.D.3" }, DependencyAnalysis.WithDependencies(new[] { "A.B.1" }, vars, deps));
        }

        [Fact]
        public void With_dependencies_survives_a_loop_and_repeats()
        {
            VarIndex vars = Vars("A.B.1", "C.D.1");
            DependencyIndex deps = Deps("A.B.1>C.D.1", "C.D.1>A.B.1");

            Assert.Equal(new[] { "A.B.1", "C.D.1" },
                DependencyAnalysis.WithDependencies(new[] { "A.B.1", "A.B.latest", "A.B.1" }, vars, deps));
        }

        [Fact]
        public void Resolve_reports_found_substitute_and_missing()
        {
            List<ResolvedReference> resolved = DependencyAnalysis.Resolve(new[] { "A.B.1", "A.B.9", "Gone.Pack.1" }, Vars("A.B.1"));

            Assert.Equal("A.B.1", resolved[0].VarName);
            Assert.False(resolved[0].IsSubstitute);
            Assert.Equal("A.B.1", resolved[1].VarName);
            Assert.True(resolved[1].IsSubstitute);
            Assert.Equal("A.B.9", resolved[1].Reference);
            Assert.Null(resolved[2].VarName);
            Assert.False(resolved[2].IsSubstitute);
        }

        [Fact]
        public void Missing_list_marks_substitutes_with_a_dollar()
        {
            List<ResolvedReference> resolved = DependencyAnalysis.Resolve(new[] { "A.B.1", "A.B.9", "Gone.Pack.1" }, Vars("A.B.1"));

            Assert.Equal(new[] { "A.B.9$", "Gone.Pack.1" }, DependencyAnalysis.MissingList(resolved));
        }

        [Fact]
        public void Latest_versions_skip_packages_with_one_version()
        {
            Dictionary<string, int> latest = DependencyAnalysis.LatestVersions(new[] { "A.B.1", "A.B.12", "A.B.3", "C.D.5" }.Select(Entry));

            Assert.Equal(12, Assert.Single(latest).Value);
            Assert.True(latest.ContainsKey("A.B"));
        }

        [Fact]
        public void Old_versions_are_everything_below_the_latest()
        {
            VarEntry[] all = new[] { "A.B.1", "A.B.2", "A.B.3", "C.D.5" }.Select(Entry).ToArray();

            Assert.Equal(new[] { "A.B.1", "A.B.2" }, DependencyAnalysis.OldVersions(all, DependencyAnalysis.LatestVersions(all)));
        }

        [Fact]
        public void Old_versions_can_be_judged_against_a_filtered_latest()
        {
            VarEntry[] all = new[] { "A.B.1", "A.B.2", "A.B.3" }.Select(Entry).ToArray();

            Dictionary<string, int> latest = DependencyAnalysis.LatestVersions(all.Take(2));

            Assert.Equal(new[] { "A.B.1", "A.B.3" }, DependencyAnalysis.OldVersions(all, latest));
        }

        [Fact]
        public void Save_files_are_scenes_and_presets()
        {
            string vam = Path.Combine(Path.GetTempPath(), "vmsaves-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(vam, "Saves", "scene"));
                Directory.CreateDirectory(Path.Combine(vam, "Custom", "Atom"));
                File.WriteAllText(Path.Combine(vam, "Saves", "scene", "a.json"), "");
                File.WriteAllText(Path.Combine(vam, "Saves", "scene", "b.txt"), "");
                File.WriteAllText(Path.Combine(vam, "Custom", "Atom", "c.vap"), "");
                File.WriteAllText(Path.Combine(vam, "Custom", "Atom", "d.json"), "");

                Assert.Equal(new[] { "a.json", "c.vap" },
                    DependencyAnalysis.SaveFiles(vam).Select(Path.GetFileName));
            }
            finally
            {
                Directory.Delete(vam, true);
            }
        }

        [Fact]
        public void Save_path_is_relative_to_vam()
        {
            Assert.Equal(@"\Saves\scene\a.json", DependencyAnalysis.SavePath(@"C:\VaM", @"C:\VaM\Saves\scene\a.json"));
        }

        [Fact]
        public void A_long_save_path_keeps_its_end()
        {
            string tail = new string('x', 255);

            Assert.Equal(tail, DependencyAnalysis.SavePath(@"C:\VaM", @"C:\VaM\Saves\" + tail));
        }
    }
}
