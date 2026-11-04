using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public sealed class MissingTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private readonly string root = Path.Combine(Path.GetTempPath(), "vmmissing-" + Guid.NewGuid().ToString("N"));
        private readonly string vam;
        private readonly string vars;
        private readonly string data;
        private readonly string storeFile;
        private readonly string links;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs = new JobRunner(null);
        private readonly FakeHub hub = new FakeHub();
        private readonly List<string> copied = new List<string>();
        private int count = -1;

        private sealed class FakeHub : IHubSource
        {
            public Dictionary<string, string> Packages { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Broken { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Names only the package list claims, as when the list runs ahead of what the Hub serves.</summary>
            public List<string> ListedOnly { get; } = new List<string>();

            public List<string> Downloaded { get; } = new List<string>();
            public bool Down { get; set; }
            public long Size { get; set; } = 1000;

            public Task<List<HubPackage>> Find(IEnumerable<string> references)
            {
                if (Down)
                    throw new IOException("no network");
                var found = new List<HubPackage>();
                foreach (string reference in references)
                {
                    string package = MissingPlanner.PackageOf(reference);
                    string? newest = Packages.Keys.Where(name => string.Equals(MissingPlanner.PackageOf(name), package, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(HubParser.VersionOf).FirstOrDefault();
                    if (newest != null)
                        found.Add(new HubPackage(newest, "https://hub.example/" + newest, Size));
                }
                return Task.FromResult(found);
            }

            public Task<string> PackageList()
            {
                return Task.FromResult("{" + string.Join(",", Packages.Keys.Concat(ListedOnly).Select(name => "\"" + name + ".var\":\"1\"")) + "}");
            }

            public Task<DownloadResult> Download(IReadOnlyList<HubPackage> packages, string folder, Action<HubPackage> finished, CancellationToken token)
            {
                var result = new DownloadResult();
                foreach (HubPackage package in packages)
                {
                    token.ThrowIfCancellationRequested();
                    Downloaded.Add(package.Name);
                    if (Broken.Contains(package.Name))
                    {
                        result.Failed[package.Name] = "broken";
                    }
                    else
                    {
                        WriteVar(Path.Combine(folder, package.Name + ".var"), Packages[package.Name]);
                        result.Stored.Add(package.Name);
                    }
                    finished(package);
                }
                return Task.FromResult(result);
            }
        }

        public MissingTests()
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

        private static void WriteVar(string file, string references)
        {
            using ZipOutputStream zip = new ZipOutputStream(File.Create(file));
            zip.PutNextEntry(new ZipEntry("meta.json"));
            byte[] bytes = Encoding.UTF8.GetBytes("{\"x\":\"" + references + "\"}");
            zip.Write(bytes, 0, bytes.Length);
            zip.CloseEntry();
        }

        private void MakeVar(string name, string references = "")
        {
            WriteVar(Path.Combine(vars, name + ".var"), references);
        }

        private void Update()
        {
            using LibraryStore store = LibraryStore.Open(storeFile);
            LibraryUpdate.Run(store, paths, (done, total) => { }, default);
        }

        private string Tidied(string name)
        {
            return Path.Combine(vars, LibraryFolders.Tidied, name.Split('.')[0], name + ".var");
        }

        private void Link(string name)
        {
            Assert.True(VarLinks.Create(Path.Combine(links, name + ".var"), Tidied(name)));
        }

        private bool IsLinked(string name)
        {
            return VarLinks.ReadInstalled(paths.AddonPackages).ContainsKey(name);
        }

        private async Task<MissingViewModel> Open()
        {
            var missing = new MissingViewModel(storeFile, paths, jobs, hub, copied.Add, value => count = value);
            await missing.Check().WaitAsync(Timeout);
            return missing;
        }

        private static MissingGroup Group(MissingViewModel missing, string title)
        {
            return missing.Groups.Single(group => group.Title == title);
        }

        private void WaitForJob()
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (jobs.IsRunning && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.False(jobs.IsRunning);
        }

        private static void WaitUntil(Func<bool> done)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!done() && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.True(done());
        }

        [Fact]
        public async Task Each_kind_of_missing_lands_in_its_group()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap\",\"b\":\"Nowhere.Pack.1:/Custom/a.vap\",\"c\":\"Stored.Pack.1:/Custom/a.vap\",\"d\":\"Newer.Pack.1:/Custom/a.vap\",\"e\":\"Listed.Pack.1:/Custom/a.vap");
            MakeVar("Stored.Pack.1");
            MakeVar("Newer.Pack.2");
            Update();
            Link("A.B.1");
            Link("Newer.Pack.2");
            hub.Packages["Hub.Pack.1"] = "";
            string install = Path.Combine(root, "SharpVaMTools");
            Directory.CreateDirectory(Path.Combine(install, "_Download_Links"));
            File.WriteAllLines(Path.Combine(install, "_Download_Links", "links.txt"), new[] { "Listed.Pack.1.var https://example" });

            MissingViewModel missing = await Open();

            Assert.Equal(1, Group(missing, "Ready on the Hub").Count);
            Assert.Equal(1, Group(missing, "Turned off in your library").Count);
            Assert.Equal(1, Group(missing, "Listed in SharpVaMTools").Count);
            Assert.Equal(1, Group(missing, "Not found anywhere").Count);
            Assert.Equal(1, Group(missing, "Already covered").Count);
            Assert.True(missing.CanGetAll);
            Assert.Equal("Get all 1", missing.GetAllLabel);
            Assert.StartsWith("1 packages ready on the Hub", missing.Title);
        }

        [Fact]
        public async Task An_older_version_is_never_fetched_over_a_newer_one()
        {
            MakeVar("A.B.1", "Wanted.Pack.1:/Custom/a.vap");
            MakeVar("Wanted.Pack.2");
            Update();
            hub.Packages["Wanted.Pack.1"] = "";

            MissingViewModel missing = await Open();

            Assert.False(missing.CanGetAll);
            Assert.Equal(1, Group(missing, "Already covered").Count);
        }

        [Fact]
        public async Task A_hub_version_too_old_for_a_min_reference_is_not_offered()
        {
            MakeVar("A.B.1", "Wanted.Pack.min5:/Custom/a.vap");
            Update();
            hub.Packages["Wanted.Pack.3"] = "";

            MissingViewModel missing = await Open();

            Assert.False(missing.CanGetAll);
            Assert.Equal(1, Group(missing, "Not found anywhere").Count);
        }

        [Fact]
        public async Task A_hub_that_cant_be_reached_still_shows_what_the_library_knows()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            Update();
            hub.Down = true;

            MissingViewModel missing = await Open();

            Assert.Contains("The Hub couldn't be reached", missing.StatusText);
            Assert.False(missing.CanGetAll);
            Assert.Equal(1, Group(missing, "Not found anywhere").Count);
        }

        [Fact]
        public async Task A_tidy_library_says_nothing_is_missing()
        {
            MakeVar("A.B.1");
            Update();

            MissingViewModel missing = await Open();

            Assert.Equal("Nothing is missing", missing.Title);
            Assert.False(missing.CanGetAll);
        }

        [Fact]
        public async Task Get_all_downloads_scans_links_and_follows_what_downloads_need()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            MakeVar("Stored.Pack.1");
            MakeVar("OffPurpose.Pack.1");
            MakeVar("Other.Pack.1", "OffPurpose.Pack.1:/Custom/a.vap");
            Update();
            Link("A.B.1");
            Link("Other.Pack.1");
            hub.Packages["Hub.Pack.1"] = "Deep.Pack.1:/Custom/a.vap\",\"b\":\"Stored.Pack.1:/Custom/a.vap";
            hub.Packages["Deep.Pack.1"] = "";
            MissingViewModel missing = await Open();

            missing.GetAllCommand.Execute(null);
            WaitForJob();

            Assert.Equal(new[] { "Hub.Pack.1", "Deep.Pack.1" }, hub.Downloaded);
            Assert.True(File.Exists(Tidied("Hub.Pack.1")));
            Assert.True(IsLinked("Hub.Pack.1"));
            Assert.True(IsLinked("Deep.Pack.1"));
            Assert.True(IsLinked("Stored.Pack.1"));
            Assert.False(IsLinked("OffPurpose.Pack.1"));
            Assert.Equal("Stored 2, failed 0.", jobs.Note);
        }

        [Fact]
        public async Task A_download_nothing_linked_needs_stays_stored_and_off()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            Update();
            hub.Packages["Hub.Pack.1"] = "";
            MissingViewModel missing = await Open();

            missing.GetAllCommand.Execute(null);
            WaitForJob();

            Assert.True(File.Exists(Tidied("Hub.Pack.1")));
            Assert.False(IsLinked("Hub.Pack.1"));
        }

        [Fact]
        public async Task A_failed_download_is_reported_and_tried_once()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            Update();
            hub.Packages["Hub.Pack.1"] = "";
            hub.Broken.Add("Hub.Pack.1");
            MissingViewModel missing = await Open();

            missing.GetAllCommand.Execute(null);
            WaitForJob();

            Assert.Equal("Stored 0, failed 1. First problem: broken", jobs.Note);
            Assert.Single(hub.Downloaded);
        }

        [Fact]
        public void A_download_that_wont_fit_stops_and_says_why()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            Update();
            hub.Packages["Hub.Pack.1"] = "";
            hub.Size = 500L << 20;

            string text = MissingCheck.GetAll(storeFile, paths, false, hub, new HashSet<string>(), _ => (1L << 30) + (100L << 20), (done, total) => { }, default);

            Assert.Contains("but only", text);
            Assert.Empty(hub.Downloaded);
        }

        [Fact]
        public void Get_all_can_be_cancelled()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            Update();
            hub.Packages["Hub.Pack.1"] = "";
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                MissingCheck.GetAll(storeFile, paths, false, hub, new HashSet<string>(), _ => long.MaxValue, (done, total) => { }, cancel.Token));
        }

        [Fact]
        public async Task Updates_are_only_offered_and_link_where_an_older_version_is_linked()
        {
            MakeVar("Linked.Pack.1");
            MakeVar("Stored.Pack.1");
            MakeVar("Same.Pack.1");
            Update();
            Link("Linked.Pack.1");
            hub.Packages["Linked.Pack.2"] = "";
            hub.Packages["Stored.Pack.2"] = "";
            hub.Packages["Same.Pack.1"] = "";
            MissingViewModel missing = await Open();

            Assert.Equal(2, Group(missing, "Updates on the Hub").Count);
            Assert.Empty(hub.Downloaded);

            Group(missing, "Updates on the Hub").Action!.Execute(null);
            WaitForJob();

            Assert.Equal(2, hub.Downloaded.Count);
            Assert.True(IsLinked("Linked.Pack.2"));
            Assert.False(IsLinked("Stored.Pack.2"));
        }

        [Fact]
        public async Task An_update_the_library_already_owns_is_not_offered()
        {
            MakeVar("Owned.Pack.2");
            Update();
            hub.Packages["Owned.Pack.2"] = "";
            hub.ListedOnly.Add("Owned.Pack.3");

            MissingViewModel missing = await Open();

            Assert.Equal(0, Group(missing, "Updates on the Hub").Count);
        }

        [Fact]
        public async Task Turn_on_all_links_what_linked_vars_need()
        {
            MakeVar("A.B.1", "Stored.Pack.1:/Custom/a.vap");
            MakeVar("Stored.Pack.1");
            Update();
            Link("A.B.1");
            MissingViewModel missing = await Open();

            Group(missing, "Turned off in your library").Action!.Execute(null);
            WaitForJob();

            Assert.True(IsLinked("Stored.Pack.1"));
            Assert.Equal("Turned on 1 var.", jobs.Note);
        }

        [Fact]
        public async Task Copy_list_copies_the_names_and_nothing_downloads_from_mirrors()
        {
            MakeVar("A.B.1", "Listed.Pack.1:/Custom/a.vap");
            Update();
            string install = Path.Combine(root, "SharpVaMTools");
            Directory.CreateDirectory(Path.Combine(install, "_Download_Links"));
            File.WriteAllLines(Path.Combine(install, "_Download_Links", "links.txt"), new[] { "Listed.Pack.1.var https://example" });
            MissingViewModel missing = await Open();

            Group(missing, "Listed in SharpVaMTools").Action!.Execute(null);

            Assert.Equal(new[] { "Listed.Pack.1" }, copied);
            Assert.False(missing.CanGetAll);
            Assert.Empty(hub.Downloaded);
        }

        [Fact]
        public async Task This_profile_only_looks_at_what_is_linked()
        {
            MakeVar("Linked.Pack.1", "Gone.One.1:/Custom/a.vap");
            MakeVar("Stored.Pack.1", "Gone.Two.1:/Custom/a.vap");
            Update();
            Link("Linked.Pack.1");
            MissingViewModel missing = await Open();
            Assert.Equal(2, Group(missing, "Not found anywhere").Count);

            missing.ScopePills.Single(pill => pill.Label == "This profile").Command.Execute(null);
            await missing.Checked.WaitAsync(Timeout);

            Assert.Equal(1, Group(missing, "Not found anywhere").Count);
            Assert.Contains("Gone.One.1", Group(missing, "Not found anywhere").Lines[0]);
        }

        [Fact]
        public async Task A_group_shows_its_lines_and_hides_them_again()
        {
            MakeVar("A.B.1", "Gone.One.1:/Custom/a.vap");
            Update();
            MissingViewModel missing = await Open();
            MissingGroup group = Group(missing, "Not found anywhere");

            missing.ToggleLinesCommand.Execute(group);
            Assert.Contains("needed by A.B.1", missing.OpenLines[0]);

            missing.ToggleLinesCommand.Execute(group);
            Assert.False(missing.HasOpenGroup);
        }

        [Fact]
        public async Task Checking_before_any_update_creates_no_store()
        {
            var missing = new MissingViewModel(storeFile, paths, jobs, hub, copied.Add, value => count = value);
            var health = new HealthViewModel(storeFile, paths, jobs, () => { });

            await missing.Count();
            await missing.Check();
            await health.Reload();

            Assert.False(File.Exists(storeFile));
            Assert.Equal("Run Update DB first.", missing.StatusText);
            Assert.Equal("Run Update DB first.", health.StatusText);
        }

        [Fact]
        public async Task The_count_comes_from_the_database_alone()
        {
            MakeVar("A.B.1", "Gone.One.1:/Custom/a.vap");
            Update();
            hub.Down = true;
            var missing = new MissingViewModel(storeFile, paths, jobs, hub, copied.Add, value => count = value);

            await missing.Count().WaitAsync(Timeout);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task The_shell_shows_the_count_and_checks_when_the_page_opens()
        {
            MakeVar("A.B.1", "Hub.Pack.1:/Custom/a.vap");
            Update();
            hub.Packages["Hub.Pack.1"] = "";
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            var shell = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 0, hub, TimeSpan.FromHours(1));
            await shell.LibraryLoaded.WaitAsync(Timeout);
            Assert.Equal("1", shell.Pages[^1].Badge);
            Assert.Empty(shell.Missing!.Groups);

            shell.SelectedPage = shell.Pages[^1];

            Assert.True(shell.IsMissingPage);
            await shell.Missing.Checked.WaitAsync(Timeout);
            Assert.True(shell.Missing.CanGetAll);
        }

        [Fact]
        public async Task A_var_dropped_in_the_repository_is_scanned_by_itself()
        {
            string settingsFile = Path.Combine(data, "settings.json");
            new AppSettings { VamPath = vam, VarsPath = vars }.Save(settingsFile);
            var shell = new ShellViewModel(settingsFile, jobs, () => false, _ => { }, () => 0, hub, TimeSpan.FromMilliseconds(100));
            await shell.LibraryLoaded.WaitAsync(Timeout);

            MakeVar("A.B.1");

            WaitUntil(() => File.Exists(Tidied("A.B.1")));
            WaitForJob();
            using LibraryStore store = LibraryStore.Open(storeFile);
            Assert.Equal(1, store.Count("vars"));
        }

        [Fact]
        public void A_var_there_at_startup_is_scanned_too()
        {
            MakeVar("A.B.1");
            int scans = 0;

            using var watch = new RepositoryWatch(vars, () => false, () => Interlocked.Increment(ref scans), null, TimeSpan.FromMilliseconds(20));

            WaitUntil(() => Volatile.Read(ref scans) == 1);
        }

        [Fact]
        public void An_empty_repository_scans_nothing()
        {
            int scans = 0;

            using (new RepositoryWatch(vars, () => false, () => Interlocked.Increment(ref scans), null, TimeSpan.FromMilliseconds(20)))
                Thread.Sleep(200);

            Assert.Equal(0, scans);
        }

        [Fact]
        public void The_watch_waits_for_a_job_to_finish()
        {
            MakeVar("A.B.1");
            int scans = 0;
            bool busy = true;

            using var watch = new RepositoryWatch(vars, () => Volatile.Read(ref busy), () => Interlocked.Increment(ref scans), null, TimeSpan.FromMilliseconds(20));
            Thread.Sleep(150);
            Assert.Equal(0, Volatile.Read(ref scans));

            Volatile.Write(ref busy, false);
            WaitUntil(() => Volatile.Read(ref scans) == 1);
        }

        [Fact]
        public void The_watch_waits_for_a_var_still_being_copied()
        {
            MakeVar("A.B.1");
            int scans = 0;
            var copying = new FileStream(Path.Combine(vars, "A.B.1.var"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            using var watch = new RepositoryWatch(vars, () => false, () => Interlocked.Increment(ref scans), null, TimeSpan.FromMilliseconds(20));
            Thread.Sleep(150);
            Assert.Equal(0, Volatile.Read(ref scans));

            copying.Dispose();
            WaitUntil(() => Volatile.Read(ref scans) == 1);
        }

        [Fact]
        public void The_watch_survives_its_folder_going_away()
        {
            string folder = Path.Combine(root, "gone");
            Directory.CreateDirectory(folder);
            using var watch = new RepositoryWatch(folder, () => false, () => { }, null, TimeSpan.FromMilliseconds(20));

            Directory.Delete(folder);
            Thread.Sleep(100);
        }
    }
}
