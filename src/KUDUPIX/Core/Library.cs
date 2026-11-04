using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kudupix.Core
{
    public enum MediaKind { Photo, Video }

    /// <summary>The kinds of file KUDUPIX shows.</summary>
    public static class MediaTypes
    {
        public static readonly string[] Photos = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".webp", ".jxr", ".wdp", ".ico" };
        public static readonly string[] Videos = { ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".avi", ".wmv", ".mkv", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg" };

        /// <summary>Photo files Windows can also write back (for "Save over the original").</summary>
        public static readonly string[] Writable = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".tif", ".tiff", ".gif" };

        public static MediaKind? KindOf(string path)
        {
            var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            if (Array.IndexOf(Photos, ext) >= 0) return MediaKind.Photo;
            if (Array.IndexOf(Videos, ext) >= 0) return MediaKind.Video;
            return null;
        }

        public static bool CanWrite(string path) => Array.IndexOf(Writable, Path.GetExtension(path ?? string.Empty).ToLowerInvariant()) >= 0;

        /// <summary>Videos whose details KUDUPIX reads itself (the others still show, with the file's date).</summary>
        public static bool IsMp4Family(string path)
        {
            var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".mp4" || ext == ".m4v" || ext == ".mov" || ext == ".3gp" || ext == ".3g2";
        }
    }

    /// <summary>One photo or video in the library.</summary>
    public sealed class MediaItem
    {
        public string Path { get; set; } = string.Empty;
        public MediaKind Kind { get; set; }
        public long Size { get; set; }
        /// <summary>The file's last change, in UTC ticks: a different value means the file changed.</summary>
        public long Modified { get; set; }
        /// <summary>When it was taken or filmed (from inside the file), in the time it was taken.</summary>
        public DateTime? Taken { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Orientation { get; set; } = 1;
        public double Seconds { get; set; }
        public string Camera { get; set; } = string.Empty;
        public string Exposure { get; set; } = string.Empty;
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        /// <summary>The picture's fingerprint for finding look-alikes (0 until it's worked out).</summary>
        public ulong Look { get; set; }
        /// <summary>The SHA-256 of the contents (empty until it's needed to find exact copies).</summary>
        public string Hash { get; set; } = string.Empty;

        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        public bool IsVideo => Kind == MediaKind.Video;

        /// <summary>When it's sorted and grouped by: when it was taken, else the file's date.</summary>
        public DateTime When => Taken ?? new DateTime(Modified, DateTimeKind.Utc).ToLocalTime();

        /// <summary>The picture's size as shown (a photo taken upright is taller than wide).</summary>
        public int ShownWidth => Orientation >= 5 && Orientation <= 8 ? Height : Width;
        public int ShownHeight => Orientation >= 5 && Orientation <= 8 ? Width : Height;

        public bool HasPlace => Latitude.HasValue && Longitude.HasValue;

        public override string ToString() => Path;
    }

    /// <summary>What a scan found.</summary>
    public sealed class ScanResult
    {
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
        public int Unchanged { get; set; }
        /// <summary>Files that moved or were renamed (the same file found under a new path), so stars and tags can follow.</summary>
        public List<(string From, string To)> Moved { get; } = new List<(string, string)>();
        public bool Cancelled { get; set; }
        public bool AnyChange => Added + Changed + Removed + Moved.Count > 0;
    }

    /// <summary>
    /// The library: every photo and video found in the chosen folders, with the details read from inside each file.
    /// Kept in one file (library.bin) so the next start shows everything at once; a scan brings it up to date, reading
    /// only files that are new or changed. Never changes a photo or video.
    /// </summary>
    public sealed class Library
    {
        private const string Magic = "KUDUPIX-LIBRARY-1";
        private readonly object _sync = new object();
        private Dictionary<string, MediaItem> _items = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; }

        public Library(string filePath)
        {
            FilePath = filePath;
        }

        public int Count
        {
            get { lock (_sync) return _items.Count; }
        }

        /// <summary>A snapshot of every item.</summary>
        public List<MediaItem> All()
        {
            lock (_sync) return _items.Values.ToList();
        }

        public MediaItem Find(string path)
        {
            lock (_sync) return path != null && _items.TryGetValue(path, out var i) ? i : null;
        }

        public void Remove(string path)
        {
            lock (_sync) _items.Remove(path);
        }

        /// <summary>Adds or replaces one item (after a file was saved, for example).</summary>
        public void Put(MediaItem item)
        {
            lock (_sync) _items[item.Path] = item;
        }

        // ------------------------------------------------------------------ scanning

        /// <summary>The folders KUDUPIX skips: Windows' own, hidden ones and app caches.</summary>
        public static bool SkipFolder(DirectoryInfo dir)
        {
            var name = dir.Name;
            if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("$", StringComparison.Ordinal)) return true;
            if (string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var a = dir.Attributes;
                if ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0) return true;
                // Links and junctions could lead round in circles or out of the chosen folders.
                if ((a & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return true;
            }
            return false;
        }

        /// <summary>Every photo and video file under a folder (skipping folders that can't be read).</summary>
        public static IEnumerable<FileInfo> Files(string root, string skip, CancellationToken cancel)
        {
            var stack = new Stack<DirectoryInfo>();
            DirectoryInfo first;
            try { first = new DirectoryInfo(root); }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException || ex is NotSupportedException) { yield break; }
            if (!first.Exists) yield break;
            stack.Push(first);
            while (stack.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                if (skip != null && Inside(dir.FullName, skip)) continue;
                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { continue; }
                foreach (var e in entries)
                {
                    if (e is DirectoryInfo d)
                    {
                        if (!SkipFolder(d)) stack.Push(d);
                    }
                    else if (e is FileInfo f && MediaTypes.KindOf(f.Name).HasValue)
                    {
                        FileAttributes a;
                        try { a = f.Attributes; }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                        if ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0) continue;
                        yield return f;
                    }
                }
            }
        }

        public static bool Inside(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            var p = path.TrimEnd('\\', '/') + "\\";
            var f = folder.TrimEnd('\\', '/') + "\\";
            return p.Replace('/', '\\').StartsWith(f.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads a file's details: what's inside it, and its size and date.</summary>
        public static MediaItem Read(FileInfo f)
        {
            var kind = MediaTypes.KindOf(f.Name) ?? MediaKind.Photo;
            var item = new MediaItem { Path = f.FullName, Kind = kind, Size = f.Length, Modified = f.LastWriteTimeUtc.Ticks };
            try
            {
                if (kind == MediaKind.Photo)
                {
                    var d = PhotoInfo.Read(f.FullName);
                    item.Taken = d.Taken;
                    item.Width = d.Width;
                    item.Height = d.Height;
                    item.Orientation = d.Orientation >= 1 && d.Orientation <= 8 ? d.Orientation : 1;
                    item.Camera = d.Camera ?? string.Empty;
                    item.Exposure = d.Exposure ?? string.Empty;
                    item.Latitude = d.Latitude;
                    item.Longitude = d.Longitude;
                }
                else if (MediaTypes.IsMp4Family(f.Name))
                {
                    var d = VideoInfo.Read(f.FullName);
                    item.Taken = d.Taken;
                    item.Width = d.Width;
                    item.Height = d.Height;
                    item.Seconds = d.Seconds;
                    item.Latitude = d.Latitude;
                    item.Longitude = d.Longitude;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // In use or unreadable now: listed with the file's date; read again when it changes.
            }
            return item;
        }

        /// <summary>
        /// Brings the library up to date with the folders: new files are read, changed ones read again, missing ones
        /// dropped. A file that disappeared and turned up elsewhere with the same name, size and date counts as moved.
        /// <paramref name="skip"/> is a folder never to look in (KUDUPIX's own).
        /// </summary>
        public ScanResult Scan(IList<string> roots, string skip = null, CancellationToken cancel = default, Action<int> progress = null)
        {
            var result = new ScanResult();
            var found = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MediaItem> before;
            lock (_sync) before = new Dictionary<string, MediaItem>(_items, StringComparer.OrdinalIgnoreCase);
            var added = new List<MediaItem>();
            var seen = 0;
            try
            {
                foreach (var root in Roots.Clean(roots))
                {
                    foreach (var f in Files(root, skip, cancel))
                    {
                        if (found.ContainsKey(f.FullName)) continue;
                        long length, ticks;
                        try
                        {
                            length = f.Length;
                            ticks = f.LastWriteTimeUtc.Ticks;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                        if (before.TryGetValue(f.FullName, out var old) && old.Size == length && old.Modified == ticks)
                        {
                            found[f.FullName] = old;
                            result.Unchanged++;
                        }
                        else
                        {
                            var item = Read(f);
                            found[f.FullName] = item;
                            if (old != null)
                            {
                                result.Changed++;
                                lock (_sync) _items[item.Path] = item; // shown at once
                            }
                            else added.Add(item);
                        }
                        if (++seen % 500 == 0) progress?.Invoke(seen);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Keep what's known: only add what was found, remove nothing.
                result.Cancelled = true;
                lock (_sync)
                    foreach (var i in added) _items[i.Path] = i;
                result.Added = added.Count;
                return result;
            }

            var gone = before.Values.Where(i => !found.ContainsKey(i.Path)).ToList();
            // Moved or renamed: the same name (or the same size and date taken) in a new place.
            foreach (var g in gone.ToList())
            {
                var match = added.FirstOrDefault(a => a.Size == g.Size && a.Kind == g.Kind && a.Taken == g.Taken &&
                                                      (string.Equals(a.Name, g.Name, StringComparison.OrdinalIgnoreCase) || a.Taken.HasValue));
                if (match == null) continue;
                result.Moved.Add((g.Path, match.Path));
                match.Look = g.Look;
                match.Hash = g.Hash;
                gone.Remove(g);
                added.Remove(match);
            }
            result.Added = added.Count;
            result.Removed = gone.Count;
            lock (_sync) _items = found;
            return result;
        }

        // ------------------------------------------------------------------ the file on disk

        public void Save()
        {
            List<MediaItem> items;
            lock (_sync) items = _items.Values.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            var temp = FilePath + ".new";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(items.Count);
                foreach (var i in items)
                {
                    w.Write(i.Path);
                    w.Write((byte)i.Kind);
                    w.Write(i.Size);
                    w.Write(i.Modified);
                    w.Write(i.Taken.HasValue ? i.Taken.Value.Ticks : 0L);
                    w.Write(i.Width);
                    w.Write(i.Height);
                    w.Write((byte)i.Orientation);
                    w.Write(i.Seconds);
                    w.Write(i.Camera ?? string.Empty);
                    w.Write(i.Exposure ?? string.Empty);
                    w.Write(i.Latitude ?? double.NaN);
                    w.Write(i.Longitude ?? double.NaN);
                    w.Write(i.Look);
                    w.Write(i.Hash ?? string.Empty);
                }
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }

        /// <summary>Loads the library kept last time; a missing or damaged file just means a fresh start.</summary>
        public bool Load()
        {
            var items = new Dictionary<string, MediaItem>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return false;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadString() != Magic) return false;
                    var count = r.ReadInt32();
                    for (var n = 0; n < count; n++)
                    {
                        var i = new MediaItem
                        {
                            Path = r.ReadString(),
                            Kind = (MediaKind)r.ReadByte(),
                            Size = r.ReadInt64(),
                            Modified = r.ReadInt64()
                        };
                        var taken = r.ReadInt64();
                        i.Taken = taken == 0 ? (DateTime?)null : new DateTime(taken);
                        i.Width = r.ReadInt32();
                        i.Height = r.ReadInt32();
                        i.Orientation = r.ReadByte();
                        i.Seconds = r.ReadDouble();
                        i.Camera = r.ReadString();
                        i.Exposure = r.ReadString();
                        var la = r.ReadDouble();
                        var lo = r.ReadDouble();
                        i.Latitude = double.IsNaN(la) ? (double?)null : la;
                        i.Longitude = double.IsNaN(lo) ? (double?)null : lo;
                        i.Look = r.ReadUInt64();
                        i.Hash = r.ReadString();
                        items[i.Path] = i;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is EndOfStreamException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException)
            {
                return false;
            }
            lock (_sync) _items = items;
            return true;
        }
    }

    /// <summary>The folders KUDUPIX looks in.</summary>
    public static class Roots
    {
        /// <summary>The folders without repeats, and without folders inside another one in the list.</summary>
        public static List<string> Clean(IEnumerable<string> folders)
        {
            var list = new List<string>();
            foreach (var f in folders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                string full;
                try { full = Path.GetFullPath(f).TrimEnd('\\', '/'); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { continue; }
                if (full.EndsWith(":")) full += "\\";
                if (list.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase) || Library.Inside(full, x))) continue;
                list.RemoveAll(x => Library.Inside(x, full));
                list.Add(full);
            }
            return list;
        }

        /// <summary>The folder a path is shown under in the folder list (the chosen folder it's in).</summary>
        public static string RootOf(string path, IEnumerable<string> roots) =>
            roots.Where(r => Library.Inside(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
    }

    /// <summary>A video's length in words (sizes and counts are in the shared Format).</summary>
    public static class VideoLength
    {
        /// <summary>"0:48", "12:05", "1:02:33".</summary>
        public static string Text(double seconds)
        {
            if (seconds <= 0) return string.Empty;
            var t = TimeSpan.FromSeconds(Math.Round(seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }
}
