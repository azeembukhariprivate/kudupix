# KUDUPIX

Your photos and videos, organised, on Windows: a fast grid of everything in the chosen folders (grouped by day),
stars, tags and albums, simple photo fixes that never spoil the original, duplicates, export and slideshows.
Part of the KUDU family of free, open-source (MIT) Windows apps by Azeem Bukhari.

## Working with Azeem (the owner)

- **Branches:** work on `main` and push to `main`. Ask before creating any other branch.
- **New features:** first say what you understood, and ask your doubts and suggestions, one question at a time.
  Only start building when Azeem says "build".
- **Look:** keep the app's current look. No redesigns unless asked.
- **About window credit:** "Made by Azeem, with Claude and ♥" (the heart in the accent colour). Keep it.
- **Commits:** author `Azeem Bukhari <12558389+azeembukhariprivate@users.noreply.github.com>`. Clear messages that say
  what changed and why. Don't put AI model names or versions in commits, code or docs.
- **Words:** the app's text, README and release notes use plain everyday words, not technical jargon.
- **Before pushing:** build and run the tests locally, and re-read the diff. After pushing, watch the GitHub Actions
  build until it's green; fix failures, never skip or disable a test to get green.

## How the app is built

- .NET Framework 4.8 WPF app. The screens are built in C# (no XAML compiler); the themes (`Themes/Light.xaml`,
  `Dark.xaml`, `Styles.xaml`) are XAML loaded at startup as embedded resources, using resource keys such as `Accent`,
  `Text`, `SurfaceBg`. Use the helpers in `Controls/Ui.cs` and existing keys rather than hard-coded colours.
- It builds with the plain .NET SDK on any OS (Microsoft.NETFramework.ReferenceAssemblies); it runs on Windows 10/11.
- Logic that doesn't need Windows lives in `Core/` and is tested by `tests/KUDUPIX.Tests` (a .NET 8 console program).
  The tests download real photos and a video from other open-source projects (pinned commits, SHA-256 checked) into
  `samples-cache`; never commit those files.
- `KUDUPIX.exe --self-test --samples <samples-cache>` drives the real app on Windows with its own data folder and
  settings (GitHub Actions runs it after the tests, keeping screenshots as a build download). Add a self-test step for
  new screens and features.
- Settings live in `HKEY_CURRENT_USER\Software\KUDUPIX`. The Inno Setup installer (`installer/KUDUPIX.iss`) installs per
  user, without administrator rights.

```
dotnet run --project tests/KUDUPIX.Tests -c Release
dotnet build src/KUDUPIX/KUDUPIX.csproj -c Release -o out/KUDUPIX
out\KUDUPIX\KUDUPIX.exe --self-test --samples tests\KUDUPIX.Tests\samples-cache        (Windows only)
```

## Notes

- Photos and videos are never changed, moved or deleted unless the person asks: "Save over the original" (asks every
  time and keeps the untouched photo in `Originals\` first, `Core/Duplicates.cs` `Originals`), and moving to the Recycle
  Bin (asks first). Keep it that way.
- Everything KUDUPIX keeps is in `%LocalAppData%\KUDUPIX` (`Core/Settings.cs` `AppPaths`): `library.bin` (`Core/Library.cs`:
  the items with the details read from inside each file, `PhotoInfo.cs` for EXIF, `VideoInfo.cs` for MP4/MOV), `userdata.json`
  (`Core/UserData.cs`: stars, tags, albums, fixes; a spare `.bak`), `Thumbs\` (`Views/Thumbs.cs`), `Originals\`.
- Fixes are an `EditRecipe` (`Core/Pixels.cs`): turn, red-eye, straighten, crop, auto-fix, brightness, contrast, warmth,
  colour, always applied to the untouched picture. Red-eye spots are kept on the turned photo (`ToTurned`).
- The library scan (`Library.Scan`) reads only new or changed files and follows moved/renamed ones (stars and tags move
  with them). `Views/Catalog.cs` scans in the background, watches the folders (FileSystemWatcher) and saves soon after
  changes.
- Duplicates (`Core/Duplicates.cs`): exact copies by SHA-256 (only for files of the same size) and look-alike photos by a
  64-bit fingerprint (`ImageOps.Look`); every photo in a group must fit with every other (shape, date), so an undated copy
  can't link two different photos. The best copy is kept (`Best`); the rest go to the Recycle Bin.
- The grid (`Views/PhotoGrid.cs`) is a virtualized list of rows (`GridRow`: a heading or a row of tiles), laid out again
  when the width or the picture size changes.
- The internet is only used for the update check and when the person clicks "Show the place on a map" (OpenStreetMap in
  the browser). Never add anything that sends data about the user.

## Shared code (kudu-common)

The files listed in `.kudu-common` (the look and controls in `Controls/`, `Core/Admin.cs`, `AppInfo.cs`, `Args.cs`,
`ThemeManager.cs`, `Unelevated.cs`, `UpdateChecker.cs`, `Views/Dialogs.cs`, `Views/Screenshots.cs`, ...) are copied from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common) and start with a comment saying so.
**Don't edit them here**: change them in kudu-common, then run `python sync.py ../kudupix` there and commit both repos.
The build fails when a copy differs from kudu-common. App-specific details go in `Core/AppDetails.cs`.

## Releasing

1. Raise `<Version>` in `src/KUDUPIX/KUDUPIX.csproj` (and `MyAppVersion` in `installer/KUDUPIX.iss`).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`, Windows) tests and builds the app. When no release exists yet for that
   version it creates the `v` tag and publishes the installer and portable zip on the Releases page, plus a copy named
   `KUDUPIX-setup.exe` that the README's download button links to (`releases/latest/download/KUDUPIX-setup.exe`).
   Pushes that keep the same version only build. Versions below 1.0 are built and tested, never released.
