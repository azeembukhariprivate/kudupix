using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>
    /// The library while KUDUPIX is open: loads what was kept, looks through the folders in the background, watches them
    /// so new photos appear by themselves, and saves stars, tags, albums and fixes soon after each change.
    /// </summary>
    public sealed class Catalog : IDisposable
    {
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _scanTimer;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private CancellationTokenSource _scanCancel;
        private bool _libraryDirty;
        private bool _peopleDirty;
        private bool _facesDirty;
        private bool _scanAgain;

        public Library Library { get; }
        public UserData Data { get; }
        public Originals Originals { get; }
        public FaceStore Faces { get; }
        public PeopleData People { get; }

        /// <summary>The library changed (a scan found something, or a file was saved or removed).</summary>
        public event Action Changed;

        /// <summary>Stars, tags, albums or fixes changed.</summary>
        public event Action DataChanged;

        /// <summary>People, or the faces found, changed.</summary>
        public event Action PeopleChanged;

        /// <summary>What the status bar says about looking through the folders.</summary>
        public event Action<string> Status;

        public bool Scanning { get; private set; }
        public int ScansDone { get; private set; }
        public ScanResult LastScan { get; private set; }

        public Catalog()
        {
            _ui = Dispatcher.CurrentDispatcher;
            Library = new Library(AppPaths.LibraryFile);
            Data = new UserData(AppPaths.UserDataFile);
            Originals = new Originals(AppPaths.Originals);
            Library.Load();
            Data.Load();
            Faces = new FaceStore(AppPaths.FacesFile);
            People = new PeopleData(AppPaths.PeopleFile);
            Faces.Load();
            People.Load();
            People.Changed += () => _ui.BeginInvoke(new Action(() =>
            {
                _peopleDirty = true;
                _saveTimer.Stop();
                _saveTimer.Start();
                PeopleChanged?.Invoke();
            }));
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _scanTimer.Tick += (s, e) =>
            {
                _scanTimer.Stop();
                Scan();
            };
            Data.Changed += () => _ui.BeginInvoke(new Action(() =>
            {
                _saveTimer.Stop();
                _saveTimer.Start();
                DataChanged?.Invoke();
            }));
        }

        public List<string> Folders => Roots.Clean(Settings.Folders);

        // ------------------------------------------------------------------ looking through the folders

        /// <summary>Looks through the folders now (in the background); a scan already running is followed by another.</summary>
        public void Scan()
        {
            if (Scanning)
            {
                _scanAgain = true;
                return;
            }
            Scanning = true;
            _scanAgain = false;
            var cancel = new CancellationTokenSource();
            _scanCancel = cancel;
            var folders = Folders;
            Status?.Invoke(Library.Count == 0 ? "Looking for photos and videos…" : "Checking for new photos and videos…");
            Task.Run(() =>
            {
                ScanResult result = null;
                try
                {
                    result = Library.Scan(folders, AppPaths.Root, cancel.Token,
                        n => _ui.BeginInvoke(new Action(() => Status?.Invoke($"Looking for photos and videos… {n:N0} so far"))));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result = new ScanResult();
                }
                _ui.BeginInvoke(new Action(() => Scanned(result, cancel)));
            });
        }

        private void Scanned(ScanResult result, CancellationTokenSource cancel)
        {
            Scanning = false;
            ScansDone++;
            LastScan = result;
            if (_scanCancel == cancel) _scanCancel = null;
            foreach (var (from, to) in result.Moved)
            {
                Data.Move(from, to);
                Faces.Move(from, to);
                People.Move(from, to);
                _facesDirty = true;
            }
            if (result.AnyChange)
            {
                _libraryDirty = true;
                _saveTimer.Stop();
                _saveTimer.Start();
                Changed?.Invoke();
            }
            Watch();
            Status?.Invoke(StatusText());
            if (_scanAgain && !result.Cancelled) Scan();
        }

        public string StatusText()
        {
            var n = Folders.Count;
            return n == 0 ? "No folders chosen yet" : $"Watching {Format.Count(n, "folder")} · up to date";
        }

        /// <summary>The folder list changed: watch the new ones and look through them.</summary>
        public void FoldersChanged()
        {
            _scanCancel?.Cancel();
            _scanAgain = true;
            Scan();
        }

        private void Watch()
        {
            var folders = Folders;
            if (_watchers.Select(w => w.Path).OrderBy(p => p).SequenceEqual(folders.OrderBy(p => p), StringComparer.OrdinalIgnoreCase)) return;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            foreach (var f in folders.Where(Directory.Exists))
            {
                try
                {
                    var w = new FileSystemWatcher(f)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024
                    };
                    FileSystemEventHandler changed = (s, e) => Soon(e.FullPath);
                    w.Created += changed;
                    w.Changed += changed;
                    w.Deleted += changed;
                    w.Renamed += (s, e) => Soon(e.FullPath);
                    w.Error += (s, e) => Soon(null);
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
                {
                    // That folder is looked through at each start instead.
                }
            }
        }

        /// <summary>Something changed in a folder: look again in a moment (many changes at once make one look).</summary>
        private void Soon(string path)
        {
            if (path != null && Library.Inside(path, AppPaths.Root)) return;
            if (path != null && !MediaTypes.KindOf(path).HasValue && Path.HasExtension(path)) return;
            _ui.BeginInvoke(new Action(() =>
            {
                _scanTimer.Stop();
                _scanTimer.Start();
            }));
        }

        // ------------------------------------------------------------------ changes made in KUDUPIX

        /// <summary>A file was saved by KUDUPIX (a fixed copy, or over the original): read it now.</summary>
        public MediaItem Refresh(string path)
        {
            var item = Library.Read(new FileInfo(path));
            var old = Library.Find(path);
            if (old != null)
            {
                item.Hash = string.Empty;
                item.Look = 0;
            }
            Library.Put(item);
            _libraryDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
            Changed?.Invoke();
            return item;
        }

        /// <summary>Moves files to the Recycle Bin and forgets them. Returns those that couldn't be moved.</summary>
        public List<MediaItem> Recycle(IList<MediaItem> items)
        {
            var failed = new List<MediaItem>();
            foreach (var chunk in items.Select((x, i) => (x, i)).GroupBy(p => p.i / 200, p => p.x))
            {
                var list = chunk.ToList();
                Recycler.Recycle(list.Select(i => i.Path).ToList());
                // Whatever is still there didn't go (in use, or Windows was asked and said no).
                failed.AddRange(list.Where(i => File.Exists(i.Path)));
            }
            var gone = items.Where(i => !File.Exists(i.Path)).ToList();
            foreach (var g in gone) Library.Remove(g.Path);
            Data.Forget(gone.Select(g => g.Path));
            People.Forget(gone.Select(g => g.Path));
            _facesDirty = true;
            _libraryDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
            Changed?.Invoke();
            return failed;
        }

        /// <summary>Keeps fingerprints and contents hashes worked out for duplicates.</summary>
        public void LibraryTouched()
        {
            _libraryDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Faces were found: keep them soon.</summary>
        public void FacesTouched()
        {
            _facesDirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow()
        {
            try
            {
                if (_peopleDirty) People.Save();
                _peopleDirty = false;
                if (_facesDirty)
                {
                    Faces.Keep(Library.All().Select(i => i.Path).ToList());
                    Faces.Save();
                }
                _facesDirty = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Saved at the next change.
            }
            try { Data.Save(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Status?.Invoke("Couldn't save stars, tags and albums: " + ex.Message); }
            if (!_libraryDirty) return;
            try
            {
                Library.Save();
                _libraryDirty = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Looked through again at the next start.
            }
        }

        public void Dispose()
        {
            _scanCancel?.Cancel();
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            _saveTimer.Stop();
            SaveNow();
        }
    }
}
