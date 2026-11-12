<p align="center">
  <img src="docs/logo-512.png" alt="KUDUPIX logo" width="128">
</p>

<h1 align="center">KUDUPIX</h1>

<p align="center">Your photos and videos, organised: see everything on your PC in one place, give stars, tags and albums, fix photos without spoiling them, and clear out duplicates. Free and open source, for Windows.</p>

<p align="center">
  <a href="https://github.com/azeembukhariprivate/kudupix/releases/latest/download/KUDUPIX-setup.exe"><img src="https://img.shields.io/badge/Download-KUDUPIX%20for%20Windows-4f46e5?style=for-the-badge" alt="Download KUDUPIX for Windows"></a>
  <br>
  <sub>The installer for Windows 10 and 11, always the latest version · <a href="https://github.com/azeembukhariprivate/kudupix/releases/latest">portable zip and what's new</a></sub>
</p>

---

## Features

- **Everything in one place**: KUDUPIX looks in the folders you choose (it starts with your Pictures, Videos and OneDrive folders) and shows every photo and video in one fast grid, grouped by day, or by month. It keeps watching those folders, so new photos appear by themselves, and moved or renamed ones keep their stars and tags.
- **Photos and videos**: JPEG, PNG, GIF, BMP, TIFF, WebP and HEIC photos (HEIC with Windows' free HEIF extension), and MP4, MOV, AVI, WMV, MKV and other videos. Videos play right in KUDUPIX.
- **Stars, tags and albums**: give photos 1 to 5 stars (the number keys work too), tag them ("beach", "Mum"), and put them in albums. Albums only point to your photos: nothing is moved or copied.
- **Find anything**: search by name, folder, tag, album, camera, year or month. The details panel shows when each photo was taken, the camera, its size, and where it was taken (with a link to see the place on a map).
- **Fix photos without spoiling them**: auto-fix, turn, crop, straighten, red-eye, brightness, contrast, warmth and colour. Your fixes are kept in KUDUPIX and can be changed or undone any time; the file stays as it is until you save.
  - **Save a copy** makes a new file next to the photo, such as `Beach (edited).jpg`, keeping the date taken and the camera details.
  - **Save over the original** asks first, and keeps the untouched photo, so **Put the original back** can undo it later.
- **People**: KUDUPIX finds the faces in your photos by itself, in the background, and puts the same person's faces together. Name someone once and they're listed under *People*, with all their photos; new photos of them join by themselves, and when KUDUPIX isn't sure it asks ("Is this Mum?"). Fix mistakes easily: *Not this person*, give a face to someone else, *Not a face*, join two people who are the same, or hide people. In the viewer, *Show faces* puts a frame with the name round each face. Names work in search too. It all happens on your PC: no photo or face ever leaves it. Faces too small to tell who it is (in big group photos) are only named by hand. Photos only, not videos.
- **Duplicates**: finds copies of the same photo or video, including photos saved smaller or sent through messaging apps. The best copy of each is ticked to keep (you can change it); the others go to the Recycle Bin, so they can be brought back.
- **Export**: copies to share, made smaller if you like, with your fixes, and without where the photos were taken (you choose). Videos are copied as they are.
- **Slideshow and printing**: full-screen slideshows that fade from one photo to the next (F5), and printing a photo per page.
- **In your language**: English, Malayalam (മലയാളം), Hindi (हिन्दी), Spanish, French and Arabic (with the windows mirrored for right-to-left). KUDUPIX starts in the same language as Windows when it has it; choose another in Settings.
- **Never changes your files** unless you ask: everything KUDUPIX adds is kept in its own folder.
- Light and dark themes, following Windows.

## Download

**[Download the installer](https://github.com/azeembukhariprivate/kudupix/releases/latest/download/KUDUPIX-setup.exe)** (`KUDUPIX-setup.exe`, always the latest version), or get any of these from the [Releases page](https://github.com/azeembukhariprivate/kudupix/releases/latest):

- `KUDUPIX-x.y.z-setup.exe` installs KUDUPIX for your Windows account (no administrator rights needed to install).
- `KUDUPIX-x.y.z-portable.zip` runs without installing: unzip it anywhere and open `KUDUPIX.exe`.

Requires 64-bit Windows 10 or 11.

## Keys

| Key | What it does |
| --- | --- |
| Enter, double-click | Open the photo or video |
| Left, Right | Previous, next (in the grid and when viewing) |
| 1 to 5, 0 | Give stars, take them off |
| [ and ] | Turn left, turn right (when viewing) |
| Delete | Move to the Recycle Bin (asks first) |
| Ctrl+A | Select everything shown |
| Ctrl+F | Search |
| F5 | Slideshow |
| Esc | Back to the photos |

## Privacy

KUDUPIX works on your computer only, and your photos, videos and the faces in them never leave it. Finding people uses two small face models from the OpenCV project, which come with KUDUPIX and run on your PC. It uses the internet for two things: an optional check for a newer version on GitHub (it can be turned off in Settings), and, only when you click *Show the place on a map*, opening OpenStreetMap in your browser.

Everything KUDUPIX adds (the list of your photos, stars, tags, albums, fixes, the faces found and the names you gave them, small pictures for the grid, and the originals kept before *Save over the original*) is kept in `%LOCALAPPDATA%\KUDUPIX`. Settings are in `HKEY_CURRENT_USER\Software\KUDUPIX`. Uninstalling asks whether to remove that folder too; your photos and videos are never touched.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later). The screens are built in C# and the themes are loaded at startup, so the project builds with `dotnet` on any operating system; the app itself runs on Windows.

```
dotnet run --project tests/KUDUPIX.Tests
dotnet build src/KUDUPIX/KUDUPIX.csproj -c Release -o out/KUDUPIX
```

People needs the two face models in a `models` folder next to `KUDUPIX.exe`; the tests download them (`tests/KUDUPIX.Tests/samples-cache`) and the GitHub Actions workflow copies them in.

The tests use real photos and a real video from other open-source projects ([exif-samples](https://github.com/ianare/exif-samples) and [ExifTool](https://github.com/exiftool/exiftool)'s tests), downloaded at a fixed version and checked before use: reading when photos were taken, cameras, places and which way up they are, video lengths and sizes, the library and its scans (new, changed, moved and removed files), stars, tags and albums, search, every photo fix, finding duplicates, names for new files, the originals kept before saving over them, and People: finding faces in real photos (OpenCV's sample pictures, including a class photo), telling the same person from different people, grouping faces, naming, asking about unsure faces, and "not this person" and "not a face". `KUDUPIX.exe --self-test --samples <the tests' samples-cache folder>` drives the real app on Windows with its own data and settings: the library, thumbnails, stars, tags, albums and search, the viewer and every fix, saving a copy, saving over the original and putting it back, a video, duplicates, export, the slideshow, printing, the folders being watched, people (finding faces, naming a group, a new photo joining by itself, search by name, face frames in the viewer), and every window in both themes; it writes `self-test.log` and exits. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and compile `installer/KUDUPIX.iss`.

### Shared code

Some files (the look, dialogs, the update check, administrator helpers) are shared by the KUDU apps and come from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common); each starts with a comment saying so, and `.kudu-common`
says which version. Change them there, not here.

## Publishing a new version

1. Raise `<Version>` in `src/KUDUPIX/KUDUPIX.csproj` (and `MyAppVersion` in `installer/KUDUPIX.iss`).
2. Commit and push to `main`. GitHub Actions tests and builds it, and publishes the installer and portable zip on the Releases page.

## License

[MIT](LICENSE) © 2026 Azeem Bukhari. Made by Azeem, with Claude and ♥.
