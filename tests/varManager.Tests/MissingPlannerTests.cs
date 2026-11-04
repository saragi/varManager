using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using varManager;
using Xunit;

namespace varManagerTests
{
    public sealed class MissingPlannerTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vmmissing-" + Guid.NewGuid().ToString("N"));

        public MissingPlannerTests()
        {
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private static VarIndex Index(params string[] names)
        {
            return new VarIndex(names.Select(name =>
            {
                string[] parts = name.Split('.');
                return new VarEntry(name, parts[0], parts[1], int.Parse(parts[2]));
            }));
        }

        private static MissingReport Plan(string[] library, string[] linked, Dictionary<string, string[]> references)
        {
            return MissingPlanner.Plan(library, name => references.TryGetValue(name, out string[] found) ? found : new string[0], Index(library), linked);
        }

        [Fact]
        public void A_package_nobody_owns_is_needed()
        {
            MissingReport report = Plan(new[] { "A.Scene.1" }, new[] { "A.Scene.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "Spaced Creator.Hair.latest" } });

            MissingPackage item = Assert.Single(report.Needed);
            Assert.Equal("Spaced Creator.Hair", item.Package);
            Assert.True(item.NeededByLinkedVar);
        }

        [Fact]
        public void A_newer_owned_version_covers_an_exact_reference()
        {
            MissingReport report = Plan(new[] { "A.Scene.1", "B.Hair.3" }, new[] { "A.Scene.1", "B.Hair.3" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "B.Hair.2" } });

            Assert.Empty(report.Needed);
            Assert.Equal(new[] { "B.Hair.2" }, report.Covered);
        }

        [Fact]
        public void An_older_owned_version_does_not_cover_a_reference()
        {
            MissingReport report = Plan(new[] { "A.Scene.1", "B.Hair.1" }, new[] { "A.Scene.1", "B.Hair.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "B.Hair.2" } });

            Assert.Equal(2, Assert.Single(report.Needed).WantedVersion);
        }

        [Fact]
        public void A_minimum_above_what_you_own_is_needed()
        {
            MissingReport report = Plan(new[] { "A.Scene.1", "B.Hair.1" }, new[] { "A.Scene.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "B.Hair.min3" } });

            Assert.Equal(3, Assert.Single(report.Needed).WantedVersion);
        }

        [Fact]
        public void The_highest_wanted_version_wins_and_every_user_is_kept()
        {
            MissingReport report = Plan(new[] { "A.One.1", "A.Two.1" }, new[] { "A.Two.1" },
                new Dictionary<string, string[]> { ["A.One.1"] = new[] { "B.Hair.2" }, ["A.Two.1"] = new[] { "B.Hair.5", "b.hair.latest" } });

            MissingPackage item = Assert.Single(report.Needed);
            Assert.Equal("B.Hair.5", item.Reference);
            Assert.Equal(new[] { "A.One.1", "A.Two.1" }, item.NeededBy);
            Assert.True(item.NeededByLinkedVar);
        }

        [Fact]
        public void A_need_from_unlinked_vars_only_is_not_marked_linked()
        {
            MissingReport report = Plan(new[] { "A.Off.1" }, new string[0],
                new Dictionary<string, string[]> { ["A.Off.1"] = new[] { "B.Hair.1" } });

            Assert.False(Assert.Single(report.Needed).NeededByLinkedVar);
        }

        [Fact]
        public void A_stored_var_a_linked_var_needs_is_turned_off_not_needed()
        {
            MissingReport report = Plan(new[] { "A.Scene.1", "B.Hair.1" }, new[] { "A.Scene.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "B.Hair.1" } });

            Assert.Empty(report.Needed);
            Assert.Equal(new[] { "A.Scene.1" }, report.TurnedOff["B.Hair.1"]);
        }

        [Fact]
        public void A_stored_var_only_unlinked_vars_need_stays_quiet()
        {
            MissingReport report = Plan(new[] { "A.Off.1", "B.Hair.1" }, new string[0],
                new Dictionary<string, string[]> { ["A.Off.1"] = new[] { "B.Hair.1" } });

            Assert.Equal(0, report.Count);
        }

        [Fact]
        public void A_newer_linked_version_means_nothing_needs_turning_on()
        {
            MissingReport report = Plan(new[] { "A.Scene.1", "B.Hair.1", "B.Hair.2" }, new[] { "A.Scene.1", "B.Hair.2" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "B.Hair.1" } });

            Assert.Empty(report.TurnedOff);
        }

        [Theory]
        [InlineData("A.Hair")]
        [InlineData("A.Hair.Extra.1")]
        [InlineData("A.Hair.beta")]
        public void Malformed_references_are_ignored(string reference)
        {
            MissingReport report = Plan(new[] { "A.Scene.1" }, new[] { "A.Scene.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { reference } });

            Assert.Equal(0, report.Count);
        }

        [Theory]
        [InlineData("A.Hair.latest", 0)]
        [InlineData("A.Hair.7", 7)]
        [InlineData("A.Hair.min12", 12)]
        public void WantedVersion_reads_every_form(string reference, int expected)
        {
            Assert.Equal(expected, MissingPlanner.WantedVersion(reference));
        }

        private static MissingPackage Needed(string package, int wanted = 0)
        {
            return new MissingPackage(package) { Reference = package + ".latest", WantedVersion = wanted };
        }

        [Fact]
        public void UpdateRequests_compares_the_newest_owned_version()
        {
            Dictionary<string, int> hub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["A.Hair"] = 3, ["B.Look"] = 2 };

            List<string> requests = MissingPlanner.UpdateRequests(new[] { "A.Hair.1", "A.Hair.2", "B.Look.1", "B.Look.2", "C.New.1" }, hub);

            Assert.Equal(new[] { "A.Hair.latest" }, requests);
        }

        [Fact]
        public void Sort_uses_the_Hub_first_then_the_listed_names()
        {
            HubPackage[] offered = { new HubPackage("A.Hair.3", "u", 1), new HubPackage("D.Old.1", "u", 1) };
            HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.Hair", "B.Look", "D.Old" };

            MissingSources sources = MissingPlanner.Sort(new[] { Needed("A.Hair"), Needed("B.Look"), Needed("C.Gone"), Needed("D.Old", 2) }, offered, listed);

            Assert.Equal(new[] { "A.Hair" }, sources.OnHub.Select(item => item.Package));
            Assert.Equal(new[] { "B.Look", "D.Old" }, sources.Listed.Select(item => item.Package));
            Assert.Equal(new[] { "C.Gone" }, sources.NotFound.Select(item => item.Package));
        }

        [Fact]
        public void Sharp_data_reads_names_and_never_the_settings()
        {
            string install = Path.Combine(root, "SharpVaMTools_V3");
            Directory.CreateDirectory(Path.Combine(install, "_Download_Links"));
            File.WriteAllText(Path.Combine(install, "_Download_Links", "A.txt"), "Spaced One.Hair.2.var   https://example.test/x\nnot a var line\n");
            File.WriteAllText(Path.Combine(install, "_Database.txt"), "\n[1] C93E514F   B.Look.1:/meta.json\n[1] 298202CE   ||| x |||   C.Morph.3:/Custom/a.vmi\n");
            File.WriteAllText(Path.Combine(install, "_Settings.txt"), "Secret.Key.1.var   hidden");

            HashSet<string> packages = SharpVaMToolsData.Packages(install);

            Assert.Equal(new[] { "B.Look", "C.Morph", "Spaced One.Hair" }, packages.OrderBy(name => name));
        }

        [Fact]
        public void Sharp_install_is_found_beside_the_VaM_folder()
        {
            string install = Path.Combine(root, "SharpVaMTools_V3");
            Directory.CreateDirectory(Path.Combine(install, "_Download_Links"));
            Directory.CreateDirectory(Path.Combine(root, "SharpVaMTools_empty"));

            Assert.Equal(install, SharpVaMToolsData.FindBeside(Path.Combine(root, "VaM") + "\\"));
            Assert.Null(SharpVaMToolsData.FindBeside(Path.Combine(root, "VaM", "deeper")));
        }

        private sealed class FakeHub : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; }
            public string LastCookie { get; private set; }
            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                LastCookie = request.Headers.TryGetValues("Cookie", out IEnumerable<string> values) ? values.First() : null;
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Reply(request));
            }
        }

        private static HttpResponseMessage Var()
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("PK-content")) };
        }

        [Fact]
        public async Task Download_stores_a_complete_var_and_sends_the_consent_cookie()
        {
            FakeHub hub = new FakeHub { Reply = request => Var() };
            using (HubDownloader downloader = new HubDownloader(hub))
                await downloader.Download(new HubPackage("Spaced Creator.Hair.2", "https://hub.test/1", 1), root, CancellationToken.None);

            Assert.Equal("PK-content", File.ReadAllText(Path.Combine(root, "Spaced Creator.Hair.2.var")));
            Assert.Equal("vamhubconsent=1", hub.LastCookie);
            Assert.Empty(Directory.GetFiles(root, "*.part"));
        }

        [Fact]
        public async Task Download_rejects_a_web_page_and_leaves_nothing()
        {
            FakeHub hub = new FakeHub { Reply = request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") } };
            using (HubDownloader downloader = new HubDownloader(hub))
                await Assert.ThrowsAsync<InvalidDataException>(() => downloader.Download(new HubPackage("A.Hair.1", "https://hub.test/1", 1), root, CancellationToken.None));

            Assert.Empty(Directory.GetFiles(root));
        }

        [Fact]
        public async Task Download_skips_a_var_already_there()
        {
            File.WriteAllText(Path.Combine(root, "A.Hair.1.var"), "mine");
            FakeHub hub = new FakeHub { Reply = request => Var() };
            using (HubDownloader downloader = new HubDownloader(hub))
                await downloader.Download(new HubPackage("A.Hair.1", "https://hub.test/1", 1), root, CancellationToken.None);

            Assert.Equal("mine", File.ReadAllText(Path.Combine(root, "A.Hair.1.var")));
            Assert.Equal(0, hub.Calls);
        }

        [Theory]
        [InlineData(@"..\Evil.Pack.1")]
        [InlineData("sub/Evil.Pack.1")]
        public async Task Download_refuses_a_name_that_leaves_the_folder(string name)
        {
            FakeHub hub = new FakeHub { Reply = request => Var() };
            using (HubDownloader downloader = new HubDownloader(hub))
                await Assert.ThrowsAsync<InvalidDataException>(() => downloader.Download(new HubPackage(name, "https://hub.test/1", 1), root, CancellationToken.None));
            Assert.Equal(0, hub.Calls);
        }

        [Fact]
        public async Task DownloadAll_reports_failures_and_keeps_going()
        {
            FakeHub hub = new FakeHub
            {
                Reply = request => request.RequestUri.AbsolutePath.EndsWith("bad") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Var()
            };
            int finished = 0;
            DownloadResult result;
            using (HubDownloader downloader = new HubDownloader(hub))
                result = await downloader.DownloadAll(new[] { new HubPackage("A.Bad.1", "https://hub.test/bad", 1), new HubPackage("A.Good.1", "https://hub.test/ok", 1) },
                    root, package => Interlocked.Increment(ref finished), CancellationToken.None);

            Assert.Equal(new[] { "A.Good.1" }, result.Stored);
            Assert.True(result.Failed.ContainsKey("A.Bad.1"));
            Assert.Equal(2, finished);
        }

        [Fact]
        public async Task DownloadAll_stops_on_cancel_without_throwing()
        {
            FakeHub hub = new FakeHub { Reply = request => Var() };
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            using (HubDownloader downloader = new HubDownloader(hub))
            {
                cancel.Cancel();
                DownloadResult result = await downloader.DownloadAll(new[] { new HubPackage("A.One.1", "https://hub.test/1", 1) }, root, null, cancel.Token);
                Assert.Empty(result.Stored);
            }
            Assert.Empty(Directory.GetFiles(root));
        }

        [Fact]
        public void Sort_uses_the_highest_version_offered_in_any_case()
        {
            HubPackage[] offered = { new HubPackage("a.hair.3", "u", 1), new HubPackage("a.hair.1", "u", 1) };

            MissingSources sources = MissingPlanner.Sort(new[] { Needed("A.Hair", 3) }, offered, new HashSet<string>());

            Assert.Single(sources.OnHub);
        }

        [Fact]
        public void UpdateRequests_ignores_the_order_owned_vars_arrive_in()
        {
            Dictionary<string, int> hub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["A.Hair"] = 2 };

            Assert.Empty(MissingPlanner.UpdateRequests(new[] { "A.Hair.2", "A.Hair.1" }, hub));
        }

        [Fact]
        public void A_later_unlinked_user_keeps_the_linked_mark()
        {
            MissingReport report = Plan(new[] { "A.Linked.1", "A.Off.1" }, new[] { "A.Linked.1" },
                new Dictionary<string, string[]> { ["A.Linked.1"] = new[] { "B.Hair.1" }, ["A.Off.1"] = new[] { "B.Hair.1" } });

            Assert.True(Assert.Single(report.Needed).NeededByLinkedVar);
        }

        [Fact]
        public void A_linked_version_below_the_minimum_leaves_the_stored_one_turned_off()
        {
            MissingReport report = Plan(new[] { "A.Scene.1", "B.Hair.1", "B.Hair.3" }, new[] { "A.Scene.1", "B.Hair.1" },
                new Dictionary<string, string[]> { ["A.Scene.1"] = new[] { "B.Hair.min3" } });

            Assert.True(report.TurnedOff.ContainsKey("B.Hair.3"));
        }

        [Fact]
        public async Task Download_rejects_a_short_file()
        {
            FakeHub hub = new FakeHub
            {
                Reply = request =>
                {
                    HttpResponseMessage response = Var();
                    response.Content.Headers.ContentLength = 100;
                    return response;
                }
            };
            using (HubDownloader downloader = new HubDownloader(hub))
                await Assert.ThrowsAsync<IOException>(() => downloader.Download(new HubPackage("A.Hair.1", "https://hub.test/1", 1), root, CancellationToken.None));

            Assert.Empty(Directory.GetFiles(root));
        }

        private sealed class NeverEndingStream : MemoryStream
        {
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                TaskCompletionSource<int> never = new TaskCompletionSource<int>();
                token.Register(() => never.TrySetCanceled());
                return never.Task;
            }
        }

