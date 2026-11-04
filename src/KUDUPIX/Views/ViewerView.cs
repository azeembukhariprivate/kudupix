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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kudupix.Controls;
using Kudupix.Core;
using Path = System.IO.Path;

namespace Kudupix.Views
{
    /// <summary>
    /// One photo or video at a time, over the whole window: the arrows (and the strip at the bottom) move through the
    /// photos of the view it was opened from. Photos get the fix panel; videos play.
    /// </summary>
    public sealed class ViewerView : Grid
    {
        private enum Mode { None, Crop, Straighten, RedEye }

        private readonly MainWindow _main;
        private readonly Grid _stage = new Grid { ClipToBounds = true };
        private readonly Grid _picture = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Image _image = new Image { Stretch = Stretch.Uniform };
        private readonly Canvas _overlay = new Canvas { Background = Brushes.Transparent };
        private readonly MediaElement _video = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
        private readonly Grid _videoBar = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(24, 0, 24, 82), VerticalAlignment = VerticalAlignment.Bottom };
        private readonly Slider _seek = new Slider { Minimum = 0, Maximum = 1 };
        private readonly TextBlock _time = new TextBlock { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        private readonly Button _play;
        private readonly TextBlock _videoNote = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new TextBlock { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel _stars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 14) };
        private readonly StackPanel _side = new StackPanel { Margin = new Thickness(18) };
        private readonly Border _sidePanel;
        private readonly DispatcherTimer _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly DispatcherTimer _saveEditTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private List<MediaItem> _items = new List<MediaItem>();
        private int _index;
        private Pixels _preview;
        private EditRecipe _edit = new EditRecipe();
        private Mode _mode;
        private int _generation;
        private bool _showDetails;
        private bool _seeking;

        public event Action Closed;

