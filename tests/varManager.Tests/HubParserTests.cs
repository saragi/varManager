using System.IO;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class HubParserTests
    {
        [Fact]
        public void DownloadLinks_reads_names_and_links()
        {
            var links = HubParser.DownloadLinks(
                "{\"packages\":{\"a\":{\"filename\":\"Spaced Creator.Hair.2.var\",\"downloadUrl\":\"https://hub/1\"}}}");

            Assert.Equal("https://hub/1", links["Spaced Creator.Hair.2"]);
        }

        [Fact]
        public void DownloadLinks_skips_entries_without_a_link_or_var_name()
        {
            var links = HubParser.DownloadLinks("{\"packages\":{"
                + "\"a\":{\"filename\":\"A.One.1.var\",\"downloadUrl\":\"null\"},"
                + "\"b\":{\"filename\":\"null\",\"downloadUrl\":\"https://hub/2\"},"
                + "\"c\":{\"filename\":\"notes.txt\",\"downloadUrl\":\"https://hub/3\"},"
                + "\"d\":{\"downloadUrl\":\"https://hub/4\"}}}");

            Assert.Empty(links);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("<html>Bad gateway</html>")]
        [InlineData("{\"error\":\"busy\"}")]
        [InlineData("{\"packages\":[]}")]
        public void DownloadLinks_rejects_a_bad_reply(string response)
        {
            Assert.Throws<InvalidDataException>(() => HubParser.DownloadLinks(response));
        }

        [Fact]
        public void DownloadLinks_accepts_an_empty_package_list()
        {
            Assert.Empty(HubParser.DownloadLinks("{\"packages\":{}}"));
        }

        [Fact]
        public void LatestVersions_keeps_the_highest_version_of_each_package()
        {
            var latest = HubParser.LatestVersions(
                "{\"A.Hair.2.var\":\"1\",\"A.Hair.10.var\":\"2\",\"A.Hair.3.var\":\"3\",\"Spaced Creator.Look.1\":\"4\"}");

            Assert.Equal(10, latest["A.Hair"]);
            Assert.Equal(1, latest["Spaced Creator.Look"]);
        }

        [Fact]
        public void LatestVersions_skips_names_without_a_version()
        {
            var latest = HubParser.LatestVersions("{\"A.Hair.latest.var\":\"1\",\"plain\":\"2\"}");

            Assert.Empty(latest);
        }

        [Theory]
        [InlineData("<html>Bad gateway</html>")]
        [InlineData("[1,2]")]
        [InlineData("{\"error\":\"busy\"}")]
        public void LatestVersions_rejects_a_bad_reply(string response)
        {
            Assert.Throws<InvalidDataException>(() => HubParser.LatestVersions(response));
        }

        [Theory]
        [InlineData("A.Hair.12", 12)]
        [InlineData("Spaced Creator.Long_Package_Name.3", 3)]
        [InlineData("A.Hair.latest", -1)]
        [InlineData("plain", -1)]
        public void VersionOf_reads_the_last_part(string name, int expected)
        {
            Assert.Equal(expected, HubParser.VersionOf(name));
        }

        [Theory]
        [InlineData("A.Variant.1")]
        [InlineData("Creator.VarietyPack.2")]
        [InlineData("X.Various.1")]
        public void DownloadLinks_cuts_only_the_file_extension(string name)
        {
            var links = HubParser.DownloadLinks(
                "{\"packages\":{\"a\":{\"filename\":\"" + name + ".var\",\"downloadUrl\":\"https://hub/1\"}}}");

            Assert.True(links.ContainsKey(name));
        }

        [Fact]
        public void DownloadLinks_finds_names_in_any_case()
        {
            var links = HubParser.DownloadLinks(
                "{\"packages\":{\"a\":{\"filename\":\"A.Hair.2.VAR\",\"downloadUrl\":\"https://hub/1\"}}}");

            Assert.Equal("https://hub/1", links["a.hair.2"]);
        }

        [Fact]
        public void LatestVersions_treats_case_variants_as_one_package()
        {
            var latest = HubParser.LatestVersions("{\"a.hair.2.VAR\":\"1\",\"A.Hair.3.var\":\"2\"}");

            Assert.Equal(3, Assert.Single(latest).Value);
        }

        [Fact]
        public void LatestVersions_skips_a_bare_number()
        {
            Assert.Empty(HubParser.LatestVersions("{\"5\":\"1\"}"));
        }

        [Fact]
        public void DownloadLinks_keeps_reading_after_a_skipped_entry()
        {
            var links = HubParser.DownloadLinks("{\"packages\":{"
                + "\"a\":{\"filename\":\"A.One.1.var\"},"
                + "\"b\":{\"filename\":\"A.Two.1.var\",\"downloadUrl\":\"https://hub/2\"}}}");

            Assert.Equal("A.Two.1", Assert.Single(links).Key);
        }

        [Fact]
        public void LatestVersions_keeps_reading_after_a_skipped_entry()
        {
            var latest = HubParser.LatestVersions("{\"plain\":\"1\",\"A.Hair.2.var\":\"2\"}");

            Assert.Equal(2, latest["A.Hair"]);
        }

        [Theory]
        [InlineData("{\"packages\":{\"a\":{\"filename\":\"A.On")]
        [InlineData("}")]
        public void Both_readers_reject_broken_json(string response)
        {
            Assert.Throws<InvalidDataException>(() => HubParser.DownloadLinks(response));
            Assert.Throws<InvalidDataException>(() => HubParser.LatestVersions(response));
        }

        [Fact]
        public void Packages_reads_the_size()
        {
            var package = Assert.Single(HubParser.Packages(
                "{\"packages\":{\"a\":{\"filename\":\"A.One.1.var\",\"downloadUrl\":\"https://hub/1\",\"file_size\":\"610993\"}}}"));

            Assert.Equal(610993, package.Size);
        }
    }
}
