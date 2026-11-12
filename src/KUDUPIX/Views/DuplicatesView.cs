using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kudupix.Controls;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>A group as the duplicates list shows it: which copies are kept.</summary>
    public sealed class DuplicateRow
    {
        public DuplicatesView Owner { get; set; }
        public DuplicateGroup Group { get; set; }
        public HashSet<MediaItem> Keep { get; } = new HashSet<MediaItem>();
    }

    /// <summary>
    /// Copies of the same photo or video: exact copies, and the same photo saved smaller or sent through a messaging app.
    /// The best copy is ticked to keep; the others go to the Recycle Bin (so they can be brought back).
    /// </summary>
    public sealed class DuplicatesView : Grid
    {
        private readonly MainWindow _main;
        private readonly TextBlock _summary = Ui.Subtle(string.Empty, 13, false);
        private readonly ListBox _list;
        private readonly Button _remove;
        private readonly Button _keepBest;
        private readonly StackPanel _busy = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _busyText = Ui.Text(string.Empty, 14);
        private readonly ProgressBar _progress = new ProgressBar { Width = 320, Height = 6, Margin = new Thickness(0, 12, 0, 0) };
        private List<DuplicateRow> _rows = new List<DuplicateRow>();
        private readonly HashSet<string> _notCopies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _cancel;
        private int _libraryCount = -1;

        public bool Working { get; private set; }
        public int GroupCount => _rows.Count;
        public IReadOnlyList<DuplicateRow> Rows => _rows;

        public DuplicatesView(MainWindow main)
        {
            _main = main;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            var bar = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
            bar.Margin = new Thickness(24, 14, 24, 6);
            bar.Children.Add(Ui.Text("Duplicates", 18, bold: true).At(0, 0));
            bar.Children.Add(_summary.Margin(14, 0, 0, 0).At(0, 1));
            _keepBest = Ui.Small(Ui.Button("Keep the best in every group", null, "SecondaryButton", (s, e) => KeepBest()));
            _keepBest.ToolTip = "The biggest (most pixels), and the one whose name doesn't look like a copy";
            bar.Children.Add(_keepBest.Margin(8, 0, 0, 0).At(0, 2));
            _remove = Ui.Small(Ui.Button("Move the others to the Recycle Bin", "IconTrash", "PrimaryButton", (s, e) => RemoveOthers()));
            bar.Children.Add(_remove.Margin(8, 0, 0, 0).At(0, 3));
            Children.Add(bar.At(0));

            _list = new ListBox().Styled("TableList");
            _list.Padding = new Thickness(24, 0, 24, 16);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
            var plain = new Style(typeof(ListBoxItem));
            plain.Setters.Add(new Setter(FocusableProperty, false));
            var t = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            plain.Setters.Add(new Setter(Control.TemplateProperty, t));
            _list.ItemContainerStyle = plain;
            _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(DuplicateRowView)) };
            System.Windows.Automation.AutomationProperties.SetName(_list, "Duplicates");
            Children.Add(_list.At(1));

            _busy.Children.Add(_busyText);
            _busy.Children.Add(_progress);
            var stop = Ui.Link("Stop", (s, e) => _cancel?.Cancel());
            stop.HorizontalAlignment = HorizontalAlignment.Center;
            _busy.Children.Add(stop.Margin(0, 10, 0, 0));
            Children.Add(_busy.At(1));
            _busy.Visibility = Visibility.Collapsed;
        }

        /// <summary>Looks for copies (again, when the library changed since last time).</summary>
        public async void Start()
        {
            if (Working) return;
            var all = _main.Catalog.Library.All();
            if (_libraryCount == all.Count && _rows.Count > 0) { Fill(); return; }
            _libraryCount = all.Count;
            Working = true;
            _cancel = new CancellationTokenSource();
            var cancel = _cancel;
            _busy.Visibility = Visibility.Visible;
            _list.Visibility = Visibility.Collapsed;
            _remove.IsEnabled = false;
            _keepBest.IsEnabled = false;
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                _busyText.Text = $"Getting photos ready to compare… {p.Done:N0} of {p.Total:N0}";
                _progress.Maximum = Math.Max(1, p.Total);
                _progress.Value = p.Done;
            });
            _busyText.Text = "Looking for copies…";
            List<DuplicateGroup> groups = null;
            try
            {
                groups = await Task.Run(() =>
                {
                    // Each photo's fingerprint, once (kept in the library for next time).
                    var photos = all.Where(i => i.Kind == MediaKind.Photo && i.Look == 0).ToList();
                    var done = 0;
                    Parallel.ForEach(photos, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = cancel.Token }, p =>
                    {
                        try { p.Look = Imaging.Look(p); }
                        catch (Exception) { p.Look = 0; }
                        var n = Interlocked.Increment(ref done);
                        if (n % 25 == 0 || n == photos.Count) ((IProgress<(int, int)>)progress).Report((n, photos.Count));
                    });
                    return Core.Duplicates.Find(all, i =>
                    {
                        if (string.IsNullOrEmpty(i.Hash)) i.Hash = Core.Duplicates.FileHash(i.Path, cancel.Token);
                        return i.Hash;
                    }, cancel.Token);
                }, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                _libraryCount = -1;
            }
            finally
            {
                Working = false;
                _busy.Visibility = Visibility.Collapsed;
                _list.Visibility = Visibility.Visible;
                _main.Catalog.LibraryTouched();
            }
            if (groups != null)
            {
                _rows = groups.Where(g => !_notCopies.Contains(Key(g))).Select(g =>
                {
                    var row = new DuplicateRow { Owner = this, Group = g };
                    row.Keep.Add(g.Keep);
                    return row;
                }).ToList();
            }
            Fill();
            _main.FillSide();
        }

        private static string Key(DuplicateGroup g) => string.Join("|", g.Items.Select(i => i.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        private void Fill()
        {
            _list.ItemsSource = null;
            _list.ItemsSource = _rows;
            var removing = _rows.SelectMany(r => r.Group.Items.Where(i => !r.Keep.Contains(i))).ToList();
            _summary.Text = _rows.Count == 0
                ? "No copies found"
                : $"{Format.Count(_rows.Count, "group")} · {Format.Size(removing.Sum(i => i.Size))} could be freed";
            _remove.IsEnabled = removing.Count > 0;
            _keepBest.IsEnabled = _rows.Count > 0;
            if (_rows.Count == 0 && !Working)
            {
                _busyText.Text = "No copies found. Every photo and video here is different.";
                _busy.Children[1].Visibility = Visibility.Collapsed;
                _busy.Children[2].Visibility = Visibility.Collapsed;
                _busy.Visibility = Visibility.Visible;
            }
            else
            {
                _busy.Children[1].Visibility = Visibility.Visible;
                _busy.Children[2].Visibility = Visibility.Visible;
            }
        }

        internal void Toggle(DuplicateRow row, MediaItem item)
        {
            if (!row.Keep.Remove(item)) row.Keep.Add(item);
            if (row.Keep.Count == 0) row.Keep.Add(item); // at least one copy always stays
            Fill();
        }

        internal void NotCopies(DuplicateRow row)
        {
            _notCopies.Add(Key(row.Group));
            _rows.Remove(row);
            Fill();
            _main.FillSide();
        }

        public void KeepBest()
        {
            foreach (var r in _rows)
            {
                r.Keep.Clear();
                r.Keep.Add(r.Group.Keep);
            }
            Fill();
        }

        /// <summary>Moves every copy not ticked to the Recycle Bin, after asking.</summary>
        public void RemoveOthers(bool ask = true)
        {
            var removing = _rows.SelectMany(r => r.Group.Items.Where(i => !r.Keep.Contains(i))).ToList();
            if (removing.Count == 0) return;
            if (ask && !Dialogs.Confirm(Window.GetWindow(this),
                    $"Move {Format.Count(removing.Count, "copy", "copies")} ({Format.Size(removing.Sum(i => i.Size))}) to the Recycle Bin?\n\nThe ticked copy of each stays. You can get the others back from the Recycle Bin.",
                    "Remove copies"))
                return;
            var failed = _main.Catalog.Recycle(removing);
            var gone = new HashSet<MediaItem>(removing.Except(failed));
            foreach (var r in _rows) r.Group.Items.RemoveAll(gone.Contains);
            _rows = _rows.Where(r => r.Group.Items.Count > 1).ToList();
            _libraryCount = _main.Catalog.Library.Count;
            Fill();
            _main.FillSide();
            _main.SetStatus($"Moved {Format.Count(gone.Count, "copy", "copies")} to the Recycle Bin");
            if (failed.Count > 0) Dialogs.Error(Window.GetWindow(this), $"{Format.Count(failed.Count, "file")} couldn't be moved (in use, or Windows was asked to keep them).");
        }
    }

    /// <summary>One group of copies, side by side.</summary>
    public sealed class DuplicateRowView : Border
    {
        public DuplicateRowView()
        {
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(0, 14, 0, 14);
            this.Res(BorderBrushProperty, "BorderBrush");
            DataContextChanged += (s, e) => Build();
        }

        private void Build()
        {
            if (!(DataContext is DuplicateRow row)) { Child = null; return; }
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var data = row.Owner == null ? null : Window.GetWindow(row.Owner) is MainWindow w ? w.Catalog.Data : null;
            foreach (var item in row.Group.Items)
            {
                var keep = row.Keep.Contains(item);
                var image = new Image { Stretch = Stretch.UniformToFill };
                var pic = new Grid { Width = 200, Height = 132, ClipToBounds = true, Cursor = Cursors.Hand, ToolTip = item.Path + "\n\nClick to keep or not keep this one" }.Res(Panel.BackgroundProperty, "SurfaceAltBg");
                pic.Children.Add(image);
                var ready = Thumbs.Get(item, data?.Edit(item.Path), b => { if (b != null) image.Source = b; });
                if (ready != null) image.Source = ready;
                if (keep)
                {
                    pic.Children.Add(new Border { BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(3) }.Res(Border.BorderBrushProperty, "Accent"));
                    var tick = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }.Res(Border.BackgroundProperty, "Accent");
                    tick.Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    pic.Children.Add(tick);
                }
                else pic.Opacity = 0.75;
                pic.MouseLeftButtonUp += (s, e) => row.Owner.Toggle(row, item);
                var name = Ui.Text(ShortPath(item.Path), 12);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.MaxWidth = 240;
                name.HorizontalAlignment = HorizontalAlignment.Center;
                var size = Ui.Subtle((keep ? Lang.T("Keep") + " · " : string.Empty) + (item.ShownWidth > 0 ? $"{item.ShownWidth} × {item.ShownHeight} · " : string.Empty) + Format.Size(item.Size), 11, false);
                size.HorizontalAlignment = HorizontalAlignment.Center;
                var col = new StackPanel { Width = 250 };
                col.Children.Add(pic);
                pic.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(name.Margin(0, 6, 0, 0));
                col.Children.Add(size);
                panel.Children.Add(col);
            }
            var first = row.Group.Items.FirstOrDefault(i => i.Taken.HasValue);
            var about = Ui.Column(
                Ui.Subtle(row.Group.Exact ? "Exact copies" : "Same photo", 12, false),
                Ui.Subtle(first?.Taken != null ? "Taken " + first.Taken.Value.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) : string.Empty, 12, false));
            var not = Ui.Link("Not copies: leave these", (s, e) => row.Owner.NotCopies(row));
            not.HorizontalAlignment = HorizontalAlignment.Left;
            about.Children.Add(not.Margin(0, 6, 0, 0));
            about.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(about.Margin(12, 0, 0, 0));
            Child = panel;
        }

        private static string ShortPath(string path)
        {
            var parts = path.Split('\\');
            return parts.Length <= 3 ? path : string.Join(" › ", parts.Skip(parts.Length - 3));
        }
    }
}
