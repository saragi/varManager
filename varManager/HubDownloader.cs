using SimpleJSON;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

    /// <summary>Downloads free Hub packages into a folder. A var appears only once it is complete.</summary>
    public sealed class HubDownloader : IDisposable
    {
        private const int ParallelDownloads = 3;
        // VaM's own Hub browser sends this after the user turns the Hub on. Without it the Hub returns a web page.
        private const string ConsentCookie = "vamhubconsent=1";
        private const string PartExtension = ".part";

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

        public void Dispose()
        {
            client.Dispose();
        }

        /// <summary>Downloads everything it can, and reports each finished package.</summary>
        public async Task<DownloadResult> DownloadAll(IEnumerable<HubPackage> packages, string folder,
            Action<HubPackage> finished, CancellationToken token)
        {
            DownloadResult result = new DownloadResult();
            object gate = new object();
            using (SemaphoreSlim slots = new SemaphoreSlim(ParallelDownloads))
            {
                IEnumerable<Task> work = packages.Select(async package =>
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
                return;

            string part = target + PartExtension;
            try
            {
                await Fetch(package.Url, part, token).ConfigureAwait(false);
                if (!LooksLikeVar(part))
                    throw new InvalidDataException("The Hub sent something other than a var.");
                File.Move(part, target);
            }
            finally
            {
                if (File.Exists(part))
                    File.Delete(part);
            }
        }

        /// <summary>A stalled connection ignores cancellation, so dropping the response is what ends the read.</summary>
        private async Task Fetch(string url, string part, CancellationToken token)
        {
            using (CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    idle.CancelAfter(idleTimeout);
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.Add("Cookie", ConsentCookie);
                        using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false))
                        using (idle.Token.Register(response.Dispose))
                        {
                            response.EnsureSuccessStatusCode();
                            long received = 0;
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
                                }
                            }
                            long? expected = response.Content.Headers.ContentLength;
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

        private static bool LooksLikeVar(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                return file.ReadByte() == 'P' && file.ReadByte() == 'K';
            }
        }
    }
}
