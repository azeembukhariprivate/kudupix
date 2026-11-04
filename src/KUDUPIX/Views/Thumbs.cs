using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>
    /// The small pictures in the grid. Made in the background (two at a time, the ones asked for last first, so what's on
    /// screen comes first) and kept in KUDUPIX's Thumbs folder, so the next time they show at once. A photo with fixes
    /// gets a small picture with the fixes.
    /// </summary>
    public static class Thumbs
    {
        public const int Size = 320;
        private const int MemoryLimit = 1500;

        private sealed class Request
        {
            public MediaItem Item;
            public EditRecipe Edit;
            public string Key;
            public Action<BitmapSource> Done;
            public Dispatcher Dispatcher;
        }

        private static readonly object Sync = new object();
        private static readonly LinkedList<Request> Waiting = new LinkedList<Request>();
        private static readonly Dictionary<string, BitmapSource> Memory = new Dictionary<string, BitmapSource>();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly SemaphoreSlim Work = new SemaphoreSlim(0);
        private static bool _started;

        /// <summary>How many are waiting to be made (the self-test waits for 0).</summary>
        public static int Pending
        {
            get { lock (Sync) return Waiting.Count + _busy; }
        }

        private static int _busy;

        /// <summary>The small picture right away when it's been made this session, else null (and <paramref name="done"/> is called later).</summary>
        public static BitmapSource Get(MediaItem item, EditRecipe edit, Action<BitmapSource> done)
        {
            var key = AppPaths.ThumbKey(item, Imaging.Stamp(edit));
            lock (Sync)
            {
                if (Memory.TryGetValue(key, out var b))
                {
                    Touch(key);
                    return b;
                }
                if (Failed.Contains(key)) return null;
                Waiting.AddFirst(new Request { Item = item, Edit = edit, Key = key, Done = done, Dispatcher = Dispatcher.CurrentDispatcher });
                // Only the latest few hundred matter: the grid asks again for anything scrolled back to.
                while (Waiting.Count > 400) Waiting.RemoveLast();
                Start();
            }
            Work.Release();
            return null;
        }

        private static void Touch(string key)
        {
            Recent.Remove(key);
            Recent.AddFirst(key);
        }

        private static void Start()
        {
            if (_started) return;
            _started = true;
            for (var i = 0; i < 2; i++)
            {
                var t = new Thread(Worker) { IsBackground = true, Name = "KUDUPIX thumbnails", Priority = ThreadPriority.BelowNormal };
                t.SetApartmentState(ApartmentState.STA); // Windows' own thumbnails need it
                t.Start();
            }
        }

        private static void Worker()
        {
            while (true)
            {
                Work.Wait();
                Request r;
                lock (Sync)
                {
                    if (Waiting.Count == 0) continue;
                    r = Waiting.First.Value;
                    Waiting.RemoveFirst();
                    if (Memory.ContainsKey(r.Key))
                    {
                        var known = Memory[r.Key];
                        r.Dispatcher.BeginInvoke(new Action(() => r.Done?.Invoke(known)));
                        continue;
                    }
                    _busy++;
                }
                BitmapSource b = null;
                try { b = Make(r); }
                catch (Exception)
                {
                    // Not a picture Windows can open: the tile shows the kind of file instead.
                }
                lock (Sync)
                {
                    _busy--;
                    if (b == null) Failed.Add(r.Key);
                    else
                    {
                        Memory[r.Key] = b;
                        Touch(r.Key);
                        while (Recent.Count > MemoryLimit)
                        {
                            Memory.Remove(Recent.Last.Value);
                            Recent.RemoveLast();
                        }
                    }
                }
                r.Dispatcher.BeginInvoke(new Action(() => r.Done?.Invoke(b)));
            }
        }

        private static BitmapSource Make(Request r)
        {
            var file = Path.Combine(AppPaths.Thumbs, r.Key.Substring(0, 2), r.Key + ".jpg");
            if (File.Exists(file))
            {
                try { return Load(file); }
                catch (Exception) { /* made again below */ }
            }
            var item = r.Item;
            BitmapSource b = null;
            if (item.Kind == MediaKind.Photo)
            {
                try
                {
                    var hasEdit = r.Edit != null && !r.Edit.IsEmpty;
                    // A fixed photo is opened a little bigger, so a crop still fills the tile.
                    b = Imaging.Decode(item.Path, item.Orientation, hasEdit ? Size * 2 : Size, item.Width, item.Height);
                    if (hasEdit) b = Imaging.ToBitmap(ImageOps.Shrink(r.Edit.Apply(Imaging.ToPixels(b)), Size));
                }
                catch (Exception ex) when (ex is IOException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException ||
                                           ex is System.Runtime.InteropServices.COMException || ex is FileFormatException || ex is UnauthorizedAccessException || ex is OverflowException)
                {
                    b = null;
                }
            }
            if (b == null) b = ShellThumbs.Get(item.Path, Size);
            if (b == null) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(new System.Windows.Media.Imaging.FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write)) encoder.Save(fs);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                // Shown anyway; made again next time.
            }
            if (b.CanFreeze && !b.IsFrozen) b.Freeze();
            return b;
        }

        private static BitmapSource Load(string file)
        {
            var image = new BitmapImage();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = fs;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        /// <summary>The size of the Thumbs folder.</summary>
        public static long SizeOnDisk()
        {
            try
            {
                return Directory.Exists(AppPaths.Thumbs) ? new DirectoryInfo(AppPaths.Thumbs).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>Removes the kept small pictures (they're made again when needed).</summary>
        public static void Clear()
        {
            lock (Sync)
            {
                Memory.Clear();
                Recent.Clear();
                Failed.Clear();
            }
            try { if (Directory.Exists(AppPaths.Thumbs)) Directory.Delete(AppPaths.Thumbs, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
