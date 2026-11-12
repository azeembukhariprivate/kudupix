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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kudupix.Controls;
using Kudupix.Core;

namespace Kudupix.Views
{
    public sealed class SettingsWindow : Window
    {
        private readonly StackPanel _folders = new StackPanel();
        private readonly List<string> _list;

        public SettingsWindow(Window owner, bool foldersFirst = false)
        {
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUPIX", owner);
            _list = Roots.Clean(Settings.Folders);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading(foldersFirst ? "Folders" : "Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Folders KUDUPIX looks in", 14).Margin(0, 0, 0, 4));
            root.Children.Add(Ui.Subtle("Photos and videos in these folders (and the folders inside them) are shown. They're only read: KUDUPIX never moves or changes them.", 12).Margin(0, 0, 0, 8));
            root.Children.Add(_folders);
            var add = Ui.Small(Ui.Button("Add a folder…", "IconPlus", "SecondaryButton", (s, e) => AddFolder()));
            var usual = Ui.Link("Add the usual ones (Pictures, Videos, OneDrive)", (s, e) =>
            {
                foreach (var f in Settings.UsualFolders()) _list.Add(f);
                SaveFolders();
            });
            root.Children.Add(Ui.Row(add, usual.Margin(14, 0, 0, 0)).Margin(0, 8, 0, 0));
            FillFolders();

