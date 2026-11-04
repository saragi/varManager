using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace varManager.Ui
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Changed([CallerMemberName] string property = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    public sealed class ArgCommand<T> : ICommand
    {
        private readonly Action<T> run;

        public ArgCommand(Action<T> run)
        {
            this.run = run;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run((T)parameter!);
    }

    public sealed class Pill : Observable
    {
        private bool isActive;

        public Pill(string label, ICommand command)
        {
            Label = label;
            Command = command;
        }

        public string Label { get; }
        public ICommand Command { get; }

        public bool IsActive
        {
            get => isActive;
            set
            {
                isActive = value;
                Changed();
            }
        }
    }

    /// <summary>A gallery entry whose picture is asked for only when it is on screen.</summary>
    public abstract class PictureTile : Observable
    {
        private readonly string? previewPath;
        private readonly ThumbnailLoader thumbnails;
        private readonly SynchronizationContext? ui;
        private WeakReference<ImageSource>? picture;
        private bool loading;
        private bool unreadable;

        protected PictureTile(string? previewPath, ThumbnailLoader thumbnails, SynchronizationContext? ui)
        {
            this.previewPath = previewPath;
            this.thumbnails = thumbnails;
            this.ui = ui;
        }

        // Held weakly, so scrolling a long way doesn't keep every picture alive.
        public ImageSource? Thumbnail
        {
            get
            {
                if (picture != null && picture.TryGetTarget(out ImageSource? image))
                    return image;
                if (!loading && !unreadable && previewPath != null)
                {
                    loading = true;
                    _ = thumbnails.Load(previewPath, Show);
                }
                return null;
            }
        }

        private void Show(ImageSource? image)
        {
            void Apply()
            {
                loading = false;
                if (image == null)
                {
                    unreadable = true;
                    return;
                }
                picture = new WeakReference<ImageSource>(image);
                Changed(nameof(Thumbnail));
            }

            if (ui == null)
                Apply();
            else
                ui.Post(_ => Apply(), null);
        }
    }

    /// <summary>A package in the gallery.</summary>
    public sealed class LibraryTile : PictureTile
    {
        private bool isSelected;

        public LibraryTile(LibraryItem item, ThumbnailLoader thumbnails, SynchronizationContext? ui)
            : base(item.PreviewPath, thumbnails, ui)
        {
            Item = item;
        }

        public LibraryItem Item { get; }
        public string Title => Item.Package;
        public string Caption => Item.Creator + " · " + Item.Type;
        public string StateText => LibraryViewModel.StateText(Item.State);
        public string StateKey => Item.State.ToString();
        public bool HasMissing => Item.State == VarState.MissingDeps;

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                isSelected = value;
                Changed();
            }
        }
    }

    public sealed class TileRow
    {
        public TileRow(IReadOnlyList<LibraryTile> tiles)
        {
            Tiles = tiles;
        }

        public IReadOnlyList<LibraryTile> Tiles { get; }
    }

    public sealed class AuthorHeaderRow
    {
        public AuthorHeaderRow(string name, int count, bool isOpen)
        {
            Name = name;
            Summary = count == 1 ? "1 package" : count + " packages";
            Chevron = isOpen ? "M6 9l6 6 6-6" : "M9 6l6 6-6 6";
        }

        public string Name { get; }
        public string Summary { get; }
        public string Chevron { get; }
    }

    public sealed class DependencyLine
    {
        public DependencyLine(string name, string state, string stateKey)
        {
            Name = name;
            State = state;
            StateKey = stateKey;
        }

        public string Name { get; }
        public string State { get; }
        public string StateKey { get; }
    }

    public sealed class MoveLine
    {
        public MoveLine(string name, string state, bool moves)
        {
            Name = name;
            State = state;
            StateKey = moves ? "MissingDeps" : "On";
        }

        public string Name { get; }
        public string State { get; }
        public string StateKey { get; }
    }

    /// <summary>The Library screen: one search box, filter pills, a gallery and a detail panel.</summary>
    public sealed class LibraryViewModel : Observable
    {
        private const string All = "All";

        private readonly ThumbnailLoader thumbnails = new ThumbnailLoader();
        private readonly JobRunner jobs;
        private readonly LibraryPaths paths;
        private readonly Action<IReadOnlyList<string>> tempLinksStale;
        private readonly SynchronizationContext? ui;
        private readonly Func<IReadOnlyCollection<string>> linkedVars;
        private readonly string storeFile;
        private readonly string varsPath;
        private readonly HashSet<string> closedAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<LibraryItem> items = new List<LibraryItem>();
        private Dictionary<string, LibraryTile> tiles = new Dictionary<string, LibraryTile>(StringComparer.OrdinalIgnoreCase);
        private VarIndex index = new VarIndex(new VarEntry[0]);
        private DependencyIndex dependencies = new DependencyIndex(new KeyValuePair<string, string>[0]);
        private LibraryUsage usage = new LibraryUsage(new VarIndex(new VarEntry[0]), new KeyValuePair<string, string>[0]);
        private MovePlan? movePlan;
        private string? undoFolder;
        private ISet<string> linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<LibraryItem> shown = new List<LibraryItem>();
        private string query = "";
        private string? typeFilter;
        private VarState? stateFilter;
        private int columns = 1;
        private LibraryTile? selected;
        private string shownText = "";
        private IReadOnlyList<object> rows = new object[0];
        private int reloads;

        public LibraryViewModel(string storeFile, LibraryPaths paths, JobRunner jobs, Func<IReadOnlyCollection<string>> linkedVars,
            Action<IReadOnlyList<string>> tempLinksStale, SynchronizationContext? ui)
        {
            this.tempLinksStale = tempLinksStale;
            this.storeFile = storeFile;
            this.paths = paths;
            this.jobs = jobs;
            varsPath = paths.VarsPath;
            this.linkedVars = linkedVars;
            this.ui = ui;
            undoFolder = MoveOut.LatestHolding(varsPath);
            ToggleCommand = new Command(Toggle);
            LoadCommand = new Command(Load);
            PlanMoveCommand = new Command(PlanMove);
            ConfirmMoveCommand = new Command(ConfirmMove);
            CancelMoveCommand = new Command(() => MovePlan = null);
            UndoMoveCommand = new Command(UndoMove);
            AddAuthorCommand = new ArgCommand<string>(AddAuthor);
            RemoveAuthorCommand = new ArgCommand<string>(RemoveAuthor);
            ToggleAuthorCommand = new ArgCommand<string>(ToggleAuthor);
            SelectCommand = new ArgCommand<LibraryTile>(Select);

            TypePills.Add(new Pill(All, new Command(() => SetType(null))) { IsActive = true });
            foreach (string type in LibraryCatalog.Types)
                TypePills.Add(new Pill(type, new Command(() => SetType(type))));
            StatePills.Add(new Pill(All, new Command(() => SetState(null))) { IsActive = true });
            foreach (VarState state in Enum.GetValues<VarState>())
                StatePills.Add(new Pill(StateText(state), new Command(() => SetState(state))));
        }

        public ObservableCollection<Pill> TypePills { get; } = new ObservableCollection<Pill>();
        public ObservableCollection<Pill> StatePills { get; } = new ObservableCollection<Pill>();
        public ObservableCollection<string> AuthorTags { get; } = new ObservableCollection<string>();
        public ObservableCollection<AuthorSuggestion> Suggestions { get; } = new ObservableCollection<AuthorSuggestion>();
        public ObservableCollection<DependencyLine> SelectedDependencies { get; } = new ObservableCollection<DependencyLine>();

        public ICommand AddAuthorCommand { get; }
        public ICommand RemoveAuthorCommand { get; }
        public ICommand ToggleAuthorCommand { get; }
        public ICommand SelectCommand { get; }
        public ICommand ToggleCommand { get; }
        public ICommand LoadCommand { get; }
        public ICommand PlanMoveCommand { get; }
        public ICommand ConfirmMoveCommand { get; }
        public ICommand CancelMoveCommand { get; }
        public ICommand UndoMoveCommand { get; }
        public ObservableCollection<MoveLine> MoveLines { get; } = new ObservableCollection<MoveLine>();

        // Replaced whole, because adding 1,500 rows one by one would raise 1,500 changes on every keystroke.
        public IReadOnlyList<object> Rows
        {
            get => rows;
            private set
            {
                rows = value;
                Changed();
            }
        }

        public int LinkedCount => linked.Count;
        public bool HasSuggestions => Suggestions.Count > 0;
        public bool HasSelection => selected != null;
        public string SearchHint => AuthorTags.Count == 0 ? "Search packages and authors" : "Add another author, or filter by name";

        public string ShownText
        {
            get => shownText;
            private set
            {
                shownText = value;
                Changed();
            }
        }

        public string Query
        {
            get => query;
            set
            {
                query = value;
                Changed();
                RefreshSuggestions();
                Apply();
            }
        }

        public int Columns
        {
            get => columns;
            set
            {
                if (value == columns || value < 1)
                    return;
                columns = value;
                BuildRows();
            }
        }

        public LibraryTile? Selected
        {
            get => selected;
            private set
            {
                if (selected != null)
                    selected.IsSelected = false;
                selected = value;
                if (selected != null)
                    selected.IsSelected = true;
                // A reload picks the same package again, and that mustn't lose its plan.
                if (movePlan != null && !string.Equals(movePlan.Target, value?.Item.VarName, StringComparison.OrdinalIgnoreCase))
                    MovePlan = null;
                RefreshDependencies();
                Changed();
                Changed(nameof(HasSelection));
                Changed(nameof(ToggleLabel));
                Changed(nameof(CanLoad));
                Changed(nameof(CanMove));
                Changed(nameof(BlockedText));
                Changed(nameof(IsBlocked));
                Changed(nameof(SelectedSize));
                Changed(nameof(SelectedNeeds));
                Changed(nameof(SelectedState));
            }
        }

        public string SelectedSize => selected == null ? "" : FormatSize(selected.Item.FileSize);
        public string SelectedState => selected == null ? "" : StateText(selected.Item.State);

        public string SelectedNeeds
        {
            get
            {
                int count = selected?.Item.Dependencies.Count ?? 0;
                return count == 0 ? "Nothing else" : count == 1 ? "1 var" : count + " vars";
            }
        }

        public bool IsOn => selected != null && linked.Contains(selected.Item.VarName);

        /// <summary>Says how many other vars come along before anything happens.</summary>
        public string ToggleLabel
        {
            get
            {
                if (selected == null)
                    return "";
                if (IsOn)
                {
                    int others = LinkedImplicated(selected.Item.VarName).Count - 1;
                    return others <= 0 ? "Turn off" : others == 1 ? "Turn off, with 1 var that needs it" : $"Turn off, with {others} vars that need it";
                }
                int more = NotLinkedClosure(selected.Item.VarName).Count - 1;
                return more <= 0 ? "Turn on" : more == 1 ? "Turn on, with 1 dependency" : $"Turn on, with {more} dependencies";
            }
        }

        public bool CanLoad => selected?.Item.FirstScene != null;
        public bool IsBlocked => selected != null && usage.NeedersOf(selected.Item.VarName).Any();
        public bool CanMove => selected != null && !IsBlocked && movePlan == null;
        public bool HasMovePlan => movePlan != null;
        public bool CanUndo => undoFolder != null;

        public string BlockedText
        {
            get
            {
                int count = selected == null ? 0 : usage.NeedersOf(selected.Item.VarName).Count();
                return count == 1 ? "Can't move out: 1 other var needs this package." : $"Can't move out: {count} other vars need this package.";
            }
        }

        public string MoveWarning => movePlan?.TargetInUseOutsideVars == true
            ? "A profile or a scene in Saves uses this package, and will lose it."
            : "";

        public string ConfirmMoveLabel
        {
            get
            {
                int count = movePlan?.Moves.Count ?? 0;
                return count == 1 ? "Move 1 var" : $"Move {count} vars";
            }
        }

        private MovePlan? MovePlan
        {
            get => movePlan;
            set
            {
                movePlan = value;
                RefreshMoveLines();
                Changed(nameof(HasMovePlan));
                Changed(nameof(CanMove));
                Changed(nameof(MoveWarning));
                Changed(nameof(ConfirmMoveLabel));
            }
        }

        public static string StateText(VarState state)
        {
            return state == VarState.On ? "On" : state == VarState.Off ? "Off" : "Missing deps";
        }

        public static string FormatSize(double bytes)
        {
            double megabytes = bytes / (1024 * 1024);
            return megabytes >= 1024 ? $"{megabytes / 1024:0.0} GB" : $"{megabytes:0.#} MB";
        }

        /// <summary>Reads the library off the UI thread. A store that doesn't exist yet is an empty library.</summary>
        public async Task Reload()
        {
            int reload = ++reloads;
            (ISet<string> names, List<LibraryItem> loaded, VarIndex newIndex, DependencyIndex newDependencies, LibraryUsage newUsage) = await Task.Run(() =>
            {
                ISet<string> found = new HashSet<string>(linkedVars(), StringComparer.OrdinalIgnoreCase);
                List<LibraryItem> read = new List<LibraryItem>();
                if (File.Exists(storeFile))
                {
                    using LibraryStore store = LibraryStore.Open(storeFile);
                    read = LibraryCatalog.Load(store, varsPath, found);
                }
                var vars = new VarIndex(read.Select(i => new VarEntry(i.VarName, i.Creator, i.Package, i.Version)));
                List<KeyValuePair<string, string>> pairs = read
                    .SelectMany(i => i.Dependencies.Select(d => new KeyValuePair<string, string>(i.VarName, d)))
                    .ToList();
                return (found, read, vars, new DependencyIndex(pairs), new LibraryUsage(vars, pairs));
            }).ConfigureAwait(true);

            // An older reload that finishes late has stale link state.
            if (reload != reloads)
                return;

            string? keep = selected?.Item.VarName;
            items = loaded;
            linked = names;
            Changed(nameof(LinkedCount));
            index = newIndex;
            dependencies = newDependencies;
            usage = newUsage;
            tiles = items.ToDictionary(i => i.VarName, i => new LibraryTile(i, thumbnails, ui), StringComparer.OrdinalIgnoreCase);
            Selected = keep != null && tiles.TryGetValue(keep, out LibraryTile? tile) ? tile : null;
            RefreshSuggestions();
            Apply();
        }

        /// <summary>Closes the list until the user types again.</summary>
        public void HideSuggestions()
        {
            Suggestions.Clear();
            Changed(nameof(HasSuggestions));
        }

        /// <summary>Enter takes the first suggested author.</summary>
        public void AcceptFirstSuggestion()
        {
            if (Suggestions.Count > 0)
                AddAuthor(Suggestions[0].Name);
        }

        /// <summary>Backspace in an empty box takes off the last author.</summary>
        public void RemoveLastAuthor()
        {
            if (query.Length == 0 && AuthorTags.Count > 0)
                RemoveAuthor(AuthorTags[^1]);
        }

        private List<string> NotLinkedClosure(string varName)
        {
            return DependencyAnalysis.WithDependencies(new[] { varName }, index, dependencies)
                .Where(name => !linked.Contains(name))
                .ToList();
        }

        private List<string> LinkedImplicated(string varName)
        {
            return DependencyAnalysis.ImplicatedVars(new[] { varName }, index, dependencies)
                .Where(linked.Contains)
                .ToList();
        }

        private void Toggle()
        {
            if (selected == null)
                return;
            string varName = selected.Item.VarName;
            Dictionary<string, string> files = items.ToDictionary(i => i.VarName, i => i.FilePath, StringComparer.OrdinalIgnoreCase);
            if (IsOn)
            {
                List<string> off = LinkedImplicated(varName);
                jobs.Start("Turning off", context =>
                {
                    Dictionary<string, string> installed = VarLinks.Installed(paths.AddonPackages);
                    int removed = off.Count(name => VarLinks.Remove(installed, name));
                    VamBridge.RequestRescan(paths.VamPath);
                    return removed == 1 ? "Turned off 1 var." : $"Turned off {removed} vars.";
                });
                return;
            }

            List<string> on = NotLinkedClosure(varName);
            jobs.Start("Turning on", context =>
            {
                Directory.CreateDirectory(Path.Combine(paths.AddonPackages, LibraryFolders.VarsLink));
                int failed = 0;
                foreach (string name in on)
                {
                    if (VarLinks.Install(paths.AddonPackages, name, files[name], false, out _) == VarLinkResult.Failed)
                        failed++;
                }
                VamBridge.RequestRescan(paths.VamPath);
                int done = on.Count - failed;
                string text = done == 1 ? "Turned on 1 var." : $"Turned on {done} vars.";
                return failed == 0 ? text : $"{text} {failed} could not be linked.";
            });
        }

        private void Load()
        {
            if (selected?.Item.FirstScene == null)
                return;
            LibraryItem item = selected.Item;
            jobs.Start("Loading in VaM", context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                tempLinksStale(SceneLauncher.Load(store, paths, item.VarName, item.FirstScene!, "scenes"));
                return $"Asked VaM to load {item.Package}.";
            });
        }

        private void PlanMove()
        {
            if (selected == null)
                return;
            string target = selected.Item.VarName;
            jobs.Start("Checking what can move", context =>
            {
                var problems = new List<string>();
                using LibraryStore store = LibraryStore.Open(storeFile);
                MovePlan plan = MoveOut.PlanNow(store, target, paths, problems.Add);
                ShowOnUi(() =>
                {
                    if (selected?.Item.VarName == target)
                        MovePlan = plan;
                });
                return problems.Count == 0 ? "Check the list before moving." : $"Check the list before moving. {problems.Count} saves could not be read.";
            });
        }

        private void ConfirmMove()
        {
            if (movePlan == null)
                return;
            MovePlan plan = movePlan;
            Dictionary<string, string> files = items.ToDictionary(i => i.VarName, i => i.FilePath, StringComparer.OrdinalIgnoreCase);
            Task? started = jobs.Start("Moving out", context =>
            {
                using LibraryStore store = LibraryStore.Open(storeFile);
                // The plan was made earlier. Something new may need these vars by now.
                if (!MoveOut.SameMoves(plan, MoveOut.PlanNow(store, plan.Target, paths, _ => { })))
                    return "The library changed, so nothing moved. Check again.";
                string holding = MoveOut.Apply(plan, name => files[name], paths);
                ShowOnUi(() => SetUndo(holding));
                store.DeleteVars(plan.Moves);
                VamBridge.RequestRescan(paths.VamPath);
                return plan.Moves.Count == 1 ? "Moved 1 var to the holding folder." : $"Moved {plan.Moves.Count} vars to the holding folder.";
            });

            // A plan stays on screen if another job kept this one from starting.
            if (started != null)
                MovePlan = null;
        }

        private void UndoMove()
        {
            string? holding = undoFolder;
            if (holding == null)
                return;
            jobs.Start("Undoing move", context =>
            {
                List<string> problems = MoveOut.Undo(holding);
                using (LibraryStore store = LibraryStore.Open(storeFile))
                    LibraryUpdate.Run(store, paths, context.Report, context.Token);
                ShowOnUi(() => SetUndo(MoveOut.LatestHolding(varsPath)));
                return problems.Count == 0 ? "Move undone." : $"Move undone, except {problems.Count} files: {problems[0]}";
            });
        }

        private void SetUndo(string? folder)
        {
            undoFolder = folder;
            Changed(nameof(CanUndo));
        }

        private void ShowOnUi(Action action)
        {
            if (ui == null)
                action();
            else
                ui.Post(_ => action(), null);
        }

        private void RefreshMoveLines()
        {
            MoveLines.Clear();
            if (movePlan == null)
                return;
            foreach (string name in movePlan.Moves)
                MoveLines.Add(new MoveLine(name, name == movePlan.Target ? "Moves" : "Moves, only used here", true));
            foreach (KeyValuePair<string, string> stay in movePlan.Stays)
                MoveLines.Add(new MoveLine(stay.Key, stay.Value, false));
        }

        private void AddAuthor(string name)
        {
            if (!AuthorTags.Contains(name, StringComparer.OrdinalIgnoreCase))
                AuthorTags.Add(name);
            query = "";
            Changed(nameof(Query));
            Changed(nameof(SearchHint));
            RefreshSuggestions();
            Apply();
        }

        private void RemoveAuthor(string name)
        {
            AuthorTags.Remove(name);
            Changed(nameof(SearchHint));
            RefreshSuggestions();
            Apply();
        }

        private void ToggleAuthor(string name)
        {
            if (!closedAuthors.Remove(name))
                closedAuthors.Add(name);
            BuildRows();
        }

        private void Select(LibraryTile tile)
        {
            Selected = tile;
        }

        private void SetType(string? type)
        {
            typeFilter = type;
            foreach (Pill pill in TypePills)
                pill.IsActive = pill.Label == (type ?? All);
            Apply();
        }

        private void SetState(VarState? state)
        {
            stateFilter = state;
            string label = state == null ? All : StateText(state.Value);
            foreach (Pill pill in StatePills)
                pill.IsActive = pill.Label == label;
            Apply();
        }

        private void RefreshSuggestions()
        {
            Suggestions.Clear();
            foreach (AuthorSuggestion suggestion in LibraryCatalog.SuggestAuthors(items, query, AuthorTags))
                Suggestions.Add(suggestion);
            Changed(nameof(HasSuggestions));
        }

        private void Apply()
        {
            shown = LibraryCatalog.Filter(items, new LibraryFilter
            {
                Text = query,
                Authors = AuthorTags,
                Type = typeFilter,
                State = stateFilter,
            });
            ShownText = $"{shown.Count:N0} of {items.Count:N0} packages";
            BuildRows();
        }

        private void BuildRows()
        {
            var built = new List<object>();
            foreach (GalleryRow row in LibraryCatalog.Rows(shown, columns, AuthorTags.Count > 1, closedAuthors))
            {
                if (row.Header != null)
                    built.Add(new AuthorHeaderRow(row.Header, row.HeaderCount, row.IsOpen));
                else
                    built.Add(new TileRow(row.Items.Select(item => tiles[item.VarName]).ToList()));
            }
            Rows = built;
        }

        private void RefreshDependencies()
        {
            SelectedDependencies.Clear();
            if (selected == null)
                return;
            foreach (ResolvedReference reference in DependencyAnalysis.Resolve(selected.Item.Dependencies, index))
            {
                if (reference.VarName == null)
                    SelectedDependencies.Add(new DependencyLine(reference.Reference, "Missing", "MissingDeps"));
                else if (linked.Contains(reference.VarName))
                    SelectedDependencies.Add(new DependencyLine(reference.VarName, "Linked", "On"));
                else
                    SelectedDependencies.Add(new DependencyLine(reference.VarName, "Off", "Off"));
            }
        }
    }
}
