using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Kudupix.Core
{
    /// <summary>What a video file says about itself.</summary>
    public sealed class VideoDetails
    {
        /// <summary>When it was filmed, in this computer's time (null when the file doesn't say).</summary>
        public DateTime? Taken { get; set; }
        public double Seconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }

    /// <summary>
    /// Reads MP4, MOV, M4V and 3GP videos (the "boxes" phones and cameras write): length, picture size (turned the way
    /// it plays), when it was filmed and where (when the phone stored it). Only the boxes needed are read.
    /// </summary>
    public static class VideoInfo
    {
        private static readonly DateTime Epoch1904 = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static VideoDetails Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                return Read(fs);
        }

        public static VideoDetails Read(Stream s)
        {
            var d = new VideoDetails();
            try
            {
                Boxes(s, 0, s.Length, d, 0);
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException || ex is OverflowException || ex is IndexOutOfRangeException)
            {
                // A damaged or cut-off file: keep what was found.
            }
            return d;
        }

        private static void Boxes(Stream s, long start, long end, VideoDetails d, int depth)
        {
            var pos = start;
            var header = new byte[16];
            while (pos + 8 <= end && depth < 8)
            {
                s.Position = pos;
                if (Fill(s, header, 8) < 8) return;
                long size = U32(header, 0);
                var type = new string(new[] { (char)header[4], (char)header[5], (char)header[6], (char)header[7] }); // "©xyz" starts with byte 0xA9
                var headerSize = 8;
                if (size == 1)
                {
                    if (Fill(s, header, 8) < 8) return;
                    size = (long)U64(header, 0);
                    headerSize = 16;
                }
                else if (size == 0) size = end - pos;
                if (size < headerSize || pos + size > end + 8) return;
                var body = pos + headerSize;
                var bodyEnd = Math.Min(pos + size, end);
                switch (type)
                {
                    case "moov":
                    case "trak":
                    case "udta":
                        Boxes(s, body, bodyEnd, d, depth + 1);
                        break;
                    case "mvhd":
                        Mvhd(s, body, d);
                        break;
                    case "tkhd":
                        Tkhd(s, body, d);
                        break;
                    case "©xyz":
                        Place(Text(s, body, bodyEnd), d);
                        break;
                    case "meta":
                        // Apple keeps the place in a "keys" list; the value is a text like "+12.3456+076.5432+010.000/".
                        if (!d.Latitude.HasValue) Place(Latin(s, body, bodyEnd), d);
                        break;
                }
                pos += size;
            }
        }

        private static void Mvhd(Stream s, long at, VideoDetails d)
        {
            s.Position = at;
            var b = new byte[32];
            if (Fill(s, b, 32) < 32) return;
            ulong created;
            long timescale;
            ulong duration;
            if (b[0] == 1)
            {
                created = U64(b, 4);
                timescale = U32(b, 20);
                duration = U64(b, 24);
            }
            else
            {
                created = (ulong)U32(b, 4);
                timescale = U32(b, 12);
                duration = (ulong)U32(b, 16);
            }
            if (timescale > 0) d.Seconds = Math.Round(duration / (double)timescale, 3);
            // Cameras without a clock write 0 (1904) or 1970.
            if (created > 2082844800UL + 86400UL * 365 && created < 2082844800UL + 4102444800UL)
            {
                var utc = Epoch1904.AddSeconds(created);
                d.Taken = utc.ToLocalTime();
            }
        }

        private static void Tkhd(Stream s, long at, VideoDetails d)
        {
            s.Position = at;
            var b = new byte[96];
            var version = s.ReadByte();
            if (version < 0) return;
            s.Position = at;
            var length = version == 1 ? 96 : 84;
            if (Fill(s, b, length) < length) return;
            var m = version == 1 ? 52 : 40; // the matrix
            var width = (int)(U32(b, m + 36) >> 16);
            var height = (int)(U32(b, m + 40) >> 16);
            if (width <= 0 || height <= 0 || d.Width > 0) return; // sound tracks have no picture; the first picture counts
            var a = (int)U32(b, m);
            var bb = (int)U32(b, m + 4);
            var turned = a == 0 && bb != 0;
            d.Width = turned ? height : width;
            d.Height = turned ? width : height;
        }

        /// <summary>ISO 6709 places, as phones write them: "+12.3456+076.5432/" or "+12.3456+076.5432+010.000/".</summary>
        public static void Place(string text, VideoDetails d)
        {
            if (string.IsNullOrEmpty(text)) return;
            var m = Regex.Match(text, @"([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)");
            if (!m.Success) return;
            var la = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var lo = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(la) > 90 || Math.Abs(lo) > 180 || (la == 0 && lo == 0)) return;
            d.Latitude = la;
            d.Longitude = lo;
        }

        private static string Text(Stream s, long start, long end) => Latin(s, start, end);

        private static string Latin(Stream s, long start, long end)
        {
            var length = (int)Math.Min(4096, Math.Max(0, end - start));
            var b = new byte[length];
            s.Position = start;
            var n = Fill(s, b, length);
            var sb = new StringBuilder(n);
            for (var i = 0; i < n; i++) sb.Append(b[i] >= 32 && b[i] < 127 ? (char)b[i] : ' ');
            return sb.ToString();
        }

        private static int Fill(Stream s, byte[] b, int count)
        {
            var total = 0;
            while (total < count)
            {
                var r = s.Read(b, total, count - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        private static long U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        private static ulong U64(byte[] b, int at) => (ulong)U32(b, at) << 32 | (ulong)U32(b, at + 4);
    }
}
