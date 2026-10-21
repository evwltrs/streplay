# Streplay

**Replays for STRAFTAT.** Every round you play is recorded automatically. Watch it back afterwards inside the real map: from your own eyes (with your HUD, arms, hitmarkers and sounds), from a free camera, or from any player. Then render clips up to 4K at any frame rate, with real motion blur, for your edits.

## Features

- **Automatic recording.** Every round is saved to `Documents\Streplay`. A small `● REC` shows while it records.
- **Markers.** Press **F8** to mark a moment. Press F8 right after a kill to flag that kill. Your kills and deaths are marked automatically.
- **Replay browser.** Click **Replays (F10)** on the main menu. Rounds are grouped by match, and markers jump straight to the moment.
- **Highlights.** Your kills across every recording, ranked best first (flicks, headshots, quickscopes, multi-kills, long range…). Filter them, then **Watch** one to open the replay with the clip already set around it.
- **My view.** What your screen showed: the same camera, your first-person arms and weapon, the game's HUD (crosshair, ammo, health, kill feed, hitmarkers), post-processing and sounds.
- **Other cameras.**
  - Free camera.
  - Any player's point of view.
  - Chase, orbit and victim angles.
- **Timeline.**
  - Scrub, pause and step frame by frame.
  - Slow motion down to 0.05x.
  - Set in/out points for a clip.
- **Render.** Any resolution (independent of your window), 30 to 240 fps, film-style motion blur, H.264/H.265/ProRes video or a PNG sequence, with the replay's sound.

Streplay only records what your own game already shows you. It is marked as vanilla-compatible, so you can still join normal lobbies.

## Install

Install with a mod manager: **r2modman** or the Thunderstore Mod Manager. BepInEx is installed for you. Start the game with **Start modded**.

To make **video** files (MP4/MOV), Streplay needs **ffmpeg**, which isn't included in the download:

1. Download a Windows build of ffmpeg, for example "ffmpeg-release-essentials" from gyan.dev.
2. Put `ffmpeg.exe` in `Documents\Streplay\ffmpeg\`, or anywhere on your PATH, or set `FfmpegPath` in the config.

Without ffmpeg, renders are saved as PNG frames plus a `.wav` file, which any video editor can import.

## Using it

1. Play. Every round is recorded. Press **F8** for anything worth keeping.
2. On the main menu, click **Replays** (or press **F10**). Pick a round and **Watch**, click a marker, or open the **Highlights** tab.
3. Set the clip with **I** / **O** (or **C** for 2.5 s either side of now). Press **R**, pick the size, frame rate and blur, then **Start render**.
4. Renders are saved in `Documents\Streplay\renders`. **Esc** cancels a render and keeps what was rendered so far.

### Shortcuts while watching

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | 5 s back / forward (Shift: 1 s) |
| `,` / `.` | One frame back / forward |
| `[` / `]` | Previous / next marker |
| `-` / `=` | Slower / faster |
| M | Add a marker |
| I / O | Clip start / end |
| C | Clip 2.5 s either side of now |
| Shift + drag | Draw a clip range on the timeline |
| Mouse wheel | Zoom the timeline (double-click to show all) |
| V | My view |
| F | Free camera |
| Right mouse | Look around; WASD/QE to fly, Shift fast, Ctrl slow, Z/X zoom |
| 1–9 / Tab | A player's POV |
| G | Smooth POV |
| R | Render panel |
| U | Game HUD on/off |
| H | Hide all replay UI |
| ? | All shortcuts |
| Esc | Back to the menu |

## Settings

Every setting is in r2modman → **Config editor** → `1nf.Streplay.cfg`. The useful ones:

- `General.ReplayFolder`: where replays are saved.
- `General.MaxFolderSizeGB`: when the folder grows past this, the oldest rounds **without** markers are deleted. Rounds with your markers are kept.
- `Recording.Enabled`: turn automatic recording off.
- `Recording.MarkerKey`: the marker key (default F8).
- `Playback.BrowserKey`: the replay browser key (default F10).
- `Render.*`: default size, fps, motion blur, format and output folder. You can also change these in the render panel.
- `HUD.Show…`: which parts of the game's HUD appear in My view and in renders.

## Tips

- **Smoother slow motion:** set `Recording.SampleRate` to 120. Recordings get about twice as big.
- **Motion blur makes renders slower:** more blur sub-frames means a longer render. 16 is a good default; Adaptive adds extra sub-frames only for fast flicks.
- **Same moment, different angles:** switch the camera and render again. Each render uses the camera you're in.

## Known limits

- Other players' POV uses their synced aim. Only your own view has the exact camera, arms and HUD.
- Ragdolls and debris are re-simulated, so they won't land exactly as they did live.
- Sounds play at normal pitch in slow motion.
- Proximity voice chat is never recorded.
- A game update can change things the replays rely on. Older recordings may then show missing objects.

## Problems?

Press **F11** in a match or a replay to write a `dump_*.txt` to the replay folder. Send it along with `BepInEx\LogOutput.log` from your r2modman profile (Settings → Browse profile folder) when you report a bug.
