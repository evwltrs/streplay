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

## Releasing

Every release ships both packages, so the latest release always has the current mod and the current ffmpeg add-on:

1. Bump the version in `thunderstore/manifest.json`, `streplay.csproj` and `src/Plugin.cs`, and add a `CHANGELOG.md` entry. Commit and push.
2. Build both zips (above) from that commit, with a clean working tree.
3. Publish them as a GitHub release on that commit:

   ```
   gh release create v<version> dist/1nf-STREPLAY-<version>.zip dist/1nf-STREPLAY_FFMPEG-<ffmpeg version>.zip --target <commit> --title "STREPLAY <version>" --notes-file <notes.md>
   ```

   Put the SHA-256 of each zip in the notes (`sha256sum dist/*.zip`).
4. Upload the same two zips to Thunderstore: the add-on only when its version changed, since Thunderstore doesn't accept the same version twice.
