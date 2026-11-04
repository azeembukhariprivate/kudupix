using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kudupix.Controls;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>A row of the grid: a day's (or month's) heading, or a row of pictures.</summary>
    public sealed class GridRow
    {
        public PhotoGrid Owner { get; set; }
        public string Title { get; set; }
        public string Note { get; set; }
        public List<MediaItem> Items { get; set; }
        public double TileWidth { get; set; }
        public double TileHeight { get; set; }
        public bool IsHeader => Items == null;
    }

    /// <summary>
    /// The photos and videos as a grid of small pictures under day (or month) headings. Only the rows on screen are made,
    /// so tens of thousands of photos scroll smoothly. Click to select, Ctrl+click to add, Shift+click for a run,
    /// double-click or Enter to open; the arrow keys move.
    /// </summary>
    public sealed class PhotoGrid : Grid
    {
        public const double Gap = 6;
        private readonly ListBox _list;
        private readonly List<GridRow> _rows = new List<GridRow>();
        private List<Timeline.Group> _groups = new List<Timeline.Group>();
        private List<MediaItem> _items = new List<MediaItem>();
        private readonly Dictionary<MediaItem, int> _rowOf = new Dictionary<MediaItem, int>();
        private double _tileSize = 160;
        private double _laidOutWidth;

        public HashSet<MediaItem> Selected { get; } = new HashSet<MediaItem>();
        public MediaItem Current { get; private set; }
        private MediaItem _anchor;

        /// <summary>Stars, fixes: asked for each tile.</summary>
        public Func<MediaItem, int> StarsOf { get; set; } = i => 0;
        public Func<MediaItem, EditRecipe> EditOf { get; set; } = i => null;

        public event Action SelectionChanged;
        public event Action<MediaItem> Opened;
        /// <summary>A right-click on a tile: the menu for the selection.</summary>
        public Func<ContextMenu> MenuFor { get; set; }

        public IReadOnlyList<MediaItem> Items => _items;
        public IReadOnlyList<GridRow> Rows => _rows;
        public int Columns { get; private set; } = 1;

        public PhotoGrid()
        {
            _list = new ListBox { Focusable = true }.Styled("TableList");
            _list.Padding = new Thickness(18, 0, 10, 12);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
            ScrollViewer.SetCanContentScroll(_list, true);
            var plain = new Style(typeof(ListBoxItem));
            plain.Setters.Add(new Setter(FocusableProperty, false));
            plain.Setters.Add(new Setter(Control.TemplateProperty, PlainItem()));
            _list.ItemContainerStyle = plain;
            _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(RowView)) };
            _list.ItemsSource = _rows;
            System.Windows.Automation.AutomationProperties.SetName(_list, "Photos and videos");
            Children.Add(_list);
            _list.SizeChanged += (s, e) =>
            {
                if (Math.Abs(e.NewSize.Width - _laidOutWidth) > 2) Layout();
            };
            _list.PreviewKeyDown += OnKey;
            _list.PreviewMouseRightButtonUp += (s, e) =>
            {
                if (Selected.Count == 0) return;
                var menu = MenuFor?.Invoke();
                if (menu == null) return;
                menu.PlacementTarget = _list;
                menu.IsOpen = true;
                e.Handled = true;
            };
        }

        private static ControlTemplate PlainItem()
        {
            var t = new ControlTemplate(typeof(ListBoxItem));
            t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            return t;
        }

        public double TileSize
        {
            get => _tileSize;
            set
            {
                if (Math.Abs(_tileSize - value) < 0.5) return;
                _tileSize = value;
                Layout();
                if (Current != null) ScrollTo(Current);
            }
        }

        /// <summary>Shows these groups (keeping what's selected, where it's still there).</summary>
        public void Show(List<Timeline.Group> groups)
        {
            _groups = groups;
            _items = groups.SelectMany(g => g.Items).ToList();
            var present = new HashSet<MediaItem>(_items);
            Selected.RemoveWhere(i => !present.Contains(i));
            if (Current != null && !present.Contains(Current)) Current = null;
            Layout();
            SelectionChanged?.Invoke();
        }

        private void Layout()
        {
            var width = _list.ActualWidth - _list.Padding.Left - _list.Padding.Right - SystemParameters.VerticalScrollBarWidth - 2;
            if (width <= 0) width = 800;
            _laidOutWidth = _list.ActualWidth;
            var n = Math.Max(1, (int)((width + Gap) / (_tileSize + Gap)));
            Columns = n;
            var tileW = Math.Floor((width - Gap * (n - 1)) / n);
            var tileH = Math.Round(_tileSize * 0.78);
            _rows.Clear();
            _rowOf.Clear();
            foreach (var g in _groups)
            {
                _rows.Add(new GridRow { Owner = this, Title = g.Title, Note = g.Note });
                for (var i = 0; i < g.Items.Count; i += n)
                {
                    var row = new GridRow { Owner = this, Items = g.Items.Skip(i).Take(n).ToList(), TileWidth = tileW, TileHeight = tileH };
                    foreach (var item in row.Items) _rowOf[item] = _rows.Count;
                    _rows.Add(row);
                }
            }
            _list.Items.Refresh();
        }

        /// <summary>Draws the tiles again (stars or fixes changed).</summary>
        public void RefreshTiles()
        {
            foreach (var v in FindRows(_list)) v.Build();
        }

        private static IEnumerable<RowView> FindRows(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is RowView r) yield return r;
                else foreach (var x in FindRows(child)) yield return x;
            }
        }

        public void ScrollTo(MediaItem item)
        {
            if (item == null || !_rowOf.TryGetValue(item, out var row)) return;
            // The heading too, when it's the first row of a day.
            var target = row > 0 && _rows[row - 1].IsHeader ? row - 1 : row;
            _list.ScrollIntoView(_rows[target]);
            _list.ScrollIntoView(_rows[row]);
        }

        // ------------------------------------------------------------------ selecting

        public void Select(MediaItem item, bool add = false, bool range = false)
        {
            if (range && _anchor != null)
            {
                var a = _items.IndexOf(_anchor);
                var b = _items.IndexOf(item);
                if (a >= 0 && b >= 0)
                {
                    if (!add) Selected.Clear();
                    for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) Selected.Add(_items[i]);
                }
            }
            else if (add)
            {
                if (!Selected.Remove(item)) Selected.Add(item);
                _anchor = item;
            }
            else
            {
                Selected.Clear();
                Selected.Add(item);
                _anchor = item;
            }
            Current = item;
            RefreshTiles();
            SelectionChanged?.Invoke();
        }

        public void SelectAll()
        {
            foreach (var i in _items) Selected.Add(i);
            RefreshTiles();
            SelectionChanged?.Invoke();
        }

        public void SelectNone()
        {
            Selected.Clear();
            RefreshTiles();
            SelectionChanged?.Invoke();
        }

        /// <summary>The selected items in the order shown.</summary>
        public List<MediaItem> SelectedInOrder => _items.Where(Selected.Contains).ToList();

        internal void TileClicked(MediaItem item, MouseButtonEventArgs e)
        {
            _list.Focus();
            if (e.ChangedButton == MouseButton.Right)
            {
                if (!Selected.Contains(item)) Select(item);
                return;
            }
            if (e.ClickCount == 2)
            {
                Select(item);
                Opened?.Invoke(item);
                return;
            }
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Select(item, ctrl, shift);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_items.Count == 0) return;
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (ctrl && e.Key == Key.A)
            {
                SelectAll();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && Current != null)
            {
                Opened?.Invoke(Current);
                e.Handled = true;
                return;
            }
            var index = Current == null ? -1 : _items.IndexOf(Current);
            int next;
            switch (e.Key)
            {
                case Key.Right: next = index + 1; break;
                case Key.Left: next = index - 1; break;
                case Key.Down: next = Below(index, 1); break;
                case Key.Up: next = Below(index, -1); break;
                case Key.Home: next = 0; break;
                case Key.End: next = _items.Count - 1; break;
                default: return;
            }
            next = Math.Max(0, Math.Min(_items.Count - 1, next));
            Select(_items[next], false, shift);
            ScrollTo(_items[next]);
            e.Handled = true;
        }

        /// <summary>The item in the same column of the row below (or above).</summary>
        private int Below(int index, int direction)
        {
            if (index < 0) return 0;
            var item = _items[index];
            if (!_rowOf.TryGetValue(item, out var row)) return index;
            var column = _rows[row].Items.IndexOf(item);
            for (var r = row + direction; r >= 0 && r < _rows.Count; r += direction)
            {
                if (_rows[r].IsHeader) continue;
                var target = _rows[r].Items[Math.Min(column, _rows[r].Items.Count - 1)];
                return _items.IndexOf(target);
            }
            return index;
        }
    }

    /// <summary>One row of the grid, made when it comes on screen (and re-used for another row when scrolled away).</summary>
    public sealed class RowView : StackPanel
    {
        public RowView()
        {
            Orientation = Orientation.Horizontal;
            DataContextChanged += (s, e) => Build();
        }

        public void Build()
        {
            Children.Clear();
            if (!(DataContext is GridRow row)) return;
            Margin = new Thickness(0, 0, 0, row.IsHeader ? 0 : PhotoGrid.Gap);
            if (row.IsHeader)
            {
                var title = Ui.Text(row.Title, 14, bold: true);
                var note = Ui.Subtle(row.Note, 12, false).Margin(10, 0, 0, 0);
                note.VerticalAlignment = VerticalAlignment.Bottom;
                Children.Add(Ui.Row(title, note).Margin(0, 16, 0, 8));
                return;
            }
            foreach (var item in row.Items)
            {
                var tile = new Tile(row.Owner, item, row.TileWidth, row.TileHeight);
                tile.Margin = new Thickness(0, 0, PhotoGrid.Gap, 0);
                Children.Add(tile);
            }
        }
    }

    /// <summary>One small picture, with its marks: star, video length, fixed, selected.</summary>
    public sealed class Tile : Grid
    {
        public MediaItem Item { get; }

        public Tile(PhotoGrid owner, MediaItem item, double width, double height)
        {
            Item = item;
            Width = width;
            Height = height;
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            this.Res(BackgroundProperty, "SurfaceAltBg");
            ToolTip = item.Name + "\n" + item.When.ToString("d MMM yyyy, HH:mm") + (item.IsVideo && item.Seconds > 0 ? " · " + VideoLength.Text(item.Seconds) : string.Empty);
            System.Windows.Automation.AutomationProperties.SetName(this, item.Name);

            var kind = Ui.Icon(item.IsVideo ? "IconVideo" : "IconPicture", 26);
            kind.HorizontalAlignment = HorizontalAlignment.Center;
            kind.Opacity = 0.4;
            kind.Res(TextElement.ForegroundProperty, "SubtleText");
            Children.Add(kind);

            var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Children.Add(image);
            var edit = owner.EditOf(item);
            var ready = Thumbs.Get(item, edit, b =>
            {
                if (b != null) image.Source = b;
            });
            if (ready != null) image.Source = ready;

            var stars = owner.StarsOf(item);
            if (stars > 0)
            {
                var star = new TextBlock { Text = stars > 1 ? "★ " + stars : "★", Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4A)), FontSize = 13, Margin = new Thickness(7, 0, 0, 5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
                star.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.7 };
                Children.Add(star);
            }
            if (item.IsVideo)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(6, 1, 7, 1),
                    Margin = new Thickness(0, 0, 6, 6),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = Ui.Row(new Glyph { Data = Application.Current.TryFindResource("IconPlay") as Geometry, Width = 9, Height = 9 }.Margin(0, 0, 4, 0),
                        new TextBlock { Text = item.Seconds > 0 ? VideoLength.Text(item.Seconds) : "Video", FontSize = 11, Foreground = Brushes.White })
                };
                TextElement.SetForeground(badge, Brushes.White);
                Children.Add(badge);
            }
            if (edit != null && !edit.IsEmpty)
            {
                var mark = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                    CornerRadius = new CornerRadius(9),
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 6, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = "Fixed in KUDUPIX (the file itself isn't changed until you save)",
                    Child = new Glyph { Data = Application.Current.TryFindResource("IconEdit") as Geometry, Width = 11, Height = 11 }
                };
                TextElement.SetForeground(mark, Brushes.White);
                Children.Add(mark);
            }
            if (owner.Selected.Contains(item))
            {
                var outline = new Border { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(3) }.Res(Border.BorderBrushProperty, "Accent");
                Children.Add(outline);
                var tick = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }
                    .Res(Border.BackgroundProperty, "Accent");
                tick.Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Children.Add(tick);
            }
            MouseLeftButtonDown += (s, e) => owner.TileClicked(item, e);
            MouseRightButtonDown += (s, e) => owner.TileClicked(item, e);
        }
    }
}
