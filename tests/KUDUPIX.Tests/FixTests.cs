using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kudupix.Core;

namespace Kudupix.Tests
{
    public static partial class Program
    {
        /// <summary>A test picture: a soft gradient with a bright square and a dark bar, different everywhere.</summary>
        private static Pixels Scene(int w, int h, int seed = 0)
        {
            var p = new Pixels(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var r = 40 + 150 * x / w;
                    var g = 60 + 120 * y / h;
                    var b = 90 + (seed * 37 % 80);
                    if (x > w / 4 && x < w / 2 && y > h / 4 && y < h / 2) { r = 230; g = 220; b = 200; }
                    if (y > 3 * h / 4 && y < 7 * h / 8) { r = 20; g = 25; b = 30; }
                    if (seed > 0 && x > (seed * 13) % w && x < (seed * 13) % w + w / 6) { r = 250 - r; g = 30; }
                    var block = (x * 8 / w) * 7 + (y * 6 / h) * 13 + seed * 29;
                    var shade = (block * 2654435761u >> 24) % 70;
                    r = Math.Min(255, r + (int)shade);
                    g = Math.Min(255, g + (int)shade);
                    p.Set(x, y, (byte)r, (byte)g, (byte)b);
                }
            return p;
        }

        private static double Mean(Pixels p, int channel)
        {
            double sum = 0;
            for (var i = channel; i < p.Bgra.Length; i += 4) sum += p.Bgra[i];
            return sum / (p.Width * p.Height);
        }

        private static bool Same(Pixels a, Pixels b) => a.Width == b.Width && a.Height == b.Height && a.Bgra.SequenceEqual(b.Bgra);

        // ================================================================== turning, cropping, straightening

        private static void TestTurning()
        {
            var p = Scene(6, 4);
            var right = ImageOps.Turn(p, 1);
            Equal(4, right.Width, "turned: as wide as it was high");
            Equal(6, right.Height, "and as high as it was wide");
            Equal(p.At(0, 3), right.At(0, 0), "the bottom-left corner goes to the top left");
            Equal(p.At(0, 0), right.At(3, 0), "the top-left corner goes to the top right");
            Check(Same(p, ImageOps.Turn(ImageOps.Turn(ImageOps.Turn(ImageOps.Turn(p, 1), 1), 1), 1)), "four turns: back as it was");
            Check(Same(ImageOps.Turn(p, 3), ImageOps.Turn(ImageOps.Turn(ImageOps.Turn(p, 1), 1), 1)), "a turn left is three to the right");
            Check(ReferenceEquals(p, ImageOps.Turn(p, 0)), "no turn: the same picture");
        }

        private static void TestCropAndStraighten()
        {
            var p = Scene(100, 50);
            var c = ImageOps.Crop(p, 0.25, 0.2, 0.75, 1);
            Equal(50, c.Width, "crop width");
            Equal(40, c.Height, "crop height");
            Equal(p.At(25, 10), c.At(0, 0), "crop corner");

            // Straightening zooms in just enough: every pixel comes from inside the picture.
            foreach (var deg in new[] { -10.0, -3.5, 2, 10 })
                foreach (var (x, y) in new[] { (0.0, 0.0), (100.0, 0.0), (0.0, 50.0), (100.0, 50.0) })
                {
                    var (sx, sy) = ImageOps.StraightenSource(x, y, 100, 50, deg);
                    Check(sx >= -0.01 && sx <= 100.01 && sy >= -0.01 && sy <= 50.01, $"corner {x},{y} at {deg}° comes from inside ({sx:0.00},{sy:0.00})");
                }
            Near(1, ImageOps.StraightenZoom(100, 50, 0), 1e-9, "no zoom without straightening");
            var s = ImageOps.Straighten(p, 4);
            Equal(100, s.Width, "same size");
            var (cx, cy) = ImageOps.StraightenSource(50, 25, 100, 50, 7);
            Near(50, cx, 1e-9, "the middle stays in the middle (x)");
            Near(25, cy, 1e-9, "the middle stays in the middle (y)");
        }

