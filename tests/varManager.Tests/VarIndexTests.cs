using System.Collections.Generic;
using System.Linq;
using varManager;
using Xunit;

namespace varManagerTests
{
    public class VarIndexTests
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
        public void Latest_picks_highest_version_whatever_the_input_order()
        {
            Assert.Equal("A.B.12", Index("A.B.3", "A.B.12", "A.B.7").Resolve("A.B.latest"));
        }

        [Fact]
        public void Latest_keyword_ignores_case()
        {
            Assert.Equal("A.B.2", Index("A.B.2").Resolve("A.B.LATEST"));
        }

        [Fact]
        public void Latest_of_unknown_package_is_missing()
        {
            Assert.Equal(VarIndex.Missing, Index("A.B.1").Resolve("A.C.latest"));
        }

        [Fact]
        public void Exact_version_returns_the_requested_name()
        {
            Assert.Equal("A.B.3", Index("A.B.3", "A.B.5").Resolve("A.B.3"));
        }

        [Fact]
        public void Exact_lookup_ignores_case_like_the_table_key()
        {
            Assert.Equal("a.b.3", Index("A.B.3").Resolve("a.b.3"));
        }

        [Fact]
        public void Absent_version_uses_next_higher_with_substitute_marker()
        {
            Assert.Equal("A.B.5$", Index("A.B.3", "A.B.5", "A.B.9").Resolve("A.B.4"));
        }

        [Fact]
        public void Version_above_all_known_uses_highest_with_substitute_marker()
        {
            Assert.Equal("A.B.5$", Index("A.B.3", "A.B.5").Resolve("A.B.8"));
        }

        [Fact]
        public void Unknown_package_is_missing()
        {
            Assert.Equal(VarIndex.Missing, Index("A.B.1").Resolve("X.Y.1"));
        }

        [Theory]
        [InlineData("A.B")]
        [InlineData("A.B.1.2")]
        [InlineData("")]
        public void Malformed_name_is_missing(string name)
        {
            Assert.Equal(VarIndex.Missing, Index("A.B.1").Resolve(name));
        }

        [Fact]
        public void Non_numeric_version_is_missing_instead_of_throwing()
        {
            Assert.Equal(VarIndex.Missing, Index("A.B.1").Resolve("A.B.min1"));
        }

        [Fact]
        public void Creator_and_package_boundaries_stay_separate()
        {
            VarIndex index = Index("AB.C.1", "A.BC.2");
            Assert.Equal("AB.C.1", index.Resolve("AB.C.latest"));
            Assert.Equal("A.BC.2", index.Resolve("A.BC.latest"));
        }

        [Fact]
        public void Creator_names_may_contain_spaces()
        {
            Assert.Equal("abc tits.Alice.2", Index("abc tits.Alice.2").Resolve("abc tits.Alice.latest"));
        }

        [Theory]
        [InlineData("A.B.5", true)]
        [InlineData("A.B.3", false)]
        [InlineData("A.B.9", true)]
        [InlineData("X.Y.1", true)]
        [InlineData("A.B.latest", true)]
        public void IsLatest_compares_against_highest_version(string name, bool expected)
        {
            Assert.Equal(expected, Index("A.B.3", "A.B.5").IsLatest(name));
        }

        [Fact]
        public void VersionCount_counts_only_that_package()
        {
            VarIndex index = Index("A.B.1", "A.B.2", "A.C.1");
            Assert.Equal(2, index.VersionCount("A", "B"));
            Assert.Equal(0, index.VersionCount("X", "Y"));
        }

        [Fact]
        public void Contains_ignores_case()
        {
            VarIndex index = Index("A.B.1");
            Assert.True(index.Contains("a.b.1"));
            Assert.False(index.Contains("A.B.2"));
        }

        [Fact]
        public void Closest_of_unknown_package_is_missing()
        {
            Assert.Equal(VarIndex.Missing, Index("A.B.1").Closest("X", "Y", 1));
        }
    }

    public class DependencyIndexTests
    {
        private static DependencyIndex Index()
        {
            return new DependencyIndex(new[]
            {
                new KeyValuePair<string, string>("Scene.A.1", "Plugin.X.latest"),
                new KeyValuePair<string, string>("Scene.A.1", "Look.Y.2"),
                new KeyValuePair<string, string>("Scene.B.1", "Plugin.X.latest"),
            });
        }

        [Fact]
        public void DependenciesOf_keeps_table_order()
        {
            Assert.Equal(new[] { "Plugin.X.latest", "Look.Y.2" }, Index().DependenciesOf("Scene.A.1"));
        }

        [Fact]
        public void DependentsOf_finds_every_var()
        {
            Assert.Equal(new[] { "Scene.A.1", "Scene.B.1" }, Index().DependentsOf("Plugin.X.latest"));
        }

        [Fact]
        public void Unknown_names_return_empty()
        {
            Assert.Empty(Index().DependenciesOf("Nope.N.1"));
            Assert.Empty(Index().DependentsOf("Nope.N.1"));
        }
    }
}
