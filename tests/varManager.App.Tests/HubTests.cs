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

        private sealed class FakeBrowser : IHubBrowser
        {
            public string Page { get; set; } = HubTests.Resources(1, 1);
            public string DetailResponse { get; set; } = "{}";
            public bool Down { get; set; }
            public List<HubQuery> Queries { get; } = new List<HubQuery>();
            public TaskCompletionSource<bool>? Gate { get; set; }

            public Task<string> Info()
            {
                return Down ? Task.FromException<string>(new IOException("no network")) : Task.FromResult(HubTests.Info);
            }

            public async Task<string> Resources(HubQuery query)
            {
                Queries.Add(query);
                TaskCompletionSource<bool>? gate = Gate;
                Gate = null;
                string page = Page;
                if (gate != null)
                    await gate.Task;
                return page;
            }

            public Task<string> Detail(string resourceId)
            {
                return Task.FromResult(DetailResponse);
            }

            public Task<byte[]> Image(string url)
            {
                return Task.FromResult(new byte[0]);
            }
        }

        private sealed class FakeDownloads : IHubSource
        {
            public List<string> Names { get; } = new List<string>();

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
                    WriteVar(Path.Combine(folder, package.Name + ".var"));
                    result.Stored.Add(package.Name);
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
            return new HubViewModel(storeFile, paths, jobs, browser, downloads, opened.Add, null) { Columns = 10 };
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
        public void An_answer_with_no_pagination_is_a_problem()
        {
            Assert.Throws<InvalidDataException>(() => HubBrowse.ParsePage("{}", _ => -1));
        }

        [Fact]
        public void Downloads_take_files_and_dependencies_the_library_lacks()
        {
            string detail = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub/a\",\"file_size\":\"10\"}],"
                + "\"dependencies\":{\"A.B.1\":[{\"filename\":\"Need.It.1\",\"downloadUrl\":\"https://hub/n\"},{\"filename\":\"Have.It.1\",\"downloadUrl\":\"https://hub/h\"},{\"filename\":\"Nowhere.It.1\",\"downloadUrl\":\"null\"}]}}";

            List<HubPackage> packages = HubBrowse.Downloads(detail, name => name == "Have.It.1");

            Assert.Equal(new[] { "A.B.1", "Need.It.1" }, packages.Select(package => package.Name));
            Assert.Equal(10, packages[0].Size);
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
        public async Task Not_owned_hides_what_the_library_has()
        {
            Own("A.B.1");
            browser.Page = Resources(1, 1, Resource("1", "Mine", File1("A.B.1")), Resource("2", "New", File1("C.D.1")));
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);
            Assert.Equal(2, Tiles(hub).Count);
            Assert.Equal("In your library", Tiles(hub)[0].ActionLabel);

            hub.NotOwned = true;

            Assert.Equal("New", Assert.Single(Tiles(hub)).Title);
            Assert.Contains("1 on this page hidden as owned", hub.StatusText);
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
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub/a\"}],\"dependencies\":{\"x\":[{\"filename\":\"Have.It.1\",\"downloadUrl\":\"https://hub/h\"}]}}";
            HubViewModel hub = Hub();
            await hub.Open().WaitAsync(Timeout);

            hub.GetCommand.Execute(Tiles(hub)[0]);
            WaitForJob();

            Assert.Equal(new[] { "A.B.1" }, downloads.Names);
            Assert.True(VarLinks.ReadInstalled(paths.AddonPackages).ContainsKey("A.B.1"));
            Assert.Equal("Stored 1, failed 0.", jobs.Note);

            await hub.Refresh().WaitAsync(Timeout);
            Assert.Equal("In your library", Tiles(hub)[0].ActionLabel);
        }

        [Fact]
        public async Task Get_with_nothing_to_fetch_says_so()
        {
            Own("A.B.2");
            browser.Page = Resources(1, 1, Resource("9", "Beach", File1("C.D.1")));
            browser.DetailResponse = "{\"hubFiles\":[{\"filename\":\"A.B.1.var\",\"urlHosted\":\"https://hub/a\"}]}";
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
    }
}
