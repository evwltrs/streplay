# Building STREPLAY

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer) and STRAFTAT installed. The game's own DLLs are referenced from your install and never committed.

1. If STRAFTAT isn't in Steam's default library (`C:\Program Files (x86)\Steam\steamapps\common\STRAFTAT`), copy `local.props.example` to `local.props` and set your game folder in it. `local.props` is git-ignored.
2. Build:

   ```
   dotnet build -c Release
   ```

   The build also copies `streplay.dll` into an r2modman profile's plugins folder (`StreplayPublic` by default; set `-p:R2Profile=<name>`, or `-p:PluginsDir=<folder>` to put it anywhere). Close the game first, or the copy is skipped.
3. Package for Thunderstore:

   ```
   dotnet build -c Release -t:ThunderstorePack
   ```

   This writes `dist\1nf-STREPLAY-<version>.zip`. The version in `thunderstore/manifest.json` and `src/Plugin.cs` must match, or it stops.

The optional ffmpeg add-on is packed with `powershell -File tools\pack-ffmpeg.ps1 -Ffmpeg <path to ffmpeg.exe>`. The icons are made from `docs/brand/streplay-logo.png` with `python tools/make-icons.py`.

Debug builds also include a scripted test harness (`src/AutoTest.cs`), which never goes into release packages.
