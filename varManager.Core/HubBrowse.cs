using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SimpleJSON;

namespace varManager
{
    /// <summary>The Hub's own lists of what a search can filter by.</summary>
    public sealed class HubFilters
    {
        public List<string> PayTypes { get; } = new List<string>();
        public List<string> Locations { get; } = new List<string>();
        public List<string> Types { get; } = new List<string>();
        public List<string> Sorts { get; } = new List<string>();
        public List<string> Tags { get; } = new List<string>();
        public List<string> Creators { get; } = new List<string>();
    }

    public sealed class HubQuery
    {
        public const int PerPage = 48;

        public string Location { get; set; } = "All";
        public string PayType { get; set; } = "Free";
        public string Type { get; set; } = "";
        public string Creator { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Search { get; set; } = "";
        public string Sort { get; set; } = "Latest Update";
        public int Page { get; set; } = 1;
    }

    public enum HubOwnership
    {
        /// <summary>The Hub lists no files, so only its page can say more.</summary>
        Unknown,
        Owned,
        Upgrade,
        NotOwned,

        /// <summary>Hosted elsewhere, such as a paid package, so it can only be opened in a browser.</summary>
        External
    }

    public sealed class HubResource
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Creator { get; set; }
        public string PayType { get; set; }
        public string Type { get; set; }
        public string TagLine { get; set; }
        public string Version { get; set; }
        public string ImageUrl { get; set; }
        public double Rating { get; set; }
        public int Downloads { get; set; }
        public string DownloadUrl { get; set; }
        public List<string> Files { get; } = new List<string>();
        public HubOwnership Ownership { get; set; }

        /// <summary>The var the user owns or would get, for pointing at it.</summary>
        public string PackageName { get; set; }

        public string OwnedVersion { get; set; }
        public string PageUrl => "https://hub.virtamate.com/resources/" + Id + "/";
    }

    public sealed class HubPage
    {
        public int TotalFound { get; set; }
        public int TotalPages { get; set; }
        public int Page { get; set; }
        public List<HubResource> Resources { get; } = new List<HubResource>();
    }

    /// <summary>What browsing needs from the Hub, so tests can stand in for it.</summary>
    public interface IHubBrowser
    {
        Task<string> Info();
        Task<string> Resources(HubQuery query);
        Task<string> Detail(string resourceId);
        Task<byte[]> Image(string url);
    }

    public sealed class RealHubBrowser : IHubBrowser
    {
        private const string ApiUrl = "https://hub.virtamate.com/citizenx/api.php";
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public Task<string> Info()
        {
            JSONClass request = Request("getInfo");
            return Post(request);
        }

        public Task<string> Resources(HubQuery query)
        {
            JSONClass request = Request("getResources");
            request.Add("latest_image", "Y");
            request.Add("location", query.Location);
            request.Add("category", query.PayType);
            if (query.Type.Length > 0)
                request.Add("type", query.Type);
            if (query.Creator.Length > 0)
                request.Add("username", query.Creator);
            if (query.Tag.Length > 0)
                request.Add("tags", query.Tag);
            if (query.Search.Length > 0)
                request.Add("search", query.Search);
            request.Add("searchall", "true");
            request.Add("sort", query.Sort);
            request.Add("perpage", HubQuery.PerPage.ToString(CultureInfo.InvariantCulture));
            request.Add("page", query.Page.ToString(CultureInfo.InvariantCulture));
            return Post(request);
        }

        public Task<string> Detail(string resourceId)
        {
            JSONClass request = Request("getResourceDetail");
            request.Add("latest_image", "Y");
            request.Add("resource_id", resourceId);
            return Post(request);
        }

        public Task<byte[]> Image(string url)
        {
            return Client.GetByteArrayAsync(url);
        }

        private static JSONClass Request(string action)
        {
            JSONClass request = new JSONClass();
            request.Add("source", "VaM");
            request.Add("action", action);
            return request;
        }