            if (!foldersFirst)
            {
                root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 18, 0, 8));
                var themes = new WrapPanel();
                foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
                {
                    var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }.Styled("ChipRadio");
                    chip.Checked += (s, e) =>
                    {
                        Settings.Theme = mode;
                        ThemeManager.Apply(mode);
                    };
                    themes.Children.Add(chip);
                }
                root.Children.Add(themes);

                root.Children.Add(Ui.Heading("Language", 14).Margin(0, 16, 0, 6));
                root.Children.Add(LangUi.Picker());

                root.Children.Add(Ui.Heading("Space KUDUPIX uses", 14).Margin(0, 18, 0, 4));
                var thumbs = Ui.Subtle(string.Empty, 12.5, true);
                void ShowThumbs() => thumbs.Text = "Small pictures for the grid: " + Format.Size(Thumbs.SizeOnDisk()) + ". They're made again when needed.";
                ShowThumbs();
                var clear = Ui.Link("Remove them", (s, e) => { Thumbs.Clear(); ShowThumbs(); });
                var row1 = Ui.Columns(Ui.Star(), Ui.Auto);
                row1.Children.Add(thumbs.At(0, 0));
                row1.Children.Add(clear.Margin(12, 0, 0, 0).At(0, 1));
                root.Children.Add(row1);
                var kept = new Originals(AppPaths.Originals);
                var originals = Ui.Subtle($"Originals kept before “Save over the original”: {Format.Count(kept.All().Count, "photo")}, {Format.Size(kept.SizeOnDisk())}.", 12.5, true);
                var open = Ui.Link("Open the folder", (s, e) =>
                {
                    Directory.CreateDirectory(AppPaths.Originals);
                    AppInfo.OpenFolder(AppPaths.Originals);
                });
                var row2 = Ui.Columns(Ui.Star(), Ui.Auto);
                row2.Children.Add(originals.At(0, 0));
                row2.Children.Add(open.Margin(12, 0, 0, 0).At(0, 1));
                root.Children.Add(row2.Margin(0, 6, 0, 0));

                root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 18, 0, 4));
                var updates = new CheckBox { Content = "Check for a new KUDUPIX version when it starts", IsChecked = Settings.CheckForUpdates };
                updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
                root.Children.Add(updates);
                root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));
            }

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUPIX", (s, e) => About.Show(this)).At(0, 0));
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            bottom.Children.Add(close.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }

        public IReadOnlyList<string> Folders => _list;

        private void FillFolders()
        {
            _folders.Children.Clear();
            if (_list.Count == 0) _folders.Children.Add(Ui.Subtle("No folders yet: add one.", 12.5, false));
            foreach (var f in _list.ToList())
            {
                var folder = f;
                var row = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
                var icon = Ui.Icon("IconFolder", 15);
                icon.Res(TextElement.ForegroundProperty, "SubtleText");
                row.Children.Add(icon.At(0, 0));
                var name = Ui.Text(f, 12.5);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                if (!Directory.Exists(f)) name.Text += "  (not there now)";
                row.Children.Add(name.Margin(8, 0, 8, 0).At(0, 1));
                row.Children.Add(Ui.Link("Remove", (s, e) =>
                {
                    _list.RemoveAll(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase));
                    SaveFolders();
                }).At(0, 2));
                _folders.Children.Add(new Border { Child = row, Padding = new Thickness(0, 6, 0, 6), BorderThickness = new Thickness(0, 0, 0, 1) }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
        }

        private void AddFolder()
        {
            var folder = Dialogs.PickFolder(this, "Choose a folder with photos or videos");
            if (folder == null) return;
            AddFolder(folder);
        }

        public void AddFolder(string folder)
        {
            // KUDUPIX's own folder (small pictures, kept originals) is never shown as photos.
            if (Library.Inside(folder, AppPaths.Root)) return;
            _list.Add(folder);
            SaveFolders();
        }

        public void RemoveFolder(string folder)
        {
            _list.RemoveAll(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase));
            SaveFolders();
        }

        private void SaveFolders()
        {
            var clean = Roots.Clean(_list);
            _list.Clear();
            _list.AddRange(clean);
            Settings.Folders = clean;
            Settings.FoldersChosen = true;
            FillFolders();
        }
    }

    public static class About
    {
        public static Window Build(Window owner)
        {
            var header = Ui.Row(LogoTile.Create(64), Ui.Column(Ui.Text("KUDUPIX", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · Your photos and videos, organised", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(130), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            var madeBy = Ui.Text("Azeem, with Claude and ", 12.5);
            var heart = new Run("♥");
            heart.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            madeBy.Inlines.Add(heart);
            Row("Made by", madeBy);
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Pictures and videos", Ui.Text("Opened and played with Windows' own codecs and player", 12.5, wrap: true));
            Row("People", Ui.Text("Faces found and told apart on this PC with the YuNet (MIT) and SFace (Apache 2.0) models from OpenCV, run by ONNX Runtime (MIT)", 12.5, wrap: true));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUPIX works on your computer only. Your photos, videos and the faces in them never leave it, and it never changes them unless you choose “Save over the original” (which keeps the untouched photo first). The internet is used only to check for a new version, and to open a map when you ask.", 11.5)
                    .Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUPIX", content, 580, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUPIX {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            return window;
        }

        public static void Show(Window owner) => Build(owner).ShowDialog();

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }

    /// <summary>What an export makes.</summary>
    public sealed class ExportResult
    {
        public int Saved { get; set; }
        public List<string> Files { get; } = new List<string>();
        public List<string> Problems { get; } = new List<string>();
        public bool Cancelled { get; set; }
    }

    /// <summary>Copies to share: smaller, with the fixes, and (by default) without where they were taken. The originals are untouched.</summary>
    public sealed class ExportWindow : Window
    {
        private readonly IList<MediaItem> _items;
        private readonly UserData _data;
        private readonly TextBox _folder = new TextBox { MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly CheckBox _noPlace = new CheckBox { Content = "Leave out where the photos were taken" };
        private readonly Slider _quality = new Slider { Minimum = 60, Maximum = 100, Width = 200 };
        private readonly TextBlock _progress = Ui.Subtle(string.Empty, 12.5, true);
        private readonly Button _go;
        private int _size;
        private CancellationTokenSource _cancel;

        public static readonly (int Size, string Name)[] Sizes =
        {
            (0, "The size it is"), (2048, "Large (2048 pixels)"), (1600, "Medium (1600 pixels)"), (1024, "Small (1024 pixels)"), (640, "For email (640 pixels)")
        };

        public ExportWindow(Window owner, IList<MediaItem> items, UserData data)
        {
            _items = items;
            _data = data;
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Export · KUDUPIX", owner);
            var photos = items.Count(i => !i.IsVideo);
            var videos = items.Count - photos;

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Export", 20));
            var what = photos > 0 && videos > 0 ? Lang.T("{0} and {1}", Format.Count(photos, "photo"), Format.Count(videos, "video"))
                : photos > 0 ? Format.Count(photos, "photo") : Format.Count(videos, "video");
            root.Children.Add(Ui.Subtle($"Copies of {what}, to share or keep elsewhere. Photos get their fixes; videos are copied as they are. The originals stay as they are.", 12.5).Margin(0, 4, 0, 14));

            root.Children.Add(Ui.Text("Photo size (the longest side)", 12.5, bold: true).Margin(0, 0, 0, 6));
            var sizes = new WrapPanel();
            _size = Settings.ExportSize;
            if (!Sizes.Any(s => s.Size == _size)) _size = 1600;
            foreach (var (size, name) in Sizes)
            {
                var value = size;
                var chip = new RadioButton { Content = name, GroupName = "size", IsChecked = _size == size }.Styled("ChipRadio");
                chip.Checked += (s, e) => _size = value;
                sizes.Children.Add(chip);
            }
            root.Children.Add(sizes);

            var qualityText = Ui.Subtle(string.Empty, 12.5, false);
            _quality.Value = Settings.ExportQuality;
            void ShowQuality() => qualityText.Text = (int)_quality.Value >= 92 ? "Best" : (int)_quality.Value >= 80 ? "Good (smaller files)" : "Smallest files";
            ShowQuality();
            _quality.ValueChanged += (s, e) => ShowQuality();
            System.Windows.Automation.AutomationProperties.SetName(_quality, "Quality");
            root.Children.Add(Ui.Row(Ui.Text("Quality", 12.5, bold: true), _quality.Margin(14, 0, 10, 0), qualityText).Margin(0, 14, 0, 0));

            _noPlace.IsChecked = Settings.ExportWithoutPlace;
            root.Children.Add(_noPlace.Margin(0, 14, 0, 0));
            root.Children.Add(Ui.Subtle("Phones store where each photo was taken. Leaving it out is safer when sharing.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Text("Save them in", 12.5, bold: true).Margin(0, 14, 0, 6));
            var folderRow = Ui.Columns(Ui.Star(), Ui.Auto);
            _folder.Text = Settings.ExportFolder.Length > 0 && Directory.Exists(Settings.ExportFolder)
                ? Settings.ExportFolder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KUDUPIX export");
            folderRow.Children.Add(_folder.At(0, 0));
            folderRow.Children.Add(Ui.Button("Choose…", null, "SecondaryButton", (s, e) =>
            {
                var f = Dialogs.PickFolder(this, "Where to save the copies", _folder.Text);
                if (f != null) _folder.Text = f;
            }).Margin(8, 0, 0, 0).At(0, 1));
            root.Children.Add(folderRow);
            root.Children.Add(_progress.Margin(0, 12, 0, 0));

            _go = Ui.Button("Export", "IconExport", "PrimaryButton", async (s, e) => await Go());
            var cancel = Ui.Button("Close", null, "SecondaryButton", (s, e) =>
            {
                if (_cancel != null) _cancel.Cancel();
                else Close();
            });
            cancel.IsCancel = true;
            var buttons = Ui.Row(cancel, _go.Margin(8, 0, 0, 0));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(buttons.Margin(0, 18, 0, 0));
            Content = root;
            Closing += (s, e) => _cancel?.Cancel();
        }

        private async Task Go()
        {
            var options = new ExportOptions { Folder = _folder.Text.Trim(), MaxSide = _size, Quality = (int)_quality.Value, KeepPlace = _noPlace.IsChecked != true };
            Settings.ExportSize = _size;
            Settings.ExportQuality = options.Quality;
            Settings.ExportWithoutPlace = !options.KeepPlace;
            Settings.ExportFolder = options.Folder;
            _go.IsEnabled = false;
            var result = await Run(options);
            _go.IsEnabled = true;
            if (result.Cancelled) _progress.Text = $"Stopped: {Format.Count(result.Saved, "copy", "copies")} saved.";
            else
            {
                _progress.Text = $"Saved {Format.Count(result.Saved, "copy", "copies")} in {options.Folder}." + (result.Problems.Count > 0 ? $" {Format.Count(result.Problems.Count, "file")} couldn't be saved: " + string.Join("; ", result.Problems.Take(3)) : string.Empty);
                if (result.Saved > 0 && !SelfTest.Running) AppInfo.OpenFolder(options.Folder);
            }
        }

        public sealed class ExportOptions
        {
            public string Folder { get; set; }
            public int MaxSide { get; set; }
            public int Quality { get; set; } = 88;
            public bool KeepPlace { get; set; }
        }

        /// <summary>Makes the copies (also used by the self-test). Never replaces a file that's there.</summary>
        public async Task<ExportResult> Run(ExportOptions options)
        {
            var result = new ExportResult();
            if (string.IsNullOrEmpty(options.Folder)) { result.Problems.Add("No folder chosen"); return result; }
            Directory.CreateDirectory(options.Folder);
            _cancel = new CancellationTokenSource();
            var cancel = _cancel;
            var items = _items.ToList();
            var edits = items.ToDictionary(i => i, i => _data.Edit(i.Path));
            try
            {
                await Task.Run(() =>
                {
                    for (var n = 0; n < items.Count; n++)
                    {
                        if (cancel.IsCancellationRequested) { result.Cancelled = true; return; }
                        var item = items[n];
                        var number = n + 1;
                        Dispatcher.BeginInvoke(new Action(() => _progress.Text = $"Saving {number:N0} of {items.Count:N0}: {item.Name}"));
                        try
                        {
                            string target;
                            if (item.IsVideo)
                            {
                                target = Names.Unique(Path.Combine(options.Folder, item.Name));
                                File.Copy(item.Path, target, false);
                            }
                            else
                            {
                                var edit = edits[item];
                                var unchanged = (edit == null || edit.IsEmpty) && options.MaxSide == 0 && options.KeepPlace;
                                if (unchanged)
                                {
                                    target = Names.Unique(Path.Combine(options.Folder, item.Name));
                                    File.Copy(item.Path, target, false);
                                }
                                else
                                {
                                    var ext = MediaTypes.CanWrite(item.Path) && Path.GetExtension(item.Path).ToLowerInvariant() != ".gif" ? Path.GetExtension(item.Path) : ".jpg";
                                    target = Names.Unique(Path.Combine(options.Folder, Path.GetFileNameWithoutExtension(item.Name) + ext));
                                    var pixels = Imaging.ToPixels(Imaging.Decode(item.Path, item.Orientation, edit == null || edit.IsEmpty ? options.MaxSide : 0, item.Width, item.Height));
                                    if (edit != null && !edit.IsEmpty) pixels = edit.Apply(pixels);
                                    pixels = ImageOps.Shrink(pixels, options.MaxSide);
                                    Imaging.Save(pixels, item.Path, target, options.Quality, options.KeepPlace);
                                }
                            }
                            result.Saved++;
                            result.Files.Add(target);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is ArgumentException)
                        {
                            result.Problems.Add(item.Name + ": " + ex.Message);
                        }
                    }
                });
            }
            finally
            {
                _cancel = null;
            }
            return result;
        }
    }

    /// <summary>Photos one after another, full screen, fading from one to the next. Esc to stop, arrows to move, Space to pause.</summary>
    public sealed class SlideshowWindow : Window
    {
        private readonly IList<MediaItem> _photos;
        private readonly UserData _data;
        private readonly Image _a = new Image { Stretch = Stretch.Uniform };
        private readonly Image _b = new Image { Stretch = Stretch.Uniform, Opacity = 0 };
        private readonly TextBlock _caption = new TextBlock { Foreground = Brushes.White, FontSize = 13, Opacity = 0.7, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        private readonly DispatcherTimer _timer;
        private int _index;
        private bool _front;
        private bool _paused;

        public int Index => _index;
        public bool Showing => (_front ? _b : _a).Source != null;

        public SlideshowWindow(IList<MediaItem> photos, int start, UserData data)
        {
            _photos = photos;
            _data = data;
            _index = start;
            Title = "Slideshow · KUDUPIX";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Background = Brushes.Black;
            Topmost = true;
            Cursor = Cursors.None;
            var grid = new Grid();
            RenderOptions.SetBitmapScalingMode(_a, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(_b, BitmapScalingMode.HighQuality);
            grid.Children.Add(_a);
            grid.Children.Add(_b);
            grid.Children.Add(_caption);
            Content = grid;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Settings.SlideSeconds) };
            _timer.Tick += (s, e) => Next(1);
            KeyDown += (s, e) =>
            {
                switch (e.Key)
                {
                    case Key.Escape: Close(); break;
                    case Key.Right: Next(1); break;
                    case Key.Left: Next(-1); break;
                    case Key.Space:
                        _paused = !_paused;
                        if (_paused) _timer.Stop(); else _timer.Start();
                        _caption.Text = Caption() + (_paused ? "  ·  paused" : string.Empty);
                        break;
                }
            };
            MouseLeftButtonUp += (s, e) => Next(1);
            MouseRightButtonUp += (s, e) => Close();
            Loaded += (s, e) =>
            {
                ShowPhoto(false);
                _timer.Start();
            };
            Closed += (s, e) => _timer.Stop();
        }

        private string Caption()
        {
            var p = _photos[_index];
            return p.Name + "  ·  " + p.When.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) + $"  ·  {_index + 1} of {_photos.Count}";
        }

        public void Next(int step)
        {
            _index = (_index + step + _photos.Count) % _photos.Count;
            ShowPhoto(true);
            if (!_paused)
            {
                _timer.Stop();
                _timer.Start();
            }
        }

        private void ShowPhoto(bool fade)
        {
            var item = _photos[_index];
            var index = _index;
            var size = (int)Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            var edit = _data.Edit(item.Path);
            Task.Run(() =>
            {
                try
                {
                    var b = Imaging.Decode(item.Path, item.Orientation, size, item.Width, item.Height);
                    if (edit != null && !edit.IsEmpty) b = Imaging.ToBitmap(edit.Apply(Imaging.ToPixels(b)));
                    return b;
                }
                catch (Exception) { return null; }
            }).ContinueWith(t =>
            {
                if (index != _index || t.Result == null) return;
                var show = _front ? _a : _b;
                var hide = _front ? _b : _a;
                _front = !_front;
                show.Source = t.Result;
                _caption.Text = Caption();
                var time = fade ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
                show.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time));
                hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>Printing photos: each on its own page, as big as fits, with its fixes.</summary>
    public static class Printing
    {
        /// <summary>The pages for these photos on paper of this size (in 1/96 inch).</summary>
        public static FixedDocument Document(IList<MediaItem> photos, UserData data, Size page) => Document(photos, data, page, new List<string>());

        /// <summary>The pages, and in <paramref name="problems"/> the photos that couldn't be opened and why.</summary>
        public static FixedDocument Document(IList<MediaItem> photos, UserData data, Size page, List<string> problems)
        {
            var doc = new FixedDocument();
            doc.DocumentPaginator.PageSize = page;
            const double margin = 48;
            foreach (var p in photos)
            {
                BitmapSource b;
                try
                {
                    b = Imaging.Decode(p.Path, p.Orientation, 3000, p.Width, p.Height);
                    var edit = data.Edit(p.Path);
                    if (edit != null && !edit.IsEmpty) b = Imaging.ToBitmap(edit.Apply(Imaging.ToPixels(b)));
                }
                catch (Exception ex)
                {
                    problems.Add(p.Name + ": " + ex.Message);
                    continue;
                }
                var fixedPage = new FixedPage { Width = page.Width, Height = page.Height };
                var image = new Image { Source = b, Stretch = Stretch.Uniform, Width = page.Width - margin * 2, Height = page.Height - margin * 2 };
                FixedPage.SetLeft(image, margin);
                FixedPage.SetTop(image, margin);
                fixedPage.Children.Add(image);
                var content = new PageContent();
                ((System.Windows.Markup.IAddChild)content).AddChild(fixedPage);
                doc.Pages.Add(content);
            }
            return doc;
        }

        public static void Print(Window owner, IList<MediaItem> photos, UserData data)
        {
            if (photos.Count == 0) return;
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return;
            var size = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
            var problems = new List<string>();
            var doc = Document(photos, data, size, problems);
            if (doc.Pages.Count == 0)
            {
                Dialogs.Error(owner, "These photos couldn't be opened for printing.\n\n" + string.Join("\n", problems.Take(5)));
                return;
            }
            dialog.PrintDocument(doc.DocumentPaginator, "KUDUPIX photos");
        }
    }
}