        private static void TestRecipe()
        {
            var p = Scene(60, 40);
            var e = new EditRecipe();
            Check(e.IsEmpty, "a new recipe does nothing");
            Check(Same(p, e.Apply(p)), "nothing applied");

            // Turning the recipe turns the result, crop and all.
            e.CropLeft = 0.1; e.CropTop = 0.2; e.CropRight = 0.7; e.CropBottom = 0.9;
            e.Brightness = 10;
            var before = e.Apply(p);
            var turned = e.Clone();
            turned.Turn(true);
            Check(Same(ImageOps.Turn(before, 1), turned.Apply(p)), "turning right turns the fixed photo");
            var back = turned.Clone();
            back.Turn(false);
            Check(Same(before, back.Apply(p)), "turning back undoes it");
            Equal(1, turned.Turns, "one turn");
            Check(!turned.IsEmpty, "has fixes");

            // A point on the fixed photo maps back to the turned photo (for red-eye spots).
            var map = new EditRecipe { CropLeft = 0.5, CropTop = 0, CropRight = 1, CropBottom = 0.5 };
            var (mx, my) = map.ToTurned(0.5, 0.5, 60, 40);
            Near(0.75, mx, 1e-9, "crop maps back (x)");
            Near(0.25, my, 1e-9, "crop maps back (y)");
            map = new EditRecipe { Straighten = 6 };
            (mx, my) = map.ToTurned(0.5, 0.5, 60, 40);
            Near(0.5, mx, 1e-9, "the middle stays (x)");
            Near(0.5, my, 1e-9, "the middle stays (y)");

            // The original pixels are never changed.
            var copy = p.Clone();
            new EditRecipe { AutoFix = true, Brightness = 40, Contrast = 30, Warmth = 50, Colour = -50, Straighten = 3, RedEye = new List<Spot> { new Spot { X = 0.5, Y = 0.5, R = 0.2 } } }.Apply(p);
            Check(Same(copy, p), "the original is left as it was");
        }

        // ================================================================== colours

        private static void TestColours()
        {
            var p = Scene(40, 30);
            Pixels With(EditRecipe e) => e.Apply(p);
            Check(Mean(With(new EditRecipe { Brightness = 50 }), 1) > Mean(p, 1) + 20, "brighter");
            Check(Mean(With(new EditRecipe { Brightness = -50 }), 1) < Mean(p, 1) - 20, "darker");
            var warm = With(new EditRecipe { Warmth = 60 });
            Check(Mean(warm, 2) > Mean(p, 2) + 10 && Mean(warm, 0) < Mean(p, 0) - 10, "warmer: more red, less blue");
            var grey = With(new EditRecipe { Colour = -100 });
            for (var i = 0; i < grey.Bgra.Length; i += 4)
                Check(Math.Abs(grey.Bgra[i] - grey.Bgra[i + 2]) <= 1 && Math.Abs(grey.Bgra[i + 1] - grey.Bgra[i + 2]) <= 1, "no colour: grey");

            double Spread(Pixels x) { var g = Enumerable.Range(0, x.Width * x.Height).Select(i => (int)x.Bgra[i * 4 + 1]).ToList(); return g.Max() - g.Min(); }
            Check(Spread(With(new EditRecipe { Contrast = 50 })) > Spread(p), "more contrast");

            // A dull, grey-blue photo: auto-fix stretches it to full brightness range and takes out the colour cast.
            var dull = new Pixels(50, 50);
            for (var y = 0; y < 50; y++)
                for (var x = 0; x < 50; x++)
                    dull.Set(x, y, (byte)(70 + 2 * x), (byte)(75 + 2 * x), (byte)(100 + 2 * x));
            var fixedPhoto = With2(dull, new EditRecipe { AutoFix = true });
            var greens = Enumerable.Range(0, 2500).Select(i => fixedPhoto.Bgra[i * 4 + 1]).ToList();
            Check(greens.Min() < 15 && greens.Max() > 240, $"stretched ({greens.Min()}..{greens.Max()})");
            Check(Math.Abs(Mean(fixedPhoto, 0) - Mean(fixedPhoto, 2)) < 6, "blue cast taken out");

            // A dark night photo isn't made into day.
            var night = new Pixels(20, 20);
            for (var i = 0; i < 400; i++) night.Set(i % 20, i / 20, (byte)(i % 30), (byte)(i % 30), (byte)(i % 35));
            Check(Mean(With2(night, new EditRecipe { AutoFix = true }), 1) < 140, "a night photo stays dark");
        }

