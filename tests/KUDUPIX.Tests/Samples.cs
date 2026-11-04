using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Kudupix.Tests
{
    /// <summary>
    /// Real photos and a real video used as test data. They belong to other open-source projects, so they're downloaded
    /// (from a fixed commit, checked by SHA-256) instead of being copied into this repository.
    /// </summary>
    public static class Samples
    {
        // Sample images for testing EXIF reading (ianare/exif-samples, now archived).
        private const string Exif = "https://raw.githubusercontent.com/ianare/exif-samples/5332a9cf0220e9c6c93f88a187daa90808051e10/";
        // ExifTool's own test files.
        private const string ExifTool = "https://raw.githubusercontent.com/exiftool/exiftool/980409314fddbd83d9092b5c160fafab7f67fd52/t/images/";

        public static string Canon => Get(Exif + "jpg/Canon_40D.jpg", "6bfdabd4fc33d112283c147acccc574e770bbe6fbdbc3d4da968ba7b606ecc2f");
        public static string Gps => Get(Exif + "jpg/gps/DSCN0010.jpg", "17307b1207eb6487d7908e9d154890b46e3d2e0192369cfd3f4c33d5a5af4035");
        public static string Portrait6 => Get(Exif + "jpg/orientation/portrait_6.jpg", "323ce0d7140be76cbe6511e268766241dfe74eddf34b73f27f4637e552c8d824");
        public static string Landscape3 => Get(Exif + "jpg/orientation/landscape_3.jpg", "533f2e6d35a62e9bc144dbb3921a9b2105cf2498eaafb10812f197e6108bd758");
        public static string Tiff => Get(Exif + "tiff/Arbitro.tiff", "26f4b11c45ad3e56a530d03967ff4627892b3264183b85fefba194ff1fe3e08e");
        public static string Movie => Get(ExifTool + "QuickTime.mov", "eea529609b6026e0cd7b3d9188b997889f905cd89a93421ad7a9063c670449ec");

        public static string Folder
        {
            get
            {
                var folder = Environment.GetEnvironmentVariable("KUDUPIX_SAMPLES");
                if (string.IsNullOrEmpty(folder))
                    folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "samples-cache");
                Directory.CreateDirectory(folder);
                return Path.GetFullPath(folder);
            }
        }

        /// <summary>Downloads all of them (the self-test uses the same folder).</summary>
        public static void All()
        {
            foreach (var p in new[] { Canon, Gps, Portrait6, Landscape3, Tiff, Movie }) { }
        }

        private static string Get(string url, string sha256)
        {
            var name = Uri.UnescapeDataString(url.Substring(url.LastIndexOf('/') + 1));
            var path = Path.Combine(Folder, name);
            if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == sha256) return path;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                var data = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (Hash(data) != sha256) throw new Exception($"Downloaded {name}, but it isn't the expected file.");
                File.WriteAllBytes(path, data);
            }
            return path;
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
