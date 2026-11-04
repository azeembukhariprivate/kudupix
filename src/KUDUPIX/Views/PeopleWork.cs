using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>
    /// People while KUDUPIX is open: looks for faces in the photos in the background (photos only, newest first, slower
    /// while the PC is being used), keeps who each face is up to date, and makes the small face pictures.
    /// </summary>
    public sealed class PeopleWork : IDisposable
    {
        private readonly Catalog _catalog;
        private readonly Dispatcher _ui;
        private readonly FaceFinder _finder;
        private readonly AutoResetEvent _wake = new AutoResetEvent(true);
        private readonly DispatcherTimer _regroup = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly Thread _thread;
        private volatile bool _stop;
        private int _regroupGeneration;

        /// <summary>Who each face is, worked out again soon after any change.</summary>
        public FaceGroups Groups { get; private set; } = new FaceGroups();

        /// <summary>Why People can't work (no face models), or null.</summary>
        public string Problem { get; }
        public bool Busy { get; private set; }
        public int Done { get; private set; }
        public int Total { get; private set; }
        public int GroupsMade { get; private set; }

        /// <summary>The groups changed (or faces were found).</summary>
        public event Action Changed;
        public event Action<string> Status;

        public PeopleWork(Catalog catalog)
        {
            _catalog = catalog;
            _ui = Dispatcher.CurrentDispatcher;
            try
            {
                if (!FaceFinder.ModelsIn(AppPaths.Models)) Problem = "The face models aren't next to KUDUPIX (install it again to get them).";
                else _finder = new FaceFinder(AppPaths.Models);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // No ONNX Runtime for this PC, or damaged models: the rest of KUDUPIX works as before.
                Problem = "People can't work on this PC: " + ex.Message;
            }
            _regroup.Tick += (s, e) =>
            {
                _regroup.Stop();
                Regroup();
            };
            catalog.Changed += () => { _wake.Set(); Soon(); };
            catalog.PeopleChanged += Soon;
            if (_finder != null)
            {
                _thread = new Thread(Work) { IsBackground = true, Name = "KUDUPIX faces", Priority = ThreadPriority.Lowest };
                _thread.Start();
            }
            Soon();
        }

        /// <summary>Works out who each face is again in a moment (many changes at once make one go).</summary>
        public void Soon()
        {
            _regroup.Stop();
            _regroup.Start();
        }

        private void Regroup()
        {
            var generation = ++_regroupGeneration;
            var faces = _catalog.Faces.All();
            var people = _catalog.People.People();
            var notFaces = _catalog.People.NotFaces();
            Task.Run(() => FaceGroups.Work(faces, people, notFaces)).ContinueWith(t =>
            {
                if (generation != _regroupGeneration || t.IsFaulted) return;
                Groups = t.Result;
                GroupsMade++;
                Changed?.Invoke();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ looking for faces

        private void Work()
        {
            var sinceSave = 0;
            while (!_stop)
            {
                _wake.WaitOne(TimeSpan.FromSeconds(30));
                if (_stop) return;
                var todo = _catalog.Library.All().Where(i => i.Kind == MediaKind.Photo && !_catalog.Faces.Done(i)).OrderByDescending(i => i.When).ToList();
                if (todo.Count == 0) continue;
                var total = _catalog.Library.All().Count(i => i.Kind == MediaKind.Photo);
                SetBusy(true, total - todo.Count, total);
                foreach (var item in todo)
                {
                    if (_stop) return;
                    if (!File.Exists(item.Path) || _catalog.Library.Find(item.Path) == null) continue;
                    var photo = new PhotoFaces { Path = item.Path, Size = item.Size, Modified = item.Modified };
                    try
                    {
                        var pixels = Imaging.ToPixels(Imaging.Decode(item.Path, item.Orientation, 1280, item.Width, item.Height));
                        photo.Faces = _finder.Find(pixels);
                        foreach (var f in photo.Faces) f.Landmarks = null; // not kept
                    }
                    catch (Exception)
                    {
                        // Not a picture Windows can open: counted as looked at, with no faces.
                    }
                    var before = _catalog.Faces.Get(item.Path);
                    // The photo changed: what was said about its old faces no longer fits.
                    if (before != null && (before.Size != item.Size || before.Modified != item.Modified))
                        _ui.Invoke(new Action(() => _catalog.People.Forget(new[] { item.Path })));
                    _catalog.Faces.Put(photo);
                    Done++;
                    if (++sinceSave >= 40)
                    {
                        sinceSave = 0;
                        _ui.BeginInvoke(new Action(() =>
                        {
                            _catalog.FacesTouched();
                            Soon();
                        }));
                    }
                    if (Done % 10 == 0) SetBusy(true, Done, Total);
                    // Slower while the PC is being used.
                    Thread.Sleep(Idle() ? 5 : 250);
                }
                SetBusy(false, total, total);
                _ui.BeginInvoke(new Action(() =>
                {
                    _catalog.FacesTouched();
                    Soon();
                }));
            }
        }

        private void SetBusy(bool busy, int done, int total)
        {
            Busy = busy;
            Done = done;
            Total = total;
            _ui.BeginInvoke(new Action(() => Status?.Invoke(busy ? $"Looking for people… {done:N0} of {total:N0} photos" : null)));
        }

        /// <summary>Starts looking (again) now.</summary>
        public void Wake() => _wake.Set();

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>No mouse or keyboard for 3 seconds.</summary>
        private static bool Idle()
        {
            try
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (!GetLastInputInfo(ref info)) return true;
                return unchecked((uint)Environment.TickCount - info.dwTime) > 3000;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ------------------------------------------------------------------ who's who

        /// <summary>The people in a photo (named or joined by themselves), in the order their faces are from the left.</summary>
        public List<Person> PeopleIn(string path)
        {
            var photo = _catalog.Faces.Get(path);
            if (photo == null) return new List<Person>();
            var g = Groups;
            return Enumerable.Range(0, photo.Faces.Count).Select(i => g.PersonOf.TryGetValue(FaceStore.Id(path, i), out var p) ? p : null)
                .Where(p => p != null).Distinct().ToList();
        }

        /// <summary>The photos a person is in.</summary>
        public HashSet<string> PhotosOf(Person person) =>
            new HashSet<string>(Groups.PersonOf.Where(x => x.Value == person).Select(x => FaceStore.Split(x.Key).Path), StringComparer.OrdinalIgnoreCase);

        public int PhotoCount(Person person) => PhotosOf(person).Count;

        // ------------------------------------------------------------------ small face pictures

        private readonly Dictionary<string, BitmapSource> _crops = new Dictionary<string, BitmapSource>();
        private readonly SemaphoreSlim _cropping = new SemaphoreSlim(2);

        /// <summary>A small square picture of a face (right away when made already, else later through <paramref name="done"/>).</summary>
        public BitmapSource Crop(string faceId, Action<BitmapSource> done)
        {
            lock (_crops)
                if (_crops.TryGetValue(faceId, out var b)) return b;
            var (path, _) = FaceStore.Split(faceId);
            var face = _catalog.Faces.Face(faceId);
            var item = _catalog.Library.Find(path);
            if (face == null || item == null) return null;
            Task.Run(async () =>
            {
                await _cropping.WaitAsync();
                try
                {
                    var p = Imaging.ToPixels(Imaging.Decode(item.Path, item.Orientation, 1280, item.Width, item.Height));
                    var side = Math.Max(face.W * p.Width, face.H * p.Height) * 1.5;
                    var cx = (face.X + face.W / 2) * p.Width;
                    var cy = (face.Y + face.H / 2) * p.Height;
                    var left = Math.Max(0, Math.Min(p.Width - side, cx - side / 2));
                    var top = Math.Max(0, Math.Min(p.Height - side, cy - side / 2));
                    var crop = ImageOps.Crop(p, left / p.Width, top / p.Height, Math.Min(1, (left + side) / p.Width), Math.Min(1, (top + side) / p.Height));
                    return Imaging.ToBitmap(ImageOps.Resize(crop, 96, 96));
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    _cropping.Release();
                }
            }).ContinueWith(t =>
            {
                if (t.Result == null) return;
                lock (_crops) _crops[faceId] = t.Result;
                done?.Invoke(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return null;
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _regroup.Stop();
            _thread?.Join(3000);
            _finder?.Dispose();
        }
    }
}
