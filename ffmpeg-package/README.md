# STREPLAY FFMPEG

An optional add-on for **[STREPLAY](https://thunderstore.io/c/straftat/p/1nf/STREPLAY/)**. Install it, and STREPLAY renders replays straight to video files (MP4 with H.264/H.265, or MOV with ProRes 4444) instead of PNG frames. You don't need to set anything up: STREPLAY finds it automatically.

It doesn't add anything to the game itself: it's ffmpeg, plus the small part of STREPLAY that starts it.

## What's inside

- `ffmpeg.exe`: **FFmpeg 9.0.2**, the "essentials" Windows build by Gyan Doshi (gyan.dev), unmodified.
- `StreplayFfmpeg.dll`: STREPLAY's ffmpeg launcher. It starts ffmpeg for a render and feeds it the frames. It lives here rather than in STREPLAY itself, so STREPLAY doesn't start any programs on its own. Source: https://github.com/evwltrs/streplay (`ffmpeg-package/bridge`), under STREPLAY's license.
- `LICENSE.txt`: the GNU General Public License, version 3 (for ffmpeg).

## License and source code

FFmpeg is free software by the FFmpeg developers (https://ffmpeg.org). This build is compiled with `--enable-gpl --enable-version3`, so it is licensed under the **GNU GPL version 3** (see LICENSE.txt). It is distributed here as a separate, unmodified program. STREPLAY runs it as an external process and isn't linked with it.

Where to get the corresponding source code:

- FFmpeg 9.0.2 source: https://ffmpeg.org/releases/ (`ffmpeg-9.0.2.tar.xz`)
- The build's configuration and the sources of every library it includes (x264, x265, and the rest): https://www.gyan.dev/ffmpeg/builds/. The essentials build page lists each component and links to its source.

If any of these links stop working, ask the package author on the Thunderstore page for a copy of the source code. It will be provided for at least three years after this version was published.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. This package isn't affiliated with or endorsed by the FFmpeg project.