        private readonly TextBlock _names = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 255, 255, 255)), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis };
        private Button _facesButton;
        private bool _showFaces;

        /// <summary>The face frames drawn (for the self-test): face id and the name shown.</summary>
        public List<(string Id, string Name)> FaceFrames { get; } = new List<(string, string)>();

        public void ShowFaces(bool show)
        {
            _showFaces = show;
            _facesButton.Content = Ui.Label("IconUser", show ? "Hide faces" : "Show faces");
            DrawOverlay();
        }

        /// <summary>Who's who changed: the names and frames again.</summary>
        public void FacesChanged()
        {
            ShowNames();
            DrawOverlay();
        }

        private void ShowNames()
        {
            var item = Item;
            var people = item == null || item.IsVideo ? new List<Person>() : _main.People.PeopleIn(item.Path);
            _names.Text = people.Count == 0 ? string.Empty : "With " + string.Join(", ", people.Select(p => p.Name));
        }

        /// <summary>The fixes move things about (turned, straightened or cropped): faces are found on the photo as taken.</summary>
        private bool Moved => _edit.Turns % 4 != 0 || Math.Abs(_edit.Straighten) >= 0.01 || _edit.HasCrop;

        private void DrawFaces(double w, double h)
        {
            FaceFrames.Clear();
            var item = Item;
            var photo = item == null || item.IsVideo ? null : _main.Catalog.Faces.Get(item.Path);
            if (photo == null) return;
            if (Moved)
            {
                _names.Text = "Faces show on the photo as it was taken: undo turning, straightening and cropping to see them.";
                return;
            }
            var groups = _main.People.Groups;
            var notFaces = _main.Catalog.People.NotFaces();
            for (var i = 0; i < photo.Faces.Count; i++)
            {
                var f = photo.Faces[i];
                var id = FaceStore.Id(item.Path, i);
                if (notFaces.Contains(id)) continue;
                var name = groups.PersonOf.TryGetValue(id, out var p) ? p.Name : "Who is this?";
                FaceFrames.Add((id, name));
                var frame = new Rectangle { Width = f.W * w, Height = f.H * h, Stroke = p != null ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4A)), StrokeThickness = 2, RadiusX = 4, RadiusY = 4, IsHitTestVisible = false };
                Canvas.SetLeft(frame, f.X * w);
                Canvas.SetTop(frame, f.Y * h);
                _overlay.Children.Add(frame);
                var label = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 2),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 12 }
                };
                Canvas.SetLeft(label, f.X * w);
                Canvas.SetTop(label, (f.Y + f.H) * h + 3);
                _overlay.Children.Add(label);
            }
        }

        public MediaItem Item => _index >= 0 && _index < _items.Count ? _items[_index] : null;
        public EditRecipe Edit => _edit;
        public bool PictureShown => _image.Source != null && _shownGeneration == _generation;
        private int _shownGeneration = -1;
        public bool VideoPlaying { get; private set; }
        public string VideoProblem { get; private set; }
        public double VideoSeconds => _video.NaturalDuration.HasTimeSpan ? _video.NaturalDuration.TimeSpan.TotalSeconds : 0;

        public ViewerView(MainWindow main)
        {
            _main = main;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            Focusable = true;

            // The picture (or video) with the crop and red-eye layer over it.
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _picture.Children.Add(_image);
            _picture.Children.Add(_overlay);
            _stage.Children.Add(new Border { Margin = new Thickness(70, 64, 70, 84), Child = _picture });
            _stage.Children.Add(new Border { Margin = new Thickness(40, 64, 40, 84), Child = _video });
            _stage.Children.Add(_videoNote);
            _overlay.MouseLeftButtonDown += OverlayDown;
            _overlay.MouseMove += OverlayMove;
            _overlay.MouseLeftButtonUp += (s, e) => { _drag = null; _overlay.ReleaseMouseCapture(); };
            _image.SizeChanged += (s, e) => DrawOverlay();

            // The top bar.
            var top = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            top.Margin = new Thickness(16, 12, 16, 0);
            top.VerticalAlignment = VerticalAlignment.Top;
            top.Children.Add(Dark(Ui.Button("Back to photos", "IconBack", "GhostButton", (s, e) => Closed?.Invoke())).Tip("Esc").At(0, 0));
            top.Children.Add(_name.Margin(14, 0, 14, 0).At(0, 1));
            top.Children.Add(new Border { Child = _stars, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), Margin = new Thickness(0, 0, 8, 0) }.At(0, 2));
            top.Children.Add(Dark(Ui.Button("Slideshow", "IconPlay", "GhostButton", (s, e) => _main.Slideshow(Item))).Margin(0, 0, 8, 0).At(0, 3));
            _facesButton = Dark(Ui.Button("Show faces", "IconUser", "GhostButton", (s, e) => ShowFaces(!_showFaces)));
            _facesButton.ToolTip = "A frame round each face, with who it is. Click a frame to name it or fix it.";
            top.Children.Add(_facesButton.Margin(0, 0, 8, 0).At(0, 4));
            top.Children.Add(Dark(Ui.Button("Details", "IconInfo", "GhostButton", (s, e) => { _showDetails = !_showDetails; FillSide(); })).Margin(0, 0, 8, 0).At(0, 5));
            top.Children.Add(Dark(Ui.Button("Delete", "IconTrash", "GhostButton", (s, e) => DeleteShown())).Tip("Move to the Recycle Bin (Delete)").At(0, 6));
            _stage.Children.Add(top);
            _names.Margin = new Thickness(70, 54, 70, 0);
            _stage.Children.Add(_names);

            Button Arrow(string icon, string tip, Action go, HorizontalAlignment side)
            {
                var b = Dark(Ui.Button(null, icon, "GhostButton", (s, e) => go()));
                b.ToolTip = tip;
                b.Width = 44;
                b.Height = 44;
                b.HorizontalAlignment = side;
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Margin = new Thickness(14);
                return b;
            }
            _stage.Children.Add(Arrow("IconChevronLeft", "Previous (Left arrow)", () => Go(_index - 1), HorizontalAlignment.Left));
            _stage.Children.Add(Arrow("IconChevronRight", "Next (Right arrow)", () => Go(_index + 1), HorizontalAlignment.Right));
            _stage.Children.Add(_strip);

            // Video controls.
            _play = Dark(Ui.Button(null, "IconPause", "GhostButton", (s, e) => PlayPause()));
            _play.ToolTip = "Play or pause (Space)";
            _videoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _videoBar.ColumnDefinitions.Add(new ColumnDefinition());
            _videoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _videoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _videoBar.Children.Add(_play.At(0, 0));
            _seek.Margin = new Thickness(12, 0, 0, 0);
            _seek.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(_seek, "Position in the video");
            _seek.PreviewMouseDown += (s, e) => _seeking = true;
            _seek.PreviewMouseUp += (s, e) =>
            {
                _seeking = false;
                _video.Position = TimeSpan.FromSeconds(_seek.Value);
            };
            _videoBar.Children.Add(_seek.At(0, 1));
            _videoBar.Children.Add(_time.At(0, 2));
            var mute = Dark(Ui.Button(null, "IconSound", "GhostButton", null));
            mute.ToolTip = "Sound on or off";
            mute.Click += (s, e) => { _video.IsMuted = !_video.IsMuted; mute.Opacity = _video.IsMuted ? 0.45 : 1; };
            _videoBar.Children.Add(mute.At(0, 3));
            _stage.Children.Add(_videoBar);
            _video.MediaOpened += (s, e) =>
            {
                VideoPlaying = true;
                _seek.Maximum = Math.Max(0.1, VideoSeconds);
                _videoNote.Visibility = Visibility.Collapsed;
            };
            _video.MediaFailed += (s, e) =>
            {
                VideoPlaying = false;
                VideoProblem = e.ErrorException?.Message ?? "unknown";
                _videoNote.Text = "Windows can't play this video here (it may need a codec Windows doesn't have).\nDouble-click the file in File Explorer to play it with another player.";
                _videoNote.Visibility = Visibility.Visible;
            };
            _video.MediaEnded += (s, e) =>
            {
                _video.Pause();
                _video.Position = TimeSpan.Zero;
                SetPlayIcon(false);
            };
            _videoTimer.Tick += (s, e) =>
            {
                if (!_video.NaturalDuration.HasTimeSpan) return;
                if (!_seeking) _seek.Value = _video.Position.TotalSeconds;
                _time.Text = VideoLength.Text(Math.Max(1, _video.Position.TotalSeconds)) + " / " + VideoLength.Text(VideoSeconds);
            };

            Children.Add(_stage.At(0, 0));
            _sidePanel = new Border { Child = new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } }
                .Res(Border.BackgroundProperty, "SurfaceBg");
            Children.Add(_sidePanel.At(0, 1));

            _renderTimer.Tick += (s, e) =>
            {
                _renderTimer.Stop();
                Render();
            };
            _saveEditTimer.Tick += (s, e) =>
            {
                _saveEditTimer.Stop();
                KeepEdit();
            };
        }

        private static Button Dark(Button b)
        {
            b.Foreground = Brushes.White;
            TextElement.SetForeground(b, Brushes.White);
            b.Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
            return b;
        }

        // ================================================================== showing

        public void Show(List<MediaItem> items, MediaItem item)
        {
            _items = items.Count > 0 ? items : new List<MediaItem> { item };
            // Found by its path: the library may hold a newer copy of the same item.
            _index = _items.FindIndex(x => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (_index < 0)
            {
                _items.Insert(0, item);
                _index = 0;
            }
            ShowCurrent();
            Focus();
        }

        /// <summary>The list behind changed (a scan, a delete): keep showing the same item if it's still there.</summary>
        public void ListChanged(IReadOnlyList<MediaItem> items)
        {
            if (Visibility != Visibility.Visible) return;
            var item = Item;
            _items = items.ToList();
            if (_items.Count == 0 && item != null && _main.Catalog.Library.Find(item.Path) is MediaItem only)
                _items.Add(only);
            if (_items.Count == 0)
            {
                Closed?.Invoke();
                return;
            }
            var i = item == null ? -1 : _items.FindIndex(x => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                _index = i;
                if (!ReferenceEquals(_items[i], item)) ShowCurrent(); // the file was saved again
                else FillStrip();
            }
            else if (item != null && _main.Catalog.Library.Find(item.Path) is MediaItem still)
            {
                // Not in the list shown behind any more (a search or a filter changed), but still there: keep showing it.
                _items.Insert(Math.Min(_index, _items.Count), still);
                _index = _items.IndexOf(still);
                if (!ReferenceEquals(still, item)) ShowCurrent();
                else FillStrip();
            }
            else
            {
                _index = Math.Min(_index, _items.Count - 1);
                ShowCurrent();
            }
        }

        public void Go(int index)
        {
            if (_items.Count == 0) return;
            KeepEdit();
            _index = (index + _items.Count) % _items.Count;
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            Stop();
            var item = Item;
            if (item == null) return;
            _mode = Mode.None;
            _name.Text = item.Name + "  ·  " + (_index + 1).ToString("N0", CultureInfo.CurrentCulture) + " of " + _items.Count.ToString("N0", CultureInfo.CurrentCulture);
            _edit = _main.Catalog.Data.Edit(item.Path) ?? new EditRecipe();
            _preview = null;
            _image.Source = null;
            _overlay.Children.Clear();
            var generation = ++_generation;
            FillStars();
            FillStrip();
            FillSide();
            ShowNames();
            if (item.IsVideo)
            {
                _picture.Visibility = Visibility.Collapsed;
                _video.Visibility = Visibility.Visible;
                _videoBar.Visibility = Visibility.Visible;
                VideoProblem = null;
                _video.Source = new Uri(item.Path);
                _video.Play();
                SetPlayIcon(true);
                _videoTimer.Start();
                return;
            }
            _picture.Visibility = Visibility.Visible;
            _video.Visibility = Visibility.Collapsed;
            _videoBar.Visibility = Visibility.Collapsed;
            // A quick look first (the small picture), then the photo itself.
            var quick = Thumbs.Get(item, _edit, b => { if (generation == _generation && _preview == null && b != null) _image.Source = b; });
            if (quick != null) _image.Source = quick;
            Task.Run(() =>
            {
                try
                {
                    var b = Imaging.Decode(item.Path, item.Orientation, 1800, item.Width, item.Height);
                    return Imaging.ToPixels(b);
                }
                catch (Exception)
                {
                    return null;
                }
            }).ContinueWith(t =>
            {
                if (generation != _generation) return;
                _preview = t.Result;
                if (_preview == null)
                {
                    _videoNote.Text = "Windows can't open this picture (it may need a codec, such as the HEIF one from the Microsoft Store, for HEIC photos).";
                    _videoNote.Visibility = Visibility.Visible;
                    _shownGeneration = _generation;
                    return;
                }
                _videoNote.Visibility = Visibility.Collapsed;
                Render();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Stops a video playing.</summary>
        public void Stop()
        {
            _videoTimer.Stop();
            if (_video.Source != null)
            {
                _video.Stop();
                _video.Close();
                _video.Source = null;
            }
            VideoPlaying = false;
            _videoNote.Visibility = Visibility.Collapsed;
        }

        private void PlayPause()
        {
            if (Item == null || !Item.IsVideo) return;
            if (_playing)
            {
                _video.Pause();
                SetPlayIcon(false);
            }
            else
            {
                _video.Play();
                SetPlayIcon(true);
            }
        }

        private bool _playing;

        private void SetPlayIcon(bool playing)
        {
            _playing = playing;
            _play.Content = Ui.Label(playing ? "IconPause" : "IconPlay", null);
        }

        private void FillStars()
        {
            _stars.Children.Clear();
            var item = Item;
            var stars = _main.Catalog.Data.Stars(item.Path);
            for (var n = 1; n <= 5; n++)
            {
                var value = n;
                var t = new TextBlock { Text = "★", FontSize = 15, Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0), ToolTip = value + (value == 1 ? " star" : " stars") + " (key " + value + ")" };
                t.Foreground = value <= stars ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4A)) : new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255));
                t.MouseLeftButtonUp += (s, e) => SetStars(stars == value ? 0 : value);
                _stars.Children.Add(t);
            }
        }

        public void SetStars(int stars)
        {
            _main.Catalog.Data.SetStars(new[] { Item.Path }, stars);
            FillStars();
        }

        private void FillStrip()
        {
            _strip.Children.Clear();
            if (_items.Count < 2) return;
            for (var i = Math.Max(0, _index - 4); i <= Math.Min(_items.Count - 1, _index + 4); i++)
            {
                var index = i;
                var item = _items[i];
                var image = new Image { Stretch = Stretch.UniformToFill };
                var b = new Border { Width = 70, Height = 48, Margin = new Thickness(3, 0, 3, 0), ClipToBounds = true, Child = image, Cursor = Cursors.Hand, Opacity = i == _index ? 1 : 0.55, BorderBrush = Brushes.White, BorderThickness = new Thickness(i == _index ? 2 : 0), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) };
                var ready = Thumbs.Get(item, _main.Catalog.Data.Edit(item.Path), x => { if (x != null) image.Source = x; });
                if (ready != null) image.Source = ready;
                b.MouseLeftButtonUp += (s, e) => Go(index);
                _strip.Children.Add(b);
            }
        }

        // ================================================================== the fixes

        /// <summary>Applies the fixes to the on-screen copy (in the background) and shows it.</summary>
        private void Render()
        {
            if (_preview == null) return;
            var source = _preview;
            var edit = _edit.Clone();
            if (_mode == Mode.Crop)
            {
                // While cropping, the whole photo is shown with the crop drawn over it.
                edit.CropLeft = 0; edit.CropTop = 0; edit.CropRight = 1; edit.CropBottom = 1;
            }
            var generation = _generation;
            var renderId = ++_renderId;
            Task.Run(() => Imaging.ToBitmap(edit.Apply(source))).ContinueWith(t =>
            {
                if (generation != _generation || renderId != _renderId || t.IsFaulted) return;
                _image.Source = t.Result;
                _shownGeneration = generation;
                DrawOverlay();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private int _renderId;

        /// <summary>A fix changed: show it soon, and keep it a little later (so dragging a slider stays smooth).</summary>
        private void Changed(bool keepNow = false)
        {
            _renderTimer.Stop();
            _renderTimer.Start();
            _saveEditTimer.Stop();
            if (keepNow) KeepEdit();
            else _saveEditTimer.Start();
            UpdateFixButtons();
        }

        /// <summary>Keeps the fixes in KUDUPIX (the file isn't changed).</summary>
        private void KeepEdit()
        {
            _saveEditTimer.Stop();
            var item = Item;
            if (item == null || item.IsVideo) return;
            var kept = _main.Catalog.Data.Edit(item.Path) ?? new EditRecipe();
            if (Imaging.Stamp(kept) == Imaging.Stamp(_edit)) return;
            _main.Catalog.Data.SetEdit(item.Path, _edit);
        }

        public void AutoFix()
        {
            _edit.AutoFix = !_edit.AutoFix;
            Changed(true);
        }

        public void TurnShown(bool right)
        {
            _edit.Turn(right);
            Changed(true);
        }

        public void UndoAll()
        {
            _edit = new EditRecipe();
            _mode = Mode.None;
            FillSide();
            Changed(true);
        }

        public void SetSlider(string which, int value)
        {
            switch (which)
            {
                case "Brightness": _edit.Brightness = value; break;
                case "Contrast": _edit.Contrast = value; break;
                case "Warmth": _edit.Warmth = value; break;
                case "Colour": _edit.Colour = value; break;
            }
            Changed();
        }

        private void SetMode(Mode mode)
        {
            _mode = _mode == mode ? Mode.None : mode;
            FillSide();
            Render();
        }

        public void StartCrop() => SetMode(Mode.Crop);

        /// <summary>Sets the crop (parts of the photo after turning and straightening).</summary>
        public void SetCrop(double left, double top, double right, double bottom)
        {
            _edit.CropLeft = Math.Max(0, Math.Min(left, right - 0.02));
            _edit.CropTop = Math.Max(0, Math.Min(top, bottom - 0.02));
            _edit.CropRight = Math.Min(1, Math.Max(right, left + 0.02));
            _edit.CropBottom = Math.Min(1, Math.Max(bottom, top + 0.02));
            DrawOverlay();
            Changed();
        }

        public void SetStraighten(double degrees)
        {
            _edit.Straighten = Math.Round(Math.Max(-10, Math.Min(10, degrees)), 1);
            Changed();
        }

        /// <summary>Adds a red-eye spot where the photo shown was clicked (parts of its width and height).</summary>
        public void AddRedEye(double x, double y)
        {
            var turned = ImageOps.Turn(_preview, _edit.Turns);
            var (tx, ty) = _edit.ToTurned(x, y, turned.Width, turned.Height);
            var shown = _image.Source as BitmapSource;
            // About the size of an eye's pupil in the photo as shown.
            var r = 0.03 * (_edit.CropRight - _edit.CropLeft) * Math.Max(turned.Width, turned.Height) / Math.Min(turned.Width, turned.Height);
            _edit.RedEye.Add(new Spot { X = tx, Y = ty, R = Math.Max(0.006, Math.Min(0.05, r)) });
            Changed(true);
        }

        // ------------------------------------------------------------------ the crop frame and red-eye clicks

        private string _drag;
        private Point _dragStart;
        private (double L, double T, double R, double B) _dragCrop;

        private void DrawOverlay()
        {
            _overlay.Children.Clear();
            _overlay.Width = _image.ActualWidth;
            _overlay.Height = _image.ActualHeight;
            _overlay.Cursor = _mode == Mode.RedEye ? Cursors.Cross : null;
            var w = _image.ActualWidth;
            var h = _image.ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (_mode == Mode.Straighten)
            {
                for (var i = 1; i < 6; i++)
                {
                    _overlay.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * i / 6, Y2 = h * i / 6, Stroke = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)), StrokeThickness = 1 });
                    _overlay.Children.Add(new Line { Y1 = 0, Y2 = h, X1 = w * i / 6, X2 = w * i / 6, Stroke = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)), StrokeThickness = 1 });
                }
                return;
            }
            if (_mode == Mode.None && _showFaces) DrawFaces(w, h);
            if (_mode != Mode.Crop) return;
            double l = _edit.CropLeft * w, t = _edit.CropTop * h, r = _edit.CropRight * w, b = _edit.CropBottom * h;
            var shade = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
            void Rect(double x, double y, double rw, double rh)
            {
                var re = new Rectangle { Width = Math.Max(0, rw), Height = Math.Max(0, rh), Fill = shade, IsHitTestVisible = false };
                Canvas.SetLeft(re, x);
                Canvas.SetTop(re, y);
                _overlay.Children.Add(re);
            }
            Rect(0, 0, w, t);
            Rect(0, b, w, h - b);
            Rect(0, t, l, b - t);
            Rect(r, t, w - r, b - t);
            var frame = new Rectangle { Width = r - l, Height = b - t, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(frame, l);
            Canvas.SetTop(frame, t);
            _overlay.Children.Add(frame);
            foreach (var (x, y) in new[] { (l, t), (r, t), (l, b), (r, b) })
            {
                var handle = new Rectangle { Width = 14, Height = 14, Fill = Brushes.White, IsHitTestVisible = false };
                Canvas.SetLeft(handle, x - 7);
                Canvas.SetTop(handle, y - 7);
                _overlay.Children.Add(handle);
            }
        }

        private void OverlayDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(_overlay);
            var w = _overlay.ActualWidth;
            var h = _overlay.ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (_mode == Mode.RedEye)
            {
                AddRedEye(p.X / w, p.Y / h);
                return;
            }
            if (_mode == Mode.None && _showFaces && !Moved && Item != null)
            {
                var photo = _main.Catalog.Faces.Get(Item.Path);
                for (var i = 0; photo != null && i < photo.Faces.Count; i++)
                {
                    var f = photo.Faces[i];
                    if (p.X < f.X * w || p.X > (f.X + f.W) * w || p.Y < f.Y * h || p.Y > (f.Y + f.H) * h) continue;
                    var id = FaceStore.Id(Item.Path, i);
                    _main.People.Groups.PersonOf.TryGetValue(id, out var current);
                    var menu = _main.PeoplePage.FaceMenu(id, current);
                    menu.PlacementTarget = _overlay;
                    menu.IsOpen = true;
                    e.Handled = true;
                    return;
                }
            }
            if (_mode != Mode.Crop) return;
            double l = _edit.CropLeft * w, t = _edit.CropTop * h, r = _edit.CropRight * w, b = _edit.CropBottom * h;
            bool Near(double a, double v) => Math.Abs(a - v) < 16;
            var horizontal = Near(p.X, l) ? "L" : Near(p.X, r) ? "R" : string.Empty;
            var vertical = Near(p.Y, t) ? "T" : Near(p.Y, b) ? "B" : string.Empty;
            _drag = horizontal + vertical;
            if (_drag.Length == 0 && p.X > l && p.X < r && p.Y > t && p.Y < b) _drag = "M";
            if (_drag.Length == 0) return;
            _dragStart = p;
            _dragCrop = (_edit.CropLeft, _edit.CropTop, _edit.CropRight, _edit.CropBottom);
            _overlay.CaptureMouse();
        }

        private void OverlayMove(object sender, MouseEventArgs e)
        {
            if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_overlay);
            var dx = (p.X - _dragStart.X) / _overlay.ActualWidth;
            var dy = (p.Y - _dragStart.Y) / _overlay.ActualHeight;
            var (l, t, r, b) = _dragCrop;
            if (_drag == "M")
            {
                dx = Math.Max(-l, Math.Min(1 - r, dx));
                dy = Math.Max(-t, Math.Min(1 - b, dy));
                SetCrop(l + dx, t + dy, r + dx, b + dy);
                return;
            }
            if (_drag.Contains("L")) l += dx;
            if (_drag.Contains("R")) r += dx;
            if (_drag.Contains("T")) t += dy;
            if (_drag.Contains("B")) b += dy;
            SetCrop(l, t, r, b);
        }

        // ================================================================== saving

        /// <summary>Saves the fixed photo as a new file next to it ("name (edited).jpg"). Returns the new file.</summary>
        public async Task<string> SaveCopy()
        {
            var item = Item;
            if (item == null || item.IsVideo) return null;
            KeepEdit();
            var edit = _edit.Clone();
            var target = Names.EditedCopy(item.Path);
            _main.SetStatus("Saving a copy…");
            try
            {
                await Task.Run(() =>
                {
                    var full = Imaging.ToPixels(Imaging.Decode(item.Path, item.Orientation, 0));
                    Imaging.Save(edit.Apply(full), item.Path, target, 95, true);
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is ArgumentException)
            {
                Dialogs.Error(Window.GetWindow(this), "The copy couldn't be saved.\n\n" + ex.Message);
                _main.SetStatus("The copy wasn't saved");
                return null;
            }
            var copy = _main.Catalog.Refresh(target);
            // The copy keeps the stars and tags.
            var data = _main.Catalog.Data;
            if (data.Stars(item.Path) > 0) data.SetStars(new[] { target }, data.Stars(item.Path));
            foreach (var tag in data.Tags(item.Path)) data.AddTag(new[] { target }, tag);
            _main.SetStatus("Saved as " + Path.GetFileName(target));
            return target;
        }

        /// <summary>Saves the fixes into the photo itself, after asking; the untouched photo is kept so it can be put back.</summary>
        public async Task<bool> SaveOver(bool ask = true)
        {
            var item = Item;
            if (item == null || item.IsVideo || !MediaTypes.CanWrite(item.Path) || _edit.IsEmpty) return false;
            if (ask && !Dialogs.Confirm(Window.GetWindow(this),
                    $"Save the fixes into “{item.Name}” itself?\n\nKUDUPIX first keeps the untouched photo in its own folder, so you can put it back later (“Put the original back”). The photo keeps its date and details.",
                    "Save over the original"))
                return false;
            KeepEdit();
            var edit = _edit.Clone();
            _main.SetStatus("Saving…");
            try
            {
                await Task.Run(() =>
                {
                    var info = new FileInfo(item.Path);
                    var created = info.CreationTimeUtc;
                    var modified = info.LastWriteTimeUtc;
                    _main.Catalog.Originals.Keep(item.Path);
                    var full = Imaging.ToPixels(Imaging.Decode(item.Path, item.Orientation, 0));
                    Imaging.Save(edit.Apply(full), item.Path, item.Path, 95, true);
                    File.SetCreationTimeUtc(item.Path, created);
                    File.SetLastWriteTimeUtc(item.Path, modified);
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException || ex is ArgumentException)
            {
                Dialogs.Error(Window.GetWindow(this), "The photo couldn't be saved; it's unchanged.\n\n" + ex.Message);
                _main.SetStatus("Not saved");
                return false;
            }
            // The fixes are in the file now.
            _main.Catalog.Data.SetEdit(item.Path, null);
            _edit = new EditRecipe();
            _main.Catalog.Refresh(item.Path);
            _main.SetStatus("Saved into " + item.Name + " (the original is kept)");
            return true;
        }

        /// <summary>Puts back the original kept before the last "Save over the original".</summary>
        public bool PutBack(bool ask = true)
        {
            var item = Item;
            var kept = item == null ? null : _main.Catalog.Originals.For(item.Path).FirstOrDefault();
            if (kept == null) return false;
            if (ask && !Dialogs.Confirm(Window.GetWindow(this), $"Put back the original of “{item.Name}”, as it was before you saved over it on {kept.Saved:d MMM yyyy, HH:mm}?\n\nThe fixes saved into it go.", "Put the original back"))
                return false;
            try { _main.Catalog.Originals.PutBack(kept); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(Window.GetWindow(this), "The original couldn't be put back.\n\n" + ex.Message);
                return false;
            }
            _main.Catalog.Refresh(item.Path);
            _main.SetStatus("The original of " + item.Name + " is back");
            return true;
        }

        private async void DeleteShown()
        {
            var item = Item;
            if (item == null) return;
            Stop();
            await Task.Delay(50);
            if (!_main.Delete(new[] { item }) && File.Exists(item.Path)) ShowCurrent();
        }

        // ================================================================== the side panel

        private readonly List<(Button Button, Func<bool> On)> _fixButtons = new List<(Button, Func<bool>)>();

        private void FillSide()
        {
            _side.Children.Clear();
            _fixButtons.Clear();
            var item = Item;
            if (item == null) return;
            if (item.IsVideo || _showDetails)
            {
                Details(item);
                return;
            }
            _side.Children.Add(Ui.Heading("Fix this photo", 16));
            _side.Children.Add(Ui.Subtle("The file stays as it is until you save. Your fixes are kept in KUDUPIX, so you can change them later.", 12));

            var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 4) };
            void Fix(string text, string icon, Action run, Func<bool> on, string tip)
            {
                var b = Ui.Button(text, icon, "SecondaryButton", (s, e) => run());
                b.Margin = new Thickness(0, 0, 8, 8);
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.ToolTip = tip;
                _fixButtons.Add((b, on));
                buttons.Children.Add(b);
            }
            Fix("Auto-fix", "IconWand", AutoFix, () => _edit.AutoFix, "Brightness and colours in one click (click again to undo)");
            Fix("Crop", "IconCrop", StartCrop, () => _mode == Mode.Crop, "Drag the corners or edges of the frame; drag inside it to move it");
            Fix("Turn left", "IconTurnLeft", () => TurnShown(false), () => false, "A quarter turn to the left ([)");
            Fix("Turn right", "IconTurnRight", () => TurnShown(true), () => false, "A quarter turn to the right (])");
            Fix("Straighten", "IconLevel", () => SetMode(Mode.Straighten), () => _mode == Mode.Straighten, "Level a sloping horizon");
            Fix("Red-eye", "IconEye", () => SetMode(Mode.RedEye), () => _mode == Mode.RedEye, "Click on each red eye in the photo");
            _side.Children.Add(buttons);

            if (_mode == Mode.Crop)
            {
                var done = Ui.Small(Ui.Button("Done cropping", "IconCheck", "PrimaryButton", (s, e) => SetMode(Mode.Crop)));
                var reset = Ui.Link("Whole photo", (s, e) => SetCrop(0, 0, 1, 1));
                _side.Children.Add(Ui.Row(done, reset.Margin(12, 0, 0, 0)).Margin(0, 0, 0, 8));
            }
            if (_mode == Mode.Straighten)
            {
                var angle = Ui.Subtle(Degrees(_edit.Straighten), 12, false);
                var s = new Slider { Minimum = -10, Maximum = 10, Value = _edit.Straighten, SmallChange = 0.1, LargeChange = 1 };
                System.Windows.Automation.AutomationProperties.SetName(s, "Straighten");
                s.ValueChanged += (x, e) =>
                {
                    SetStraighten(s.Value);
                    angle.Text = Degrees(_edit.Straighten);
                };
                var head = Ui.Columns(Ui.Star(), Ui.Auto);
                head.Children.Add(Ui.Text("Straighten", 12.5).At(0, 0));
                head.Children.Add(angle.At(0, 1));
                _side.Children.Add(Ui.Column(head, s).Margin(0, 0, 0, 8));
            }
            if (_mode == Mode.RedEye)
            {
                _side.Children.Add(Ui.Banner("IconEye", Ui.Text("Click on each red eye in the photo. Click Red-eye again when you're done.", 12, wrap: true)).Margin(0, 0, 0, 8));
                if (_edit.RedEye.Count > 0)
                    _side.Children.Add(Ui.Link($"Undo the red-eye fixes ({_edit.RedEye.Count})", (s, e) => { _edit.RedEye.Clear(); Changed(true); FillSide(); }).Margin(0, 0, 0, 8));
            }

            void SliderRow(string name, int value)
            {
                var label = Ui.Subtle(value > 0 ? "+" + value : value.ToString(CultureInfo.CurrentCulture), 12, false);
                var s = new Slider { Minimum = -100, Maximum = 100, Value = value, SmallChange = 1, LargeChange = 10 };
                System.Windows.Automation.AutomationProperties.SetName(s, name);
                s.ValueChanged += (x, e) =>
                {
                    var v = (int)Math.Round(s.Value);
                    label.Text = v > 0 ? "+" + v : v.ToString(CultureInfo.CurrentCulture);
                    SetSlider(name, v);
                };
                s.MouseDoubleClick += (x, e) => s.Value = 0;
                s.ToolTip = "Double-click to put it back to 0";
                var head = Ui.Columns(Ui.Star(), Ui.Auto);
                head.Children.Add(Ui.Text(name, 12.5).At(0, 0));
                head.Children.Add(label.At(0, 1));
                _side.Children.Add(Ui.Column(head, s).Margin(0, 6, 0, 4));
            }
            SliderRow("Brightness", _edit.Brightness);
            SliderRow("Contrast", _edit.Contrast);
            SliderRow("Warmth", _edit.Warmth);
            SliderRow("Colour", _edit.Colour);

            var undo = Ui.Link("Undo all the fixes", (s, e) => UndoAll());
            undo.HorizontalAlignment = HorizontalAlignment.Left;
            _side.Children.Add(undo.Margin(0, 8, 0, 0));

            var copy = Ui.Button("Save a copy", "IconCopy", "PrimaryButton", async (s, e) => await SaveCopy());
            copy.Margin = new Thickness(0, 16, 0, 8);
            _side.Children.Add(copy);
            var over = Ui.Button("Save over the original…", null, "SecondaryButton", async (s, e) => await SaveOver());
            if (!MediaTypes.CanWrite(item.Path))
            {
                over.IsEnabled = false;
                ToolTipService.SetShowOnDisabled(over, true);
                over.ToolTip = "Windows can't write " + Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant() + " photos; save a copy instead (it's saved as JPEG).";
            }
            _side.Children.Add(over);
            _fixButtons.Add((copy, () => false));
            var note = Ui.Text($"“Save a copy” makes {Path.GetFileName(Names.EditedCopy(item.Path))} next to the photo. “Save over the original” asks first and keeps the untouched photo, so you can put it back later.", 12, wrap: true);
            _side.Children.Add(new Border { Child = note, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 14, 0, 0) }.Res(Border.BackgroundProperty, "SelectionBg"));
            var kept = _main.Catalog.Originals.For(item.Path).FirstOrDefault();
            if (kept != null)
            {
                var back = Ui.Link($"Put the original back (saved over on {kept.Saved:d MMM yyyy})", (s, e) => PutBack());
                back.HorizontalAlignment = HorizontalAlignment.Left;
                _side.Children.Add(back.Margin(0, 12, 0, 0));
            }
            UpdateFixButtons();
        }

        private static string Degrees(double d) => (d > 0 ? "+" : string.Empty) + d.ToString("0.0", CultureInfo.CurrentCulture) + "°";

        private void UpdateFixButtons()
        {
            foreach (var (b, on) in _fixButtons)
            {
                if (b.Style == (Style)Application.Current.TryFindResource("PrimaryButton")) continue;
                if (on()) b.Res(Button.BorderBrushProperty, "Accent").Res(Button.BackgroundProperty, "ChipBg");
                else
                {
                    b.ClearValue(Button.BorderBrushProperty);
                    b.ClearValue(Button.BackgroundProperty);
                }
            }
        }

        private void Details(MediaItem item)
        {
            _side.Children.Add(Ui.Heading(item.Name, 15));
            _side.Children.Add(Ui.Subtle(item.Folder, 12));
            var kv = Ui.Columns(Ui.Px(90), Ui.Star());
            var row = 0;
            void Detail(string label, string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                kv.Children.Add(Ui.Subtle(label, 12, false).Margin(0, 3, 8, 3).At(row, 0));
                kv.Children.Add(Ui.Text(value, 12, wrap: true).Margin(0, 3, 0, 3).At(row, 1));
                row++;
            }
            Detail(item.IsVideo ? "Filmed" : "Taken", item.Taken?.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) ?? "Not stored in the file");
            Detail("Size", Format.Size(item.Size) + (item.ShownWidth > 0 ? $" · {item.ShownWidth} × {item.ShownHeight}" : string.Empty));
            if (item.IsVideo) Detail("Length", VideoLength.Text(item.Seconds));
            Detail("Camera", string.Join(" · ", new[] { item.Camera, item.Exposure }.Where(x => !string.IsNullOrEmpty(x))));
            if (item.HasPlace) Detail("Place", item.Latitude.Value.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + item.Longitude.Value.ToString("0.0000", CultureInfo.InvariantCulture));
            var tags = _main.Catalog.Data.Tags(item.Path);
            if (tags.Count > 0) Detail("Tags", string.Join(", ", tags));
            var albums = _main.Catalog.Data.AlbumsOf(item.Path);
            if (albums.Count > 0) Detail("Albums", string.Join(", ", albums.Select(a => a.Name)));
            _side.Children.Add(kv.Margin(0, 14, 0, 0));
            if (item.IsVideo)
                _side.Children.Add(Ui.Subtle("Videos play here with Windows' own player. Stars, tags and albums work for videos too; fixes are for photos.", 12).Margin(0, 16, 0, 0));
            else
            {
                var fix = Ui.Link("Back to the fixes", (s, e) => { _showDetails = false; FillSide(); });
                fix.HorizontalAlignment = HorizontalAlignment.Left;
                _side.Children.Add(fix.Margin(0, 16, 0, 0));
            }
            var folder = Ui.Link("Show in File Explorer", (s, e) => _main.ShowInFolder(item));
            folder.HorizontalAlignment = HorizontalAlignment.Left;
            _side.Children.Add(folder.Margin(0, 8, 0, 0));
        }

        // ================================================================== keys

        public void OnKey(KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            switch (e.Key)
            {
                case Key.Escape:
                    if (_mode != Mode.None) SetMode(_mode);
                    else Closed?.Invoke();
                    break;
                case Key.Left: Go(_index - 1); break;
                case Key.Right: Go(_index + 1); break;
                case Key.Space:
                    if (Item?.IsVideo == true) PlayPause();
                    else Go(_index + 1);
                    break;
                case Key.Delete: DeleteShown(); break;
                case Key.OemOpenBrackets: if (Item?.IsVideo == false) TurnShown(false); break;
                case Key.OemCloseBrackets: if (Item?.IsVideo == false) TurnShown(true); break;
                case Key.F5: _main.Slideshow(Item); break;
                default:
                    if (e.Key >= Key.D0 && e.Key <= Key.D5 && Keyboard.Modifiers == ModifierKeys.None) SetStars(e.Key - Key.D0);
                    else return;
                    break;
            }
            e.Handled = true;
        }
    }
}
