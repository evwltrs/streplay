# Streplay FFmpeg

An optional add-on for **Streplay**. Install it, and Streplay renders replays straight to video files (MP4 with H.264/H.265, or MOV with ProRes 4444) instead of PNG frames. You don't need to set anything up: Streplay finds it automatically.

This package contains only **ffmpeg.exe**. It doesn't add anything to the game itself.

## What's inside

- `ffmpeg.exe`: **FFmpeg 9.0.2**, the "essentials" Windows build by Gyan Doshi (gyan.dev), unmodified.
- `LICENSE.txt`: the GNU General Public License, version 3.

## License and source code

FFmpeg is free software by the FFmpeg developers (https://ffmpeg.org). This build is compiled with `--enable-gpl --enable-version3`, so it is licensed under the **GNU GPL version 3** (see LICENSE.txt). It is distributed here as a separate, unmodified program. Streplay runs it as an external process and isn't linked with it.

Where to get the corresponding source code:

- FFmpeg 9.0.2 source: https://ffmpeg.org/releases/ (`ffmpeg-9.0.2.tar.xz`)
- The build's configuration and the sources of every library it includes (x264, x265, and the rest): https://www.gyan.dev/ffmpeg/builds/. The essentials build page lists each component and links to its source.

If any of these links stop working, ask the package author on the Thunderstore page for a copy of the source code. It will be provided for at least three years after this version was published.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. This package isn't affiliated with or endorsed by the FFmpeg project.
