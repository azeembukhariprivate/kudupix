using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>
    /// KUDUPIX.exe --self-test --samples folder: makes a small photo collection from the test photos and video (the
    /// ones the unit tests download) plus some made here, and uses the real app on it: the library and its folders,
    /// thumbnails, stars, tags, albums, search, the viewer and every fix, saving a copy, saving over the original and
    /// putting it back, a video, duplicates (and removing them), export, the slideshow, printing pages, the folders
    /// being watched, and every window in both themes. Its own data folder and settings are used, so the user's library
    /// isn't touched. Writes self-test.log next to the exe; exits with 0 when everything passed.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;
        private static string _samples;

        private static string Sample(string name)
        {
            var path = Path.Combine(_samples ?? string.Empty, name);
            if (!File.Exists(path)) throw new Exception($"the sample file {name} is missing (run the unit tests first, then --self-test --samples <their samples-cache folder>)");
            return path;
        }

        public static async Task<int> Run(string samples)
        {
            Running = true;
            _log = new StringBuilder();
            _samples = samples;
            _temp = Path.Combine(Path.GetTempPath(), "kudupix-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            Settings.InMemory = new Dictionary<string, string>();
            AppPaths.Root = Path.Combine(_temp, "data");
            var pictures = Path.Combine(_temp, "Pictures");
            var videos = Path.Combine(_temp, "Videos");
            MainWindow main = null;
            Catalog catalog = null;
            MediaItem Find(string name) => catalog.Library.All().FirstOrDefault(i => i.Name == name);

            await Step("Load themes and styles", () => { ThemeManager.Init(ThemeMode.Light); return Task.CompletedTask; });
            await Step("Theme keys", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "SurfaceAltBg", "Text", "SubtleText", "Accent", "BorderBrush", "InputBorder", "SelectionBg", "ChipBg", "ChipText",
                                            "PrimaryButton", "SecondaryButton", "GhostButton", "LinkButton", "ChipRadio", "ChipToggle", "TableList",
                                            "IconPicture", "IconVideo", "IconStar", "IconAlbum", "IconTag", "IconCrop", "IconLevel", "IconWand", "IconEye", "IconTurnLeft",
                                            "IconTurnRight", "IconCopy", "IconFolder", "IconCalendar", "IconChevronLeft", "IconChevronRight", "IconSound", "IconPlay", "IconPause" })
                    Application.Current.FindResource(key);
                return Task.CompletedTask;
            });

            await Step("A photo collection to look at", () =>
            {
                Directory.CreateDirectory(pictures);
                Directory.CreateDirectory(Path.Combine(pictures, "Holiday"));
                Directory.CreateDirectory(videos);
                File.Copy(Sample("Canon_40D.jpg"), Path.Combine(pictures, "Canon_40D.jpg"));
                File.Copy(Sample("DSCN0010.jpg"), Path.Combine(pictures, "Holiday", "DSCN0010.jpg"));
                File.Copy(Sample("DSCN0010.jpg"), Path.Combine(pictures, "Holiday", "DSCN0010 (1).jpg")); // an exact copy
                File.Copy(Sample("portrait_6.jpg"), Path.Combine(pictures, "portrait_6.jpg"));
                File.Copy(Sample("landscape_3.jpg"), Path.Combine(pictures, "landscape_3.jpg"));
                File.Copy(Sample("Arbitro.tiff"), Path.Combine(pictures, "Arbitro.tiff"));
                File.Copy(Sample("QuickTime.mov"), Path.Combine(videos, "QuickTime.mov"));
                // The same photo, smaller and without its details, as messaging apps send it.
                var small = ImageOps.Shrink(Imaging.ToPixels(Imaging.Decode(Sample("DSCN0010.jpg"), 1, 0)), 320);
                Imaging.Save(small, null, Path.Combine(pictures, "WhatsApp Image 2026-11-02.jpg"), 80, false);
                // Photos made here, so the grid has more than one day and plenty to scroll.
                for (var i = 0; i < 24; i++)
                {
                    var path = Path.Combine(pictures, $"Made {i:00}.png");
                    Imaging.Save(Scene(160, 120, i + 1), null, path, 90, true);
                    File.SetLastWriteTime(path, new DateTime(2025, 1 + i % 12, 1 + i, 10, 0, 0));
                }
                Settings.Folders = new List<string> { pictures, videos };
                Settings.FoldersChosen = true;
                return Task.CompletedTask;
            });

            await Step("Main window and the library", async () =>
            {
                catalog = new Catalog();
                main = new MainWindow(catalog);
                OffScreen(main);
                main.Width = 1440;
                main.Height = 900;
                main.Show();
                catalog.Scan();
                await Wait(() => catalog.ScansDone >= 1, 60, "the folders to be looked through");
                Expect(catalog.Library.Count == 32, "32 photos and videos found, got " + catalog.Library.Count);
                await Wait(() => main.Grid.Items.Count == 32, 10, "the grid to show them");
                var gps = Find("DSCN0010.jpg");
                Expect(gps.HasPlace && gps.Camera == "NIKON COOLPIX P6000", "details read");
                Expect(Find("portrait_6.jpg").ShownHeight > Find("portrait_6.jpg").ShownWidth, "a photo taken upright is shown upright");
                Expect(Find("QuickTime.mov").IsVideo && Find("QuickTime.mov").Seconds > 4.9, "the video's length");
                Expect(main.Grid.Rows.Count(r => r.IsHeader) > 3, "grouped by day");
                Expect(catalog.StatusText().StartsWith("Watching 2 folders"), "status");
            });

            await Step("Thumbnails", async () =>
            {
                await Wait(() => Thumbs.Pending == 0, 90, "the small pictures");
                Pump();
                var made = catalog.Library.All().Where(i => !i.IsVideo).Count(i => Thumbs.Get(i, null, null) != null);
                Note($"{made} of {catalog.Library.All().Count(i => !i.IsVideo)} photo thumbnails made");
                Expect(made >= 30, "photo thumbnails made, got " + made);
                var video = Thumbs.Get(Find("QuickTime.mov"), null, null);
                Note(video != null ? "video thumbnail made by Windows" : "Windows made no video thumbnail here (the tile shows a video sign)");
            });

            await Step("Details of a photo", () =>
            {
                main.Grid.Select(Find("DSCN0010.jpg"));
                main.Grid.ScrollTo(Find("DSCN0010.jpg"));
                Pump();
                return Task.CompletedTask;
            });

            await Step("Stars, tags, albums and search", async () =>
            {
                var data = catalog.Data;
                var gps = Find("DSCN0010.jpg");
                var canon = Find("Canon_40D.jpg");
                data.SetStars(new[] { gps.Path, canon.Path }, 4);
                data.AddTag(new[] { gps.Path }, "Italy");
                data.AddTag(new[] { gps.Path, canon.Path }, "favourites");
                var album = main.NewAlbum(new[] { gps, canon, Find("QuickTime.mov") }, "Best of 2008");
                Expect(album != null && album.Paths.Count == 3, "album made");
                main.Go(ViewKind.Album, album.Id);
                Expect(main.Grid.Items.Count == 3, "the album shows its 3 items");
                main.Go(ViewKind.Starred);
                Expect(main.Grid.Items.Count == 2, "starred");
                main.Go(ViewKind.Tag, "italy");
                Expect(main.Grid.Items.Count == 1, "tagged Italy");
                main.Go(ViewKind.Videos);
                Expect(main.Grid.Items.Count == 1, "videos");
                main.Go(ViewKind.Folder, Path.Combine(pictures, "Holiday"));
                Expect(main.Grid.Items.Count == 2, "a folder");
                main.Go(ViewKind.ByMonth);
                Expect(main.Grid.Rows.First().IsHeader && main.Grid.Rows.First().Title.Contains("2008") == false, "by month");
                main.Go(ViewKind.All);
                SetSearch(main, "nikon");
                await Wait(() => main.Grid.Items.Count == 2, 5, "search by camera");
                SetSearch(main, "best 2008");
                await Wait(() => main.Grid.Items.Count == 2, 5, "search by album and year (photos)");
                SetSearch(main, string.Empty);
                await Wait(() => main.Grid.Items.Count == 32, 5, "search cleared");
                main.Grid.Select(canon);
                Pump();
                await Task.Delay(300);
            });

            await Step("Viewer and fixes", async () =>
            {
                var canon = Find("Canon_40D.jpg");
                main.Open(canon);
                var v = main.Viewer;
                await Wait(() => v.PictureShown, 30, "the photo");
                v.AutoFix();
                v.SetSlider("Brightness", 20);
                v.SetSlider("Warmth", 15);
                v.TurnShown(true);
                v.StartCrop();
                v.SetCrop(0.1, 0.05, 0.9, 0.95);
                Pump();
                Screenshots.All("Cropping");
                v.StartCrop();
                v.AddRedEye(0.5, 0.5);
                await Task.Delay(700);
                await Wait(() => v.PictureShown, 10, "the fixed photo");
                var kept = catalog.Data.Edit(canon.Path);
                Expect(kept != null && kept.AutoFix && kept.Turns == 1 && kept.Brightness == 20 && kept.HasCrop && kept.RedEye.Count == 1, "the fixes are kept in KUDUPIX");
                Expect(new FileInfo(canon.Path).Length == canon.Size, "the file itself isn't changed");
            });

            await Step("Save a copy", async () =>
            {
                var canon = Find("Canon_40D.jpg");
                var copy = await main.Viewer.SaveCopy();
                Expect(copy != null && Path.GetFileName(copy) == "Canon_40D (edited).jpg" && File.Exists(copy), "saved as Canon_40D (edited).jpg");
                var d = PhotoInfo.Read(copy);
                Expect(d.Taken == canon.Taken, "the copy keeps the date taken");
                Expect(d.Camera == "Canon EOS 40D", "and the camera");
                Expect(d.Orientation == 1, "and is marked upright");
                Expect(d.Height > d.Width, "turned (it was wider than high)");
                Expect(catalog.Library.Find(copy) != null, "the copy is in the library");
                Expect(catalog.Data.Stars(copy) == 4, "with the stars");
            });

            await Step("Save over the original, and put it back", async () =>
            {
                var item = Find("landscape_3.jpg");
                var before = File.ReadAllBytes(item.Path);
                var date = File.GetLastWriteTimeUtc(item.Path);
                main.Viewer.Show(main.Grid.Items.ToList(), item);
                await Wait(() => main.Viewer.PictureShown, 30, "the photo");
                main.Viewer.SetSlider("Contrast", 30);
                Expect(await main.Viewer.SaveOver(false), "saved over");
                Expect(!File.ReadAllBytes(item.Path).SequenceEqual(before), "the file has the fix");
                Expect(File.GetLastWriteTimeUtc(item.Path) == date, "and keeps its date");
                Expect(catalog.Data.Edit(item.Path) == null, "the fix is in the file now, not kept separately");
                var kept = catalog.Originals.For(item.Path);
                Expect(kept.Count == 1 && File.ReadAllBytes(kept[0].Copy).SequenceEqual(before), "the untouched photo was kept first");
                Expect(PhotoInfo.Read(item.Path).Orientation == 1, "turned the right way and marked upright");
                await Wait(() => main.Viewer.PictureShown, 30, "the saved photo");
                Screenshots.All("Saved over the original");
                Expect(main.Viewer.PutBack(false), "put back");
                Expect(File.ReadAllBytes(item.Path).SequenceEqual(before), "the original is back");
                Expect(catalog.Originals.For(item.Path).Count == 0, "and no longer kept twice");
            });

            await Step("A video", async () =>
            {
                main.Viewer.Show(main.Grid.Items.ToList(), Find("QuickTime.mov"));
                await Wait(() => main.Viewer.VideoPlaying || main.Viewer.VideoProblem != null, 30, "the video to open");
                Note(main.Viewer.VideoPlaying ? $"the video plays ({main.Viewer.VideoSeconds:0.0} s)" : "Windows can't play it here: " + main.Viewer.VideoProblem + " (the plain-words note is shown)");
                await Task.Delay(800);
                Screenshots.All("A video");
                main.Viewer.Stop();
                main.CloseViewer();
            });

            await Step("Duplicates", async () =>
            {
                main.Go(ViewKind.Duplicates);
                var d = main.Duplicates;
                await Wait(() => !d.Working, 90, "the copies to be found");
                Expect(d.GroupCount == 1, "one group of copies, got " + d.GroupCount);
                var group = d.Rows[0];
                Expect(group.Group.Items.Count == 3, "the photo, its exact copy and the small one: got " + string.Join(", ", group.Group.Items.Select(i => i.Name)));
                Expect(group.Keep.Single().Name == "DSCN0010.jpg", "keep the original");
                Pump();
                Screenshots.All("Duplicates");
                d.RemoveOthers(false);
                await Task.Delay(300);
                Expect(!File.Exists(Path.Combine(pictures, "Holiday", "DSCN0010 (1).jpg")) && !File.Exists(Path.Combine(pictures, "WhatsApp Image 2026-11-02.jpg")), "the copies went to the Recycle Bin");
                Expect(File.Exists(Path.Combine(pictures, "Holiday", "DSCN0010.jpg")), "the original stayed");
                Expect(catalog.Library.Count == 31, "the library follows, got " + catalog.Library.Count);
                Expect(d.GroupCount == 0, "no copies left");
                main.Go(ViewKind.All);
            });

            await Step("Export", async () =>
            {
                var items = new[] { Find("DSCN0010.jpg"), Find("Canon_40D.jpg"), Find("QuickTime.mov") };
                var w = new ExportWindow(main, items, catalog.Data);
                OffScreen(w);
                w.Show();
                var folder = Path.Combine(_temp, "Export");
                var result = await w.Run(new ExportWindow.ExportOptions { Folder = folder, MaxSide = 64, Quality = 85, KeepPlace = false });
                w.Close();
                Expect(result.Saved == 3 && result.Problems.Count == 0, "3 saved: " + string.Join("; ", result.Problems));
                var gps = PhotoInfo.Read(Path.Combine(folder, "DSCN0010.jpg"));
                Expect(!gps.Latitude.HasValue, "the place was left out");
                Expect(gps.Taken.HasValue, "the date was kept");
                Expect(Math.Max(gps.Width, gps.Height) <= 64, "made smaller");
                var canon = PhotoInfo.Read(Path.Combine(folder, "Canon_40D.jpg"));
                Expect(canon.Height > canon.Width, "with the fixes (turned)");
                Expect(new FileInfo(Path.Combine(folder, "QuickTime.mov")).Length == Find("QuickTime.mov").Size, "the video copied as it is");
                var again = await new ExportWindow(main, items.Take(1).ToList(), catalog.Data).Run(new ExportWindow.ExportOptions { Folder = folder, MaxSide = 64 });
                Expect(Path.GetFileName(again.Files[0]) == "DSCN0010 (2).jpg", "never replaces a file");
            });

            await Step("Slideshow", async () =>
            {
                var photos = main.Grid.Items.Where(i => !i.IsVideo).Take(5).ToList();
                var s = new SlideshowWindow(photos, 0, catalog.Data);
                s.Show();
                await Wait(() => s.Showing, 20, "the first photo");
                s.Next(1);
                await Task.Delay(800);
                Expect(s.Index == 1, "the next photo");
                Screenshots.One(s, "Slideshow");
                s.Close();
            });

            await Step("Print pages", () =>
            {
                var photos = main.Grid.Items.Where(i => !i.IsVideo).Take(3).ToList();
                var doc = Printing.Document(photos, catalog.Data, new Size(816, 1056));
                Expect(doc.Pages.Count == 3, "a page for each photo");
                return Task.CompletedTask;
            });

            await Step("Folders are watched", async () =>
            {
                var added = Path.Combine(pictures, "Holiday", "new arrival.jpg");
                File.Copy(Sample("Canon_40D.jpg"), added);
                await Wait(() => catalog.Library.Find(added) != null, 30, "a new photo to appear by itself");
                var starred = Find("DSCN0010.jpg");
                var moved = Path.Combine(pictures, "Holiday", "Italy 2008.jpg");
                File.Move(starred.Path, moved);
                await Wait(() => catalog.Library.Find(moved) != null && catalog.Library.Find(starred.Path) == null, 30, "a renamed photo to be followed");
                Expect(catalog.Data.Stars(moved) == 4 && catalog.Data.Tags(moved).Contains("Italy"), "its stars and tags follow it");
                File.Delete(added);
                await Wait(() => catalog.Library.Find(added) == null, 30, "a deleted photo to go");
            });

            await Step("Folders window", () =>
            {
                var w = new SettingsWindow(main, true);
                w.AddFolder(AppPaths.Root);
                Expect(!w.Folders.Contains(AppPaths.Root), "KUDUPIX's own folder isn't added");
                w.AddFolder(Path.Combine(pictures, "Holiday"));
                Expect(w.Folders.Count == 2, "a folder inside one already there counts once");
                ShowAndClose(w);
                return Task.CompletedTask;
            });
            await Step("Settings window", () => { ShowAndClose(new SettingsWindow(main)); return Task.CompletedTask; });
            await Step("About window", () => { ShowAndClose(About.Build(main)); return Task.CompletedTask; });
            await Step("Export window", () => { ShowAndClose(new ExportWindow(main, main.Grid.Items.Take(4).ToList(), catalog.Data)); return Task.CompletedTask; });

            await Step("Dark theme", async () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                main.Grid.Select(Find("Canon_40D.jpg"));
                Pump();
                Screenshots.All("Dark theme");
                main.Open(Find("Canon_40D.jpg"));
                await Wait(() => main.Viewer.PictureShown, 30, "the photo");
                Screenshots.All("Dark theme viewer");
                main.CloseViewer();
                ShowAndClose(new SettingsWindow(main));
                ThemeManager.Apply(ThemeMode.Light);
            });

            await Step("Everything is kept for next time", () =>
            {
                var count = catalog.Library.Count;
                main.Close(); // saves
                var again = new Catalog();
                Expect(again.Library.Count == count, "the library");
                Expect(again.Data.Albums().Any(a => a.Name == "Best of 2008"), "the album");
                Expect(again.Data.Stars(Path.Combine(pictures, "Holiday", "Italy 2008.jpg")) == 4, "the stars");
                Expect(again.Data.Edit(Path.Combine(pictures, "Canon_40D.jpg"))?.AutoFix == true, "the fixes");
                again.Dispose();
                return Task.CompletedTask;
            });

            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        private static void SetSearch(MainWindow main, string text)
        {
            var box = FindChild<System.Windows.Controls.TextBox>(main, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Search");
            box.Text = text;
            box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, System.Windows.Input.Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
        }

        private static T FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t && match(t)) return t;
                var found = FindChild(c, match);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>A made-up photo: a gradient with blocks, different for each number.</summary>
        private static Pixels Scene(int w, int h, int seed)
        {
            var p = new Pixels(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var block = (x * 8 / w) * 7 + (y * 6 / h) * 13 + seed * 29;
                    var shade = (int)((uint)(block * 2654435761u) >> 24) % 90;
                    p.Set(x, y, (byte)Math.Min(255, 40 + 150 * x / w + shade), (byte)Math.Min(255, 60 + 120 * y / h + shade), (byte)(60 + seed * 37 % 150));
                }
            return p;
        }

        // ================================================================== helpers

        private static async Task Step(string name, Func<Task> action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                await action();
                Pump();
                Screenshots.All(name);
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void Note(string text) => _log.AppendLine("      " + text);

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static async Task Wait(Func<bool> condition, int seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out waiting for " + what);
                await Task.Delay(50);
            }
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void ShowAndClose(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            Screenshots.One(w, w.Title);
            w.Close();
        }
    }
}
