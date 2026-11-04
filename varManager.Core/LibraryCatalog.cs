using System;
using System.Collections.Generic;
using System.Linq;

namespace varManager
{
    public enum VarState
    {
        On,
        Off,
        MissingDeps
    }

    /// <summary>One package as the Library screen shows it.</summary>
    public sealed class LibraryItem
    {
        public string VarName { get; set; }
        public string Creator { get; set; }
        public string Package { get; set; }
        public int Version { get; set; }
        public string Type { get; set; }
        public VarState State { get; set; }
        public double FileSize { get; set; }
        public string PreviewPath { get; set; }
        public IReadOnlyList<string> Dependencies { get; set; } = new string[0];
    }

    public sealed class AuthorSuggestion
    {
        public AuthorSuggestion(string name, int packages)
        {
            Name = name;
            Packages = packages;
        }

        public string Name { get; }
        public int Packages { get; }
    }

    /// <summary>What the search box and the filter pills ask for. Null means no filter.</summary>
    public sealed class LibraryFilter
    {
        public string Text { get; set; } = "";
        public ICollection<string> Authors { get; set; } = new string[0];
        public string Type { get; set; }
        public VarState? State { get; set; }
    }

    /// <summary>A line of the gallery: an author header, or a run of tiles.</summary>
    public sealed class GalleryRow
    {
        public string Header { get; set; }
        public int HeaderCount { get; set; }
        public bool IsOpen { get; set; }
        public IReadOnlyList<LibraryItem> Items { get; set; } = new LibraryItem[0];
    }

    public static class LibraryCatalog
    {
        public const string Other = "Other";

        private const int MaxSuggestions = 6;

        public static readonly IReadOnlyList<string> Types = new[] { "Scene", "Look", "Plugin", "Hair", "Clothing", Other };

        public static string TypeOf(VarRecord record)
        {
            if (record.Scenes > 0) return "Scene";
            if (record.Looks > 0) return "Look";
            if (record.Plugins > 0) return "Plugin";
            if (record.Hairstyle > 0) return "Hair";
            if (record.Clothing > 0) return "Clothing";
            return Other;
        }

        /// <param name="linked">Names of the vars linked in the current profile. Link state belongs to the profile, not the store.</param>
        public static List<LibraryItem> Load(LibraryStore store, string varsPath, ISet<string> linked)
        {
            IReadOnlyList<VarRecord> records = store.LoadVars();
            var vars = new VarIndex(records.Select(v => new VarEntry(v.VarName, v.CreatorName, v.PackageName, v.Version ?? 0)));
            var dependencies = new DependencyIndex(store.LoadDependencies());

            var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScenePreview preview in store.LoadPreviews())
            {
                if (!previews.ContainsKey(preview.VarName))
                    previews[preview.VarName] = ScenePreviews.PicturePath(varsPath, preview.Type, preview.VarName, preview.Picture);
            }

            var items = new List<LibraryItem>(records.Count);
            foreach (VarRecord record in records)
            {
                IReadOnlyList<string> needs = dependencies.DependenciesOf(record.VarName);
                bool missing = DependencyAnalysis.Resolve(needs, vars).Any(r => r.VarName == null);
                string preview;
                previews.TryGetValue(record.VarName, out preview);
                items.Add(new LibraryItem
                {
                    VarName = record.VarName,
                    Creator = record.CreatorName,
                    Package = record.PackageName,
                    Version = record.Version ?? 0,
                    Type = TypeOf(record),
                    State = missing ? VarState.MissingDeps : linked.Contains(record.VarName) ? VarState.On : VarState.Off,
                    FileSize = record.FileSize ?? 0,
                    PreviewPath = preview,
                    Dependencies = needs,
                });
            }
            return items;
        }

        public static List<LibraryItem> Filter(IEnumerable<LibraryItem> items, LibraryFilter filter)
        {
            string text = filter.Text.Trim();
            var authors = new HashSet<string>(filter.Authors, StringComparer.OrdinalIgnoreCase);
            return items.Where(item =>
                    (authors.Count == 0 || authors.Contains(item.Creator))
                    && (filter.Type == null || item.Type == filter.Type)
                    && (filter.State == null || item.State == filter.State)
                    && (text.Length == 0 || item.VarName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
        }

        /// <summary>Authors matching the text, names that start with it first, then those with the most packages.</summary>
        public static List<AuthorSuggestion> SuggestAuthors(IEnumerable<LibraryItem> items, string text, ICollection<string> chosen)
        {
            string typed = text.Trim();
            if (typed.Length == 0)
                return new List<AuthorSuggestion>();
            var taken = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);
            return items.GroupBy(item => item.Creator, StringComparer.OrdinalIgnoreCase)
                .Where(author => !taken.Contains(author.Key) && author.Key.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(author => author.Key.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(author => author.Count())
                .ThenBy(author => author.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)
                .Select(author => new AuthorSuggestion(author.Key, author.Count()))
                .ToList();
        }

        /// <summary>Lays items out in rows. With two or more authors chosen, each gets a header that can close.</summary>
        public static List<GalleryRow> Rows(IReadOnlyList<LibraryItem> items, int columns, bool grouped, ISet<string> closed)
        {
            int width = Math.Max(1, columns);
            var rows = new List<GalleryRow>();
            if (!grouped)
            {
                AddTileRows(rows, items, width);
                return rows;
            }

            foreach (IGrouping<string, LibraryItem> author in items.GroupBy(item => item.Creator, StringComparer.OrdinalIgnoreCase))
            {
                List<LibraryItem> packages = author.ToList();
                bool open = !closed.Contains(author.Key);
                rows.Add(new GalleryRow { Header = author.Key, HeaderCount = packages.Count, IsOpen = open });
                if (open)
                    AddTileRows(rows, packages, width);
            }
            return rows;
        }

        private static void AddTileRows(List<GalleryRow> rows, IReadOnlyList<LibraryItem> items, int columns)
        {
            for (int start = 0; start < items.Count; start += columns)
                rows.Add(new GalleryRow { Items = items.Skip(start).Take(columns).ToList() });
        }
    }
}
