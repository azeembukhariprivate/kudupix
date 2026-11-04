using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Kudupix.Core
{
    /// <summary>A picture as plain pixels: 4 bytes each, blue, green, red, alpha, row after row.</summary>
    public sealed class Pixels
    {
        public Pixels(int width, int height, byte[] bgra = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("A picture needs a size.");
            Width = width;
            Height = height;
            Bgra = bgra ?? new byte[width * height * 4];
            if (Bgra.Length != width * height * 4) throw new ArgumentException("The pixels don't match the size.");
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Bgra { get; }
        public int Stride => Width * 4;

        public Pixels Clone() => new Pixels(Width, Height, (byte[])Bgra.Clone());

        public (byte B, byte G, byte R) At(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (Bgra[i], Bgra[i + 1], Bgra[i + 2]);
        }

        public void Set(int x, int y, byte r, byte g, byte b)
        {
            var i = (y * Width + x) * 4;
            Bgra[i] = b;
            Bgra[i + 1] = g;
            Bgra[i + 2] = r;
            Bgra[i + 3] = 255;
        }
    }

    /// <summary>A red-eye spot: where (as parts of the turned photo's width and height) and how big (part of its shorter side).</summary>
    [DataContract]
    public sealed class Spot
    {
        [DataMember] public double X { get; set; }
        [DataMember] public double Y { get; set; }
        [DataMember] public double R { get; set; }
    }

    /// <summary>
    /// The fixes for one photo, kept in KUDUPIX until a copy is saved: turned (quarter turns to the right), red-eye,
    /// straightened (degrees), cropped (parts of the width and height), then auto-fix, brightness, contrast, warmth and
    /// colour. Applied in that order, always to the untouched original, so every fix can be changed or undone.
    /// </summary>
    [DataContract]
    public sealed class EditRecipe
    {
        [DataMember] public int Turns { get; set; }
        [DataMember] public double Straighten { get; set; }
        [DataMember] public double CropLeft { get; set; }
        [DataMember] public double CropTop { get; set; }
        [DataMember] public double CropRight { get; set; } = 1;
        [DataMember] public double CropBottom { get; set; } = 1;
        [DataMember] public bool AutoFix { get; set; }
        [DataMember] public int Brightness { get; set; }
        [DataMember] public int Contrast { get; set; }
        [DataMember] public int Warmth { get; set; }
        [DataMember] public int Colour { get; set; }
        [DataMember] public List<Spot> RedEye { get; set; } = new List<Spot>();

        public bool HasCrop => CropLeft > 0.0005 || CropTop > 0.0005 || CropRight < 0.9995 || CropBottom < 0.9995;
        public bool HasColour => AutoFix || Brightness != 0 || Contrast != 0 || Warmth != 0 || Colour != 0;

        public bool IsEmpty => Turns % 4 == 0 && Math.Abs(Straighten) < 0.01 && !HasCrop && !HasColour && (RedEye == null || RedEye.Count == 0);

        public EditRecipe Clone()
        {
            var c = (EditRecipe)MemberwiseClone();
            c.RedEye = (RedEye ?? new List<Spot>()).Select(s => new Spot { X = s.X, Y = s.Y, R = s.R }).ToList();
            return c;
        }

        /// <summary>Turns the photo a quarter to the right (or left); the crop turns with it.</summary>
        public void Turn(bool right)
        {
            double l = CropLeft, t = CropTop, r = CropRight, b = CropBottom;
            if (right)
            {
                CropLeft = 1 - b; CropTop = l; CropRight = 1 - t; CropBottom = r;
                foreach (var s in RedEye ?? new List<Spot>()) { var x = s.X; s.X = 1 - s.Y; s.Y = x; }
                Turns = (Turns + 1) % 4;
            }
            else
            {
                CropLeft = t; CropTop = 1 - r; CropRight = b; CropBottom = 1 - l;
                foreach (var s in RedEye ?? new List<Spot>()) { var x = s.X; s.X = s.Y; s.Y = 1 - x; }
                Turns = (Turns + 3) % 4;
            }
        }

        /// <summary>The photo with every fix applied (the original pixels are left as they are).</summary>
        public Pixels Apply(Pixels source)
        {
            var p = ImageOps.Turn(source, Turns);
            if (RedEye != null && RedEye.Count > 0)
            {
                if (ReferenceEquals(p, source)) p = p.Clone();
                foreach (var s in RedEye) ImageOps.RedEye(p, s);
            }
            if (Math.Abs(Straighten) >= 0.01) p = ImageOps.Straighten(p, Straighten);
            if (HasCrop) p = ImageOps.Crop(p, CropLeft, CropTop, CropRight, CropBottom);
            if (HasColour)
            {
                if (ReferenceEquals(p, source)) p = p.Clone();
                ImageOps.Colours(p, this);
            }
            return p;
        }

        /// <summary>
        /// Where a point on the fixed photo (parts of its width and height) is on the turned photo before straightening
        /// and cropping: red-eye spots are kept there, so they stay on the eye whatever else changes.
        /// </summary>
        public (double X, double Y) ToTurned(double x, double y, int turnedWidth, int turnedHeight)
        {
            var sx = CropLeft + x * (CropRight - CropLeft);
            var sy = CropTop + y * (CropBottom - CropTop);
            if (Math.Abs(Straighten) < 0.01) return (sx, sy);
            var (u, v) = ImageOps.StraightenSource(sx * turnedWidth, sy * turnedHeight, turnedWidth, turnedHeight, Straighten);
            return (u / turnedWidth, v / turnedHeight);
        }
    }

    /// <summary>The picture work itself, on plain pixels.</summary>
    public static class ImageOps
    {
        private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

        /// <summary>Quarter turns to the right (0 to 3; the same picture for 0).</summary>
        public static Pixels Turn(Pixels p, int turns)
        {
            turns = ((turns % 4) + 4) % 4;
            if (turns == 0) return p;
            var w = p.Width;
            var h = p.Height;
            var outW = turns == 2 ? w : h;
            var outH = turns == 2 ? h : w;
            var o = new Pixels(outW, outH);
            var src = p.Bgra;
            var dst = o.Bgra;
            for (var y = 0; y < outH; y++)
                for (var x = 0; x < outW; x++)
                {
                    int sx, sy;
                    switch (turns)
                    {
                        case 1: sx = y; sy = h - 1 - x; break;      // a quarter to the right
                        case 2: sx = w - 1 - x; sy = h - 1 - y; break;
                        default: sx = w - 1 - y; sy = x; break;      // a quarter to the left
                    }
                    Buffer.BlockCopy(src, (sy * w + sx) * 4, dst, (y * outW + x) * 4, 4);
                }
            return o;
        }

        /// <summary>A part of the picture (parts of its width and height, 0 to 1).</summary>
        public static Pixels Crop(Pixels p, double left, double top, double right, double bottom)
        {
            var x0 = (int)Math.Round(Math.Max(0, Math.Min(1, left)) * p.Width);
            var y0 = (int)Math.Round(Math.Max(0, Math.Min(1, top)) * p.Height);
            var x1 = (int)Math.Round(Math.Max(0, Math.Min(1, right)) * p.Width);
            var y1 = (int)Math.Round(Math.Max(0, Math.Min(1, bottom)) * p.Height);
            var w = Math.Max(1, x1 - x0);
            var h = Math.Max(1, y1 - y0);
            x0 = Math.Min(x0, p.Width - w);
            y0 = Math.Min(y0, p.Height - h);
            var o = new Pixels(w, h);
            for (var y = 0; y < h; y++) Buffer.BlockCopy(p.Bgra, ((y0 + y) * p.Width + x0) * 4, o.Bgra, y * w * 4, w * 4);
            return o;
        }

        /// <summary>How much straightening has to zoom in so no empty corners show.</summary>
        public static double StraightenZoom(int w, int h, double degrees)
        {
            var a = Math.Abs(degrees) * Math.PI / 180;
            return Math.Cos(a) + Math.Max((double)w / h, (double)h / w) * Math.Sin(a);
        }

        /// <summary>Where a point of the straightened picture comes from in the picture before (in pixels).</summary>
        public static (double X, double Y) StraightenSource(double x, double y, int w, int h, double degrees)
        {
            var a = degrees * Math.PI / 180;
            var k = StraightenZoom(w, h, degrees);
            var dx = x - w / 2.0;
            var dy = y - h / 2.0;
            var cos = Math.Cos(a);
            var sin = Math.Sin(a);
            return ((dx * cos + dy * sin) / k + w / 2.0, (-dx * sin + dy * cos) / k + h / 2.0);
        }

        /// <summary>Turns the picture a few degrees (to the right for more than 0), zoomed in just enough to fill it.</summary>
        public static Pixels Straighten(Pixels p, double degrees)
        {
            var w = p.Width;
            var h = p.Height;
            var o = new Pixels(w, h);
            var src = p.Bgra;
            var dst = o.Bgra;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var (sx, sy) = StraightenSource(x + 0.5, y + 0.5, w, h, degrees);
                    Sample(src, w, h, sx - 0.5, sy - 0.5, dst, (y * w + x) * 4);
                }
            return o;
        }

        /// <summary>Bilinear sampling (edges repeat).</summary>
        private static void Sample(byte[] src, int w, int h, double x, double y, byte[] dst, int at)
        {
            x = Math.Max(0, Math.Min(w - 1, x));
            y = Math.Max(0, Math.Min(h - 1, y));
            var x0 = (int)x;
            var y0 = (int)y;
            var x1 = Math.Min(w - 1, x0 + 1);
            var y1 = Math.Min(h - 1, y0 + 1);
            var fx = x - x0;
            var fy = y - y0;
            for (var c = 0; c < 4; c++)
            {
                var top = src[(y0 * w + x0) * 4 + c] * (1 - fx) + src[(y0 * w + x1) * 4 + c] * fx;
                var bottom = src[(y1 * w + x0) * 4 + c] * (1 - fx) + src[(y1 * w + x1) * 4 + c] * fx;
                dst[at + c] = Clamp(top * (1 - fy) + bottom * fy);
            }
        }

        /// <summary>Takes the red out of eyes in a round spot: strongly red pixels get the brightness of their green and blue.</summary>
        public static void RedEye(Pixels p, Spot s)
        {
            var r = Math.Max(1, s.R * Math.Min(p.Width, p.Height));
            var cx = s.X * p.Width;
            var cy = s.Y * p.Height;
            var x0 = Math.Max(0, (int)(cx - r));
            var x1 = Math.Min(p.Width - 1, (int)(cx + r));
            var y0 = Math.Max(0, (int)(cy - r));
            var y1 = Math.Min(p.Height - 1, (int)(cy + r));
            var b = p.Bgra;
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    if (dx * dx + dy * dy > r * r) continue;
                    var i = (y * p.Width + x) * 4;
                    int blue = b[i], green = b[i + 1], red = b[i + 2];
                    var others = (green + blue) / 2.0;
                    if (red > 60 && red > others * 1.6)
                        b[i + 2] = Clamp(others);
                }
        }

        /// <summary>Where auto-fix puts black and white for each colour: the darkest and brightest 0.5% are let go.</summary>
        public static (int[] Low, int[] High) Levels(Pixels p)
        {
            var hist = new int[3, 256];
            var b = p.Bgra;
            var step = Math.Max(1, (p.Width * p.Height) / 250000); // a sample is enough for big photos
            var n = 0;
            for (var i = 0; i < b.Length; i += 4 * step)
            {
                hist[0, b[i]]++;
                hist[1, b[i + 1]]++;
                hist[2, b[i + 2]]++;
                n++;
            }
            var low = new int[3];
            var high = new int[3];
            var cut = Math.Max(1, n / 200);
            for (var c = 0; c < 3; c++)
            {
                int sum = 0, v = 0;
                for (; v < 255 && sum + hist[c, v] <= cut; v++) sum += hist[c, v];
                low[c] = v;
                sum = 0;
                v = 255;
                for (; v > 0 && sum + hist[c, v] <= cut; v--) sum += hist[c, v];
                high[c] = v;
                // Never stretch more than three times: a photo of the night sky stays dark, a grey wall stays grey.
                const int narrowest = 85;
                if (high[c] - low[c] < narrowest)
                {
                    var middle = (low[c] + high[c]) / 2.0;
                    low[c] = (int)Math.Max(0, Math.Round(middle - narrowest / 2.0));
                    high[c] = Math.Min(255, low[c] + narrowest);
                    low[c] = high[c] - narrowest;
                }
            }
            return (low, high);
        }

        /// <summary>Auto-fix, brightness, contrast, warmth and colour, in one pass.</summary>
        public static void Colours(Pixels p, EditRecipe e)
        {
            var lut = new byte[3][];
            int[] low = { 0, 0, 0 }, high = { 255, 255, 255 };
            if (e.AutoFix) (low, high) = Levels(p);
            var contrast = (100.0 + Math.Max(-100, Math.Min(100, e.Contrast))) / 100.0;
            var bright = Math.Max(-100, Math.Min(100, e.Brightness)) * 0.6;
            var warm = Math.Max(-100, Math.Min(100, e.Warmth)) * 0.3;
            for (var c = 0; c < 3; c++)
            {
                lut[c] = new byte[256];
                var shift = c == 2 ? warm : c == 0 ? -warm : 0; // red up and blue down is warmer
                for (var v = 0; v < 256; v++)
                {
                    double x = v;
                    if (e.AutoFix) x = (x - low[c]) * 255.0 / (high[c] - low[c]);
                    x += bright;
                    x = (x - 128) * contrast + 128;
                    x += shift;
                    lut[c][v] = Clamp(x);
                }
            }
            var sat = 1 + Math.Max(-100, Math.Min(100, e.Colour)) / 100.0;
            var b = p.Bgra;
            for (var i = 0; i < b.Length; i += 4)
            {
                double blue = lut[0][b[i]], green = lut[1][b[i + 1]], red = lut[2][b[i + 2]];
                if (sat != 1)
                {
                    var lum = 0.299 * red + 0.587 * green + 0.114 * blue;
                    red = lum + (red - lum) * sat;
                    green = lum + (green - lum) * sat;
                    blue = lum + (blue - lum) * sat;
                }
                b[i] = Clamp(blue);
                b[i + 1] = Clamp(green);
                b[i + 2] = Clamp(red);
            }
        }

        /// <summary>A smaller copy no bigger than <paramref name="maxSide"/> (each new pixel the average of the ones it covers).</summary>
        public static Pixels Shrink(Pixels p, int maxSide)
        {
            var longest = Math.Max(p.Width, p.Height);
            if (maxSide <= 0 || longest <= maxSide) return p;
            var scale = (double)maxSide / longest;
            return Resize(p, Math.Max(1, (int)Math.Round(p.Width * scale)), Math.Max(1, (int)Math.Round(p.Height * scale)));
        }

        /// <summary>Resizes by averaging (made for making pictures smaller).</summary>
        public static Pixels Resize(Pixels p, int w, int h)
        {
            var o = new Pixels(w, h);
            var src = p.Bgra;
            var sx = (double)p.Width / w;
            var sy = (double)p.Height / h;
            var sum = new double[4];
            for (var y = 0; y < h; y++)
            {
                var ya = (int)(y * sy);
                var yb = Math.Max(ya + 1, Math.Min(p.Height, (int)Math.Ceiling((y + 1) * sy)));
                for (var x = 0; x < w; x++)
                {
                    var xa = (int)(x * sx);
                    var xb = Math.Max(xa + 1, Math.Min(p.Width, (int)Math.Ceiling((x + 1) * sx)));
                    Array.Clear(sum, 0, 4);
                    var count = 0;
                    for (var yy = ya; yy < yb; yy++)
                        for (var xx = xa; xx < xb; xx++)
                        {
                            var i = (yy * p.Width + xx) * 4;
                            sum[0] += src[i]; sum[1] += src[i + 1]; sum[2] += src[i + 2]; sum[3] += src[i + 3];
                            count++;
                        }
                    var at = (y * w + x) * 4;
                    for (var c = 0; c < 4; c++) o.Bgra[at + c] = Clamp(sum[c] / count);
                }
            }
            return o;
        }

        /// <summary>
        /// The picture's fingerprint for finding look-alikes: shrunk to 9 × 8 grey squares, each bit says whether a square
        /// is brighter than the one to its right. Resized, re-saved or slightly changed copies get the same or nearly the
        /// same fingerprint.
        /// </summary>
        public static ulong Look(Pixels p)
        {
            var small = Resize(p, 9, 8);
            ulong hash = 0;
            var bit = 0;
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                {
                    double Grey(int xx)
                    {
                        var i = (y * 9 + xx) * 4;
                        return 0.114 * small.Bgra[i] + 0.587 * small.Bgra[i + 1] + 0.299 * small.Bgra[i + 2];
                    }
                    if (Grey(x) > Grey(x + 1)) hash |= 1UL << bit;
                    bit++;
                }
            // 0 means "not worked out yet": a perfectly even picture gets a fingerprint of its own.
            return hash == 0 ? 1UL << 63 : hash;
        }

        public static int Distance(ulong a, ulong b)
        {
            var x = a ^ b;
            var n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }
    }
}
