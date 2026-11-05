using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace varManager.Ui
{
    public sealed class HubTile : Observable
    {
        private readonly HubImageCache images;
        private readonly ThumbnailLoader thumbnails;
        private readonly SynchronizationContext? ui;
        private ImageSource? picture;
        private bool asked;

        public HubTile(HubResource resource, HubImageCache images, ThumbnailLoader thumbnails, SynchronizationContext? ui)
        {
            Resource = resource;
            this.images = images;
            this.thumbnails = thumbnails;
            this.ui = ui;
        }

        public HubResource Resource { get; }
        public string Title => Resource.Title;
        public string Caption => $"{Resource.Creator} · {Resource.Type}";
        public string Detail => $"{Resource.PayType} · {Resource.Rating:F1} stars · {Resource.Downloads:N0} downloads";

        public string ActionLabel => Resource.Ownership switch
        {
            HubOwnership.Owned => "Show in Library",
            HubOwnership.Upgrade => $"Get update from {Resource.OwnedVersion}",
            HubOwnership.NotOwned => "Get",
            _ => "Open on the Hub",
        };

        public bool CanGet => Resource.Ownership is HubOwnership.NotOwned or HubOwnership.Upgrade;
        public bool CanOpen => !CanGet;

        public ImageSource? Thumbnail
        {
            get
            {
                if (!asked)
                {
                    asked = true;
                    _ = Fetch();
                }
                return picture;
            }
        }

        private async Task Fetch()
        {
            string? file;
            try
            {
                file = await images.Get(Resource.ImageUrl).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            // ThumbnailLoader turns a picture it can't read into null, so this can't throw on bad bytes.
            if (file != null)
                await thumbnails.Load(file, Show).ConfigureAwait(false);
        }

        private void Show(ImageSource? image)
        {
            void Apply()
            {
                picture = image;
                Changed(nameof(Thumbnail));
            }
            if (ui == null)
                Apply();
            else
                ui.Post(_ => Apply(), null);
        }
    }

    public sealed class HubRow
    {
        public HubRow(IReadOnlyList<HubTile> tiles)
        {
            Tiles = tiles;
        }

        public IReadOnlyList<HubTile> Tiles { get; }
    }

    /// <summary>Browses the Hub page by page, and gets what the user picks.</summary>
    public sealed class HubViewModel : Observable
    {
        private const string All = "All";
        private const int MostSuggestions = 8;

        private readonly string storeFile;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs;
        private readonly IHubBrowser browser;
        private readonly IHubSource hub;
        private readonly Action<string> openUrl;
        private readonly Action<string> showInLibrary;
        private readonly SynchronizationContext? ui;
        private readonly HubImageCache images;
        private readonly ThumbnailLoader thumbnails = new ThumbnailLoader(200);
        private HubFilters filters = new HubFilters();
        private List<HubResource> resources = new List<HubResource>();
        private IReadOnlyList<HubRow> rows = new HubRow[0];
        private IReadOnlyList<string> suggestions = new string[0];
        private int columns = 1;
        private bool notOwned;
        private string search = "";
        private string creatorText = "";
        private string creator = "";
        private string payType = "Free";
        private string location = All;
        private string type = All;
        private string tag = All;
        private string sort = "Latest Update";
        private int page = 1;
        private int totalPages;
        private int totalFound;
        private string statusText = "";
        private int requests;
        private bool loaded;
        private bool hasPage;

        public HubViewModel(string storeFile, LibraryPaths paths, JobRunner jobs, IHubBrowser browser, IHubSource hub, Action<string> openUrl,
            Action<string> showInLibrary, SynchronizationContext? ui)
        {
            this.showInLibrary = showInLibrary;
            this.storeFile = storeFile;
            this.paths = paths;
            this.jobs = jobs;
            this.browser = browser;
            this.hub = hub;
            this.openUrl = openUrl;
            this.ui = ui;
            images = new HubImageCache(Path.Combine(paths.DataFolder, "HubImages"), browser.Image);
            PreviousCommand = new Command(() => GoTo(page - 1));
            NextCommand = new Command(() => GoTo(page + 1));
            SearchCommand = new Command(() => GoTo(1));
            GetCommand = new ArgCommand<HubTile>(Get);
            OpenCommand = new ArgCommand<HubTile>(Open);
            PickCreatorCommand = new ArgCommand<string>(PickCreator);
            ClearCreatorCommand = new Command(() => PickCreator(""));
        }

        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand SearchCommand { get; }
        public ICommand GetCommand { get; }
        public ICommand OpenCommand { get; }
        public ICommand PickCreatorCommand { get; }
        public ICommand ClearCreatorCommand { get; }

        /// <summary>The last page asked for, for whoever needs to wait on it.</summary>
        public Task Loaded { get; private set; } = Task.CompletedTask;

        public IReadOnlyList<string> PayTypes => WithAll(filters.PayTypes, true);
        public IReadOnlyList<string> Locations => WithAll(filters.Locations, true);
        public IReadOnlyList<string> Types => WithAll(filters.Types, true);
        public IReadOnlyList<string> Sorts => WithAll(filters.Sorts, false);
        public IReadOnlyList<string> Tags => WithAll(filters.Tags, true);

        public IReadOnlyList<HubRow> Rows
        {
            get => rows;
            private set
            {
                rows = value;
                Changed();
            }
        }

        public IReadOnlyList<string> Suggestions
        {
            get => suggestions;
            private set
            {
                suggestions = value;
                Changed();
                Changed(nameof(HasSuggestions));
            }
        }

        public bool HasSuggestions => suggestions.Count > 0;

        public int Columns
        {
            get => columns;
            set
            {
                if (value == columns || value < 1)
                    return;
                columns = value;
                Show();
            }
        }

        public string Search
        {
            get => search;
            set
            {
                search = value;
                Changed();
            }
        }

        /// <summary>What's typed in the author box. It only filters once a name is picked.</summary>
        public string CreatorText
        {
            get => creatorText;
            set
            {
                creatorText = value;
                Changed();
                Suggestions = string.Equals(value, creator, StringComparison.OrdinalIgnoreCase) ? new string[0] : HubBrowse.MatchCreators(filters.Creators, value, MostSuggestions);
            }
        }

        public string Creator => creator;
        public bool HasCreator => creator.Length > 0;

        public bool NotOwned
        {
            get => notOwned;
            set
            {
                notOwned = value;
                Changed();
                Show();
            }
        }

        public string PayType
        {
            get => payType;
            set => Filter(ref payType, value);
        }

        public string Location
        {
            get => location;
            set => Filter(ref location, value);
        }

        public string Type
        {
            get => type;
            set => Filter(ref type, value);
        }

        public string Tag
        {
            get => tag;
            set => Filter(ref tag, value);
        }

        public string Sort
        {
            get => sort;
            set => Filter(ref sort, value);
        }

        public string PageText => totalPages == 0 ? "" : $"Page {page} of {totalPages}";
        public bool CanGoBack => page > 1;
        public bool CanGoOn => page < totalPages;

        public string StatusText
        {
            get => statusText;
            private set
            {
                statusText = value;
                Changed();
            }
        }

        private void Open(HubTile tile)
        {
            if (tile.Resource.Ownership == HubOwnership.Owned && tile.Resource.PackageName != null)
                showInLibrary(MissingPlanner.PackageOf(tile.Resource.PackageName));
            else
                openUrl(tile.Resource.PageUrl);
        }

        /// <summary>Loads the filters and the first page, once.</summary>
        public Task Open()
        {
            if (!hasPage)
                Loaded = LoadFirst();
            return Loaded;
        }

        /// <summary>Shows what the library owns now, after a job changed it.</summary>
        public Task Refresh()
        {
            if (hasPage)
                Loaded = LoadPage(page);
            return Loaded;
        }

        private async Task LoadFirst()
        {
            StatusText = "Asking the Hub.";
            try
            {
                if (!loaded)
                {
                    filters = HubBrowse.ParseFilters(await browser.Info().ConfigureAwait(true));
                    loaded = true;
                    if (!filters.Sorts.Contains(sort))
                        sort = filters.Sorts[0];
                    if (!PayTypes.Contains(payType))
                        payType = All;
                    Changed(nameof(PayTypes));
                    Changed(nameof(Locations));
                    Changed(nameof(Types));
                    Changed(nameof(Sorts));
                    Changed(nameof(Tags));
                    // A box drops its selection while its list is empty, so each is told again.
                    Changed(nameof(PayType));
                    Changed(nameof(Location));
                    Changed(nameof(Type));
                    Changed(nameof(Tag));
                    Changed(nameof(Sort));
                }
            }
            catch (Exception ex)
            {
                StatusText = "The Hub couldn't be reached: " + ex.Message;
                return;
            }
            await LoadPage(page).ConfigureAwait(true);
        }

        private void Filter(ref string field, string? value)
        {
            if (value == null || value == field)
                return;
            field = value;
            GoTo(1);
        }

        private void GoTo(int number)
        {
            Loaded = LoadPage(Math.Max(1, number));
        }

        private void PickCreator(string name)
        {
            creator = name;
            creatorText = name;
            Changed(nameof(CreatorText));
            Changed(nameof(Creator));
            Changed(nameof(HasCreator));
            Suggestions = new string[0];
            GoTo(1);
        }

        // The page number only moves once that page has arrived, so a failed load can be tried again.
        private async Task LoadPage(int wanted)
        {
            int request = ++requests;
            StatusText = "Asking the Hub.";
            var query = new HubQuery
            {
                Location = location,
                PayType = payType,
                Type = type == All ? "" : type,
                Tag = tag == All ? "" : tag,
                Creator = creator,
                Search = search.Trim(),
                Sort = sort,
                Page = wanted,
            };
            try
            {
                string response = await browser.Resources(query).ConfigureAwait(true);
                Dictionary<string, int> owned = await Task.Run(NewestOwned).ConfigureAwait(true);
                HubPage result = await Task.Run(() => HubBrowse.ParsePage(response, name => owned.TryGetValue(name, out int version) ? version : -1)).ConfigureAwait(true);
                if (request != requests)
                    return;
                resources = result.Resources;
                hasPage = true;
                totalPages = result.TotalPages;
                totalFound = result.TotalFound;
                page = Math.Max(1, Math.Min(result.Page, Math.Max(result.TotalPages, 1)));
                Show();
            }
            catch (Exception ex)
            {
                if (request == requests)
                    StatusText = "The Hub couldn't be reached: " + ex.Message;
            }
        }

        private Dictionary<string, int> NewestOwned()
        {
            var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(storeFile))
                return owned;
            using LibraryStore store = LibraryStore.Open(storeFile);
            foreach (VarRecord record in store.LoadVars())
            {
                string package = record.CreatorName + "." + record.PackageName;
                int version = record.Version ?? 0;
                if (!owned.TryGetValue(package, out int known) || version > known)
                    owned[package] = version;
            }
            return owned;
        }

        private void Show()
        {
            List<HubResource> shown = notOwned ? resources.Where(item => item.Ownership != HubOwnership.Owned).ToList() : resources;
            var built = new List<HubRow>();
            int width = columns;
            for (int start = 0; start < shown.Count; start += width)
                built.Add(new HubRow(shown.Skip(start).Take(width).Select(item => new HubTile(item, images, thumbnails, ui)).ToList()));
            Rows = built;

            int hidden = resources.Count - shown.Count;
            // A resize can land here before the first page has come back.
            if (hasPage)
                StatusText = hidden > 0 ? $"{totalFound:N0} found, {hidden} on this page hidden as owned." : $"{totalFound:N0} found.";
            Changed(nameof(PageText));
            Changed(nameof(CanGoBack));
            Changed(nameof(CanGoOn));
        }

        private void Get(HubTile tile)
        {
            HubResource resource = tile.Resource;
            jobs.Start("Getting " + resource.Title, context =>
            {
                string detail = browser.Detail(resource.Id).GetAwaiter().GetResult();
                Dictionary<string, int> owned = NewestOwned();
                Func<string, bool> isOwned = name =>
                    owned.TryGetValue(MissingPlanner.PackageOf(name), out int version) && version >= HubParser.VersionOf(name);
                List<HubPackage> packages = HubBrowse.Downloads(detail, isOwned);
                Dictionary<string, string> linked = VarLinks.ReadInstalled(paths.AddonPackages);
                List<string> alsoLink = HubBrowse.Names(detail).Where(isOwned)
                    .Select(name => MissingPlanner.PackageOf(name) + "." + owned[MissingPlanner.PackageOf(name)])
                    .Where(name => !linked.ContainsKey(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (packages.Count + alsoLink.Count == 0)
                    return "You already have everything it needs.";
                return MissingCheck.GetPackages(packages, alsoLink, storeFile, paths, hub, MissingCheck.FreeSpace, context.Report, context.Token);
            });
        }

        private static IReadOnlyList<string> WithAll(List<string> list, bool addAll)
        {
            return addAll ? new[] { All }.Concat(list).ToList() : list;
        }
    }
}