        private static Pixels With2(Pixels p, EditRecipe e) => e.Apply(p);

        private static void TestRedEye()
        {
            var p = new Pixels(40, 40);
            for (var y = 0; y < 40; y++)
                for (var x = 0; x < 40; x++)
                    p.Set(x, y, 200, 40, 30); // red everywhere
            var e = new EditRecipe { RedEye = new List<Spot> { new Spot { X = 0.25, Y = 0.25, R = 0.1 } } };
            var r = e.Apply(p);
            var (b, g, red) = r.At(10, 10);
            Check(red < 50, "red taken out in the spot");
            Check(r.At(30, 30).R == 200, "left alone outside the spot");
            Check(r.At(10, 18).R == 200, "the spot is round and small");
            // Skin (red but not much redder than green and blue) is left alone.
            var skin = new Pixels(10, 10);
            for (var i = 0; i < 100; i++) skin.Set(i % 10, i / 10, 220, 170, 140);
            Check(new EditRecipe { RedEye = new List<Spot> { new Spot { X = 0.5, Y = 0.5, R = 0.5 } } }.Apply(skin).At(5, 5).R == 220, "skin isn't touched");
        }

        private static void TestShrink()
        {
            var p = Scene(400, 300);
            var s = ImageOps.Shrink(p, 100);
            Equal(100, s.Width, "longest side");
            Equal(75, s.Height, "shape kept");
            Check(ReferenceEquals(p, ImageOps.Shrink(p, 1000)), "never made bigger");
            Near(Mean(p, 1), Mean(s, 1), 2, "about the same brightness");
        }

        // ================================================================== duplicates

        private static void TestLookAlikes()
        {
            var photo = Scene(800, 600, 3);
            var small = ImageOps.Shrink(photo, 320);
            var brighter = new EditRecipe { Brightness = 8 }.Apply(photo);
            var other = Scene(800, 600, 11);
            var a = ImageOps.Look(photo);
            Check(ImageOps.Distance(a, ImageOps.Look(small)) <= 2, "a smaller copy looks the same");
            Check(ImageOps.Distance(a, ImageOps.Look(brighter)) <= 2, "a slightly brighter copy looks the same");
            Check(ImageOps.Distance(a, ImageOps.Look(other)) > Duplicates.MaxDistance, "a different photo doesn't");
            Equal(0, ImageOps.Distance(a, a), "the same fingerprint");
            Check(ImageOps.Look(new Pixels(10, 10)) != 0, "a flat picture still gets a fingerprint (0 means not known)");
        }

        private static void TestFindDuplicates()
        {
            var dir = Folder("dups");
            var photo = Scene(800, 600, 3);
            var look = ImageOps.Look(photo);
            var lookSmall = ImageOps.Look(ImageOps.Shrink(photo, 400));
            MediaItem Make(string name, long size, int w, int h, ulong l, DateTime? taken = null, byte fill = 1)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, Enumerable.Repeat(fill, (int)size).ToArray());
                return new MediaItem { Path = path, Kind = name.EndsWith(".mp4") ? MediaKind.Video : MediaKind.Photo, Size = size, Width = w, Height = h, Look = l, Taken = taken, Modified = DateTime.UtcNow.Ticks };
            }
            var when = new DateTime(2026, 11, 2, 16, 45, 0);
            var original = Make("IMG_4471.jpg", 4000, 4032, 3024, look, when);
            var whatsapp = Make("WhatsApp Image 2026-11-02.jpg", 380, 1600, 1200, lookSmall);
            var exact1 = Make("clip.mp4", 5000, 1920, 1080, 0, null, 7);
            var exact2 = Make("clip (1).mp4", 5000, 1920, 1080, 0, null, 7);
            var sameSizeDifferent = Make("other.mp4", 5000, 1920, 1080, 0, null, 9);
            var cropped = Make("IMG_4471 crop.jpg", 2000, 3000, 3000, look, when); // square: a crop, not a copy
            var lookOtherDay = Make("IMG_9999.jpg", 3900, 4032, 3024, look, when.AddDays(30));
            var different = Make("different.jpg", 3000, 4032, 3024, ImageOps.Look(Scene(800, 600, 11)));

