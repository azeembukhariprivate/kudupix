using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Kudupix.Core;

namespace Kudupix.Tests
{
    public static partial class Program
    {
        private static int _failed;
        private static int _passed;
        private static string _root;

        public static int Main(string[] args)
        {
            _root = Path.Combine(Path.GetTempPath(), "kudupix-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            try
            {
                var tests = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test"));
                if (args.Length > 0) tests = tests.Where(m => args.Any(a => m.Name.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0));
                foreach (var m in tests)
                {
                    try
                    {
                        m.Invoke(null, null);
                        _passed++;
                        Console.WriteLine("PASS " + m.Name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        _failed++;
                        Console.WriteLine("FAIL " + m.Name + ": " + ex.InnerException);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { /* ignore */ }
            }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("Check failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected [{expected}], got [{actual}]");
        }

        private static void Near(double expected, double actual, double within, string what)
        {
            if (Math.Abs(expected - actual) > within) throw new Exception($"{what}: expected about {expected}, got {actual}");
        }

        private static string Folder(string name)
        {
            var dir = Path.GetFullPath(Path.Combine(_root, name));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ================================================================== reading photos

        private static void TestCanonPhoto()
        {
            var d = PhotoInfo.Read(Samples.Canon);
            Equal(new DateTime(2008, 5, 30, 15, 56, 1), d.Taken, "taken");
            Equal("Canon EOS 40D", d.Camera, "camera (the maker isn't said twice)");
            Equal(100, d.Width, "width");
            Equal(68, d.Height, "height");
            Equal(1, d.Orientation, "upright");
            Check(d.Exposure.Contains("f/") && d.Exposure.Contains("ISO"), "exposure: " + d.Exposure);
            Check(!d.Latitude.HasValue, "no place");
        }

        private static void TestPhotoWithPlace()
        {
            var d = PhotoInfo.Read(Samples.Gps);
            Equal(new DateTime(2008, 10, 22, 16, 28, 39), d.Taken, "taken");
            Equal("NIKON COOLPIX P6000", d.Camera, "camera");
            Equal(640, d.Width, "width");
            Equal(480, d.Height, "height");
            Check(d.Latitude.HasValue && d.Longitude.HasValue, "a place");
            Near(43 + 28 / 60.0 + 2.814 / 3600, d.Latitude.Value, 0.00001, "latitude");
            Near(11 + 53 / 60.0 + 6.456 / 3600, d.Longitude.Value, 0.00001, "longitude");
        }

        private static void TestOrientation()
        {
            var turned = PhotoInfo.Read(Samples.Portrait6);
            Equal(6, turned.Orientation, "turned right");
            Check(turned.Turned, "counts as turned");
            var item = new MediaItem { Width = turned.Width, Height = turned.Height, Orientation = turned.Orientation };
            Equal(450, item.ShownWidth, "shown width");
            Equal(600, item.ShownHeight, "shown height (upright)");
            var upside = PhotoInfo.Read(Samples.Landscape3);
            Equal(3, upside.Orientation, "upside down");
            Equal(600, new MediaItem { Width = upside.Width, Height = upside.Height, Orientation = 3 }.ShownWidth, "not turned sideways");
        }

        private static void TestOtherPictureFiles()
        {
            var tiff = PhotoInfo.Read(Samples.Tiff);
            Equal(174, tiff.Width, "TIFF width");
            Equal(38, tiff.Height, "TIFF height");

            // Picture headers made here: PNG, GIF, BMP and the three kinds of WebP.
            var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x0F, 0xA0, 0, 0, 0x0B, 0xB8, 8, 6, 0, 0, 0 };
            var p = PhotoInfo.Read(new MemoryStream(png));
            Equal(4000, p.Width, "PNG width");
            Equal(3000, p.Height, "PNG height");
            var gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 0x40, 0x01, 0xF0, 0x00, 0, 0, 0, 0, 0, 0 }).ToArray();
            p = PhotoInfo.Read(new MemoryStream(gif));
            Equal(320, p.Width, "GIF width");
            Equal(240, p.Height, "GIF height");
            var bmp = new byte[30];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(800).CopyTo(bmp, 18);
            BitConverter.GetBytes(-600).CopyTo(bmp, 22); // stored top to bottom
            p = PhotoInfo.Read(new MemoryStream(bmp));
            Equal(800, p.Width, "BMP width");
            Equal(600, p.Height, "BMP height");
            var webp = new byte[32];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(webp, 0);
            Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(webp, 8);
            webp[24] = 0x7F; webp[25] = 0x07; // 1920 - 1
            webp[27] = 0x37; webp[28] = 0x04; // 1080 - 1
            p = PhotoInfo.Read(new MemoryStream(webp));
            Equal(1920, p.Width, "WebP width");
            Equal(1080, p.Height, "WebP height");

            // A file cut short, or not a picture at all: nothing found, nothing breaks.
            var cut = File.ReadAllBytes(Samples.Gps).Take(300).ToArray();
            p = PhotoInfo.Read(new MemoryStream(cut));
            Check(p.Width == 0 || p.Width == 640, "a cut-off file is read as far as it goes");
            p = PhotoInfo.Read(new MemoryStream(Encoding.ASCII.GetBytes("this is not a picture at all")));
            Equal(0, p.Width, "not a picture");

            Equal(new DateTime(2024, 3, 5, 9, 30, 0), PhotoInfo.ParseDate("2024:03:05 09:30:00"), "EXIF date");
            Equal(new DateTime(2024, 3, 5, 9, 30, 0), PhotoInfo.ParseDate("2024-03-05T09:30:00"), "dashes");
            Check(PhotoInfo.ParseDate("0000:00:00 00:00:00") == null, "an empty date");
            Check(PhotoInfo.ParseDate("    :  :     :  :  ") == null, "blanks");
        }

        // ================================================================== reading videos

        private static void TestRealVideo()
        {
            var v = VideoInfo.Read(Samples.Movie);
            Near(4.97, v.Seconds, 0.01, "length");
            Equal(320, v.Width, "width");
            Equal(240, v.Height, "height");
            Check(v.Taken.HasValue, "filmed date");
            Equal(new DateTime(2005, 8, 11, 14, 3, 54, DateTimeKind.Utc), v.Taken.Value.ToUniversalTime(), "filmed (UTC)");
            Equal("0:05", VideoLength.Text(v.Seconds), "length in words");
        }

        /// <summary>A phone-style MP4: a 64-bit header, a video filmed upright (turned picture) and the place.</summary>
        private static byte[] PhoneVideo()
        {
            byte[] Box(string type, params byte[][] body)
            {
                var content = body.SelectMany(b => b).ToArray();
                var b2 = new byte[8 + content.Length];
                var size = b2.Length;
                b2[0] = (byte)(size >> 24); b2[1] = (byte)(size >> 16); b2[2] = (byte)(size >> 8); b2[3] = (byte)size;
                Encoding.GetEncoding("ISO-8859-1").GetBytes(type).CopyTo(b2, 4);
                content.CopyTo(b2, 8);
                return b2;
            }
            byte[] U32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            byte[] U64(ulong v) => U32((long)(v >> 32)).Concat(U32((long)(v & 0xFFFFFFFF))).ToArray();
            var created = (ulong)(new DateTime(2026, 11, 2, 11, 15, 12, DateTimeKind.Utc) - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var mvhd = Box("mvhd", new byte[] { 1, 0, 0, 0 }, U64(created), U64(created), U32(1000), U64(48250), new byte[80]);
            var matrix = U32(0).Concat(U32(0x10000)).Concat(U32(0)).Concat(U32(-0x10000 & 0xFFFFFFFF)).Concat(U32(0)).Concat(U32(0)).Concat(U32(0)).Concat(U32(0)).Concat(U32(0x40000000)).ToArray();
            var tkhd = Box("tkhd", new byte[] { 0, 0, 0, 7 }, U32(0), U32(0), U32(1), U32(0), U32(48250), new byte[8], new byte[8], matrix, U32(1920L << 16), U32(1080L << 16));
            var sound = Box("tkhd", new byte[] { 0, 0, 0, 7 }, U32(0), U32(0), U32(2), U32(0), U32(48250), new byte[8], new byte[8], new byte[36], U32(0), U32(0));
            var place = Box("©xyz", new byte[] { 0, 18, 0x15, 0xC7 }, Encoding.ASCII.GetBytes("+09.4981+076.3388/"));
            var moov = Box("moov", mvhd, Box("trak", sound), Box("trak", tkhd), Box("udta", place));
            return Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0)).Concat(Box("mdat", new byte[100])).Concat(moov).ToArray();
        }

        private static void TestPhoneVideo()
        {
            var v = VideoInfo.Read(new MemoryStream(PhoneVideo()));
            Near(48.25, v.Seconds, 0.001, "length");
            Equal(1080, v.Width, "filmed upright: as wide as the picture is high");
            Equal(1920, v.Height, "and as high as it is wide");
            Equal(new DateTime(2026, 11, 2, 11, 15, 12, DateTimeKind.Utc), v.Taken.Value.ToUniversalTime(), "filmed");
            Near(9.4981, v.Latitude.Value, 0.00001, "latitude");
            Near(76.3388, v.Longitude.Value, 0.00001, "longitude");
            Equal("0:48", VideoLength.Text(v.Seconds), "length in words");
            Equal("1:02:33", VideoLength.Text(3753), "an hour");

            // Cut off half way: what was read stays, nothing breaks.
            var whole = PhoneVideo();
            var cut = VideoInfo.Read(new MemoryStream(whole.Take(whole.Length - 30).ToArray()));
            Check(cut.Seconds == 48.25 || cut.Seconds == 0, "cut off");
            var d = new VideoDetails();
            VideoInfo.Place("+00.0000+000.0000/", d);
            Check(!d.Latitude.HasValue, "0,0 isn't a place");
            VideoInfo.Place("-33.8688+151.2093+058.000/", d);
            Near(-33.8688, d.Latitude.Value, 0.00001, "south");
        }

        // ================================================================== the library

        private static string Put(string folder, string name, string sample)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.Copy(sample, path, true);
            return path;
        }

