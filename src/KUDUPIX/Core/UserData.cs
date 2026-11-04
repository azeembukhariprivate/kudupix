using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Kudupix.Core
{
    /// <summary>What you've added to one photo or video.</summary>
    [DataContract]
    public sealed class ItemData
    {
        [DataMember] public string Path { get; set; } = string.Empty;
        /// <summary>0 (none) to 5.</summary>
        [DataMember] public int Stars { get; set; }
        [DataMember] public List<string> Tags { get; set; } = new List<string>();
        /// <summary>Fixes kept in KUDUPIX (the file itself is unchanged until a copy is saved).</summary>
        [DataMember] public EditRecipe Edit { get; set; }

        public bool IsEmpty => Stars == 0 && (Tags == null || Tags.Count == 0) && (Edit == null || Edit.IsEmpty);
    }

    [DataContract]
    public sealed class Album
    {
        [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember] public string Name { get; set; } = string.Empty;
        [DataMember] public List<string> Paths { get; set; } = new List<string>();
        [DataMember] public DateTime Created { get; set; } = DateTime.Now;

        public override string ToString() => Name;
    }

    [DataContract]
    internal sealed class UserDataFile
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public List<ItemData> Items { get; set; } = new List<ItemData>();
        [DataMember] public List<Album> Albums { get; set; } = new List<Album>();
    }

    /// <summary>
    /// Stars, tags, albums and fixes, kept in KUDUPIX's own file (userdata.json) and never written into the photos.
    /// Albums only point to the files; nothing is moved or copied.
    /// </summary>
    public sealed class UserData
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ItemData> _items = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Album> _albums = new List<Album>();

        public string FilePath { get; }

        /// <summary>Something was changed (stars, tags, albums or a fix).</summary>
        public event Action Changed;

        public UserData(string filePath)
        {
            FilePath = filePath;
        }

        private ItemData Get(string path, bool create)
        {
            if (path == null) return null;
            if (_items.TryGetValue(path, out var d)) return d;
            if (!create) return null;
            d = new ItemData { Path = path };
            _items[path] = d;
            return d;
        }

        private void Tidy(string path)
        {
            if (_items.TryGetValue(path, out var d) && d.IsEmpty) _items.Remove(path);
        }

        private void Raise() => Changed?.Invoke();

        // ------------------------------------------------------------------ stars

        public int Stars(string path)
        {
            lock (_sync) return Get(path, false)?.Stars ?? 0;
        }

        public void SetStars(IEnumerable<string> paths, int stars)
        {
            stars = Math.Max(0, Math.Min(5, stars));
            lock (_sync)
                foreach (var p in paths)
                {
                    Get(p, true).Stars = stars;
                    Tidy(p);
                }
            Raise();
        }

        // ------------------------------------------------------------------ tags

        /// <summary>Tags are kept as typed, but "Beach" and "beach" count as the same tag.</summary>
        public static string CleanTag(string tag) => string.Join(" ", (tag ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim(',', ';', '#');

        public List<string> Tags(string path)
        {
            lock (_sync) return Get(path, false)?.Tags?.ToList() ?? new List<string>();
        }

        public void AddTag(IEnumerable<string> paths, string tag)
        {
            tag = CleanTag(tag);
            if (tag.Length == 0) return;
            lock (_sync)
            {
                // Use the spelling already in use, if any.
                var existing = _items.Values.SelectMany(i => i.Tags).FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                tag = existing ?? tag;
                foreach (var p in paths)
                {
                    var d = Get(p, true);
                    if (!d.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) d.Tags.Add(tag);
                }
            }
            Raise();
        }

        public void RemoveTag(IEnumerable<string> paths, string tag)
        {
            lock (_sync)
                foreach (var p in paths)
                {
                    var d = Get(p, false);
                    if (d == null) continue;
                    d.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                    Tidy(p);
                }
            Raise();
        }

        /// <summary>Every tag in use, with how many items have it, most used first.</summary>
        public List<(string Tag, int Count)> AllTags()
        {
            lock (_sync)
                return _items.Values.SelectMany(i => i.Tags)
                    .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.First(), g.Count()))
                    .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
        }

        public HashSet<string> PathsWithTag(string tag)
        {
            lock (_sync)
                return new HashSet<string>(_items.Values.Where(i => i.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).Select(i => i.Path),
                    StringComparer.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ fixes

        public EditRecipe Edit(string path)
        {
            lock (_sync) return Get(path, false)?.Edit?.Clone();
        }

        public void SetEdit(string path, EditRecipe edit)
        {
            lock (_sync)
            {
                Get(path, true).Edit = edit == null || edit.IsEmpty ? null : edit.Clone();
                Tidy(path);
            }
            Raise();
        }

        // ------------------------------------------------------------------ albums

        public List<Album> Albums()
        {
            lock (_sync) return _albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public Album FindAlbum(string id)
        {
            lock (_sync) return _albums.FirstOrDefault(a => a.Id == id);
        }

        public Album AddAlbum(string name)
        {
            var album = new Album { Name = UniqueAlbumName(CleanTag(name)) };
            lock (_sync) _albums.Add(album);
            Raise();
            return album;
        }

        private string UniqueAlbumName(string name)
        {
            if (name.Length == 0) name = "New album";
            lock (_sync)
            {
                var n = name;
                for (var i = 2; _albums.Any(a => string.Equals(a.Name, n, StringComparison.CurrentCultureIgnoreCase)); i++) n = $"{name} ({i})";
                return n;
            }
        }

        public void RenameAlbum(Album album, string name)
        {
            name = CleanTag(name);
            if (name.Length == 0 || string.Equals(album.Name, name, StringComparison.CurrentCulture)) return;
            lock (_sync)
                album.Name = _albums.Any(a => a != album && string.Equals(a.Name, name, StringComparison.CurrentCultureIgnoreCase)) ? UniqueAlbumName(name) : name;
            Raise();
        }

        /// <summary>Removes an album; the photos stay where they are.</summary>
        public void RemoveAlbum(Album album)
        {
            lock (_sync) _albums.Remove(album);
            Raise();
        }

        public void AddToAlbum(Album album, IEnumerable<string> paths)
        {
            lock (_sync)
                foreach (var p in paths)
                    if (!album.Paths.Contains(p, StringComparer.OrdinalIgnoreCase)) album.Paths.Add(p);
            Raise();
        }

        public void RemoveFromAlbum(Album album, IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            lock (_sync) album.Paths.RemoveAll(set.Contains);
            Raise();
        }

        public List<Album> AlbumsOf(string path)
        {
            lock (_sync) return _albums.Where(a => a.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ------------------------------------------------------------------ files moving and going

        /// <summary>A file moved or was renamed: its stars, tags, fixes and albums follow it.</summary>
        public void Move(string from, string to)
        {
            lock (_sync)
            {
                if (_items.TryGetValue(from, out var d))
                {
                    _items.Remove(from);
                    d.Path = to;
                    _items[to] = d;
                }
                foreach (var a in _albums)
                    for (var i = 0; i < a.Paths.Count; i++)
                        if (string.Equals(a.Paths[i], from, StringComparison.OrdinalIgnoreCase)) a.Paths[i] = to;
            }
            Raise();
        }

        /// <summary>A file was deleted on purpose (sent to the Recycle Bin from KUDUPIX): forget it everywhere.</summary>
        public void Forget(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                foreach (var p in set) _items.Remove(p);
                foreach (var a in _albums) a.Paths.RemoveAll(set.Contains);
            }
            Raise();
        }

        // ------------------------------------------------------------------ the file on disk

        public void Save()
        {
            var data = new UserDataFile();
            lock (_sync)
            {
                data.Items = _items.Values.Where(i => !i.IsEmpty).ToList();
                data.Albums = _albums.ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            var temp = FilePath + ".new";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                new DataContractJsonSerializer(typeof(UserDataFile)).WriteObject(fs, data);
            if (File.Exists(FilePath))
            {
                // One earlier copy is kept, in case the PC is switched off at the wrong moment.
                File.Replace(temp, FilePath, FilePath + ".bak");
            }
            else File.Move(temp, FilePath);
        }

        public bool Load()
        {
            foreach (var path in new[] { FilePath, FilePath + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    UserDataFile data;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        data = (UserDataFile)new DataContractJsonSerializer(typeof(UserDataFile)).ReadObject(fs);
                    lock (_sync)
                    {
                        _items.Clear();
                        foreach (var i in data.Items ?? new List<ItemData>())
                        {
                            if (string.IsNullOrEmpty(i.Path)) continue;
                            i.Tags = i.Tags ?? new List<string>();
                            _items[i.Path] = i;
                        }
                        _albums.Clear();
                        foreach (var a in data.Albums ?? new List<Album>())
                        {
                            a.Paths = a.Paths ?? new List<string>();
                            _albums.Add(a);
                        }
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException || ex is InvalidCastException)
                {
                    // Try the earlier copy.
                }
            }
            return false;
        }
    }

    /// <summary>Finding photos by words: the name, folder, tags, albums, camera, year and month.</summary>
    public static class Search
    {
        public static List<string> Words(string text) =>
            (text ?? string.Empty).ToLowerInvariant().Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        /// <summary>Every word typed must be found somewhere about the item (as the start of a word, or inside the name).</summary>
        public static bool Matches(MediaItem item, IList<string> words, IList<string> tags, IList<string> albums)
        {
            if (words.Count == 0) return true;
            var when = item.When;
            var about = new StringBuilder();
            about.Append(item.Path).Append(' ');
            foreach (var t in tags) about.Append(t).Append(' ');
            foreach (var a in albums) about.Append(a).Append(' ');
            about.Append(item.Camera).Append(' ');
            about.Append(when.Year.ToString(CultureInfo.InvariantCulture)).Append(' ');
            about.Append(when.ToString("MMMM", CultureInfo.CurrentCulture)).Append(' ');
            about.Append(when.ToString("MMMM", CultureInfo.InvariantCulture)).Append(' ');
            about.Append(item.IsVideo ? "video videos" : "photo photos picture pictures");
            if (item.HasPlace) about.Append(" place location gps");
            var text = about.ToString().ToLowerInvariant();
            var parts = new HashSet<string>(Split(text));
            var name = item.Name.ToLowerInvariant();
            return words.All(w => name.Contains(w) || parts.Any(p => p.StartsWith(w, StringComparison.Ordinal)));
        }

        private static IEnumerable<string> Split(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }

    /// <summary>Photos grouped by day, or by month, newest or oldest first.</summary>
    public static class Timeline
    {
        public sealed class Group
        {
            public DateTime Start { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
            public List<MediaItem> Items { get; set; } = new List<MediaItem>();
        }

        public static List<Group> ByDay(IEnumerable<MediaItem> items, bool newestFirst = true) => Make(items, newestFirst, false);

        public static List<Group> ByMonth(IEnumerable<MediaItem> items, bool newestFirst = true) => Make(items, newestFirst, true);

        private static List<Group> Make(IEnumerable<MediaItem> items, bool newestFirst, bool month)
        {
            var sorted = newestFirst ? items.OrderByDescending(i => i.When).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                                     : items.OrderBy(i => i.When).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase);
            var groups = new List<Group>();
            Group current = null;
            foreach (var i in sorted)
            {
                var key = month ? new DateTime(i.When.Year, i.When.Month, 1) : i.When.Date;
                if (current == null || current.Start != key)
                {
                    current = new Group
                    {
                        Start = key,
                        Title = month ? key.ToString("MMMM yyyy", CultureInfo.CurrentCulture) : DayTitle(key)
                    };
                    groups.Add(current);
                }
                current.Items.Add(i);
            }
            foreach (var g in groups) g.Note = Note(g.Items);
            return groups;
        }

        public static string DayTitle(DateTime day)
        {
            var today = DateTime.Today;
            if (day == today) return "Today";
            if (day == today.AddDays(-1)) return "Yesterday";
            return day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary>"Camera Roll · 9 items": the folder most of them are in, and how many.</summary>
        public static string Note(IList<MediaItem> items)
        {
            var folder = items.GroupBy(i => Path.GetFileName(i.Folder.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;
            var count = Format.Count(items.Count, "item");
            return folder.Length > 0 ? folder + " · " + count : count;
        }
    }
}