            var hashed = new List<string>();
            var groups = Duplicates.Find(new[] { original, whatsapp, exact1, exact2, sameSizeDifferent, cropped, lookOtherDay, different }, i =>
            {
                hashed.Add(i.Name);
                return Duplicates.FileHash(i.Path);
            });
            Equal(2, groups.Count, "two groups: the video copies, and the photo with its smaller copy");
            var video = groups.First(g => g.Items.Contains(exact1));
            Check(video.Exact && video.Items.Count == 2 && !video.Items.Contains(sameSizeDifferent), "exact copies (same size, different contents isn't one)");
            Equal(exact1, video.Keep, "keep the one whose name doesn't look like a copy");
            var pic = groups.First(g => g.Items.Contains(original));
            Check(!pic.Exact && pic.Items.Count == 2 && pic.Items.Contains(whatsapp), "the smaller copy found by its look");
            Equal(original, pic.Keep, "keep the biggest");
            Equal(380L, pic.Saving, "what removing the copy frees");
            Check(!groups.Any(g => g.Items.Contains(cropped)), "a crop is a different shape: not a copy");
            Check(!groups.Any(g => g.Items.Contains(lookOtherDay)), "taken on another day: not a copy");
            Check(!groups.Any(g => g.Items.Contains(different)), "a different photo");
            Check(!hashed.Contains("IMG_4471.jpg"), "contents are only read for files of the same size");
            Equal(groups[0].Saving >= groups[1].Saving, true, "biggest saving first");
        }

        // ================================================================== names, originals

        private static void TestNames()
        {
            var dir = Folder("names");
            var photo = Path.Combine(dir, "Beach.jpg");
            File.WriteAllText(photo, "x");
            Equal(Path.Combine(dir, "Beach (edited).jpg"), Names.EditedCopy(photo), "first copy");
            File.WriteAllText(Path.Combine(dir, "Beach (edited).jpg"), "x");
            Equal(Path.Combine(dir, "Beach (edited 2).jpg"), Names.EditedCopy(photo), "second copy");
            Equal(Path.Combine(dir, "Phone (edited).jpg"), Names.EditedCopy(Path.Combine(dir, "Phone.heic")), "HEIC copies are saved as JPEG");
            Equal(Path.Combine(dir, "Beach (2).jpg"), Names.Unique(photo), "never replaces a file");
            Equal(Path.Combine(dir, "New.jpg"), Names.Unique(Path.Combine(dir, "New.jpg")), "a free name stays");
            Equal("a_b_c", Names.Safe("a/b:c"), "safe names");
            Equal("photo", Names.Safe("..."), "an empty name");
        }

        private static void TestKeptOriginals()
        {
            var dir = Folder("originals");
            var photo = Path.Combine(dir, "Family.jpg");
            File.WriteAllText(photo, "the original");
            var when = new DateTime(2020, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(photo, when);
            var store = new Originals(Path.Combine(_root, "kept"));
            var kept = store.Keep(photo);
            Check(File.Exists(kept.Copy) && File.ReadAllText(kept.Copy) == "the original", "a copy is kept");
            File.WriteAllText(photo, "saved over");
            Equal(1, store.For(photo).Count, "found for the photo");
            Equal(12L, store.SizeOnDisk(), "size of what's kept");
            store.PutBack(store.For(photo)[0]);
            Equal("the original", File.ReadAllText(photo), "put back");
            Equal(when, File.GetLastWriteTimeUtc(photo), "with its date");
            Equal(0, store.For(photo).Count, "and no longer kept");
        }

        private static void TestThumbKeys()
        {
            var item = new MediaItem { Path = @"C:\Pictures\a.jpg", Size = 10, Modified = 5 };
            var key = AppPaths.ThumbKey(item, null);
            Equal(key, AppPaths.ThumbKey(new MediaItem { Path = @"c:\pictures\A.JPG", Size = 10, Modified = 5 }, null), "same file, same key");
            Check(key != AppPaths.ThumbKey(item, "fixed"), "a fix changes the key");
            Check(key != AppPaths.ThumbKey(new MediaItem { Path = item.Path, Size = 11, Modified = 5 }, null), "a changed file changes the key");
        }
    }
}
