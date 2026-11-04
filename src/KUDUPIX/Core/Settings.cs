using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Kudupix.Core
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Where KUDUPIX keeps its own files: %LocalAppData%\KUDUPIX (another folder for the self-test).</summary>
    public static class AppPaths
    {
        private static string _root;

        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _root = Path.Combine(string.IsNullOrEmpty(local) ? Path.GetTempPath() : local, "KUDUPIX");
                }
                return _root;
            }
            set => _root = value;
        }

        public static string LibraryFile => Path.Combine(Root, "library.bin");
        public static string UserDataFile => Path.Combine(Root, "userdata.json");
        public static string FacesFile => Path.Combine(Root, "faces.bin");
        public static string PeopleFile => Path.Combine(Root, "people.json");

        /// <summary>The face models, in a "models" folder next to KUDUPIX.exe (put there by the installer).</summary>
        public static string Models => Path.Combine(Path.GetDirectoryName(typeof(AppPaths).Assembly.Location) ?? ".", "models");
        public static string Thumbs => Path.Combine(Root, "Thumbs");
        public static string Originals => Path.Combine(Root, "Originals");
        public static string Temp => Path.Combine(Root, "Temp");

        /// <summary>The name of a photo's small picture in the Thumbs folder: changes when the file or its fixes change.</summary>
        public static string ThumbKey(MediaItem item, string editStamp)
        {
            var text = item.Path.ToLowerInvariant() + "|" + item.Size + "|" + item.Modified + "|" + (editStamp ?? string.Empty);
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).Substring(0, 24).ToLowerInvariant();
        }
    }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        private const string KeyPath = @"Software\KUDUPIX";

        /// <summary>For the self-test: settings are kept in memory and the user's are left alone.</summary>
        public static Dictionary<string, string> InMemory { get; set; }

        private static string Get(string name, string fallback)
        {
            if (InMemory != null) return InMemory.TryGetValue(name, out var v) ? v : fallback;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return key?.GetValue(name) as string ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static void Set(string name, string value)
        {
            if (InMemory != null)
            {
                InMemory[name] = value ?? string.Empty;
                return;
            }
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key?.SetValue(name, value ?? string.Empty);
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");
        private static int GetInt(string name, int fallback) => int.TryParse(Get(name, fallback.ToString()), out var v) ? v : fallback;
        private static void SetInt(string name, int value) => Set(name, value.ToString());

        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        /// <summary>The folders KUDUPIX looks in. Empty until the first start chooses the usual ones.</summary>
        public static List<string> Folders
        {
            get => Get("Folders", string.Empty).Split('\n').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            set => Set("Folders", string.Join("\n", Roots.Clean(value)));
        }

        public static bool FoldersChosen
        {
            get => GetBool("FoldersChosen", false);
            set => SetBool("FoldersChosen", value);
        }

        /// <summary>The usual places for photos and videos: Pictures, Videos and OneDrive's Pictures (those that exist).</summary>
        public static List<string> UsualFolders()
        {
            var list = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(oneDrive))
            {
                list.Add(Path.Combine(oneDrive, "Pictures"));
                list.Add(Path.Combine(oneDrive, "Videos"));
            }
            return Roots.Clean(list.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)));
        }

        /// <summary>The picture size in the grid, in pixels (the slider at the bottom).</summary>
        public static int TileSize
        {
            get => Math.Max(90, Math.Min(320, GetInt("TileSize", 160)));
            set => SetInt("TileSize", value);
        }

        public static bool ShowDetails
        {
            get => GetBool("ShowDetails", true);
            set => SetBool("ShowDetails", value);
        }

        public static int SlideSeconds
        {
            get => Math.Max(2, Math.Min(30, GetInt("SlideSeconds", 4)));
            set => SetInt("SlideSeconds", value);
        }

        /// <summary>Export: the longest side in pixels (0 = the size it is).</summary>
        public static int ExportSize
        {
            get => GetInt("ExportSize", 1600);
            set => SetInt("ExportSize", value);
        }

        public static int ExportQuality
        {
            get => Math.Max(50, Math.Min(100, GetInt("ExportQuality", 88)));
            set => SetInt("ExportQuality", value);
        }

        /// <summary>Export: leave out where the photo was taken (on by default, for sharing).</summary>
        public static bool ExportWithoutPlace
        {
            get => GetBool("ExportWithoutPlace", true);
            set => SetBool("ExportWithoutPlace", value);
        }

        public static string ExportFolder
        {
            get => Get("ExportFolder", string.Empty);
            set => Set("ExportFolder", value);
        }
    }
}