        private static async Task<string> Post(JSONClass request)
        {
            using (var data = new StringContent(request.ToString(), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await Client.PostAsync(ApiUrl, data).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
    }

    public static class HubBrowse
    {
        private const string VarExtension = ".var";

        /// <summary>A Hub answer can only point at the Hub, whatever it says. Pictures come from its CDN, so they aren't held to this.</summary>
        public static bool IsHubUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && (string.Equals(uri.Host, "virtamate.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".virtamate.com", StringComparison.OrdinalIgnoreCase));
        }

        public static HubFilters ParseFilters(string response)
        {
            JSONNode json = JSON.Parse(response);
            var filters = new HubFilters();
            AddArray(filters.PayTypes, json["category"]);
            AddArray(filters.Locations, json["location"]);
            AddArray(filters.Types, json["type"]);
            AddArray(filters.Sorts, json["sort"]);
            AddKeys(filters.Tags, json["tags"]);
            AddKeys(filters.Creators, json["users"]);
            if (filters.Sorts.Count == 0)
                throw new InvalidDataException("The Hub returned no filters.");
            return filters;
        }

        private static void AddArray(List<string> list, JSONNode node)
        {
            JSONArray array = node as JSONArray;
            if (array != null)
                list.AddRange(array.Childs.Select(item => item.Value));
        }

        private static void AddKeys(List<string> list, JSONNode node)
        {
            JSONClass keys = node as JSONClass;
            if (keys != null)
                list.AddRange(keys.Keys);
        }

        /// <param name="newestOwned">Gives the highest owned version of a creator.package, or -1.</param>
        public static HubPage ParsePage(string response, Func<string, int> newestOwned)
        {
            JSONNode json = JSON.Parse(response);
            JSONClass pagination = json["pagination"] as JSONClass;
            if (pagination == null)
                throw new InvalidDataException("The Hub returned no results.");

            var page = new HubPage
            {
                TotalFound = Number(pagination["total_found"].Value),
                TotalPages = Number(pagination["total_pages"].Value),
                Page = Number(pagination["page"].Value),
            };
            JSONArray resources = json["resources"] as JSONArray;
            if (resources != null)
            {
                foreach (JSONNode item in resources.Childs)
                    page.Resources.Add(Parse(item, newestOwned));
            }
            return page;
        }

        private static HubResource Parse(JSONNode item, Func<string, int> newestOwned)
        {
            double rating;
            double.TryParse(item["rating_avg"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rating);
            var resource = new HubResource
            {
                Id = item["resource_id"].Value,
                Title = item["title"].Value,
                Creator = item["username"].Value,
                PayType = item["category"].Value,
                Type = item["type"].Value,
                TagLine = item["tag_line"].Value,
                Version = item["version_string"].Value,
                ImageUrl = item["image_url"].Value,
                Rating = rating,
                Downloads = Number(item["download_count"].Value),
                DownloadUrl = item["download_url"].Value,
            };

            JSONClass whole = item as JSONClass;
            JSONArray files = whole != null && whole.HasKey("hubFiles") ? item["hubFiles"] as JSONArray : null;
            if (files != null)
            {
                foreach (JSONNode file in files.Childs)
                    resource.Files.Add(WithoutExtension(file["filename"].Value));
            }
            SetOwnership(resource, newestOwned);
            return resource;
        }

        private static void SetOwnership(HubResource resource, Func<string, int> newestOwned)
        {
            if (resource.Files.Count == 0)
            {
                resource.Ownership = string.IsNullOrEmpty(resource.DownloadUrl) || resource.DownloadUrl == "null" ? HubOwnership.Unknown : HubOwnership.External;
                return;
            }

            // Anything missing outranks an upgrade, and an upgrade outranks owning it all.
            resource.Ownership = HubOwnership.Unknown;
            foreach (string file in resource.Files)
            {
                int version = HubParser.VersionOf(file);
                if (version < 0)
                    continue;
                if (resource.Ownership == HubOwnership.Unknown)
                    resource.Ownership = HubOwnership.Owned;
                int owned = newestOwned(MissingPlanner.PackageOf(file));
                resource.PackageName = resource.PackageName ?? file;
                if (owned < 0)
                {
                    resource.Ownership = HubOwnership.NotOwned;
                    resource.PackageName = file;
                    return;
                }
                if (owned < version)
                {
                    resource.Ownership = HubOwnership.Upgrade;
                    resource.OwnedVersion = owned.ToString(CultureInfo.InvariantCulture);
                    resource.PackageName = file;
                }
            }
        }

        /// <summary>What a resource needs that the library doesn't own: its files, then their dependencies.</summary>
        /// <param name="isOwned">Says whether a var, or a newer version of it, is already in the library.</param>
        public static List<HubPackage> Downloads(string detailResponse, Func<string, bool> isOwned)
        {
            JSONNode json = JSON.Parse(detailResponse);
            var wanted = new Dictionary<string, HubPackage>(StringComparer.OrdinalIgnoreCase);
            JSONArray files = json["hubFiles"] as JSONArray;
            if (files != null)
            {
                foreach (JSONNode file in files.Childs)
                    Add(wanted, file["filename"].Value, file["urlHosted"].Value, file["file_size"].Value, isOwned);
            }
            JSONClass dependencies = json["dependencies"] as JSONClass;
            if (dependencies != null)
            {
                foreach (JSONNode group in dependencies.Childs)
                {
                    JSONArray list = group as JSONArray;
                    if (list == null)
                        continue;
                    foreach (JSONNode file in list.Childs)
                        Add(wanted, file["filename"].Value, file["downloadUrl"].Value, file["file_size"].Value, isOwned);
                }
            }
            return wanted.Values.ToList();
        }

        /// <summary>Every var a resource names, owned or not: its files, then their dependencies.</summary>
        public static List<string> Names(string detailResponse)
        {
            JSONNode json = JSON.Parse(detailResponse);
            var names = new List<string>();
            JSONArray files = json["hubFiles"] as JSONArray;
            if (files != null)
                names.AddRange(files.Childs.Select(file => WithoutExtension(file["filename"].Value)));
            JSONClass dependencies = json["dependencies"] as JSONClass;
            if (dependencies != null)
            {
                foreach (JSONArray list in dependencies.Childs.OfType<JSONArray>())
                    names.AddRange(list.Childs.Select(file => WithoutExtension(file["filename"].Value)));
            }
            return names.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Add(Dictionary<string, HubPackage> wanted, string filename, string url, string size, Func<string, bool> isOwned)
        {
            string name = WithoutExtension(filename);
            // The resource's own entry comes first, and a dependency naming the same var doesn't replace it.
            if (!IsHubUrl(url) || name.Length == 0 || isOwned(name) || wanted.ContainsKey(name))
                return;
            long bytes;
            long.TryParse(size, out bytes);
            wanted[name] = new HubPackage(name, url, bytes);
        }

        private static string WithoutExtension(string filename)
        {
            return filename.EndsWith(VarExtension, StringComparison.OrdinalIgnoreCase)
                ? filename.Substring(0, filename.Length - VarExtension.Length) : filename;
        }

        private static int Number(string text)
        {
            int number;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        /// <summary>Authors whose name holds the typed text, names that start with it first.</summary>
        public static List<string> MatchCreators(IEnumerable<string> creators, string typed, int most)
        {
            string text = typed.Trim();
            if (text.Length == 0)
                return new List<string>();
            return creators.Where(name => name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(name => name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(most)
                .ToList();
        }
    }

    /// <summary>Keeps Hub pictures on disk, so paging back or reopening doesn't fetch them again.</summary>
    public sealed class HubImageCache
    {
        private const long LargestImage = 20L << 20;

        private readonly string folder;
        private readonly Func<string, Task<byte[]>> fetch;

        public HubImageCache(string folder, Func<string, Task<byte[]>> fetch)
        {
            this.folder = folder;
            this.fetch = fetch;
        }

        /// <returns>The file holding the picture, or null if it can't be had.</returns>
        public async Task<string> Get(string url)
        {
            // The Hub writes a missing value as the word null.
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                return null;
            string file = Path.Combine(folder, NameOf(url));
            if (File.Exists(file))
                return file;

            byte[] bytes;
            try
            {
                bytes = await fetch(url).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A tile with no picture is all a failed fetch ever costs.
                return null;
            }
            if (bytes == null || bytes.Length == 0 || bytes.Length > LargestImage)
                return null;

            Directory.CreateDirectory(folder);
            string part = file + "." + Guid.NewGuid().ToString("N") + ".part";
            File.WriteAllBytes(part, bytes);
            try
            {
                File.Move(part, file);
            }
            catch (IOException)
            {
                // Another fetch of the same picture got there first.
                File.Delete(part);
            }
            return file;
        }

        private static string NameOf(string url)
        {
            using (SHA1 sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(url)).Select(value => value.ToString("x2", CultureInfo.InvariantCulture))) + ".img";
        }
    }
}
