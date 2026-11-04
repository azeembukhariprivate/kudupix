using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Kudupix.Core
{
    /// <summary>What a photo file says about itself: when it was taken, its size, the camera, where, and which way up.</summary>
    public sealed class PhotoDetails
    {
        public DateTime? Taken { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>1 = as stored; 3 = upside down; 6 = turned right; 8 = turned left (2, 4, 5, 7 are mirrored).</summary>
        public int Orientation { get; set; } = 1;
        public string Camera { get; set; } = string.Empty;
        public string Exposure { get; set; } = string.Empty;
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        /// <summary>The size as shown, with the orientation applied (a phone photo taken upright is taller than wide).</summary>
        public bool Turned => Orientation >= 5 && Orientation <= 8;
    }

    /// <summary>
    /// Reads photo details straight from the file (only the first part of it is read): EXIF in JPEG and TIFF files, and
    /// the size of PNG, GIF, BMP and WebP pictures. Never changes anything.
    /// </summary>
    public static class PhotoInfo
    {
        public static PhotoDetails Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                return Read(fs);
        }

        public static PhotoDetails Read(Stream s)
        {
            var d = new PhotoDetails();
            var head = new byte[32];
            var n = ReadAll(s, head, 0, head.Length);
            if (n < 12) return d;
            s.Position = 0;
            try
            {
                if (head[0] == 0xFF && head[1] == 0xD8) Jpeg(s, d);
                else if ((head[0] == 'I' && head[1] == 'I' && head[2] == 42) || (head[0] == 'M' && head[1] == 'M' && head[3] == 42)) Tiff(ReadUpTo(s, 4 * 1024 * 1024), 0, d, true);
                else if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G' && n >= 24)
                {
                    d.Width = BigEndian(head, 16);
                    d.Height = BigEndian(head, 20);
                }
                else if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
                {
                    d.Width = head[6] | head[7] << 8;
                    d.Height = head[8] | head[9] << 8;
                }
                else if (head[0] == 'B' && head[1] == 'M' && n >= 26)
                {
                    d.Width = BitConverter.ToInt32(head, 18);
                    d.Height = Math.Abs(BitConverter.ToInt32(head, 22));
                }
                else if (Ascii(head, 0, 4) == "RIFF" && Ascii(head, 8, 4) == "WEBP") WebP(head, n, d);
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IndexOutOfRangeException || ex is ArgumentException || ex is IOException || ex is OverflowException)
            {
                // A damaged file: keep what was found.
            }
            return d;
        }

        // ------------------------------------------------------------------ JPEG

        private static void Jpeg(Stream s, PhotoDetails d)
        {
            s.Position = 2;
            var b = new byte[4];
            while (true)
            {
                if (ReadAll(s, b, 0, 2) < 2) return;
                if (b[0] != 0xFF) return;
                var marker = b[1];
                if (marker == 0xFF) { s.Position--; continue; }
                if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) continue;
                if (marker == 0xD9 || marker == 0xDA) return; // the picture itself starts: no more details
                if (ReadAll(s, b, 0, 2) < 2) return;
                var length = (b[0] << 8 | b[1]) - 2;
                if (length < 0) return;
                var start = s.Position;
                if (marker == 0xE1 && length > 6 && d.Camera.Length == 0 && !d.Taken.HasValue)
                {
                    var app1 = new byte[length];
                    if (ReadAll(s, app1, 0, length) < length) return;
                    if (Ascii(app1, 0, 6) == "Exif\0\0") Tiff(app1, 6, d, false);
                }
                else if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) || (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
                {
                    var sof = new byte[5];
                    if (ReadAll(s, sof, 0, 5) < 5) return;
                    d.Height = sof[1] << 8 | sof[2];
                    d.Width = sof[3] << 8 | sof[4];
                }
                s.Position = start + length;
            }
        }

        // ------------------------------------------------------------------ TIFF / EXIF

        /// <summary>Reads a TIFF structure (the EXIF block) starting at <paramref name="at"/>.</summary>
        private static void Tiff(byte[] data, int at, PhotoDetails d, bool sizeFromMain)
        {
            var little = data[at] == 'I';
            int U16(int p) => p + 2 > data.Length ? 0 : little ? data[p] | data[p + 1] << 8 : data[p] << 8 | data[p + 1];
            long U32(int p) => p + 4 > data.Length ? 0 : little
                ? (uint)(data[p] | data[p + 1] << 8 | data[p + 2] << 16 | data[p + 3] << 24)
                : (uint)(data[p] << 24 | data[p + 1] << 16 | data[p + 2] << 8 | data[p + 3]);

            string make = null, model = null, original = null, digitized = null, plain = null;
            double? exposure = null, fNumber = null, focal = null;
            int iso = 0;
            string latRef = null, lonRef = null;
            double[] lat = null, lon = null;
            int exifWidth = 0, exifHeight = 0;

            void Ifd(long offset, int kind, int depth)
            {
                if (offset <= 0 || depth > 3) return;
                var p = at + (int)offset;
                var count = U16(p);
                if (count <= 0 || count > 1000) return;
                for (var i = 0; i < count; i++)
                {
                    var e = p + 2 + i * 12;
                    if (e + 12 > data.Length) return;
                    var tag = U16(e);
                    var type = U16(e + 2);
                    var n = U32(e + 4);
                    var size = TypeSize(type) * n;
                    var valueAt = size <= 4 ? e + 8 : at + (int)U32(e + 8);
                    if (valueAt < 0 || valueAt + size > data.Length || size < 0) continue;

                    string Text() => size <= 0 ? null : Encoding.ASCII.GetString(data, valueAt, (int)size).TrimEnd('\0', ' ').Trim();
                    double Rational(int k) { var num = U32(valueAt + k * 8); var den = U32(valueAt + k * 8 + 4); return den == 0 ? 0 : (double)num / den; }
                    long Number() => type == 3 ? U16(valueAt) : U32(valueAt);

                    if (kind == 0)
                    {
                        switch (tag)
                        {
                            case 0x010F: make = Text(); break;
                            case 0x0110: model = Text(); break;
                            case 0x0112: d.Orientation = (int)Number(); break;
                            case 0x0132: plain = Text(); break;
                            case 0x0100: if (sizeFromMain) d.Width = (int)Number(); break;
                            case 0x0101: if (sizeFromMain) d.Height = (int)Number(); break;
                            case 0x8769: Ifd(U32(e + 8), 1, depth + 1); break;
                            case 0x8825: Ifd(U32(e + 8), 2, depth + 1); break;
                        }
                    }
                    else if (kind == 1)
                    {
                        switch (tag)
                        {
                            case 0x9003: original = Text(); break;
                            case 0x9004: digitized = Text(); break;
                            case 0x829A: if (type == 5) exposure = Rational(0); break;
                            case 0x829D: if (type == 5) fNumber = Rational(0); break;
                            case 0x920A: if (type == 5) focal = Rational(0); break;
                            case 0x8827: iso = (int)Number(); break;
                            case 0xA002: exifWidth = (int)Number(); break;
                            case 0xA003: exifHeight = (int)Number(); break;
                        }
                    }
                    else
                    {
                        switch (tag)
                        {
                            case 1: latRef = Text(); break;
                            case 2: if (type == 5 && n >= 3) lat = new[] { Rational(0), Rational(1), Rational(2) }; break;
                            case 3: lonRef = Text(); break;
                            case 4: if (type == 5 && n >= 3) lon = new[] { Rational(0), Rational(1), Rational(2) }; break;
                        }
                    }
                }
            }

            if (Ascii(data, at, 2) != "II" && Ascii(data, at, 2) != "MM") return;
            Ifd(U32(at + 4), 0, 0);

            d.Taken = ParseDate(original) ?? ParseDate(digitized) ?? ParseDate(plain);
            make = make ?? string.Empty;
            model = model ?? string.Empty;
            // "Canon Canon EOS 40D" reads better as "Canon EOS 40D".
            d.Camera = model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase) || make.Length == 0 ? model : (make + " " + model).Trim();
            if (d.Width == 0 && exifWidth > 0) { d.Width = exifWidth; d.Height = exifHeight; }
            var parts = new System.Collections.Generic.List<string>();
            if (fNumber > 0) parts.Add("f/" + fNumber.Value.ToString("0.#", CultureInfo.InvariantCulture));
            if (exposure > 0) parts.Add(exposure.Value >= 1 ? exposure.Value.ToString("0.#", CultureInfo.InvariantCulture) + " s" : "1/" + Math.Round(1 / exposure.Value).ToString(CultureInfo.InvariantCulture) + " s");
            if (focal > 0) parts.Add(focal.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm");
            if (iso > 0) parts.Add("ISO " + iso.ToString(CultureInfo.InvariantCulture));
            d.Exposure = string.Join(" · ", parts);
            if (lat != null && lon != null)
            {
                var la = lat[0] + lat[1] / 60 + lat[2] / 3600;
                var lo = lon[0] + lon[1] / 60 + lon[2] / 3600;
                if (string.Equals(latRef, "S", StringComparison.OrdinalIgnoreCase)) la = -la;
                if (string.Equals(lonRef, "W", StringComparison.OrdinalIgnoreCase)) lo = -lo;
                // 0,0 is a camera that had no fix, not a place in the sea.
                if (Math.Abs(la) <= 90 && Math.Abs(lo) <= 180 && !(la == 0 && lo == 0))
                {
                    d.Latitude = Math.Round(la, 6);
                    d.Longitude = Math.Round(lo, 6);
                }
            }
        }

        private static long TypeSize(int type)
        {
            switch (type)
            {
                case 1: case 2: case 6: case 7: return 1;
                case 3: case 8: return 2;
                case 4: case 9: case 11: return 4;
                case 5: case 10: case 12: return 8;
                default: return 0;
            }
        }

        /// <summary>EXIF dates look like "2026:11:02 16:45:12" (some cameras use dashes or leave blanks).</summary>
        public static DateTime? ParseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("0000")) return null;
            var t = text.Trim().Replace('-', ':').Replace('T', ' ');
            if (t.Length >= 19) t = t.Substring(0, 19);
            if (DateTime.TryParseExact(t, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) && v.Year >= 1826 && v.Year <= 2200) return v;
            if (t.Length >= 10 && DateTime.TryParseExact(t.Substring(0, 10), "yyyy:MM:dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out v) && v.Year >= 1826) return v;
            return null;
        }

        // ------------------------------------------------------------------ WebP

        private static void WebP(byte[] h, int n, PhotoDetails d)
        {
            var chunk = Ascii(h, 12, 4);
            if (chunk == "VP8X" && n >= 30)
            {
                d.Width = 1 + (h[24] | h[25] << 8 | h[26] << 16);
                d.Height = 1 + (h[27] | h[28] << 8 | h[29] << 16);
            }
            else if (chunk == "VP8 " && n >= 30)
            {
                d.Width = (h[26] | h[27] << 8) & 0x3FFF;
                d.Height = (h[28] | h[29] << 8) & 0x3FFF;
            }
            else if (chunk == "VP8L" && n >= 25)
            {
                var bits = h[21] | h[22] << 8 | h[23] << 16 | h[24] << 24;
                d.Width = (bits & 0x3FFF) + 1;
                d.Height = ((bits >> 14) & 0x3FFF) + 1;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

        private static string Ascii(byte[] b, int at, int count) => at + count > b.Length ? string.Empty : Encoding.ASCII.GetString(b, at, count);

        private static int ReadAll(Stream s, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var r = s.Read(buffer, offset + total, count - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        private static byte[] ReadUpTo(Stream s, int max)
        {
            var length = (int)Math.Min(max, s.Length);
            var b = new byte[length];
            ReadAll(s, b, 0, length);
            return b;
        }
    }
}