        private static void TestLibraryScan()
        {
            var photos = Folder("lib/Pictures");
            var videos = Folder("lib/Videos");
            Put(photos, "canon.jpg", Samples.Canon);
            var gps = Put(Path.Combine(photos, "Holiday"), "DSCN0010.JPG", Samples.Gps);
            Put(photos, "portrait.jpg", Samples.Portrait6);
            Put(videos, "clip.mov", Samples.Movie);
            File.WriteAllBytes(Path.Combine(videos, "phone.mp4"), PhoneVideo());
            File.WriteAllText(Path.Combine(photos, "notes.txt"), "not a photo");
            var hidden = Folder("lib/Pictures/.thumbnails");
            Put(hidden, "cache.jpg", Samples.Canon);
            var own = Folder("lib/Pictures/KUDUPIX-data");
            Put(own, "kept.jpg", Samples.Canon);

            var lib = new Library(Path.Combine(_root, "lib-data", "library.bin"));
            var r = lib.Scan(new[] { photos, videos, Path.Combine(photos, "Holiday") }, own);
            Equal(5, r.Added, "photos and videos found (not the text file, the hidden folder or KUDUPIX's own)");
            Equal(5, lib.Count, "in the library");
            var g = lib.Find(gps);
            Check(g != null && g.HasPlace && g.Camera == "NIKON COOLPIX P6000" && g.Kind == MediaKind.Photo, "details read");
            var clip = lib.All().First(i => i.Name == "clip.mov");
            Check(clip.IsVideo && clip.Seconds > 4.9 && clip.Width == 320, "video details read");
            Check(lib.All().First(i => i.Name == "phone.mp4").HasPlace, "the phone video's place");

            // Nothing changed: nothing read again.
            r = lib.Scan(new[] { photos, videos }, own);
            Check(!r.AnyChange && r.Unchanged == 5, "second scan: nothing new");

            // Kept and loaded again.
            lib.Save();
            var again = new Library(lib.FilePath);
            Check(again.Load(), "loaded");
            Equal(5, again.Count, "the same items");
            var g2 = again.Find(gps);
            Check(g2.Taken == g.Taken && g2.Latitude == g.Latitude && g2.Camera == g.Camera && g2.Width == g.Width, "the same details");

            // Changed, moved, new and gone.
            var canon = Path.Combine(photos, "canon.jpg");
            File.AppendAllText(canon, "x");
            var moved = Path.Combine(photos, "Holiday", "Italy", "DSCN0010.JPG");
            Directory.CreateDirectory(Path.GetDirectoryName(moved));
            File.Move(gps, moved);
            File.Delete(Path.Combine(videos, "phone.mp4"));
            Put(photos, "new.jpg", Samples.Landscape3);
            r = again.Scan(new[] { photos, videos }, own);
            Equal(1, r.Changed, "changed");
            Equal(1, r.Added, "added");
            Equal(1, r.Removed, "removed");
            Equal(1, r.Moved.Count, "moved");
            Equal(moved, r.Moved[0].To, "moved to");
            Check(again.Find(moved) != null && again.Find(gps) == null, "the library follows the move");

            // A folder taken off the list: its items go.
            r = again.Scan(new[] { photos }, own);
            Check(again.All().All(i => !i.IsVideo), "videos folder no longer looked in");

            // A damaged library file: a fresh start, no crash.
            File.WriteAllText(lib.FilePath, "damaged");
            Check(!new Library(lib.FilePath).Load(), "damaged file isn't loaded");
        }

