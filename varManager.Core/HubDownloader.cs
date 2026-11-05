using ICSharpCode.SharpZipLib.Zip;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace varManager
{
    /// <summary>The Hub's two read calls.</summary>
    public static class HubClient
    {
        private const string ApiUrl = "https://hub.virtamate.com/citizenx/api.php";
        private const string PackageListUrl = "https://s3cdn.virtamate.com/data/packages.json";

        public static async Task<string> FindPackages(string packages)
        {
            JSONClass request = new JSONClass();
            request.Add("source", "VaM");
            request.Add("action", "findPackages");
            request.Add("packages", packages);
            using (HttpClient client = new HttpClient())
            using (StringContent data = new StringContent(request.ToString(), Encoding.UTF8, "application/json"))
            {
                HttpResponseMessage response = await client.PostAsync(ApiUrl, data);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }

        public static async Task<string> GetPackageList()
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(PackageListUrl);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }

        /// <summary>Looks packages up in batches, so a long list stays a reasonable request.</summary>
        public static async Task<List<HubPackage>> FindAll(IEnumerable<string> references)
        {
            const int batchSize = 200;
            List<string> all = references.ToList();
            List<HubPackage> found = new List<HubPackage>();
            for (int start = 0; start < all.Count; start += batchSize)
                found.AddRange(HubParser.Packages(await FindPackages(string.Join(",", all.Skip(start).Take(batchSize)))));
            return found;
        }
    }

    public sealed class DownloadResult
    {
        public List<string> Stored { get; } = new List<string>();
        public Dictionary<string, string> Failed { get; } = new Dictionary<string, string>();
    }

    public enum DownloadStage
    {
        Waiting,
        Downloading,
        Stored,
        Failed,
    }

    /// <summary>Where one package's download has got to.</summary>
    public sealed class DownloadUpdate
    {
        public DownloadUpdate(HubPackage package, DownloadStage stage, long received = 0, long? total = null, string problem = null)
        {
            Name = package.Name;
            Url = package.Url;
            Stage = stage;
            Received = received;
            Total = total;
            Problem = problem;
        }

        public string Name { get; }
        public string Url { get; }
        public DownloadStage Stage { get; }
        public long Received { get; }

        /// <summary>Null when the server doesn't say how big the file is.</summary>
        public long? Total { get; }
        public string Problem { get; }
    }

    /// <summary>Downloads free packages into a folder. A var appears only once it is complete.</summary>
    public sealed class HubDownloader : IDisposable
    {
        private const int ParallelDownloads = 3;
        // VaM's own Hub browser sends this after the user turns the Hub on. Without it the Hub returns a web page.
        private const string ConsentCookie = "vamhubconsent=1";
        private const string PartExtension = ".part";
        private const int ReportEveryMilliseconds = 200;
        private const string MirrorHost = "pixeldrain.com";
        private const string PageHost = "www.mediafire.com";
        private static readonly Regex FileHost = new Regex(@"^download\d+\.mediafire\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PageFileLink = new Regex(@"href=\x22(https://download\d+\.mediafire\.com/[^\x22]+)\x22", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ScrambledLink = new Regex(@"data-scrambled-url=\x22([A-Za-z0-9+/=]+)\x22", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HttpClient client;
        private readonly TimeSpan idleTimeout;

        /// <param name="idleTimeout">How long a download may go without data. Default one minute.</param>
        public HubDownloader(HttpMessageHandler handler = null, TimeSpan? idleTimeout = null)
        {
            this.idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(1);
            // With cookie handling on, the handler drops a Cookie header set by hand.
            client = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false });
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        /// <summary>Told about every package as it moves along. Runs on the download's thread.</summary>
        public Action<DownloadUpdate> Watch { get; set; }

        public void Dispose()
        {
            client.Dispose();
        }

        /// <summary>A link on a mirror the community list can be fetched from.</summary>
        public static bool IsMirrorUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            return string.Equals(uri.Host, MirrorHost, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/api/file/", StringComparison.Ordinal)
                || IsMirrorPage(url)
                || FileHost.IsMatch(uri.Host);
        }

        private static bool IsMirrorPage(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, PageHost, StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/file/", StringComparison.Ordinal);
        }

        // Mediafire links open a page, and the file's address is only in that page.
        private async Task<string> FileUrlOnPage(string page, CancellationToken token)
        {
            using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(idleTimeout);
                using (HttpResponseMessage response = await client.GetAsync(page, idle.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    string html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Match link = PageFileLink.Match(html);
                    if (link.Success)
                        return link.Groups[1].Value;
                    Match scrambled = ScrambledLink.Match(html);
                    string decoded = scrambled.Success ? Encoding.UTF8.GetString(Convert.FromBase64String(scrambled.Groups[1].Value)) : null;
                    if (decoded != null && IsMirrorUrl(decoded) && !IsMirrorPage(decoded))
                        return decoded;
                    throw new InvalidDataException("The Mediafire page had no file link, so the file may be gone.");
                }
            }
        }

        /// <summary>Downloads everything it can, and reports each finished package.</summary>
        public async Task<DownloadResult> DownloadAll(IEnumerable<HubPackage> packages, string folder,
            Action<HubPackage> finished, CancellationToken token)
        {
            DownloadResult result = new DownloadResult();
            object gate = new object();
            List<HubPackage> all = packages.ToList();
            foreach (HubPackage package in all)
                Watch?.Invoke(new DownloadUpdate(package, DownloadStage.Waiting));
            using (SemaphoreSlim slots = new SemaphoreSlim(ParallelDownloads))
            {
                IEnumerable<Task> work = all.Select(async package =>
                {
                    await slots.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await Download(package, folder, token).ConfigureAwait(false);
                        lock (gate)
                            result.Stored.Add(package.Name);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        lock (gate)
                            result.Failed[package.Name] = ex.Message;
                        Watch?.Invoke(new DownloadUpdate(package, DownloadStage.Failed, problem: ex.Message));
                    }
                    finally
                    {
                        slots.Release();
                    }
                    finished?.Invoke(package);
                }).ToList();
                try
                {
                    await Task.WhenAll(work).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Vars already stored stay in the result.
                }
            }
            return result;
        }

        public async Task Download(HubPackage package, string folder, CancellationToken token)
        {
            string fileName = Path.GetFileName(package.Name + ".var");
            if (fileName != package.Name + ".var")
                throw new InvalidDataException("Unsafe file name from the Hub: " + package.Name);
            string target = Path.Combine(folder, fileName);
            if (File.Exists(target))
            {
                Watch?.Invoke(new DownloadUpdate(package, DownloadStage.Stored));
                return;
            }

            string part = target + PartExtension;
            Watch?.Invoke(new DownloadUpdate(package, DownloadStage.Downloading));
            try
            {
                string url = IsMirrorPage(package.Url) ? await FileUrlOnPage(package.Url, token).ConfigureAwait(false) : package.Url;
                await Fetch(url, part, (received, total) => Watch?.Invoke(new DownloadUpdate(package, DownloadStage.Downloading, received, total)), token).ConfigureAwait(false);
                if (!LooksLikeVar(part))
                    throw new InvalidDataException("The download was something other than a var.");
                if (IsMirrorUrl(package.Url))
                    RequireFreeContent(part);
                File.Move(part, target);
                Watch?.Invoke(new DownloadUpdate(package, DownloadStage.Stored));
            }
            finally
            {
                if (File.Exists(part))
                    File.Delete(part);
            }
        }

        /// <summary>A stalled connection ignores cancellation, so dropping the response is what ends the read.</summary>
        private async Task Fetch(string url, string part, Action<long, long?> progress, CancellationToken token)
        {
            using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    idle.CancelAfter(idleTimeout);
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (!IsMirrorUrl(url))
                            request.Headers.Add("Cookie", ConsentCookie);
                        using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false))
                        using (idle.Token.Register(response.Dispose))
                        {
                            response.EnsureSuccessStatusCode();
                            long received = 0;
                            long? expected = response.Content.Headers.ContentLength;
                            int lastReport = Environment.TickCount;
                            using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (FileStream file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[81920];
                                while (true)
                                {
                                    idle.CancelAfter(idleTimeout);
                                    int read = await source.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                                    if (read == 0)
                                        break;
                                    await file.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                                    received += read;
                                    if (Environment.TickCount - lastReport >= ReportEveryMilliseconds)
                                    {
                                        lastReport = Environment.TickCount;
                                        progress(received, expected);
                                    }
                                }
                            }
                            // Dropping a stalled response can end the read as if the file were complete.
                            idle.Token.ThrowIfCancellationRequested();
                            if (expected.HasValue && received != expected.Value)
                                throw new IOException("The download stopped early.");
                        }
                    }
                }
                catch (Exception ex) when (idle.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    throw new IOException("The download stalled.", ex);
                }
            }
        }

        // A re-upload can be a creator's paid work, and only the var itself says so.
        private static void RequireFreeContent(string path)
        {
            string license;
            try
            {
                using (ZipFile zip = new ZipFile(path))
                {
                    ZipEntry meta = zip.GetEntry("meta.json");
                    if (meta == null)
                        throw new InvalidDataException("The var has no meta.json.");
                    using (StreamReader reader = new StreamReader(zip.GetInputStream(meta)))
                        license = JSON.Parse(reader.ReadToEnd())["licenseType"].Value.Trim();
                }
            }
            catch (ZipException ex)
            {
                throw new InvalidDataException("The var can't be read.", ex);
            }
            if (string.Equals(license, "PC", StringComparison.OrdinalIgnoreCase) || string.Equals(license, "PC EA", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Paid content, so it wasn't kept.");
        }

        private static bool LooksLikeVar(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                return file.ReadByte() == 'P' && file.ReadByte() == 'K';
            }
        }
    }
}