        [Fact]
        public async Task A_stalled_download_fails_and_leaves_nothing()
        {
            FakeHub hub = new FakeHub { Reply = request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) } };
            using (HubDownloader downloader = new HubDownloader(hub, TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAsync<IOException>(() => downloader.Download(new HubPackage("A.Hair.1", "https://hub.test/1", 1), root, CancellationToken.None));

            Assert.Empty(Directory.GetFiles(root));
        }

        [Fact]
        public async Task Cancelling_a_stalled_download_counts_as_cancelled()
        {
            FakeHub hub = new FakeHub { Reply = request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) } };
            using (CancellationTokenSource cancel = new CancellationTokenSource(200))
            using (HubDownloader downloader = new HubDownloader(hub))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.Download(new HubPackage("A.Hair.1", "https://hub.test/1", 1), root, cancel.Token));

            Assert.Empty(Directory.GetFiles(root));
        }

        private sealed class CountingHub : HttpMessageHandler
        {
            private int running;
            public int Peak { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                int now = Interlocked.Increment(ref running);
                lock (this)
                    Peak = Math.Max(Peak, now);
                await Task.Delay(30);
                Interlocked.Decrement(ref running);
                return Var();
            }
        }

        [Fact]
        public async Task DownloadAll_runs_at_most_three_at_once()
        {
            CountingHub hub = new CountingHub();
            HubPackage[] packages = Enumerable.Range(1, 9).Select(number => new HubPackage("A.Pack" + number + ".1", "https://hub.test/" + number, 1)).ToArray();
            using (HubDownloader downloader = new HubDownloader(hub))
                await downloader.DownloadAll(packages, root, null, CancellationToken.None);

            Assert.InRange(hub.Peak, 1, 3);
            Assert.Equal(9, Directory.GetFiles(root).Length);
        }

        private sealed class EndsOnDisposeStream : Stream
        {
            private readonly TaskCompletionSource<int> disposed = new TaskCompletionSource<int>();
            private bool sentStart;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (sentStart)
                    return disposed.Task;
                sentStart = true;
                buffer[offset] = (byte)'P';
                buffer[offset + 1] = (byte)'K';
                return Task.FromResult(2);
            }

            protected override void Dispose(bool disposing)
            {
                disposed.TrySetResult(0);
                base.Dispose(disposing);
            }
        }

        [Fact]
        public async Task A_stall_that_ends_like_a_finished_file_stores_nothing()
        {
            FakeHub hub = new FakeHub { Reply = request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndsOnDisposeStream()) } };
            using (HubDownloader downloader = new HubDownloader(hub, TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAsync<IOException>(() => downloader.Download(new HubPackage("A.Hair.1", "https://hub.test/1", 1), root, CancellationToken.None));

            Assert.Empty(Directory.GetFiles(root));
        }
    }
}
