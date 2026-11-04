using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using varManager.Ui;
using Xunit;

namespace varManager.App.Tests
{
    public sealed class LibraryTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "vmlibrary-" + Guid.NewGuid().ToString("N"));
        private readonly string storeFile;
        private readonly HashSet<string> linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public LibraryTests()
        {
            Directory.CreateDirectory(root);
            storeFile = Path.Combine(root, "library.db");
        }

        public void Dispose()
        {
            Directory.Delete(root, true);
        }

        private static LibraryItem Item(string name, string type = "Scene", VarState state = VarState.Off)
        {
            string[] parts = name.Split('.');
            return new LibraryItem { VarName = name, Creator = parts[0], Package = parts[1], Version = int.Parse(parts[2]), Type = type, State = state };
        }

        private void Save(string name, Action<VarScan>? change = null, params string[] dependencies)
        {
            string[] parts = name.Split('.');
            var scan = new VarScan { CreatorName = parts[0], PackageName = parts[1], Version = int.Parse(parts[2]), FileSize = 3 * 1024 * 1024 };
            scan.Dependencies.AddRange(dependencies);
            change?.Invoke(scan);
            using LibraryStore store = LibraryStore.Open(storeFile);
            store.SaveVar(name, "x", scan);
        }

        private async Task<LibraryViewModel> Library(int columns = 4)
        {
            var library = new LibraryViewModel(storeFile, new LibraryPaths(root, root, root), new JobRunner(null), () => linked, _ => { }, null);
            await library.Reload();
            library.Columns = columns;
            return library;
        }

        private static List<string> Shown(LibraryViewModel library)
        {
            return library.Rows.OfType<TileRow>().SelectMany(row => row.Tiles).Select(tile => tile.Item.VarName).ToList();
        }

        private static string PngFile(string path)
        {
            var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Gray8, null, new byte[16], 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(path);
            encoder.Save(file);
            return path;
        }

        [Theory]
        [InlineData(1, 1, 1, 1, 1, "Scene")]
        [InlineData(0, 1, 1, 1, 1, "Look")]
        [InlineData(0, 0, 1, 1, 1, "Plugin")]
        [InlineData(0, 0, 0, 1, 1, "Hair")]
        [InlineData(0, 0, 0, 0, 1, "Clothing")]
        [InlineData(0, 0, 0, 0, 0, "Other")]
        public void A_var_takes_its_main_type(int scenes, int looks, int plugins, int hair, int clothing, string type)
        {
            var record = new VarRecord { Scenes = scenes, Looks = looks, Plugins = plugins, Hairstyle = hair, Clothing = clothing };

            Assert.Equal(type, LibraryCatalog.TypeOf(record));
        }

        [Fact]
        public void A_var_with_no_counts_is_other()
        {
            Assert.Equal("Other", LibraryCatalog.TypeOf(new VarRecord()));
        }

        [Fact]
        public void Load_works_out_state_preview_and_dependencies()
        {
            Save("A.B.1", scan =>
            {
                scan.Scenes = 1;
                scan.Items.Add(new VarItem { Type = "scenes", EntryName = "s/a.json", Preview = "first.jpg" });
                scan.Items.Add(new VarItem { Type = "scenes", EntryName = "s/b.json", Preview = "second.jpg" });
            }, "C.D.latest");
            Save("C.D.1");
            Save("E.F.1", null, "Gone.Pack.1");
            using LibraryStore store = LibraryStore.Open(storeFile);

            List<LibraryItem> items = LibraryCatalog.Load(store, root, new HashSet<string> { "A.B.1", "E.F.1" });

            LibraryItem a = items.Single(i => i.VarName == "A.B.1");
            Assert.Equal(VarState.On, a.State);
            Assert.Equal("Scene", a.Type);
            Assert.Equal(ScenePreviews.PicturePath(root, "scenes", "A.B.1", "first.jpg"), a.PreviewPath);
            Assert.Equal(new[] { "C.D.latest" }, a.Dependencies);
            LibraryItem c = items.Single(i => i.VarName == "C.D.1");
            Assert.Equal(VarState.Off, c.State);
            Assert.Null(c.PreviewPath);
            Assert.Equal(VarState.MissingDeps, items.Single(i => i.VarName == "E.F.1").State);
        }

        [Fact]
        public void A_substitute_version_does_not_count_as_missing()
        {
            Save("A.B.1", null, "C.D.9");
            Save("C.D.1");
            using LibraryStore store = LibraryStore.Open(storeFile);

            List<LibraryItem> items = LibraryCatalog.Load(store, root, new HashSet<string>());

            Assert.Equal(VarState.Off, items.Single(i => i.VarName == "A.B.1").State);
        }

        [Fact]
        public void Filter_combines_text_authors_type_and_state()
        {
            var items = new[] { Item("Ann.Hair.1", "Hair", VarState.On), Item("Ann.Coat.1", "Clothing"), Item("Bob.Hair.1", "Hair"), Item("Cy.Hair.1", "Hair", VarState.On) };

            Assert.Equal(3, LibraryCatalog.Filter(items, new LibraryFilter { Text = " HAIR " }).Count);
            Assert.Equal(new[] { "Ann.Hair.1", "Ann.Coat.1", "Bob.Hair.1" },
                LibraryCatalog.Filter(items, new LibraryFilter { Authors = new[] { "ann", "BOB" } }).Select(i => i.VarName));
            Assert.Equal(new[] { "Ann.Hair.1" },
                LibraryCatalog.Filter(items, new LibraryFilter { Text = "hair", Authors = new[] { "Ann", "Bob" }, State = VarState.On, Type = "Hair" }).Select(i => i.VarName));
        }

        [Fact]
        public void Suggestions_put_prefix_matches_first_then_the_biggest()
        {
            var items = new[] { Item("Lana.A.1"), Item("Lana.B.1"), Item("Lana.C.1"), Item("Alan.A.1"), Item("Alan.B.1"), Item("Lani.A.1") };

            Assert.Equal(new[] { "Lana", "Lani", "Alan" },
                LibraryCatalog.SuggestAuthors(items, "la", new string[0]).Select(s => s.Name));
            Assert.Equal(3, LibraryCatalog.SuggestAuthors(items, "lana", new string[0])[0].Packages);
        }

        [Fact]
        public void Suggestions_skip_chosen_authors_blank_text_and_stop_at_six()
        {
            var items = Enumerable.Range(1, 9).Select(i => Item($"Ann{i}.Pack.1")).ToList();

            Assert.Equal(6, LibraryCatalog.SuggestAuthors(items, "ann", new string[0]).Count);
            Assert.DoesNotContain("Ann1", LibraryCatalog.SuggestAuthors(items, "ann", new[] { "ANN1" }).Select(s => s.Name));
            Assert.Empty(LibraryCatalog.SuggestAuthors(items, "  ", new string[0]));
        }

        [Fact]
        public void Rows_wrap_at_the_column_count()
        {
            var items = Enumerable.Range(1, 5).Select(i => Item($"A.P{i}.1")).ToList();

            List<GalleryRow> rows = LibraryCatalog.Rows(items, 2, false, new HashSet<string>());

            Assert.Equal(new[] { 2, 2, 1 }, rows.Select(r => r.Items.Count));
            Assert.All(rows, row => Assert.Null(row.Header));
        }

        [Fact]
        public void Rows_group_by_author_and_hide_closed_authors()
        {
            var items = new[] { Item("Ann.A.1"), Item("Ann.B.1"), Item("Bob.A.1") };

            List<GalleryRow> rows = LibraryCatalog.Rows(items, 4, true, new HashSet<string> { "Bob" });

            Assert.Equal(new[] { "Ann", null, "Bob" }, rows.Select(r => r.Header));
            Assert.Equal(2, rows[0].HeaderCount);
            Assert.True(rows[0].IsOpen);
            Assert.False(rows[2].IsOpen);
        }

        [Fact]
        public void A_zero_column_count_still_makes_rows()
        {
            Assert.Single(LibraryCatalog.Rows(new[] { Item("A.B.1") }, 0, false, new HashSet<string>()));
        }

        [Fact]
        public async Task No_store_yet_is_an_empty_library_and_creates_nothing()
        {
            LibraryViewModel library = await Library();

            Assert.Empty(library.Rows);
            Assert.Equal("0 of 0 packages", library.ShownText);
            Assert.False(File.Exists(storeFile));
        }

        [Fact]
        public async Task Typing_narrows_the_gallery_and_suggests_authors()
        {
            Save("Ann.Hair.1");
            Save("Bob.Coat.1");
            LibraryViewModel library = await Library();

            library.Query = "an";

            Assert.Equal(new[] { "Ann.Hair.1" }, Shown(library));
            Assert.Equal("Ann", Assert.Single(library.Suggestions).Name);
            Assert.True(library.HasSuggestions);
            Assert.Equal("1 of 2 packages", library.ShownText);
        }

        [Fact]
        public async Task Enter_turns_the_first_suggestion_into_a_tag_and_clears_the_box()
        {
            Save("Ann.Hair.1");
            Save("Bob.Coat.1");
            LibraryViewModel library = await Library();
            library.Query = "ann";

            library.AcceptFirstSuggestion();

            Assert.Equal(new[] { "Ann" }, library.AuthorTags);
            Assert.Equal("", library.Query);
            Assert.False(library.HasSuggestions);
            Assert.Equal(new[] { "Ann.Hair.1" }, Shown(library));
        }

        [Fact]
        public async Task Suggestions_hide_on_request_and_return_with_more_typing()
        {
            Save("Ann.Hair.1");
            LibraryViewModel library = await Library();
            library.Query = "an";

            library.HideSuggestions();

            Assert.False(library.HasSuggestions);
            Assert.Equal("an", library.Query);

            library.Query = "ann";

            Assert.True(library.HasSuggestions);
        }

        [Fact]
        public async Task Enter_with_no_suggestion_does_nothing()
        {
            Save("Ann.Hair.1");
            LibraryViewModel library = await Library();
            library.Query = "zzz";

            library.AcceptFirstSuggestion();

            Assert.Empty(library.AuthorTags);
            Assert.Equal("zzz", library.Query);
        }

        [Fact]
        public async Task Backspace_in_an_empty_box_removes_the_last_tag()
        {
            Save("Ann.Hair.1");
            Save("Bob.Coat.1");
            LibraryViewModel library = await Library();
            library.AddAuthorCommand.Execute("Ann");
            library.AddAuthorCommand.Execute("Bob");

            library.RemoveLastAuthor();

            Assert.Equal(new[] { "Ann" }, library.AuthorTags);
        }

        [Fact]
        public async Task Backspace_with_text_keeps_the_tags()
        {
            Save("Ann.Hair.1");
            LibraryViewModel library = await Library();
            library.AddAuthorCommand.Execute("Ann");
            library.Query = "h";

            library.RemoveLastAuthor();

            Assert.Equal(new[] { "Ann" }, library.AuthorTags);
        }

        [Fact]
        public async Task A_tag_can_be_removed_and_is_not_added_twice()
        {
            Save("Ann.Hair.1");
            Save("Bob.Coat.1");
            LibraryViewModel library = await Library();
            library.AddAuthorCommand.Execute("Ann");
            library.AddAuthorCommand.Execute("ANN");
            Assert.Single(library.AuthorTags);

            library.RemoveAuthorCommand.Execute("Ann");

            Assert.Empty(library.AuthorTags);
            Assert.Equal(2, Shown(library).Count);
        }

        [Fact]
        public async Task Two_authors_get_headers_that_close()
        {
            Save("Ann.Hair.1");
            Save("Bob.Coat.1");
            LibraryViewModel library = await Library();
            library.AddAuthorCommand.Execute("Ann");
            library.AddAuthorCommand.Execute("Bob");

            Assert.Equal(2, library.Rows.OfType<AuthorHeaderRow>().Count());
            Assert.Equal("1 package", library.Rows.OfType<AuthorHeaderRow>().First().Summary);

            library.ToggleAuthorCommand.Execute("Bob");
            Assert.Equal(new[] { "Ann.Hair.1" }, Shown(library));

            library.ToggleAuthorCommand.Execute("Bob");
            Assert.Equal(2, Shown(library).Count);
        }

        [Fact]
        public async Task Pills_filter_and_show_which_is_active()
        {
            Save("Ann.Hair.1", scan => scan.Hairstyle = 1);
            Save("Bob.Coat.1", scan => scan.Clothing = 1);
            linked.Add("Bob.Coat.1");
            LibraryViewModel library = await Library();

            library.TypePills.Single(p => p.Label == "Hair").Command.Execute(null);

            Assert.Equal(new[] { "Ann.Hair.1" }, Shown(library));
            Assert.Equal("Hair", library.TypePills.Single(p => p.IsActive).Label);

            library.TypePills.Single(p => p.Label == "All").Command.Execute(null);
            library.StatePills.Single(p => p.Label == "On").Command.Execute(null);

            Assert.Equal(new[] { "Bob.Coat.1" }, Shown(library));
            Assert.Equal("On", library.StatePills.Single(p => p.IsActive).Label);
        }

        [Fact]
        public async Task Changing_columns_rewraps_the_tiles()
        {
            Save("A.P1.1");
            Save("A.P2.1");
            Save("A.P3.1");
            LibraryViewModel library = await Library(columns: 2);
            Assert.Equal(2, library.Rows.Count);

            library.Columns = 3;

            Assert.Single(library.Rows);
        }

        [Fact]
        public async Task Selecting_shows_details_and_each_dependency_state()
        {
            Save("A.B.1", null, "C.D.1", "E.F.1", "Gone.Pack.1");
            Save("C.D.1");
            Save("E.F.1");
            linked.Add("C.D.1");
            LibraryViewModel library = await Library();
            LibraryTile tile = library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).Single(t => t.Item.VarName == "A.B.1");

            library.SelectCommand.Execute(tile);

            Assert.True(tile.IsSelected);
            Assert.True(library.HasSelection);
            Assert.Equal("3 MB", library.SelectedSize);
            Assert.Equal("3 vars", library.SelectedNeeds);
            Assert.Equal("Missing deps", library.SelectedState);
            Assert.Equal(new[] { "Linked", "Off", "Missing" }, library.SelectedDependencies.Select(d => d.State));
        }

        [Fact]
        public async Task Choosing_another_tile_clears_the_first()
        {
            Save("A.B.1");
            Save("C.D.1");
            LibraryViewModel library = await Library();
            List<LibraryTile> tiles = library.Rows.OfType<TileRow>().SelectMany(r => r.Tiles).ToList();

            library.SelectCommand.Execute(tiles[0]);
            library.SelectCommand.Execute(tiles[1]);

            Assert.False(tiles[0].IsSelected);
            Assert.True(tiles[1].IsSelected);
        }

        [Fact]
        public async Task Reloading_keeps_the_selection_and_shows_new_link_state()
        {
            Save("A.B.1");
            LibraryViewModel library = await Library();
            library.SelectCommand.Execute(library.Rows.OfType<TileRow>().Single().Tiles[0]);
            linked.Add("A.B.1");

            await library.Reload();

            Assert.Equal("On", library.SelectedState);
            Assert.True(library.Selected!.IsSelected);
        }

        [Fact]
        public async Task Reloading_drops_a_selection_that_left_the_library()
        {
            Save("A.B.1");
            LibraryViewModel library = await Library();
            library.SelectCommand.Execute(library.Rows.OfType<TileRow>().Single().Tiles[0]);
            using (LibraryStore store = LibraryStore.Open(storeFile))
                store.DeleteVars(new[] { "A.B.1" });

            await library.Reload();

            Assert.False(library.HasSelection);
        }

        [Theory]
        [InlineData(0, "0 MB")]
        [InlineData(1572864, "1.5 MB")]
        [InlineData(1610612736, "1.5 GB")]
        public void Sizes_read_naturally(double bytes, string text)
        {
            Assert.Equal(text, LibraryViewModel.FormatSize(bytes));
        }

        [Fact]
        public async Task Thumbnails_forget_the_least_recently_used_first()
        {
            var loader = new ThumbnailLoader(capacity: 2);
            string a = PngFile(Path.Combine(root, "a.png"));
            string b = PngFile(Path.Combine(root, "b.png"));
            string c = PngFile(Path.Combine(root, "c.png"));
            await loader.Load(a, _ => { });
            await loader.Load(b, _ => { });
            await loader.Load(a, _ => { });
            await loader.Load(c, _ => { });
            File.Delete(a);
            File.Delete(b);
            BitmapSource? keptA = null, lostB = null;

            await loader.Load(a, picture => keptA = picture);
            await loader.Load(b, picture => lostB = picture);

            Assert.NotNull(keptA);
            Assert.Null(lostB);
            Assert.Equal(2, loader.CachedCount);
        }

        [Fact]
        public async Task A_cached_thumbnail_comes_back_without_decoding_again()
        {
            var loader = new ThumbnailLoader();
            string file = PngFile(Path.Combine(root, "a.png"));
            BitmapSource? first = null, second = null;
            await loader.Load(file, picture => first = picture);
            File.Delete(file);

            await loader.Load(file, picture => second = picture);

            Assert.Same(first, second);
        }

        [Fact]
        public async Task A_bad_or_missing_picture_gives_null()
        {
            var loader = new ThumbnailLoader();
            string bad = Path.Combine(root, "bad.jpg");
            File.WriteAllText(bad, "not a picture");
            var pictures = new List<BitmapSource?>();

            await loader.Load(bad, pictures.Add);
            await loader.Load(Path.Combine(root, "gone.jpg"), pictures.Add);

            Assert.Equal(new BitmapSource?[] { null, null }, pictures);
        }

        [Fact]
        public void A_tile_asks_for_its_picture_only_when_read()
        {
            var loader = new ThumbnailLoader();
            var tile = new LibraryTile(new LibraryItem { VarName = "A.B.1", Creator = "A", Package = "B", PreviewPath = PngFile(Path.Combine(root, "a.png")) },
                loader, null);
            using var loaded = new ManualResetEventSlim();
            tile.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LibraryTile.Thumbnail))
                    loaded.Set();
            };
            Assert.Equal(0, loader.Requests);

            _ = tile.Thumbnail;

            Assert.Equal(1, loader.Requests);
            Assert.True(loaded.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotNull(tile.Thumbnail);
            Assert.Equal(1, loader.Requests);
        }

        [Fact]
        public async Task A_tile_with_an_unreadable_picture_asks_only_once()
        {
            string bad = Path.Combine(root, "bad.jpg");
            File.WriteAllText(bad, "not a picture");
            var loader = new ThumbnailLoader();
            var tile = new LibraryTile(new LibraryItem { VarName = "A.B.1", Creator = "A", Package = "B", PreviewPath = bad }, loader, null);

            _ = tile.Thumbnail;
            for (int i = 0; i < 100 && tile.Thumbnail == null && loader.Requests == 1; i++)
                await Task.Delay(10);

            Assert.Null(tile.Thumbnail);
            Assert.Equal(1, loader.Requests);
        }

        [Fact]
        public async Task An_older_reload_finishing_late_is_ignored()
        {
            Save("A.B.1");
            using var gate = new ManualResetEventSlim();
            int calls = 0;
            var library = new LibraryViewModel(storeFile, new LibraryPaths(root, root, root), new JobRunner(null), () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    gate.Wait(TimeSpan.FromSeconds(10));
                    return new string[0];
                }
                return new[] { "A.B.1" };
            }, _ => { }, null);

            Task older = library.Reload();
            while (Volatile.Read(ref calls) == 0)
                await Task.Delay(5);
            await library.Reload();
            gate.Set();
            await older;

            Assert.Equal(1, library.LinkedCount);
            Assert.Equal("On", ((TileRow)library.Rows.Single()).Tiles[0].StateText);
        }

        [Fact]
        public async Task Filters_and_tags_survive_a_reload()
        {
            Save("Ann.Hair.1", scan => scan.Hairstyle = 1);
            Save("Ann.Coat.1", scan => scan.Clothing = 1);
            Save("Bob.Hair.1", scan => scan.Hairstyle = 1);
            LibraryViewModel library = await Library();
            library.AddAuthorCommand.Execute("Ann");
            library.TypePills.Single(p => p.Label == "Hair").Command.Execute(null);

            await library.Reload();

            Assert.Equal(new[] { "Ann.Hair.1" }, Shown(library));
            Assert.Equal(new[] { "Ann" }, library.AuthorTags);
        }

        [Fact]
        public void A_tile_with_no_preview_asks_for_nothing()
        {
            var loader = new ThumbnailLoader();
            var tile = new LibraryTile(Item("A.B.1"), loader, null);

            Assert.Null(tile.Thumbnail);
            Assert.Equal(0, loader.CachedCount);
        }
    }
}
