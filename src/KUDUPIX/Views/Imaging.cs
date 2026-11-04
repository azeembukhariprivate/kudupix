using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kudupix.Core;

namespace Kudupix.Views
{
    /// <summary>Opening and saving pictures with Windows' own codecs (WIC), turned the right way up.</summary>
    public static class Imaging
    {
        /// <summary>
        /// Opens a photo no bigger than <paramref name="maxSide"/> (0 = full size), turned the way the camera says.
        /// JPEG photos are opened straight at the smaller size, which is much quicker.
        /// </summary>
        public static BitmapSource Decode(string path, int orientation, int maxSide, int knownWidth = 0, int knownHeight = 0)
        {
            BitmapSource bitmap;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = fs;
                if (maxSide > 0 && knownWidth > 0 && knownHeight > 0 && Math.Max(knownWidth, knownHeight) > maxSide)
                {
                    if (knownWidth >= knownHeight) image.DecodePixelWidth = maxSide;
                    else image.DecodePixelHeight = maxSide;
                }
                image.EndInit();
                bitmap = image;
            }
            if (maxSide > 0 && Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) > maxSide * 1.05)
            {
                var scale = (double)maxSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            }
            bitmap = Orient(bitmap, orientation);
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Turns (and mirrors) a picture the way EXIF orientation 1-8 says.</summary>
        public static BitmapSource Orient(BitmapSource b, int orientation)
        {
            Transform t;
            switch (orientation)
            {
                case 2: t = new ScaleTransform(-1, 1); break;
                case 3: t = new RotateTransform(180); break;
                case 4: t = new ScaleTransform(1, -1); break;
                case 5: t = new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } }; break;
                case 6: t = new RotateTransform(90); break;
                case 7: t = new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } }; break;
                case 8: t = new RotateTransform(270); break;
                default: return b;
            }
            return new TransformedBitmap(b, t);
        }

        public static Pixels ToPixels(BitmapSource b)
        {
            var converted = b.Format == PixelFormats.Bgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            var p = new Pixels(converted.PixelWidth, converted.PixelHeight);
            converted.CopyPixels(p.Bgra, p.Stride, 0);
            return p;
        }

        public static BitmapSource ToBitmap(Pixels p)
        {
            var b = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, p.Bgra, p.Stride);
            b.Freeze();
            return b;
        }

        /// <summary>A short text that changes when the fixes change (part of the thumbnail's name).</summary>
        public static string Stamp(EditRecipe e)
        {
            if (e == null || e.IsEmpty) return null;
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(EditRecipe)).WriteObject(ms, e);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>The photo's fingerprint for finding look-alikes (opened small, which is quick).</summary>
        public static ulong Look(MediaItem item)
        {
            var b = Decode(item.Path, item.Orientation, 96, item.Width, item.Height);
            return ImageOps.Look(ToPixels(b));
        }

        // ------------------------------------------------------------------ saving

        /// <summary>
        /// Saves pixels as a picture file, of the kind its name says (JPEG, PNG, TIFF, BMP or GIF). The details inside
        /// the original (date taken, camera, place) are kept where the kind of file allows, marked upright since the
        /// pixels are turned already; <paramref name="keepPlace"/> false leaves out where it was taken. Written to a new
        /// file next to the target first, so a failed save never leaves half a photo.
        /// </summary>
        public static void Save(Pixels pixels, string originalPath, string target, int quality, bool keepPlace)
        {
            var bitmap = ToBitmap(pixels);
            var ext = Path.GetExtension(target).ToLowerInvariant();
            var temp = target + ".kudupix-saving";
            try
            {
                Exception last = null;
                foreach (var withDetails in new[] { true, false })
                {
                    try
                    {
                        var metadata = withDetails ? Details(originalPath, ext, keepPlace) : null;
                        if (withDetails && metadata == null) continue;
                        var encoder = Encoder(ext, quality);
                        encoder.Frames.Add(BitmapFrame.Create(ext == ".jpg" || ext == ".jpeg" || ext == ".jpe" || ext == ".jfif" ? (BitmapSource)new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0) : bitmap, null, metadata, null));
                        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                            encoder.Save(fs);
                        last = null;
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
                    {
                        last = ex;
                    }
                }
                if (last != null) throw new IOException("The picture couldn't be saved: " + last.Message, last);
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
            }
        }

        private static BitmapEncoder Encoder(string ext, int quality)
        {
            switch (ext)
            {
                case ".png": return new PngBitmapEncoder();
                case ".bmp": return new BmpBitmapEncoder();
                case ".tif":
                case ".tiff": return new TiffBitmapEncoder();
                case ".gif": return new GifBitmapEncoder();
                default: return new JpegBitmapEncoder { QualityLevel = Math.Max(30, Math.Min(100, quality)) };
            }
        }

        /// <summary>The original's details, ready for the new file (JPEG to JPEG and TIFF to TIFF), or null.</summary>
        private static BitmapMetadata Details(string originalPath, string targetExt, bool keepPlace)
        {
            var sourceExt = Path.GetExtension(originalPath ?? string.Empty).ToLowerInvariant();
            bool Jpeg(string e) => e == ".jpg" || e == ".jpeg" || e == ".jpe" || e == ".jfif";
            bool Tiff(string e) => e == ".tif" || e == ".tiff";
            if (!(Jpeg(sourceExt) && Jpeg(targetExt)) && !(Tiff(sourceExt) && Tiff(targetExt))) return null;
            if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath)) return null;
            using (var fs = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                if (!(decoder.Frames[0].Metadata is BitmapMetadata m)) return null;
                var copy = m.Clone();
                var root = Jpeg(targetExt) ? "/app1/ifd" : "/ifd";
                if (copy.ContainsQuery(root + "/{ushort=274}")) copy.SetQuery(root + "/{ushort=274}", (ushort)1);
                if (!keepPlace && copy.ContainsQuery(root + "/gps")) copy.RemoveQuery(root + "/gps");
                // A small preview kept inside the original would show the photo before the fixes.
                if (Jpeg(targetExt) && copy.ContainsQuery("/app1/thumb")) copy.RemoveQuery("/app1/thumb");
                return copy;
            }
        }
    }
}