        private static void TestFolderList()
        {
            var a = Path.Combine(_root, "roots", "Pictures");
            var b = Path.Combine(a, "Camera Roll");
            var c = Path.Combine(_root, "roots", "Videos");
            var clean = Roots.Clean(new[] { b, a, c, a + Path.DirectorySeparatorChar, "", null });
            Equal(2, clean.Count, "a folder inside another counts once");
            Check(clean.Contains(a) && clean.Contains(c), "the outer folders stay");
            Equal(a, Roots.RootOf(Path.Combine(b, "x.jpg"), clean), "which folder a photo is under");
            Check(Library.Inside(Path.Combine(a, "x.jpg"), a), "inside");
            Check(!Library.Inside(a + "2" + Path.DirectorySeparatorChar + "x.jpg", a), "a folder with a longer name isn't inside");
            Check(MediaTypes.KindOf("A.HEIC") == MediaKind.Photo && MediaTypes.KindOf("b.MOV") == MediaKind.Video && MediaTypes.KindOf("c.txt") == null, "kinds of file");
            Check(MediaTypes.CanWrite("a.jpg") && !MediaTypes.CanWrite("a.heic"), "files that can be written back");
        }

        // ================================================================== stars, tags, albums, search

        private static void TestUserData()
        {
            var file = Path.Combine(Folder("user"), "userdata.json");
            var data = new UserData(file);
            var changes = 0;
            data.Changed += () => changes++;
            var a = Path.Combine(_root, "a.jpg");
            var b = Path.Combine(_root, "b.jpg");
            data.SetStars(new[] { a }, 4);
            data.SetStars(new[] { b }, 9);
            Equal(4, data.Stars(a), "stars");
            Equal(5, data.Stars(b), "at most 5");
            data.AddTag(new[] { a, b }, "  Beach ");
            data.AddTag(new[] { a }, "beach");
            data.AddTag(new[] { a }, "Kerala");
            Equal(2, data.Tags(a).Count, "\"beach\" and \"Beach\" are one tag");
            Equal("Beach", data.Tags(a)[0], "the first spelling is kept");
            Equal(("Beach", 2), data.AllTags()[0], "most used first");
            Check(data.PathsWithTag("BEACH").SetEquals(new[] { a, b }), "photos with a tag");

            var album = data.AddAlbum("Kerala trip");
            var second = data.AddAlbum("kerala trip");
            Equal("kerala trip (2)", second.Name, "album names don't repeat");
            data.AddToAlbum(album, new[] { a, b, a });
            Equal(2, album.Paths.Count, "each photo once");
            Equal(1, data.AlbumsOf(a).Count, "albums of a photo");
            data.RenameAlbum(second, "Best of 2026");
            Equal("Best of 2026", data.FindAlbum(second.Id).Name, "renamed");

            var edit = new EditRecipe { Brightness = 12 };
            data.SetEdit(a, edit);
            edit.Brightness = 50;
            Equal(12, data.Edit(a).Brightness, "fixes are kept as a copy");

            var moved = Path.Combine(_root, "moved", "a.jpg");
            data.Move(a, moved);
            Equal(4, data.Stars(moved), "stars follow a move");
            Check(album.Paths.Contains(moved) && !album.Paths.Contains(a), "albums follow a move");
            Equal(12, data.Edit(moved).Brightness, "fixes follow a move");

            data.Save();
            data.Save(); // the second save keeps the first as a spare copy
            var loaded = new UserData(file);
            Check(loaded.Load(), "loaded");
            Equal(4, loaded.Stars(moved), "stars kept");
            Equal(2, loaded.Tags(moved).Count, "tags kept");
            Equal(2, loaded.Albums().Count, "albums kept");
            Equal(12, loaded.Edit(moved).Brightness, "fixes kept");

            // A damaged file: the spare copy is used.
            File.WriteAllText(file, "{ damaged");
            var spare = new UserData(file);
            Check(spare.Load() && spare.Stars(moved) == 4, "the spare copy is read when the file is damaged");

            data.Forget(new[] { b });
            Check(data.Stars(b) == 0 && !album.Paths.Contains(b), "a deleted photo is forgotten everywhere");
            data.RemoveTag(new[] { moved }, "kerala");
            Equal(1, data.Tags(moved).Count, "tag removed");
            data.RemoveAlbum(album);
            Equal(1, data.Albums().Count, "album removed");
            Check(changes > 10, "changes are announced");
        }

