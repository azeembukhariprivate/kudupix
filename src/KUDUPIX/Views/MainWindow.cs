using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kudupix.Controls;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>What the grid shows.</summary>
    public enum ViewKind { All, Videos, Starred, ByMonth, Duplicates, Album, Folder, Tag, People, Person }

    /// <summary>
    /// The main window: the library on the left (everything, videos, starred, by month, duplicates, albums, folders,
    /// tags), the photos in the middle, the selected photo's stars, tags, albums and details on the right.
    /// </summary>
    public sealed class MainWindow : Window
    {
        private readonly Catalog _catalog;
        private readonly Grid _root = new Grid();
        private readonly StackPanel _side = new StackPanel { Margin = new Thickness(8, 10, 8, 14) };
        private readonly PhotoGrid _grid = new PhotoGrid();
        private readonly Grid _gridPage = new Grid();
        private readonly ContentControl _middle = new ContentControl { Focusable = false };
        private readonly StackPanel _info = new StackPanel { Margin = new Thickness(16) };
        private readonly Border _infoPanel;
        private readonly TextBlock _title = Ui.Text(string.Empty, 18, bold: true);
        private readonly TextBox _search = new TextBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(30, 0, 8, 0) };
        private readonly TextBlock _searchHint = Ui.Subtle("Search names, tags, albums, folders, cameras, dates…", 13, false);
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _counts = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBlock _selectedText = Ui.Subtle(string.Empty, 12, false);
        private readonly Slider _size = new Slider { Minimum = 90, Maximum = 320, Width = 130, VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleButton _oldest = new ToggleButton { Content = "Oldest first" };
        private readonly ToggleButton _onlyPhotos = new ToggleButton { Content = "Photos" };
        private readonly ToggleButton _onlyVideos = new ToggleButton { Content = "Videos" };
        private readonly ToggleButton _onlyStarred = new ToggleButton { Content = "★ Starred" };
        private readonly Border _banner = new Border { Visibility = Visibility.Collapsed };
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private ViewerView _viewer;
        private DuplicatesView _duplicates;
        private readonly PeopleWork _people;
        private PeopleView _peopleView;
        private Button _personButton;

        public ViewKind Kind { get; private set; } = ViewKind.All;
        /// <summary>The album id, folder path or tag shown.</summary>
        public string ViewKey { get; private set; }

        public Catalog Catalog => _catalog;
        public PhotoGrid Grid => _grid;
        public ViewerView Viewer => _viewer;
        public DuplicatesView Duplicates => _duplicates;
        public PeopleWork People => _people;
        public PeopleView PeoplePage => _peopleView ?? (_peopleView = new PeopleView(this));

        public MainWindow(Catalog catalog)
        {
            _catalog = catalog;
            Width = 1440;
            Height = 900;
            MinWidth = 960;
            MinHeight = 600;
            Dialogs.Prepare(this, AppInfo.Name);
            this.Res(BackgroundProperty, "WindowBg");

            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition());
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.Children.Add(BuildHeader().At(0));
            _root.Children.Add(_banner.At(1));

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            var side = new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            body.Children.Add(new Border { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) }
                .Res(Border.BackgroundProperty, "SurfaceAltBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0, 0));
            BuildGridPage();
            _middle.Content = _gridPage;
            body.Children.Add(new Border { Child = _middle }.Res(Border.BackgroundProperty, "SurfaceBg").At(0, 1));
            _infoPanel = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } }
                .Res(Border.BackgroundProperty, "SurfaceAltBg").Res(Border.BorderBrushProperty, "BorderBrush");
            body.Children.Add(_infoPanel.At(0, 2));
            _root.Children.Add(body.At(2));
            _root.Children.Add(BuildStatus().At(3));
            Content = _root;

            _grid.StarsOf = i => _catalog.Data.Stars(i.Path);
            _grid.EditOf = i => _catalog.Data.Edit(i.Path);
            _grid.SelectionChanged += ShowInfo;
            _grid.Opened += Open;
            _grid.MenuFor = ItemMenu;
            _grid.TileSize = Settings.TileSize;

            _catalog.Changed += () =>
            {
                Refresh();
                FillSide();
            };
            _catalog.DataChanged += () =>
            {
                _grid.RefreshTiles();
                FillSide();
            };
            _catalog.Status += t => _status.Text = t;
            _people = new PeopleWork(catalog);
            _people.Changed += () =>
            {
                FillSide();
                if (Kind == ViewKind.People) PeoplePage.Fill();
                else if (Kind == ViewKind.Person) Refresh();
                UpdatePersonButton();
                if (_viewer != null && ViewerOpen) _viewer.FacesChanged();
                if (_grid.Selected.Count == 1) ShowInfo();
            };
            _people.Status += t => _status.Text = t ?? (_catalog.Scanning ? _status.Text : _catalog.StatusText());

            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                Refresh();
            };
            PreviewKeyDown += OnKey;
            Loaded += async (s, e) =>
            {
                FillSide();
                Refresh();
                await CheckForUpdates();
            };
            Closed += (s, e) =>
            {
                _people.Dispose();
                _catalog.Dispose();
            };
        }

        // ================================================================== header and status

        private UIElement BuildHeader()
        {
            var header = Ui.Columns(Ui.Auto, Ui.Px(400), Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            header.Children.Add(Ui.Row(LogoTile.Create(26), Ui.Text(AppInfo.Name, 15, bold: true).Margin(10, 0, 0, 0)).Margin(0, 0, 18, 0).At(0, 0));

            var box = new Grid();
            _search.TextChanged += (s, e) =>
            {
                _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _searchTimer.Stop();
                _searchTimer.Start();
            };
            _search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) _search.Text = string.Empty;
                if (e.Key == Key.Enter) { _searchTimer.Stop(); Refresh(); }
            };
            System.Windows.Automation.AutomationProperties.SetName(_search, "Search");
            box.Children.Add(_search);
            var icon = Ui.Icon("IconSearch", 15).Margin(10, 0, 0, 0);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Res(TextElement.ForegroundProperty, "SubtleText");
            box.Children.Add(icon);
            _searchHint.IsHitTestVisible = false;
            _searchHint.Margin = new Thickness(32, 0, 0, 0);
            box.Children.Add(_searchHint);
            header.Children.Add(box.At(0, 1));

            _update.Visibility = Visibility.Collapsed;
            header.Children.Add(_update.Margin(14, 0, 14, 0).At(0, 2));
            header.Children.Add(Ui.Button("Slideshow", "IconPlay", "GhostButton", (s, e) => Slideshow()).Tip("Show the photos here one after another, full screen (F5)").At(0, 3));
            header.Children.Add(Ui.Button("Export", "IconExport", "GhostButton", (s, e) => Export()).Tip("Save copies to share: smaller, without the place, fixes included").At(0, 4));
            header.Children.Add(Ui.Button("Folders…", "IconFolder", "GhostButton", (s, e) => ShowSettings(true)).Tip("Choose the folders KUDUPIX looks in").At(0, 5));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => ShowSettings(false)).At(0, 6));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 7));
            return new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush");
        }

        private UIElement BuildStatus()
        {
            var g = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto);
            g.Children.Add(Ui.Row(Ui.Dot(), _status.Margin(7, 0, 0, 0)).At(0, 0));
            g.Children.Add(_counts.Margin(18, 0, 0, 0).At(0, 1));
            g.Children.Add(_selectedText.Margin(18, 0, 0, 0).At(0, 2));
            _size.Value = Settings.TileSize;
            _size.ToolTip = "Picture size";
            System.Windows.Automation.AutomationProperties.SetName(_size, "Picture size");
            _size.ValueChanged += (s, e) =>
            {
                _grid.TileSize = _size.Value;
                Settings.TileSize = (int)_size.Value;
            };
            var small = Ui.Icon("IconPicture", 12);
            var big = Ui.Icon("IconPicture", 18);
            foreach (var i in new[] { small, big }) i.Res(TextElement.ForegroundProperty, "SubtleText");
            g.Children.Add(Ui.Row(small, _size.Margin(8, 0, 8, 0), big).At(0, 4));
            return new Border { Child = g, Padding = new Thickness(16, 7, 16, 7), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush");
        }

        /// <summary>The note under the header the first time: which folders are being looked in.</summary>
        public void ShowFirstStartNote(IList<string> folders)
        {
            var names = folders.Count == 0 ? "no folders yet" : string.Join(", ", folders.Select(f => Path.GetFileName(f.TrimEnd('\\')) is string n && n.Length > 0 ? n : f));
            var text = Ui.Text($"KUDUPIX is looking for photos and videos in {names}. It only reads them: nothing is moved or changed.", 12.5, wrap: true);
            var row = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            row.Children.Add(text.At(0, 0));
            row.Children.Add(Ui.Link("Choose folders", (s, e) => ShowSettings(true)).Margin(12, 0, 0, 0).At(0, 1));
            row.Children.Add(Ui.Link("OK", (s, e) => _banner.Visibility = Visibility.Collapsed).Margin(14, 0, 0, 0).At(0, 2));
            _banner.Child = Ui.Banner("IconInfo", row).Margin(16, 10, 16, 0);
            _banner.Visibility = Visibility.Visible;
        }

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                _update.Content = $"{AppInfo.Name} {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // No internet: nothing to show.
            }
        }

        // ================================================================== the left side

        public void FillSide()
        {
            _side.Children.Clear();
            var all = _catalog.Library.All();
            void Heading(string text) => _side.Children.Add(Ui.Text(text, 12, "SubtleText", bold: true).Margin(10, _side.Children.Count == 0 ? 0 : 14, 0, 6));
            void Item(string icon, string text, ViewKind kind, string key, string count, int indent = 0, string countStyle = null, ContextMenu menu = null)
            {
                var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
                var glyph = Ui.Icon(icon, 15);
                var on = Kind == kind && (key == null || string.Equals(ViewKey, key, StringComparison.OrdinalIgnoreCase));
                glyph.Res(TextElement.ForegroundProperty, on ? "Accent" : "SubtleText");
                g.Children.Add(glyph.At(0, 0));
                var label = Ui.Text(text, 13, bold: on);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                g.Children.Add(label.Margin(9, 0, 6, 0).At(0, 1));
                if (!string.IsNullOrEmpty(count))
                {
                    FrameworkElement c = countStyle == "warn" ? Ui.Chip(count, Tone.Warn) : (FrameworkElement)Ui.Subtle(count, 12, false);
                    g.Children.Add(c.At(0, 2));
                }
                var b = new Border { Child = g, Padding = new Thickness(10 + indent * 16, 6, 10, 6), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = key ?? text };
                if (on) b.Res(Border.BackgroundProperty, "SelectionBg");
                b.MouseEnter += (s, e) => { if (!on) b.Res(Border.BackgroundProperty, "HoverBg"); };
                b.MouseLeave += (s, e) => { if (!on) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (s, e) => Go(kind, key);
                b.ContextMenu = menu;
                System.Windows.Automation.AutomationProperties.SetName(b, text);
                _side.Children.Add(b);
            }
            string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

            Heading("Library");
            Item("IconPicture", "All photos and videos", ViewKind.All, null, N(all.Count));
            Item("IconVideo", "Videos", ViewKind.Videos, null, N(all.Count(i => i.IsVideo)));
            Item("IconStar", "Starred", ViewKind.Starred, null, N(all.Count(i => _catalog.Data.Stars(i.Path) > 0)));
            Item("IconCalendar", "By month", ViewKind.ByMonth, null, null);
            Item("IconCopy", "Duplicates", ViewKind.Duplicates, null, _duplicates?.GroupCount > 0 ? N(_duplicates.GroupCount) : null, 0, "warn");

            if (_people != null && _people.Problem == null)
            {
                Heading("People");
                var groups = _people.Groups;
                var asks = groups.Ask.Where(a => !a.Key.Hidden).Sum(a => a.Value.Count);
                Item("IconUser", "People", ViewKind.People, null, groups.Unnamed.Count + asks > 0 ? N(groups.Unnamed.Count + asks) : null, 0, "warn");
                foreach (var p in _catalog.People.People().Where(p => !p.Hidden))
                    Item("IconUser", p.Name, ViewKind.Person, p.Id, N(_people.PhotoCount(p)), 1);
            }

            Heading("Albums");
            foreach (var a in _catalog.Data.Albums())
            {
                var album = a;
                var menu = new ContextMenu();
                var rename = new MenuItem { Header = "Rename…" };
                rename.Click += (s, e) => RenameAlbum(album);
                var remove = new MenuItem { Header = "Remove the album (the photos stay)" };
                remove.Click += (s, e) => RemoveAlbum(album);
                menu.Items.Add(rename);
                menu.Items.Add(remove);
                Item("IconAlbum", a.Name, ViewKind.Album, a.Id, N(a.Paths.Count), 0, null, menu);
            }
            var add = Ui.Link("+  New album", (s, e) => NewAlbum(null));
            add.HorizontalAlignment = HorizontalAlignment.Left;
            _side.Children.Add(add.Margin(10, 4, 0, 0));

            Heading("Folders");
            var roots = _catalog.Folders;
            if (roots.Count == 0)
            {
                var choose = Ui.Link("Choose folders…", (s, e) => ShowSettings(true));
                choose.HorizontalAlignment = HorizontalAlignment.Left;
                _side.Children.Add(choose.Margin(10, 2, 0, 0));
            }
            foreach (var root in roots)
            {
                var inRoot = all.Where(i => Library.Inside(i.Path, root)).ToList();
                var name = Path.GetFileName(root.TrimEnd('\\', '/'));
                Item("IconFolder", name.Length > 0 ? name : root, ViewKind.Folder, root, N(inRoot.Count));
                // The folders directly inside, with photos somewhere in them.
                var children = inRoot.Select(i => Child(root, i.Folder)).Where(c => c != null).GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => Path.GetFileName(g.Key), StringComparer.CurrentCultureIgnoreCase).Take(40);
                foreach (var c in children) Item("IconFolder", Path.GetFileName(c.Key), ViewKind.Folder, c.Key, N(c.Count()), 1);
            }

            var tags = _catalog.Data.AllTags();
            if (tags.Count > 0)
            {
                Heading("Tags");
                foreach (var (tag, count) in tags.Take(30)) Item("IconTag", tag, ViewKind.Tag, tag, N(count));
            }
        }

        /// <summary>The folder directly inside <paramref name="root"/> that <paramref name="folder"/> is in (null if it's the root itself).</summary>
        private static string Child(string root, string folder)
        {
            if (!Library.Inside(folder, root)) return null;
            var rest = folder.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
            if (rest.Length == 0) return null;
            var first = rest.Split('\\', '/')[0];
            return Path.Combine(root, first);
        }

        /// <summary>Shows a part of the library.</summary>
        public void Go(ViewKind kind, string key = null)
        {
            CloseViewer();
            Kind = kind;
            ViewKey = key;
            if (kind == ViewKind.Duplicates)
            {
                if (_duplicates == null) _duplicates = new DuplicatesView(this);
                _middle.Content = _duplicates;
                _infoPanel.Visibility = Visibility.Collapsed;
                _duplicates.Start();
            }
            else if (kind == ViewKind.People)
            {
                _middle.Content = PeoplePage;
                _infoPanel.Visibility = Visibility.Collapsed;
                PeoplePage.ShowOverview();
                UpdateCounts(null);
            }
            else
            {
                _middle.Content = _gridPage;
                _infoPanel.Visibility = Visibility.Visible;
                Refresh();
            }
            UpdatePersonButton();
            FillSide();
        }

        // ================================================================== the grid

        private void BuildGridPage()
        {
            _personButton = Ui.Small(Ui.Button("Faces and questions", "IconUser", "SecondaryButton", (s, e) =>
            {
                var person = _catalog.People.Find(ViewKey);
                if (person == null) return;
                Go(ViewKind.People);
                PeoplePage.ShowPerson(person);
            }));
            _personButton.Visibility = Visibility.Collapsed;
            _gridPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _gridPage.RowDefinitions.Add(new RowDefinition());
            var bar = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            bar.Margin = new Thickness(18, 12, 18, 2);
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            bar.Children.Add(_title.At(0, 0));
            var chips = Ui.Row();
            foreach (var t in new[] { _oldest, _onlyPhotos, _onlyVideos, _onlyStarred })
            {
                t.Styled("ChipToggle");
                t.Margin = new Thickness(6, 0, 0, 0);
                t.Click += (s, e) =>
                {
                    if (s == _onlyPhotos && _onlyPhotos.IsChecked == true) _onlyVideos.IsChecked = false;
                    if (s == _onlyVideos && _onlyVideos.IsChecked == true) _onlyPhotos.IsChecked = false;
                    Refresh();
                };
                chips.Children.Add(t);
            }
            bar.Children.Add(chips.Margin(14, 0, 0, 0).At(0, 1));
            Button addTo = null;
            addTo = Ui.Small(Ui.Button("Add to album", "IconAlbum", "SecondaryButton", (s, e) => AlbumMenu(addTo)));
            bar.Children.Add(addTo.Margin(8, 0, 0, 0).At(0, 2));
            bar.Children.Add(_personButton.Margin(8, 0, 0, 0).At(0, 4));
            bar.Children.Add(Ui.Small(Ui.Button("Fix", "IconEdit", "SecondaryButton", (s, e) =>
            {
                var item = _grid.Current ?? _grid.SelectedInOrder.FirstOrDefault();
                if (item != null) Open(item);
            })).Tip("Open the photo to fix it (rotate, crop, colours, red-eye)").Margin(8, 0, 0, 0).At(0, 3));
            _gridPage.Children.Add(bar.At(0));
            _gridPage.Children.Add(_grid.At(1));
        }

        /// <summary>The items the current view shows, after the search and the filters.</summary>
        public List<MediaItem> ViewItems()
        {
            IEnumerable<MediaItem> items = _catalog.Library.All();
            var data = _catalog.Data;
            switch (Kind)
            {
                case ViewKind.Videos: items = items.Where(i => i.IsVideo); break;
                case ViewKind.Starred: items = items.Where(i => data.Stars(i.Path) > 0); break;
                case ViewKind.Album:
                    var album = data.FindAlbum(ViewKey);
                    var set = new HashSet<string>(album?.Paths ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                    items = items.Where(i => set.Contains(i.Path));
                    break;
                case ViewKind.Folder: items = items.Where(i => Library.Inside(i.Path, ViewKey)); break;
                case ViewKind.Person:
                    var person = _catalog.People.Find(ViewKey);
                    var photos = person == null ? new HashSet<string>() : _people.PhotosOf(person);
                    items = items.Where(i => photos.Contains(i.Path));
                    break;
                case ViewKind.Tag:
                    var tagged = data.PathsWithTag(ViewKey);
                    items = items.Where(i => tagged.Contains(i.Path));
                    break;
            }
            if (_onlyPhotos.IsChecked == true) items = items.Where(i => !i.IsVideo);
            if (_onlyVideos.IsChecked == true) items = items.Where(i => i.IsVideo);
            if (_onlyStarred.IsChecked == true) items = items.Where(i => data.Stars(i.Path) > 0);
            var words = Search.Words(_search.Text);
            if (words.Count > 0)
            {
                var albums = data.Albums();
                items = items.Where(i => Search.Matches(i, words, data.Tags(i.Path).Concat(_people.PeopleIn(i.Path).Select(p => p.Name)).ToList(), albums.Where(a => a.Paths.Contains(i.Path, StringComparer.OrdinalIgnoreCase)).Select(a => a.Name).ToList()));
            }
            return items.ToList();
        }

        /// <summary>Shows the current view again (after a scan, a search or a change).</summary>
        public void Refresh()
        {
            if (Kind == ViewKind.Duplicates || Kind == ViewKind.People)
            {
                UpdateCounts(null);
                return;
            }
            var items = ViewItems();
            var newest = _oldest.IsChecked != true;
            var groups = Kind == ViewKind.ByMonth ? Timeline.ByMonth(items, newest) : Timeline.ByDay(items, newest);
            _grid.Show(groups);
            switch (Kind)
            {
                case ViewKind.Videos: _title.Text = "Videos"; break;
                case ViewKind.Starred: _title.Text = "Starred"; break;
                case ViewKind.ByMonth: _title.Text = "By month"; break;
                case ViewKind.Album: _title.Text = _catalog.Data.FindAlbum(ViewKey)?.Name ?? "Album"; break;
                case ViewKind.Folder: _title.Text = Path.GetFileName(ViewKey?.TrimEnd('\\', '/')) ?? ViewKey; break;
                case ViewKind.Tag: _title.Text = "Tagged “" + ViewKey + "”"; break;
                case ViewKind.Person: _title.Text = _catalog.People.Find(ViewKey)?.Name ?? "Person"; break;
                default: _title.Text = "All photos and videos"; break;
            }
            if (Search.Words(_search.Text).Count > 0) _title.Text = "Search results";
            UpdateCounts(items);
            if (_viewer != null) _viewer.ListChanged(_grid.Items);
        }

        private void UpdateCounts(List<MediaItem> shown)
        {
            var all = _catalog.Library.All();
            _counts.Text = shown == null || shown.Count == all.Count
                ? $"{Format.Count(all.Count, "item")} · {Format.Size(all.Sum(i => i.Size))}"
                : $"{Format.Count(shown.Count, "item")} shown of {all.Count:N0}";
        }

        // ================================================================== the right side: the selected photo

        private void ShowInfo()
        {
            _info.Children.Clear();
            var selected = _grid.SelectedInOrder;
            _selectedText.Text = selected.Count == 0 ? string.Empty : selected.Count == 1 ? "1 selected" : $"{selected.Count:N0} selected";
            var data = _catalog.Data;
            if (selected.Count == 0)
            {
                _info.Children.Add(Ui.Subtle("Select a photo or video to see its details, give it stars and tags, or add it to an album.\n\nDouble-click to open it. Ctrl+click or Shift+click to select more than one.", 12.5));
                return;
            }
            var item = selected[0];
            var paths = selected.Select(i => i.Path).ToList();
            if (selected.Count == 1)
            {
                var preview = new Grid { Height = 190, ClipToBounds = true }.Res(Panel.BackgroundProperty, "SurfaceBg");
                var image = new Image { Stretch = Stretch.Uniform };
                preview.Children.Add(image);
                var ready = Thumbs.Get(item, data.Edit(item.Path), b => { if (b != null) image.Source = b; });
                if (ready != null) image.Source = ready;
                preview.Cursor = Cursors.Hand;
                preview.MouseLeftButtonUp += (s, e) => Open(item);
                _info.Children.Add(preview);
                var name = Ui.Text(item.Name, 14, bold: true, wrap: true).Margin(0, 12, 0, 0);
                _info.Children.Add(name);
                var where = Ui.Link(ShortFolder(item.Folder), (s, e) => ShowInFolder(item));
                where.HorizontalAlignment = HorizontalAlignment.Left;
                where.ToolTip = "Show it in File Explorer";
                _info.Children.Add(where.Margin(0, 2, 0, 0));
            }
            else
            {
                _info.Children.Add(Ui.Heading($"{selected.Count:N0} selected", 15));
                _info.Children.Add(Ui.Subtle(Format.Size(selected.Sum(i => i.Size)) + " · stars and tags go on all of them", 12));
            }

            // Stars: click a star to give it, click the same star again to take them off.
            var current = selected.Select(i => data.Stars(i.Path)).Distinct().Count() == 1 ? data.Stars(item.Path) : 0;
            var stars = Ui.Row();
            for (var n = 1; n <= 5; n++)
            {
                var value = n;
                var star = new TextBlock { Text = "★", FontSize = 22, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 3, 0), ToolTip = value == 1 ? "1 star" : value + " stars" };
                if (value <= current) star.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xB4, 0x00));
                else star.Res(TextBlock.ForegroundProperty, "InputBorder");
                star.MouseLeftButtonUp += (s, e) =>
                {
                    data.SetStars(paths, current == value ? 0 : value);
                    ShowInfo();
                };
                stars.Children.Add(star);
            }
            System.Windows.Automation.AutomationProperties.SetName(stars, "Stars");
            _info.Children.Add(stars.Margin(0, 10, 0, 0));

            // Tags.
            _info.Children.Add(Ui.Text("Tags", 12, "SubtleText", bold: true).Margin(0, 14, 0, 6));
            var tagPanel = new WrapPanel();
            var tags = selected.SelectMany(i => data.Tags(i.Path)).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            foreach (var t in tags)
            {
                var tag = t;
                var chip = Chip(tag, () =>
                {
                    data.RemoveTag(paths, tag);
                    ShowInfo();
                }, "Take this tag off");
                tagPanel.Children.Add(chip);
            }
            var tagBox = new TextBox { MinWidth = 120, MinHeight = 28, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 6) };
            System.Windows.Automation.AutomationProperties.SetName(tagBox, "New tag");
            var addTag = DashedChip("+ Add a tag", () =>
            {
                tagBox.Visibility = Visibility.Visible;
                tagBox.Focus();
            });
            tagBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && tagBox.Text.Trim().Length > 0)
                {
                    data.AddTag(paths, tagBox.Text);
                    ShowInfo();
                }
                else if (e.Key == Key.Escape) tagBox.Visibility = Visibility.Collapsed;
            };
            tagPanel.Children.Add(addTag);
            tagPanel.Children.Add(tagBox);
            _info.Children.Add(tagPanel);

            // Albums.
            _info.Children.Add(Ui.Text("Albums", 12, "SubtleText", bold: true).Margin(0, 10, 0, 6));
            var albumPanel = new WrapPanel();
            foreach (var a in data.Albums().Where(a => paths.Any(p => a.Paths.Contains(p, StringComparer.OrdinalIgnoreCase))))
            {
                var album = a;
                albumPanel.Children.Add(Chip(a.Name, () =>
                {
                    data.RemoveFromAlbum(album, paths);
                    ShowInfo();
                    if (Kind == ViewKind.Album) Refresh();
                }, "Take it out of this album (the photo stays where it is)"));
            }
            FrameworkElement addAlbum = null;
            addAlbum = DashedChip("+ Add to album", () => AlbumMenu(addAlbum));
            albumPanel.Children.Add(addAlbum);
            _info.Children.Add(albumPanel);

            if (selected.Count > 1) return;

            // Details.
            _info.Children.Add(Ui.Text("Details", 12, "SubtleText", bold: true).Margin(0, 10, 0, 6));
            var kv = Ui.Columns(Ui.Px(90), Ui.Star());
            var row = 0;
            void Detail(string label, string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                kv.Children.Add(Ui.Subtle(label, 12, false).Margin(0, 2, 8, 2).At(row, 0));
                kv.Children.Add(Ui.Text(value, 12, wrap: true).Margin(0, 2, 0, 2).At(row, 1));
                row++;
            }
            Detail(item.IsVideo ? "Filmed" : "Taken", item.Taken.HasValue ? item.Taken.Value.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) : "Not stored in the file");
            Detail("File date", new DateTime(item.Modified, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            Detail("Size", Format.Size(item.Size) + (item.ShownWidth > 0 ? $" · {item.ShownWidth} × {item.ShownHeight}" : string.Empty));
            if (item.IsVideo) Detail("Length", VideoLength.Text(item.Seconds));
            var who = _people.PeopleIn(item.Path);
            if (who.Count > 0) Detail("People", string.Join(", ", who.Select(p => p.Name)));
            Detail("Camera", string.Join(" · ", new[] { item.Camera, item.Exposure }.Where(x => !string.IsNullOrEmpty(x))));
            if (item.HasPlace)
                Detail("Place", item.Latitude.Value.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + item.Longitude.Value.ToString("0.0000", CultureInfo.InvariantCulture));
            _info.Children.Add(kv);
            if (item.HasPlace)
            {
                var map = Ui.Link("Show the place on a map", (s, e) => AppInfo.OpenUrl(
                    string.Format(CultureInfo.InvariantCulture, "https://www.openstreetmap.org/?mlat={0:0.######}&mlon={1:0.######}#map=15/{0:0.######}/{1:0.######}", item.Latitude.Value, item.Longitude.Value)));
                map.HorizontalAlignment = HorizontalAlignment.Left;
                map.ToolTip = "Opens OpenStreetMap in your browser";
                _info.Children.Add(map.Margin(0, 6, 0, 0));
            }
        }

        private static string ShortFolder(string folder)
        {
            var parts = folder.TrimEnd('\\').Split('\\');
            return parts.Length <= 2 ? folder : string.Join(" › ", parts.Skip(Math.Max(0, parts.Length - 2)));
        }

        private static Border Chip(string text, Action remove, string removeTip)
        {
            var x = new TextBlock { Text = "✕", FontSize = 10, Margin = new Thickness(7, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = removeTip, VerticalAlignment = VerticalAlignment.Center };
            x.MouseLeftButtonUp += (s, e) => remove();
            var panel = Ui.Row(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, x);
            panel.Res(TextElement.ForegroundProperty, "ChipText");
            return new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 9, 4), Margin = new Thickness(0, 0, 6, 6), Child = panel }
                .Res(Border.BackgroundProperty, "ChipBg");
        }

        private static Border DashedChip(string text, Action click)
        {
            var label = Ui.Subtle(text, 12, false);
            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(1),
                Child = label,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            }.Res(Border.BorderBrushProperty, "InputBorder");
            b.MouseLeftButtonUp += (s, e) => click();
            System.Windows.Automation.AutomationProperties.SetName(b, text.TrimStart('+', ' '));
            return b;
        }

        // ================================================================== albums

        private void AlbumMenu(FrameworkElement at)
        {
            var selected = _grid.SelectedInOrder;
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            if (selected.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "Select photos first", IsEnabled = false });
            }
            else
            {
                foreach (var a in _catalog.Data.Albums())
                {
                    var album = a;
                    var mi = new MenuItem { Header = a.Name };
                    mi.Click += (s, e) => AddToAlbum(album, selected);
                    menu.Items.Add(mi);
                }
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                var make = new MenuItem { Header = "New album…" };
                make.Click += (s, e) => NewAlbum(selected);
                menu.Items.Add(make);
            }
            menu.IsOpen = true;
        }

        public void AddToAlbum(Album album, IList<MediaItem> items)
        {
            _catalog.Data.AddToAlbum(album, items.Select(i => i.Path));
            _status.Text = $"Added {Format.Count(items.Count, "item")} to “{album.Name}”";
            ShowInfo();
        }

        public Album NewAlbum(IList<MediaItem> items, string name = null)
        {
            name = name ?? Dialogs.Ask(this, "New album", "A name for the album. Albums only point to your photos: nothing is moved or copied.", "New album");
            if (string.IsNullOrWhiteSpace(name)) return null;
            var album = _catalog.Data.AddAlbum(name);
            if (items != null && items.Count > 0) AddToAlbum(album, items);
            FillSide();
            return album;
        }

        private void RenameAlbum(Album album)
        {
            var name = Dialogs.Ask(this, "Rename album", "A new name for the album.", album.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            _catalog.Data.RenameAlbum(album, name);
            FillSide();
            if (Kind == ViewKind.Album && ViewKey == album.Id) Refresh();
        }

        private void RemoveAlbum(Album album)
        {
            if (!Dialogs.Confirm(this, $"Remove the album “{album.Name}”?\n\nOnly the album goes: its {Format.Count(album.Paths.Count, "photo")} stay where they are.")) return;
            _catalog.Data.RemoveAlbum(album);
            if (Kind == ViewKind.Album && ViewKey == album.Id) Go(ViewKind.All);
            else FillSide();
        }

        // ================================================================== actions on photos

        private ContextMenu ItemMenu()
        {
            var selected = _grid.SelectedInOrder;
            if (selected.Count == 0) return null;
            var one = selected.Count == 1;
            var menu = new ContextMenu();
            void Add(string text, Action run, bool enabled = true)
            {
                var mi = new MenuItem { Header = text, IsEnabled = enabled };
                mi.Click += (s, e) => run();
                menu.Items.Add(mi);
            }
            Add("Open", () => Open(selected[0]), one);
            var photos = selected.Where(i => !i.IsVideo).ToList();
            Add("Turn right", () => Turn(photos, true), photos.Count > 0);
            Add("Turn left", () => Turn(photos, false), photos.Count > 0);
            menu.Items.Add(new Separator());
            var stars = new MenuItem { Header = "Stars" };
            for (var n = 0; n <= 5; n++)
            {
                var value = n;
                var mi = new MenuItem { Header = n == 0 ? "No stars" : new string('★', n) };
                mi.Click += (s, e) => _catalog.Data.SetStars(selected.Select(i => i.Path), value);
                stars.Items.Add(mi);
            }
            menu.Items.Add(stars);
            var albums = new MenuItem { Header = "Add to album" };
            foreach (var a in _catalog.Data.Albums())
            {
                var album = a;
                var mi = new MenuItem { Header = a.Name };
                mi.Click += (s, e) => AddToAlbum(album, selected);
                albums.Items.Add(mi);
            }
            var make = new MenuItem { Header = "New album…" };
            make.Click += (s, e) => NewAlbum(selected);
            albums.Items.Add(make);
            menu.Items.Add(albums);
            if (Kind == ViewKind.Album)
            {
                var album = _catalog.Data.FindAlbum(ViewKey);
                if (album != null) Add("Take out of this album", () =>
                {
                    _catalog.Data.RemoveFromAlbum(album, selected.Select(i => i.Path));
                    Refresh();
                });
            }
            menu.Items.Add(new Separator());
            Add("Export…", Export);
            Add("Print…", () => Printing.Print(this, selected.Where(i => !i.IsVideo).ToList(), _catalog.Data), photos.Count > 0);
            Add("Show in File Explorer", () => ShowInFolder(selected[0]), one);
            Add("Copy the file path", () => Ui.Copy(string.Join(Environment.NewLine, selected.Select(i => i.Path))));
            menu.Items.Add(new Separator());
            Add(one ? "Move to the Recycle Bin…" : $"Move {selected.Count:N0} to the Recycle Bin…", () => Delete(selected));
            return menu;
        }

        /// <summary>Turns photos a quarter (kept as a fix; the files aren't changed).</summary>
        public void Turn(IList<MediaItem> photos, bool right)
        {
            foreach (var p in photos)
            {
                var e = _catalog.Data.Edit(p.Path) ?? new EditRecipe();
                e.Turn(right);
                _catalog.Data.SetEdit(p.Path, e);
            }
        }

        public void ShowInFolder(MediaItem item)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.Path + "\""); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { }
        }

        /// <summary>Moves photos to the Recycle Bin, after asking.</summary>
        public bool Delete(IList<MediaItem> items, bool ask = true)
        {
            if (items.Count == 0) return false;
            var what = items.Count == 1 ? $"“{items[0].Name}”" : Format.Count(items.Count, "item");
            if (ask && !Dialogs.Confirm(this, $"Move {what} to the Recycle Bin?\n\nYou can get {(items.Count == 1 ? "it" : "them")} back from the Recycle Bin. Stars, tags and albums for {(items.Count == 1 ? "it" : "them")} are forgotten."))
                return false;
            var failed = _catalog.Recycle(items);
            if (failed.Count > 0) Dialogs.Error(this, $"{Format.Count(failed.Count, "file")} couldn't be moved to the Recycle Bin (in use, or Windows was asked to keep {(failed.Count == 1 ? "it" : "them")}):\n\n" + string.Join("\n", failed.Take(8).Select(f => f.Path)));
            _status.Text = $"Moved {Format.Count(items.Count - failed.Count, "item")} to the Recycle Bin";
            return failed.Count == 0;
        }

        private void Export()
        {
            var items = _grid.SelectedInOrder;
            if (items.Count == 0) items = _grid.Items.ToList();
            if (items.Count == 0)
            {
                Dialogs.Info(this, "There's nothing here to export. Select photos, or choose a part of the library with some in it.");
                return;
            }
            new ExportWindow(this, items, _catalog.Data).ShowDialog();
        }

        public void Slideshow(MediaItem start = null)
        {
            var photos = (_grid.Selected.Count > 1 ? _grid.SelectedInOrder : _grid.Items.ToList()).Where(i => !i.IsVideo).ToList();
            if (photos.Count == 0)
            {
                Dialogs.Info(this, "There are no photos here for a slideshow.");
                return;
            }
            var index = start == null ? 0 : Math.Max(0, photos.IndexOf(start));
            new SlideshowWindow(photos, index, _catalog.Data).Show();
        }

        public void ShowSettings(bool folders)
        {
            var before = string.Join("\n", _catalog.Folders);
            new SettingsWindow(this, folders).ShowDialog();
            if (string.Join("\n", _catalog.Folders) != before)
            {
                _catalog.FoldersChanged();
                FillSide();
                Refresh();
            }
        }

        public void SetStatus(string text) => _status.Text = text;

        // ================================================================== the viewer

        /// <summary>Opens one photo on its own (from a face on the People page).</summary>
        public void OpenAlone(MediaItem item)
        {
            Open(item);
            _viewer.Show(new List<MediaItem> { item }, item);
        }

        private void UpdatePersonButton()
        {
            var person = Kind == ViewKind.Person ? _catalog.People.Find(ViewKey) : null;
            _personButton.Visibility = person == null ? Visibility.Collapsed : Visibility.Visible;
            if (person == null) return;
            var asks = _people.Groups.Ask.TryGetValue(person, out var list) ? list.Count : 0;
            _personButton.Content = Ui.Label("IconUser", asks > 0 ? $"Is this {person.Name}? ({asks:N0})" : "Faces and questions");
        }

        public void Open(MediaItem item)
        {
            if (item == null) return;
            if (_viewer == null)
            {
                _viewer = new ViewerView(this);
                _viewer.Closed += CloseViewer;
                _root.Children.Add(_viewer.At(0));
                System.Windows.Controls.Grid.SetRowSpan(_viewer, 4);
            }
            _viewer.Visibility = Visibility.Visible;
            _viewer.Show(_grid.Items.ToList(), item);
        }

        public void CloseViewer()
        {
            if (_viewer == null || _viewer.Visibility != Visibility.Visible) return;
            var item = _viewer.Item;
            _viewer.Stop();
            _viewer.Visibility = Visibility.Collapsed;
            if (item != null && _grid.Items.Contains(item))
            {
                _grid.Select(item);
                _grid.ScrollTo(item);
            }
            _grid.Focus();
        }

        public bool ViewerOpen => _viewer != null && _viewer.Visibility == Visibility.Visible;

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (ViewerOpen)
            {
                _viewer.OnKey(e);
                return;
            }
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                Slideshow();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && !(Keyboard.FocusedElement is TextBox) && _grid.Selected.Count > 0)
            {
                Delete(_grid.SelectedInOrder);
                e.Handled = true;
            }
            else if (!(Keyboard.FocusedElement is TextBox) && _grid.Selected.Count > 0 && e.Key >= Key.D0 && e.Key <= Key.D5 && Keyboard.Modifiers == ModifierKeys.None)
            {
                _catalog.Data.SetStars(_grid.SelectedInOrder.Select(i => i.Path), e.Key - Key.D0);
                ShowInfo();
                e.Handled = true;
            }
        }
    }
}
