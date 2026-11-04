using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace varManager.Ui
{
    public sealed class MissingGroup
    {
        public MissingGroup(string title, int count, string text, IReadOnlyList<string> lines, string? actionLabel = null, ICommand? action = null)
        {
            Title = title;
            Count = count;
            Text = text;
            Lines = lines;
            ActionLabel = actionLabel ?? "";
            Action = action;
        }

        public string Title { get; }
        public int Count { get; }
        public string Text { get; }
        public IReadOnlyList<string> Lines { get; }
        public string ActionLabel { get; }
        public ICommand? Action { get; }
        public bool HasAction => Action != null && Count > 0;
        public bool HasLines => Lines.Count > 0;
    }

    /// <summary>Checks what the library needs and gets it in one click. Downloads stay Hub only.</summary>
    public sealed class MissingViewModel : Observable
    {
        private const string WholeLibrary = "Whole library";
        private const string ThisProfile = "This profile";
        private const int MostLines = 200;

        private readonly string storeFile;
        private readonly LibraryPaths paths;
        private readonly JobRunner jobs;
        private readonly IHubSource hub;
        private readonly Action<string> copy;
        private readonly Action<int> countChanged;
        private HashSet<string>? listed;
        private MissingState view = new MissingState();
        private IReadOnlyList<MissingGroup> groups = new MissingGroup[0];
        private MissingGroup? open;
        private bool profileOnly;
        private string title = "Checking";
        private string statusText = "";
        private int checks;

        public MissingViewModel(string storeFile, LibraryPaths paths, JobRunner jobs, IHubSource hub, Action<string> copy, Action<int> countChanged)
        {
            this.storeFile = storeFile;
            this.paths = paths;
            this.jobs = jobs;
            this.hub = hub;
            this.copy = copy;
            this.countChanged = countChanged;
            GetAllCommand = new Command(GetAll);
            ToggleLinesCommand = new ArgCommand<MissingGroup>(group => OpenGroup = open == group ? null : group);
            ScopePills = new[]
            {
                new Pill(WholeLibrary, new Command(() => SetScope(false))) { IsActive = true },
                new Pill(ThisProfile, new Command(() => SetScope(true))),
            };
        }

        public IReadOnlyList<Pill> ScopePills { get; }
        public ICommand GetAllCommand { get; }
        public ICommand ToggleLinesCommand { get; }

        /// <summary>The last check, for whoever needs to wait on it.</summary>
        public Task Checked { get; private set; } = Task.CompletedTask;

        public IReadOnlyList<MissingGroup> Groups
        {
            get => groups;
            private set
            {
                groups = value;
                Changed();
            }
        }

        public MissingGroup? OpenGroup
        {
            get => open;
            private set
            {
                open = value;
                Changed();
                Changed(nameof(OpenLines));
                Changed(nameof(HasOpenGroup));
            }
        }

        public bool HasOpenGroup => open != null;
        public IReadOnlyList<string> OpenLines => open?.Lines ?? new string[0];

        public string Title
        {
            get => title;
            private set
            {
                title = value;
                Changed();
            }
        }

        public string StatusText
        {
            get => statusText;
            private set
            {
                statusText = value;
                Changed();
            }
        }

        public bool CanGetAll => view.Sources.OnHub.Count > 0;
        public string GetAllLabel => "Get all " + view.Sources.OnHub.Count;

        /// <summary>Counts from the database alone, so this is cheap enough after every change.</summary>
        public async Task Count()
        {
            // Opening a store creates it, and an empty one would stop the Access import from ever running.
            if (!File.Exists(storeFile))
                return;
            int count = await Task.Run(() =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                return new MissingLibrary(store, paths).Plan(false).Count;
            }).ConfigureAwait(true);
            countChanged(count);
        }

        /// <summary>Asks the Hub, which only happens while this page is open.</summary>
        public Task Check()
        {
            Checked = RunCheck();
            return Checked;
        }

        private async Task RunCheck()
        {
            int check = ++checks;
            bool scope = profileOnly;
            if (!File.Exists(storeFile))
            {
                Title = "Nothing to check yet";
                StatusText = "Run Update DB first.";
                return;
            }
            StatusText = "Checking the Hub.";
            try
            {
                MissingState checkedView = await Task.Run(async () =>
                {
                    listed ??= MissingCheck.ListedPackages(paths.VamPath);
                    MissingLibrary library;
                    using (LibraryStore store = LibraryStore.Open(storeFile))
                        library = new MissingLibrary(store, paths);
                    return await MissingCheck.Run(library, scope, hub, listed, default).ConfigureAwait(false);
                }).ConfigureAwait(true);
                if (check != checks)
                    return;
                view = checkedView;
                Show();
            }
            catch (Exception ex)
            {
                // Nothing else would ever take "Checking" off the page.
                if (check != checks)
                    return;
                view = new MissingState();
                Show();
                Title = "Could not check";
                StatusText = ex.Message;
            }
        }

        private void SetScope(bool profile)
        {
            profileOnly = profile;
            foreach (Pill pill in ScopePills)
                pill.IsActive = pill.Label == (profile ? ThisProfile : WholeLibrary);
            _ = Check();
        }

        private void Show()
        {
            int ready = view.Sources.OnHub.Count;
            Title = ready > 0 ? $"{ready} packages ready on the Hub ({MissingCheck.Size(view.HubBytes)})"
                : view.Report.Count == 0 ? "Nothing is missing" : "Nothing to download from the Hub";
            StatusText = view.HubProblem ?? (ready > 0 ? "One click downloads, scans and links them to the current profile. You keep using the app." : "");
            Changed(nameof(CanGetAll));
            Changed(nameof(GetAllLabel));
            OpenGroup = null;

            MissingReport report = view.Report;
            MissingSources sources = view.Sources;
            Groups = new[]
            {
                new MissingGroup("Ready on the Hub", sources.OnHub.Count, "Downloaded, scanned and linked by Get all.", Lines(sources.OnHub)),
                new MissingGroup("Turned off in your library", report.TurnedOff.Count, "Already stored, so nothing to download.",
                    Lines(report.TurnedOff.Select(off => $"{off.Key}, needed by {Users(off.Value)}"), report.TurnedOff.Count), "Turn on all", new Command(TurnOnAll)),
                new MissingGroup("Updates on the Hub", view.Updates.Count, "Never automatic, because a new version can change a scene.",
                    Lines(view.Updates.Select(update => $"{update.Name}, {MissingCheck.Size(update.Size)}"), view.Updates.Count), "Get updates", new Command(GetUpdates)),
                new MissingGroup("Listed in SharpVaMTools", sources.Listed.Count, "Copy once. Files you add to the repository are found and scanned automatically.",
                    Lines(sources.Listed), "Copy list", new Command(CopyList)),
                new MissingGroup("Not found anywhere", sources.NotFound.Count, "Listed with what needs them.", Lines(sources.NotFound)),
                new MissingGroup("Already covered", report.Covered.Count, "You own a newer version, so these are skipped.", Lines(report.Covered, report.Covered.Count)),
            };
        }

        private static IReadOnlyList<string> Lines(IEnumerable<MissingPackage> packages)
        {
            List<MissingPackage> all = packages.ToList();
            return Lines(all.Select(item => $"{item.Reference}, needed by {Users(item.NeededBy)}"), all.Count);
        }

        private static IReadOnlyList<string> Lines(IEnumerable<string> lines, int count)
        {
            List<string> shown = lines.Take(MostLines).ToList();
            if (count > MostLines)
                shown.Add($"And {count - MostLines:N0} more.");
            return shown;
        }

        private static string Users(ICollection<string> users)
        {
            string? first = users.FirstOrDefault();
            return users.Count <= 1 ? first ?? "" : $"{first} and {users.Count - 1} more";
        }

        private void GetAll()
        {
            bool scope = profileOnly;
            HashSet<string> names = listed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            jobs.Start("Getting packages from the Hub", context =>
                MissingCheck.GetAll(storeFile, paths, scope, hub, names, MissingCheck.FreeSpace, context.Report, context.Token));
        }

        private void GetUpdates()
        {
            bool scope = profileOnly;
            HashSet<string> names = listed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            jobs.Start("Getting updates from the Hub", context =>
                MissingCheck.GetUpdates(storeFile, paths, scope, hub, names, MissingCheck.FreeSpace, context.Report, context.Token));
        }

        private void TurnOnAll()
        {
            bool scope = profileOnly;
            jobs.Start("Turning on stored vars", context => MissingCheck.TurnOnAll(storeFile, paths, scope, context.Report, context.Token));
        }

        private void CopyList()
        {
            copy(string.Join("\r\n", view.Sources.Listed.Select(item => item.Reference)));
            jobs.Say($"List of {view.Sources.Listed.Count} copied.");
        }
    }
}