        private static void TestSearch()
        {
            var item = new MediaItem { Path = Path.Combine(_root, "Pictures", "Kerala", "IMG_4471.jpg"), Kind = MediaKind.Photo, Taken = new DateTime(2025, 10, 25, 9, 0, 0), Camera = "Pixel 9", Latitude = 9.5, Longitude = 76.3 };
            var tags = new List<string> { "Beach", "family" };
            var albums = new List<string> { "Kerala trip 2025" };
            bool M(string q) => Search.Matches(item, Search.Words(q), tags, albums);
            Check(M(""), "nothing typed");
            Check(M("kerala"), "folder");
            Check(M("4471"), "part of the name");
            Check(M("beach fam"), "tags, and the start of a word");
            Check(M("trip"), "album");
            Check(M("pixel 2025 october"), "camera, year, month");
            Check(M("photos"), "kind");
            Check(M("place"), "has a place");
            Check(!M("beach zzz"), "every word must match");
            Check(!M("video"), "not a video");
        }

        private static void TestTimeline()
        {
            MediaItem At(string name, DateTime when, string folder = "Camera Roll") =>
                new MediaItem { Path = Path.Combine(_root, folder, name), Taken = when };
            var items = new[]
            {
                At("a.jpg", new DateTime(2026, 11, 2, 16, 45, 0)),
                At("b.jpg", new DateTime(2026, 11, 2, 9, 0, 0)),
                At("c.jpg", new DateTime(2026, 10, 25, 12, 0, 0), "Kochi"),
                At("d.jpg", new DateTime(2025, 1, 1, 0, 0, 0)),
                new MediaItem { Path = Path.Combine(_root, "x", "e.jpg"), Modified = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks }
            };
            var days = Timeline.ByDay(items);
            Equal(3, days.Count, "three days");
            Equal(new DateTime(2026, 11, 2), days[0].Start, "newest day first");
            Equal(3, days[0].Items.Count, "a photo without a date taken goes by the file's date");
            Equal("a.jpg", days[0].Items[0].Name, "newest first within a day");
            Equal("Camera Roll · 3 items", days[0].Note, "where most of them are, and how many");
            var oldest = Timeline.ByDay(items, false);
            Equal(new DateTime(2025, 1, 1), oldest[0].Start, "oldest first");
            var months = Timeline.ByMonth(items);
            Equal(3, months.Count, "three months");
            Equal(4, months[0].Items.Count + months[1].Items.Count, "November and October 2026");
        }
    }
}
