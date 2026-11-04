using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kudupix.Core
{
    /// <summary>Copies of the same photo or video, with the one to keep.</summary>
    public sealed class DuplicateGroup
    {
        public List<MediaItem> Items { get; set; } = new List<MediaItem>();
        public MediaItem Keep { get; set; }
        /// <summary>Every copy is the very same file contents (not just the same picture).</summary>
        public bool Exact { get; set; }

        /// <summary>What the copies not kept take up.</summary>
        public long Saving => Items.Where(i => i != Keep).Sum(i => i.Size);
    }

    /// <summary>
    /// Finds copies: files with exactly the same contents (photos and videos), and photos that look the same (resized,
    /// re-saved or sent through a messaging app) by their fingerprints.
    /// </summary>
    public static class Duplicates
    {
        /// <summary>How many of the 64 fingerprint bits may differ for two photos to count as the same picture.</summary>
        public const int MaxDistance = 4;

        public static string FileHash(string path, CancellationToken cancel = default)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, n, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Groups the copies. <paramref name="hash"/> gives a file's contents hash (worked out only for files of the same
        /// size); photos are compared by their fingerprint (<see cref="MediaItem.Look"/>, 0 = not known).
        /// </summary>
        public static List<DuplicateGroup> Find(IList<MediaItem> items, Func<MediaItem, string> hash, CancellationToken cancel = default)
        {
            var parent = new Dictionary<MediaItem, MediaItem>();
            MediaItem Root(MediaItem i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }
            foreach (var i in items) parent[i] = i;
            // Exact copies are joined first; their groups are merged into the look-alike groups below.
            var exactJoins = new List<(MediaItem, MediaItem)>();
            void Join(MediaItem a, MediaItem b) => exactJoins.Add((a, b));

            // Exactly the same contents: only files of the same size can be.
            var hashes = new Dictionary<MediaItem, string>();
            foreach (var same in items.Where(i => i.Size > 0).GroupBy(i => i.Size).Where(g => g.Count() > 1))
            {
                cancel.ThrowIfCancellationRequested();
                var byHash = new Dictionary<string, MediaItem>();
                foreach (var i in same)
                {
                    string h;
                    try { h = hash(i); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                    if (string.IsNullOrEmpty(h)) continue;
                    hashes[i] = h;
                    if (byHash.TryGetValue(h, out var first)) Join(first, i);
                    else byHash[h] = i;
                }
            }

            foreach (var (a, b) in exactJoins)
            {
                var ra = Root(a);
                var rb = Root(b);
                if (ra != rb) parent[ra] = rb;
            }

            // The same picture: fingerprints that differ in few bits. Splitting each into 8 bytes, two within 4 bits share at
            // least one byte in the same place, so only photos sharing a byte are compared.
            var photos = items.Where(i => i.Kind == MediaKind.Photo && i.Look != 0 && Usable(i.Look)).ToList();
            var buckets = new Dictionary<(int, byte), List<MediaItem>>();
            foreach (var p in photos)
                for (var band = 0; band < 8; band++)
                {
                    var key = (band, (byte)(p.Look >> (band * 8)));
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<MediaItem>();
                    list.Add(p);
                }
            // Every pair that looks alike, closest first (and, between equals, the biggest photos first, so a small copy
            // goes with the best match). A pair is only joined when everyone in both groups fits together: a copy
            // without a date can't link two photos taken on different days.
            var pairs = new List<(MediaItem A, MediaItem B, int Distance)>();
            var seenPairs = new HashSet<(MediaItem, MediaItem)>();
            foreach (var list in buckets.Values)
            {
                if (list.Count < 2 || list.Count > 2000) continue; // a huge bucket is a flat pattern, not copies
                cancel.ThrowIfCancellationRequested();
                for (var x = 0; x < list.Count; x++)
                    for (var y = x + 1; y < list.Count; y++)
                    {
                        var (p1, p2) = string.CompareOrdinal(list[x].Path, list[y].Path) < 0 ? (list[x], list[y]) : (list[y], list[x]);
                        if (!seenPairs.Add((p1, p2)) || !LookAlike(p1, p2)) continue;
                        pairs.Add((p1, p2, ImageOps.Distance(p1.Look, p2.Look)));
                    }
            }
            var members = items.GroupBy(Root).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var (pa, pb, _) in pairs.OrderBy(x => x.Distance).ThenByDescending(x => Math.Max(x.A.Size, x.B.Size)).ThenBy(x => x.A.Path, StringComparer.Ordinal))
            {
                var ra = Root(pa);
                var rb = Root(pb);
                if (ra == rb) continue;
                var ga = members[ra];
                var gb = members[rb];
                if (!ga.All(m => gb.All(n => Fits(m, n, hashes)))) continue;
                parent[ra] = rb;
                gb.AddRange(ga);
            }

            var groups = items.GroupBy(Root).Where(g => g.Count() > 1).Select(g =>
            {
                var members = g.ToList();
                var exact = members.All(m => hashes.TryGetValue(m, out var h) && h == hashes[members[0]]);
                return new DuplicateGroup { Items = members.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList(), Exact = exact, Keep = Best(members) };
            }).ToList();
            return groups.OrderByDescending(g => g.Saving).ToList();
        }

        /// <summary>Fingerprints of nearly flat pictures (all black, all white) say little: they aren't compared.</summary>
        private static bool Usable(ulong look)
        {
            var n = ImageOps.Distance(look, 0);
            return n >= 6 && n <= 58;
        }

        /// <summary>Two items can be in the same group: exact copies, or photos that look alike (and fit by shape and date).</summary>
        private static bool Fits(MediaItem a, MediaItem b, Dictionary<MediaItem, string> hashes) =>
            (hashes.TryGetValue(a, out var ha) && hashes.TryGetValue(b, out var hb) && ha == hb) ||
            (a.Kind == MediaKind.Photo && b.Kind == MediaKind.Photo && a.Look != 0 && b.Look != 0 && LookAlike(a, b));

        public static bool LookAlike(MediaItem a, MediaItem b)
        {
            if (ImageOps.Distance(a.Look, b.Look) > MaxDistance) return false;
            // A crop isn't the same photo: the shape must match (within 3%).
            if (a.ShownWidth > 0 && a.ShownHeight > 0 && b.ShownWidth > 0 && b.ShownHeight > 0)
            {
                var ra = (double)a.ShownWidth / a.ShownHeight;
                var rb = (double)b.ShownWidth / b.ShownHeight;
                if (Math.Abs(ra - rb) / Math.Max(ra, rb) > 0.03) return false;
            }
            // Two photos taken on different days are different photos, however alike (a burst of the same view is kept).
            if (a.Taken.HasValue && b.Taken.HasValue && Math.Abs((a.Taken.Value - b.Taken.Value).TotalSeconds) > 2) return false;
            return true;
        }

        private static readonly Regex CopyName = new Regex(@"(\(\d+\)|\bcopy\b|\bkopie\b|-\d+$|~\d+$|\bwhatsapp\b|\bedited\b)", RegexOptions.IgnoreCase);

        /// <summary>
        /// The copy to keep: the most pixels, then the biggest file, then the one whose name doesn't look like a copy,
        /// then the oldest file, then the shortest path.
        /// </summary>
        public static MediaItem Best(IList<MediaItem> items) =>
            items.OrderByDescending(i => (long)i.Width * i.Height)
                 .ThenByDescending(i => i.Size)
                 .ThenBy(i => CopyName.IsMatch(Path.GetFileNameWithoutExtension(i.Name)) ? 1 : 0)
                 .ThenBy(i => i.Modified)
                 .ThenBy(i => i.Path.Length)
                 .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                 .First();
    }

    /// <summary>Names for files KUDUPIX makes: it never replaces a file that's there.</summary>
    public static class Names
    {
        /// <summary>The path itself if nothing's there, else "name (2).ext", "name (3).ext"...</summary>
        public static string Unique(string path, Func<string, bool> exists = null)
        {
            exists = exists ?? (p => File.Exists(p) || Directory.Exists(p));
            if (!exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!exists(p)) return p;
            }
        }

        /// <summary>
        /// Where "Save a copy" puts a fixed photo: next to it, "Beach (edited).jpg", then "Beach (edited 2).jpg"...
        /// Photos Windows can't write (HEIC, WebP) become JPEG.
        /// </summary>
        public static string EditedCopy(string path, Func<string, bool> exists = null)
        {
            exists = exists ?? File.Exists;
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = MediaTypes.CanWrite(path) ? Path.GetExtension(path) : ".jpg";
            var first = Path.Combine(dir, name + " (edited)" + ext);
            if (!exists(first)) return first;
            for (var i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{name} (edited {i}){ext}");
                if (!exists(p)) return p;
            }
        }

        /// <summary>A name safe to use as a file name.</summary>
        public static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToArray();
            var s = new string((name ?? string.Empty).Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return s.Length == 0 ? "photo" : s;
        }
    }

    /// <summary>One original kept safe before "Save over the original".</summary>
    [System.Runtime.Serialization.DataContract]
    public sealed class KeptOriginal
    {
        [System.Runtime.Serialization.DataMember] public string Path { get; set; } = string.Empty;
        [System.Runtime.Serialization.DataMember] public string Copy { get; set; } = string.Empty;
        [System.Runtime.Serialization.DataMember] public DateTime Saved { get; set; }
        [System.Runtime.Serialization.DataMember] public long Size { get; set; }
    }

    /// <summary>
    /// The untouched photos kept before "Save over the original", so "Put the original back" can undo it. Kept in
    /// KUDUPIX's own folder (Originals), one folder per save, with a note of where each one came from.
    /// </summary>
    public sealed class Originals
    {
        public string Folder { get; }

        public Originals(string folder)
        {
            Folder = folder;
        }

        /// <summary>Copies the photo away before it's replaced; returns the record.</summary>
        public KeptOriginal Keep(string path)
        {
            var info = new FileInfo(path);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var dir = Names.Unique(System.IO.Path.Combine(Folder, stamp));
            Directory.CreateDirectory(dir);
            var copy = System.IO.Path.Combine(dir, info.Name);
            File.Copy(path, copy, false);
            File.SetLastWriteTimeUtc(copy, info.LastWriteTimeUtc);
            File.SetCreationTimeUtc(copy, info.CreationTimeUtc);
            var kept = new KeptOriginal { Path = info.FullName, Copy = copy, Saved = DateTime.Now, Size = info.Length };
            using (var fs = new FileStream(System.IO.Path.Combine(dir, "original.json"), FileMode.Create))
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(KeptOriginal)).WriteObject(fs, kept);
            return kept;
        }

        /// <summary>Every original kept, newest first.</summary>
        public List<KeptOriginal> All()
        {
            var list = new List<KeptOriginal>();
            if (!Directory.Exists(Folder)) return list;
            foreach (var dir in Directory.GetDirectories(Folder))
            {
                var note = System.IO.Path.Combine(dir, "original.json");
                try
                {
                    if (!File.Exists(note)) continue;
                    using (var fs = File.OpenRead(note))
                    {
                        var k = (KeptOriginal)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(KeptOriginal)).ReadObject(fs);
                        if (File.Exists(k.Copy)) list.Add(k);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is System.Runtime.Serialization.SerializationException || ex is UnauthorizedAccessException)
                {
                    // A damaged note: that original can still be found in the folder by hand.
                }
            }
            return list.OrderByDescending(k => k.Saved).ToList();
        }

        /// <summary>The originals kept for a photo, newest first.</summary>
        public List<KeptOriginal> For(string path) => All().Where(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>Puts the kept original back in place of the saved photo, and forgets it.</summary>
        public void PutBack(KeptOriginal kept)
        {
            File.Copy(kept.Copy, kept.Path, true);
            File.SetLastWriteTimeUtc(kept.Path, File.GetLastWriteTimeUtc(kept.Copy));
            Directory.Delete(System.IO.Path.GetDirectoryName(kept.Copy) ?? kept.Copy, true);
        }

        public long SizeOnDisk() => All().Sum(k => k.Size);
    }
}
