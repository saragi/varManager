using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public sealed class HubTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private const string Info = "{\"category\":[\"Free\",\"Paid\"],\"location\":[\"Hub And Dependencies\",\"Hub Only\"],\"type\":[\"Scenes\",\"Looks\"],\"sort\":[\"Latest Update\",\"Rating\"],\"tags\":{\"cute\":\"1\"},\"users\":{\"Alice\":\"1\",\"Malice\":\"1\",\"Bob\":\"1\"}}";

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmhub-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string data;
        private readonly string storeFile;
        private readonly string links;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs = new JobRunner(null);
        private readonly FakeBrowser browser = new FakeBrowser();
        private readonly FakeDownloads downloads = new FakeDownloads();
        private readonly List<string> opened = new List<string>();
        private readonly List<string> shown = new List<string>();

        private sealed class FakeBrowser : IHubBrowser
        {
            public string Page { get; set; } = HubTests.Resources(1, 1);
            public string DetailResponse { get; set; } = "{}";
            public string InfoResponse { get; set; } = HubTests.Info;
            public bool Down { get; set; }
            public bool ResourcesDown { get; set; }
            public Exception? DetailError { get; set; }
            public List<HubQuery> Queries { get; } = new List<HubQuery>();
            public TaskCompletionSource<bool>? Gate { get; set; }

            public Task<string> Info()
            {
                return Down ? Task.FromException<string>(new IOException("no network")) : Task.FromResult(InfoResponse);
            }

            public async Task<string> Resources(HubQuery query)
            {
                Queries.Add(query);
                if (ResourcesDown)
                    throw new IOException("no network");
                TaskCompletionSource<bool>? gate = Gate;
                Gate = null;
                string page = Page;
                if (gate != null)
                    await gate.Task;
                return page;
            }

            public Task<string> Detail(string resourceId)
            {
                return DetailError == null ? Task.FromResult(DetailResponse) : Task.FromException<string>(DetailError);
            }

            public Task<byte[]> Image(string url)
            {
                return Task.FromResult(new byte[0]);
            }
        }

        private sealed class FakeDownloads : IHubSource
        {
            public List<string> Names { get; } = new List<string>();
            public bool Fail { get; set; }

            public Task<List<HubPackage>> Find(IEnumerable<string> references)
            {
                return Task.FromResult(new List<HubPackage>());
            }

            public Task<string> PackageList()
            {
                return Task.FromResult("{}");
            }

            public Task<DownloadResult> Download(IReadOnlyList<HubPackage> packages, string folder, Action<HubPackage> finished, CancellationToken token)
            {
                var result = new DownloadResult();
                foreach (HubPackage package in packages)
                {
                    Names.Add(package.Name);
                    if (Fail)
                    {
                        result.Failed[package.Name] = "broken";
                    }
                    else
                    {
                        WriteVar(Path.Combine(folder, package.Name + ".var"));
                        result.Stored.Add(package.Name);
                    }
                    finished(package);
                }
                return Task.FromResult(result);
            }
        }

        public HubTests()
        {
            vam = Path.Combine(root, "VaM");
            vars = Path.Combine(root, "vars");
            data = Path.Combine(root, "app");
            Directory.CreateDirectory(vam);
            Directory.CreateDirectory(vars);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(vam, "VaM.exe"), "");
            VarSwitches.Names(vam);
            VarSwitches.EnsureLink(vam);
            links = VarSwitches.LinkFolder(vam, "default");
            Directory.CreateDirectory(links);
            paths = new LibraryPaths(vars, vam, data);
            storeFile = Path.Combine(data, "library.db");
        }

        public void Dispose()
        {
            string packs = Path.Combine(vam, "AddonPackages");
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                try
                {
                    if (new DirectoryInfo(packs).Exists)
                        new DirectoryInfo(packs).Delete();
                    Directory.Delete(root, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private static void WriteVar(string file)
        {
            using ZipOutputStream zip = new ZipOutputStream(File.Create(file));
            zip.PutNextEntry(new ZipEntry("meta.json"));
            byte[] bytes = Encoding.UTF8.GetBytes("{}");
            zip.Write(bytes, 0, bytes.Length);
            zip.CloseEntry();
        }

        private void Own(params string[] names)
        {
            foreach (string name in names)
                WriteVar(Path.Combine(vars, name + ".var"));
            using LibraryStore store = LibraryStore.Open(storeFile);
            LibraryUpdate.Run(store, paths, (done, total) => { }, default);
        }

        private static string Resource(string id, string title, string files, string extra = "")
        {
            return "{\"resource_id\":\"" + id + "\",\"title\":\"" + title + "\",\"username\":\"Alice\",\"category\":\"Free\",\"type\":\"Scenes\",\"tag_line\":\"t\",\"version_string\":\"1\","
                + "\"image_url\":\"\",\"rating_avg\":\"4.5\",\"download_count\":\"12\"" + (files == null ? "" : ",\"hubFiles\":[" + files + "]") + extra + "}";
        }

        private static string File1(string name)
        {
            return "{\"filename\":\"" + name + ".var\"}";
        }

        private static string Resources(int page, int pages, params string[] items)
        {
            return "{\"pagination\":{\"total_found\":\"" + items.Length + "\",\"total_pages\":\"" + pages + "\",\"page\":\"" + page + "\"},\"resources\":[" + string.Join(",", items) + "]}";
        }

        private HubViewModel Hub()
        {
            return new HubViewModel(storeFile, paths, jobs, browser, downloads, opened.Add, shown.Add, null) { Columns = 10 };
        }

        private static List<HubTile> Tiles(HubViewModel hub)
        {
            return hub.Rows.SelectMany(row => row.Tiles).ToList();
        }

        private void WaitForJob()
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (jobs.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.False(jobs.IsRunning);
        }

        [Theory]
        [InlineData("https://hub.virtamate.com/resources/1/download?file=2", true)]
        [InlineData("https://s3cdn.virtamate.com/a.var", true)]
        [InlineData("https://HUB.VIRTAMATE.COM/a", true)]
        [InlineData("http://hub.virtamate.com/a", false)]
        [InlineData("https://virtamate.com.evil.example/a", false)]
        [InlineData("https://evilvirtamate.com/a", false)]
        [InlineData("https://mega.example/a.var", false)]
        [InlineData("null", false)]
        [InlineData("", false)]
        public void Only_hub_addresses_count_for_downloads(string url, bool allowed)
        {
            Assert.Equal(allowed, HubBrowse.IsHubUrl(url));
        }

        [Fact]
        public void A_download_from_anywhere_else_is_left_out()
        {
            string detail = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://mega.example/a\"}],"
                + "\"dependencies\":{\"x\":[{\"filename\":\"C.D.1\",\"downloadUrl\":\"https://hub.virtamate.com/d\"}]}}";

            Assert.Equal("C.D.1", Assert.Single(HubBrowse.Downloads(detail, _ => false)).Name);
        }

        [Fact]
        public void Filters_come_from_the_hub()
        {
            HubFilters filters = HubBrowse.ParseFilters(Info);

            Assert.Equal(new[] { "Free", "Paid" }, filters.PayTypes);
            Assert.Equal(new[] { "Scenes", "Looks" }, filters.Types);
            Assert.Equal(new[] { "cute" }, filters.Tags);
            Assert.Equal(3, filters.Creators.Count);
        }

        [Fact]
        public void An_answer_with_no_filters_is_a_problem()
        {
            Assert.Throws<InvalidDataException>(() => HubBrowse.ParseFilters("{}"));
        }

        [Theory]
        [InlineData(-1, HubOwnership.NotOwned)]
        [InlineData(1, HubOwnership.Upgrade)]
        [InlineData(2, HubOwnership.Owned)]
        [InlineData(3, HubOwnership.Owned)]
        public void A_resource_says_how_it_compares_to_the_library(int owned, HubOwnership expected)
        {
            HubPage page = HubBrowse.ParsePage(Resources(1, 1, Resource("1", "T", File1("A.B.2"))), _ => owned);

            HubResource resource = Assert.Single(page.Resources);
            Assert.Equal(expected, resource.Ownership);
            Assert.Equal("A.B.2", resource.PackageName);
        }

        [Fact]
        public void A_missing_file_outranks_an_owned_one()
        {
            HubPage page = HubBrowse.ParsePage(Resources(1, 1, Resource("1", "T", File1("Have.It.1") + "," + File1("Need.It.1"))),
                name => name == "Have.It" ? 1 : -1);

            Assert.Equal(HubOwnership.NotOwned, page.Resources[0].Ownership);
        }

        [Fact]
        public void A_resource_with_no_hub_files_is_external_or_unknown()
        {
            HubPage page = HubBrowse.ParsePage(Resources(1, 1, Resource("1", "Paid", null, ",\"download_url\":\"https://x\""), Resource("2", "None", null)), _ => -1);

            Assert.Equal(HubOwnership.External, page.Resources[0].Ownership);
            Assert.Equal(HubOwnership.Unknown, page.Resources[1].Ownership);
        }

        [Fact]
        public void A_resource_whose_files_have_no_version_is_unknown()
        {
            HubPage page = HubBrowse.ParsePage(Resources(1, 1, Resource("1", "T", File1("Weird"))), _ => 5);

            Assert.Equal(HubOwnership.Unknown, page.Resources[0].Ownership);
        }

        [Fact]
        public void An_answer_with_no_pagination_is_a_problem()
        {
            Assert.Throws<InvalidDataException>(() => HubBrowse.ParsePage("{}", _ => -1));
        }

        [Fact]
        public void Downloads_take_files_and_dependencies_the_library_lacks()
        {
            string detail = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub.virtamate.com/a\",\"file_size\":\"10\"}],"
                + "\"dependencies\":{\"A.B.1\":[{\"filename\":\"Need.It.1\",\"downloadUrl\":\"https://hub.virtamate.com/n\"},{\"filename\":\"Have.It.1\",\"downloadUrl\":\"https://hub.virtamate.com/h\"},{\"filename\":\"Nowhere.It.1\",\"downloadUrl\":\"null\"},{\"filename\":\"A.B.1\",\"downloadUrl\":\"https://hub.virtamate.com/other\"}]}}";

            List<HubPackage> packages = HubBrowse.Downloads(detail, name => name == "Have.It.1");

            Assert.Equal(new[] { "A.B.1", "Need.It.1" }, packages.Select(package => package.Name));
            Assert.Equal(10, packages[0].Size);
            Assert.Equal("https://hub.virtamate.com/a", packages[0].Url);
        }

        [Fact]
        public void Author_search_puts_names_that_start_with_the_text_first()
        {
            Assert.Equal(new[] { "Alice", "Malice" }, HubBrowse.MatchCreators(new[] { "Malice", "Bob", "Alice" }, "ali", 8));
            Assert.Single(HubBrowse.MatchCreators(new[] { "Malice", "Alice" }, "ali", 1));
            Assert.Empty(HubBrowse.MatchCreators(new[] { "Alice" }, "  ", 8));
        }

        [Fact]
        public async Task The_image_cache_fetches_a_picture_once()
        {
            int fetches = 0;
            var cache = new HubImageCache(Path.Combine(data, "images"), url =>
            {
                fetches++;
                return Task.FromResult(new byte[] { 1, 2, 3 });
            });

            string first = await cache.Get("https://hub/a.jpg");
            string second = await cache.Get("https://hub/a.jpg");

            Assert.Equal(first, second);
            Assert.Equal(1, fetches);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));
            Assert.Single(Directory.GetFiles(Path.Combine(data, "images")));
        }

        [Fact]
        public async Task The_image_cache_survives_a_failed_or_empty_picture()
        {
            var failing = new HubImageCache(data, url => Task.FromException<byte[]>(new HttpRequestException("no")));
            var empty = new HubImageCache(data, url => Task.FromResult(new byte[0]));

            Assert.Null(await failing.Get("https://hub/a.jpg"));
            Assert.Null(await empty.Get("https://hub/a.jpg"));
            Assert.Null(await empty.Get(""));
            Assert.Null(await empty.Get("null"));
        }

        [Fact]
        public async Task The_image_cache_refuses_a_huge_picture()
        {
            var cache = new HubImageCache(Path.Combine(data, "images"), url => Task.FromResult(new byte[(20 << 20) + 1]));

            Assert.Null(await cache.Get("https://hub/a.jpg"));
            Assert.False(Directory.Exists(Path.Combine(data, "images")));
        }

        [Fact]
        public async Task A_tile_asks_for_its_picture_once()
        {
            int fetches = 0;
            var held = new TaskCompletionSource<byte[]>();
            var cache = new HubImageCache(Path.Combine(data, "images"), url =>
            {
                Interlocked.Increment(ref fetches);
                return held.Task;
            });
            var tile = new HubTile(new HubResource { ImageUrl = "https://hub/a.jpg" }, cache, new ThumbnailLoader(), null);

            // The fetch stays open, so only the tile's own guard can stop a second one.
            Assert.Null(tile.Thumbnail);
            Assert.Null(tile.Thumbnail);
            Assert.Null(tile.Thumbnail);

            Assert.Equal(1, Volatile.Read(ref fetches));
            held.SetResult(new byte[] { 1, 2, 3 });
            await Task.Delay(50);
        }

        [Theory]
        [InlineData(HubOwnership.NotOwned, "Get", true)]
        [InlineData(HubOwnership.Upgrade, "Get update from 1", true)]
        [InlineData(HubOwnership.Owned, "Show in Library", false)]
        [InlineData(HubOwnership.External, "Open on the Hub", false)]
        [InlineData(HubOwnership.Unknown, "Open on the Hub", false)]
        public void A_tile_offers_what_fits_its_package(HubOwnership ownership, string label, bool canGet)
        {
            var tile = new HubTile(new HubResource { Ownership = ownership, OwnedVersion = "1" }, new HubImageCache(data, _ => Task.FromResult(new byte[0])), new ThumbnailLoader(), null);

            Assert.Equal(label, tile.ActionLabel);
            Assert.Equal(canGet, tile.CanGet);
            Assert.Equal(!canGet, tile.CanOpen);
        }

        [Fact]
        public async Task Opening_loads_filters_and_the_first_page_once()
        {
            browser.Page = Resources(1, 3, Resource("1", "Beach", File1("A.B.1")));
            HubViewModel hub = Hub();

            await hub.Open().WaitAsync(Timeout);
            await hub.Open().WaitAsync(Timeout);

            Assert.Single(browser.Queries);
            Assert.Contains("Paid", hub.PayTypes);
            Assert.Equal("All", hub.Types[0]);
            Assert.Equal("Page 1 of 3", hub.PageText);
            Assert.Equal("Beach", Assert.Single(Tiles(hub)).Title);
            Assert.Equal("Get", Tiles(hub)[0].ActionLabel);
        }

        [Fact]
        public void A_resize_before_the_first_page_says_nothing_was_found()
        {
            HubViewModel hub = Hub();

            hub.Columns = 3;

            Assert.Equal("", hub.StatusText);
        }

        [Fact]
        public async Task A_hub_that_cant_be_reached_says_so_and_can_be_retried()
        {
            browser.Down = true;
            HubViewModel hub = Hub();

            await hub.Open().WaitAsync(Timeout);
            Assert.Contains("The Hub couldn't be reached", hub.StatusText);

            browser.Down = false;
            await hub.Open().WaitAsync(Timeout);
            Assert.Single(browser.Queries);
        }

        [Fact]
        public async Task A_first_page_that_fails_is_asked_for_again()
        {
            browser.Page = Resources(1, 1, Resource("1", "Beach", File1("A.B.1")));
            browser.ResourcesDown = true;
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            Assert.Contains("couldn't be reached", hub.StatusText);

            browser.ResourcesDown = false;
            await hub.Open().WaitAsync(Timeout);

            Assert.Single(Tiles(hub));
        }

        [Fact]
        public async Task A_failed_page_keeps_the_page_number_and_the_rows()
        {
            browser.Page = Resources(2, 3, Resource("1", "Beach", File1("A.B.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            hub.NextCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal("Page 2 of 3", hub.PageText);

            browser.ResourcesDown = true;
            hub.NextCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Contains("couldn't be reached", hub.StatusText);
            Assert.Single(Tiles(hub));

            browser.ResourcesDown = false;
            hub.NextCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal(3, browser.Queries[^1].Page);
        }

        [Fact]
        public async Task Paging_stops_at_the_ends_and_trusts_only_real_pages()
        {
            browser.Page = Resources(9, 3, Resource("1", "Beach", File1("A.B.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            Assert.Equal("Page 3 of 3", hub.PageText);
            Assert.False(hub.CanGoOn);
            Assert.True(hub.CanGoBack);

            browser.Page = Resources(1, 3, Resource("1", "Beach", File1("A.B.1")));
            hub.PreviousCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.False(hub.CanGoBack);

            hub.PreviousCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal(1, browser.Queries[^1].Page);

            browser.Page = Resources(1, 0);
            hub.SearchCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal("", hub.PageText);
            Assert.False(hub.CanGoOn);
        }

        [Fact]
        public async Task Rows_follow_the_width_without_asking_the_hub_again()
        {
            browser.Page = Resources(1, 1, Enumerable.Range(1, 7).Select(i => Resource(i.ToString(), "T" + i, File1("A.B." + i))).ToArray());
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            int asked = browser.Queries.Count;

            hub.Columns = 3;
            Assert.Equal(new[] { 3, 3, 1 }, hub.Rows.Select(row => row.Tiles.Count));

            hub.Columns = 2;
            Assert.Equal(4, hub.Rows.Count);
            Assert.Equal(asked, browser.Queries.Count);
        }

        [Fact]
        public async Task The_box_defaults_fall_back_when_the_hub_lists_something_else()
        {
            browser.InfoResponse = "{\"category\":[\"Paid\"],\"location\":[\"Hub Only\"],\"type\":[],\"sort\":[\"Rating\"],\"tags\":{},\"users\":{}}";
            HubViewModel hub = Hub();

            await hub.Open().WaitAsync(Timeout);

            Assert.Equal("All", hub.PayType);
            Assert.Equal("Rating", hub.Sort);
            Assert.Equal("All", hub.Location);
            Assert.Equal("Rating", browser.Queries[^1].Sort);
        }

        [Fact]
        public async Task Ownership_uses_the_newest_version_and_ignores_case()
        {
            // Added newest first as well as oldest first, so neither first-wins nor last-wins passes.
            Own("A.B.1");
            Own("A.B.3");
            Own("E.F.9");
            Own("E.F.2");
            Own("c.d.2");
            browser.Page = Resources(1, 1, Resource("1", "Older", File1("A.B.2")), Resource("2", "Case", File1("C.D.2")), Resource("3", "Newer", File1("A.B.4")), Resource("4", "Other", File1("E.F.5")));
            HubViewModel hub = Hub();

            await hub.Open().WaitAsync(Timeout);

            Assert.Equal(HubOwnership.Owned, Tiles(hub)[0].Resource.Ownership);
            Assert.Equal(HubOwnership.Owned, Tiles(hub)[1].Resource.Ownership);
            Assert.Equal("Get update from 3", Tiles(hub)[2].ActionLabel);
            Assert.Equal(HubOwnership.Owned, Tiles(hub)[3].Resource.Ownership);
        }

        [Fact]
        public async Task Not_owned_hides_what_the_library_has()
        {
            Own("A.B.1");
            browser.Page = Resources(1, 1, Resource("1", "Mine", File1("A.B.1")), Resource("2", "New", File1("C.D.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            Assert.Equal(2, Tiles(hub).Count);
            Assert.Equal("Show in Library", Tiles(hub)[0].ActionLabel);

            hub.NotOwned = true;

            Assert.Equal("New", Assert.Single(Tiles(hub)).Title);
            Assert.Contains("1 on this page hidden as owned", hub.StatusText);

            hub.NotOwned = false;
            Assert.Equal(2, Tiles(hub).Count);
        }

        [Fact]
        public async Task Filters_and_paging_reach_the_query()
        {
            browser.Page = Resources(2, 3, Resource("1", "T", File1("A.B.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.Type = "Looks";
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal("Looks", browser.Queries[^1].Type);
            Assert.Equal(1, browser.Queries[^1].Page);

            hub.Tag = "All";
            hub.NextCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal(3, browser.Queries[^1].Page);
            Assert.Equal("", browser.Queries[^1].Tag);

            hub.Search = " beach ";
            hub.SearchCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal("beach", browser.Queries[^1].Search);
            Assert.Equal(1, browser.Queries[^1].Page);
        }

        [Fact]
        public async Task Author_search_suggests_and_filters_once_picked()
        {
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            int asked = browser.Queries.Count;

            hub.CreatorText = "ali";
            Assert.Equal(new[] { "Alice", "Malice" }, hub.Suggestions);
            Assert.Equal(asked, browser.Queries.Count);

            hub.PickCreatorCommand.Execute("Alice");
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal("Alice", browser.Queries[^1].Creator);
            Assert.False(hub.HasSuggestions);
            Assert.True(hub.HasCreator);

            hub.ClearCreatorCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            Assert.Equal("", browser.Queries[^1].Creator);
        }

        [Fact]
        public async Task A_slow_earlier_page_does_not_overwrite_a_later_one()
        {
            browser.Page = Resources(1, 1, Resource("1", "First", File1("A.B.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            var gate = new TaskCompletionSource<bool>();
            browser.Gate = gate;
            browser.Page = Resources(1, 1, Resource("2", "Old", File1("A.B.1")));

            hub.SearchCommand.Execute(null);
            Task slow = hub.Loaded;
            browser.Page = Resources(1, 1, Resource("3", "New", File1("A.B.1")));
            hub.SearchCommand.Execute(null);
            await hub.Loaded.WaitAsync(Timeout);
            gate.SetResult(true);
            await slow.WaitAsync(Timeout);

            Assert.Equal("New", Assert.Single(Tiles(hub)).Title);
        }

        [Fact]
        public async Task Get_downloads_what_it_lacks_links_it_and_shows_it_owned_after()
        {
            Own("Have.It.1");
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("A.B.1")));
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub.virtamate.com/a\"}],\"dependencies\":{\"x\":[{\"filename\":\"Have.It.1\",\"downloadUrl\":\"https://hub.virtamate.com/h\"}]}}";
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.GetCommand.Execute(Tiles(hub)[0]);
            WaitForJob();

            Assert.Equal(new[] { "A.B.1" }, downloads.Names);
            Assert.True(VarLinks.ReadInstalled(paths.AddonPackages).ContainsKey("A.B.1"));
            Assert.True(VarLinks.ReadInstalled(paths.AddonPackages).ContainsKey("Have.It.1"));
            Assert.Equal("Stored 1, failed 0.", jobs.Note);

            await hub.Refresh().WaitAsync(Timeout);
            Assert.Equal("Show in Library", Tiles(hub)[0].ActionLabel);
        }

        [Fact]
        public async Task Get_with_everything_owned_turns_on_what_this_profile_lacks()
        {
            Own("A.B.2");
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("C.D.1")));
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub.virtamate.com/a\"}]}";
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.GetCommand.Execute(Tiles(hub)[0]);
            WaitForJob();

            Assert.Empty(downloads.Names);
            Assert.True(VarLinks.ReadInstalled(paths.AddonPackages).ContainsKey("A.B.2"));
            Assert.Equal("Turned on 1 var.", jobs.Note);
        }

        [Fact]
        public async Task Get_says_when_the_hub_fails()
        {
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("A.B.1")));
            browser.DetailError = new IOException("no network");
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.GetCommand.Execute(Tiles(hub)[0]);
            WaitForJob();

            Assert.Contains("failed: no network", jobs.Note);
        }

        [Fact]
        public async Task Get_reports_a_failed_download()
        {
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("A.B.1")));
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub.virtamate.com/a\"}]}";
            downloads.Fail = true;
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.GetCommand.Execute(Tiles(hub)[0]);
            WaitForJob();

            Assert.Equal("Stored 0, failed 1. First problem: broken", jobs.Note);
        }

        [Fact]
        public void A_get_that_stored_nothing_turns_nothing_on_later()
        {
            Own("Have.It.1");
            downloads.Fail = true;
            var packages = new[] { new HubPackage("A.B.1", "https://hub.virtamate.com/a", 10) };

            MissingCheck.GetPackages(packages, new[] { "Have.It.1" }, storeFile, paths, downloads, _ => long.MaxValue, (done, total) => { }, default);

            Assert.False(File.Exists(paths.MigratedList));
        }

        [Fact]
        public void A_get_that_wont_fit_stops_and_says_why()
        {
            var packages = new[] { new HubPackage("A.B.1", "https://hub.virtamate.com/a", 500L << 20) };

            string text = MissingCheck.GetPackages(packages, new string[0], storeFile, paths, downloads, _ => 1200L << 20, (done, total) => { }, default);

            Assert.Contains("but only", text);
            Assert.Empty(downloads.Names);
        }

        [Fact]
        public async Task Get_with_nothing_to_fetch_says_so()
        {
            Own("A.B.2");
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("C.D.1")));
            string link = Path.Combine(links, "A.B.2.var");
            Assert.True(VarLinks.Create(link, Path.Combine(vars, LibraryFolders.Tidied, "A", "A.B.2.var")));
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub.virtamate.com/a\"}]}";
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.GetCommand.Execute(Tiles(hub)[0]);
            WaitForJob();

            Assert.Empty(downloads.Names);
            Assert.Equal("You already have everything it needs.", jobs.Note);
        }

        [Fact]
        public async Task A_paid_package_only_opens_its_hub_page()
        {
            browser.Page = Resources(1, 1, Resource("7", "Paid", null, ",\"download_url\":\"https://elsewhere\""));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            HubTile tile = Tiles(hub)[0];

            Assert.False(tile.CanGet);
            hub.OpenCommand.Execute(tile);

            Assert.Equal(new[] { "https://hub.virtamate.com/resources/7/" }, opened);
        }

        [Fact]
        public async Task An_owned_package_shows_in_the_library()
        {
            Own("A.B.1");
            browser.Page = Resources(1, 1, Resource("1", "Mine", File1("A.B.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.OpenCommand.Execute(Tiles(hub)[0]);

            Assert.Equal(new[] { "A.B" }, shown);
            Assert.Empty(opened);
        }

        [Fact]
        public async Task The_shell_opens_the_hub_from_missing_and_comes_back()
        {
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            using var shell = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 0, downloads, TimeSpan.FromHours(1), browser);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            shell.SelectedPage = shell.Pages[^1];
            Assert.True(shell.IsMissingList);

            shell.BrowseHubCommand.Execute(null);

            Assert.True(shell.IsHubPage);
            Assert.False(shell.IsMissingList);
            await shell.Hub!.Loaded.WaitAsync(Timeout);
            Assert.Single(browser.Queries);

            shell.LeaveHubCommand.Execute(null);
            Assert.True(shell.IsMissingList);
        }

        [Fact]
        public async Task Leaving_missing_while_browsing_comes_back_to_the_list()
        {
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            using var shell = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 0, downloads, TimeSpan.FromHours(1), browser);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            shell.SelectedPage = shell.Pages[^1];
            shell.BrowseHubCommand.Execute(null);

            shell.SelectedPage = shell.Pages[0];
            shell.SelectedPage = shell.Pages[^1];

            Assert.True(shell.IsMissingList);
            Assert.False(shell.IsHubPage);
            await shell.Hub!.Loaded.WaitAsync(Timeout);
        }

        [Fact]
        public async Task A_get_on_the_hub_page_refreshes_it_by_itself()
        {
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("A.B.1")));
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub.virtamate.com/a\"}]}";
            using var shell = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 0, downloads, TimeSpan.FromHours(1), browser);
            await shell.LibraryLoaded.WaitAsync(Timeout);
            shell.SelectedPage = shell.Pages[^1];
            shell.BrowseHubCommand.Execute(null);
            await shell.Hub!.Loaded.WaitAsync(Timeout);
            shell.Hub.Columns = 10;

            shell.Hub.GetCommand.Execute(Tiles(shell.Hub)[0]);
            WaitForJob();

            DateTime deadline = DateTime.UtcNow + Timeout;
            while (Tiles(shell.Hub)[0].ActionLabel != "Show in Library" && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Equal("Show in Library", Tiles(shell.Hub)[0].ActionLabel);
        }
    }
}
