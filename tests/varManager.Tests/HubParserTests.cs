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
    }
}
