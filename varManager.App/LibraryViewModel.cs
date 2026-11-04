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

    /// <summary>A package in the gallery. Its picture is asked for only when the tile is on screen.</summary>
    public sealed class LibraryTile : Observable
    {
        private readonly ThumbnailLoader thumbnails;
        private readonly SynchronizationContext? ui;
        private WeakReference<ImageSource>? picture;
        private bool loading;
        private bool isSelected;

        public LibraryTile(LibraryItem item, ThumbnailLoader thumbnails, SynchronizationContext? ui)
        {
            Item = item;
            this.thumbnails = thumbnails;
            this.ui = ui;
        }

        public LibraryItem Item { get; }
        public string Title => Item.Package;
        public string Caption => Item.Creator + " · " + Item.Type;
        public string StateText => LibraryViewModel.StateText(Item.State);
        public string StateKey => Item.State.ToString();

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                isSelected = value;
                Changed();
            }
        }

        // Held weakly, so scrolling a long way doesn't keep every picture alive.
        public ImageSource? Thumbnail
        {
            get
            {
                if (picture != null && picture.TryGetTarget(out ImageSource? image))
                    return image;
                if (!loading && Item.PreviewPath != null)
                {
                    loading = true;
                    _ = thumbnails.Load(Item.PreviewPath, Show);
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
                    return;
                picture = new WeakReference<ImageSource>(image);
                Changed(nameof(Thumbnail));
            }

            if (ui == null)
                Apply();
            else
                ui.Post(_ => Apply(), null);
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

    /// <summary>The Library screen: one search box, filter pills, a gallery and a detail panel.</summary>
    public sealed class LibraryViewModel : Observable
    {
        private const string All = "All";

        private readonly ThumbnailLoader thumbnails = new ThumbnailLoader();
        private readonly SynchronizationContext? ui;
        private readonly Func<IReadOnlySet<string>> linkedVars;
        private readonly string storeFile;
        private readonly string varsPath;
        private readonly HashSet<string> closedAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<LibraryItem> items = new List<LibraryItem>();
        private Dictionary<string, LibraryTile> tiles = new Dictionary<string, LibraryTile>(StringComparer.OrdinalIgnoreCase);
        private VarIndex index = new VarIndex(new VarEntry[0]);
        private ISet<string> linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<LibraryItem> shown = new List<LibraryItem>();
        private string query = "";
        private string? typeFilter;
        private VarState? stateFilter;
        private int columns = 1;
        private LibraryTile? selected;
        private string shownText = "";

        public LibraryViewModel(string storeFile, string varsPath, Func<IReadOnlySet<string>> linkedVars, SynchronizationContext? ui)
        {
            this.storeFile = storeFile;
            this.varsPath = varsPath;
            this.linkedVars = linkedVars;
            this.ui = ui;
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
        public ObservableCollection<object> Rows { get; } = new ObservableCollection<object>();
        public ObservableCollection<DependencyLine> SelectedDependencies { get; } = new ObservableCollection<DependencyLine>();

        public ICommand AddAuthorCommand { get; }
        public ICommand RemoveAuthorCommand { get; }
        public ICommand ToggleAuthorCommand { get; }
        public ICommand SelectCommand { get; }

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
                RefreshDependencies();
                Changed();
                Changed(nameof(HasSelection));
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
            string? keep = selected?.Item.VarName;
            ISet<string> names = new HashSet<string>(linkedVars(), StringComparer.OrdinalIgnoreCase);
            List<LibraryItem> loaded = await Task.Run(() =>
            {
                if (!File.Exists(storeFile))
                    return new List<LibraryItem>();
                using LibraryStore store = LibraryStore.Open(storeFile);
                return LibraryCatalog.Load(store, varsPath, names);
            }).ConfigureAwait(true);

            items = loaded;
            linked = names;
            index = new VarIndex(items.Select(i => new VarEntry(i.VarName, i.Creator, i.Package, i.Version)));
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
            Rows.Clear();
            foreach (GalleryRow row in LibraryCatalog.Rows(shown, columns, AuthorTags.Count > 1, closedAuthors))
            {
                if (row.Header != null)
                    Rows.Add(new AuthorHeaderRow(row.Header, row.HeaderCount, row.IsOpen));
                else
                    Rows.Add(new TileRow(row.Items.Select(item => tiles[item.VarName]).ToList()));
            }
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
