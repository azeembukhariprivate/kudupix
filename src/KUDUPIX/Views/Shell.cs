using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Kudupix.Views
{
    /// <summary>Moving files to the Recycle Bin, as File Explorer does (so they can be brought back).</summary>
    public static class Recycler
    {
        /// <summary>Moves files to the Recycle Bin without asking again (KUDUPIX has asked). True when all of it worked.</summary>
        public static bool Recycle(IList<string> paths)
        {
            if (paths.Count == 0) return true;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", paths.Select(p => p.StartsWith(@"\\?\") ? p.Substring(4) : p)) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR | FOF_WANTNUKEWARNING
            };
            try
            {
                var code = SHFileOperation(ref op);
                return code == 0 && !op.fAnyOperationsAborted;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOCONFIRMMKDIR = 0x0200, FOF_NOERRORUI = 0x0400;
        // If the Recycle Bin can't take something (too big, a network drive), Windows asks before deleting it for good.
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }

    /// <summary>
    /// Windows' own thumbnails (the ones File Explorer shows): used for videos, and for pictures WPF can't open itself
    /// (when Windows has a codec for them, such as HEIC with the HEIF extension).
    /// </summary>
    public static class ShellThumbs
    {
        /// <summary>A thumbnail about <paramref name="size"/> pixels, or null. Must be called on an STA thread.</summary>
        public static BitmapSource Get(string path, int size)
        {
            IShellItemImageFactory factory = null;
            var bitmap = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                // THUMBNAILONLY: no file-type icon when there's no picture; BIGGERSIZEOK: speed.
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // Windows' thumbnails keep their transparency in a way WPF reads as see-through: show it solid.
                var solid = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                solid.Freeze();
                return solid;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is InvalidCastException || ex is UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        private const int SIIGBF_BIGGERSIZEOK = 0x01, SIIGBF_THUMBNAILONLY = 0x08;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
