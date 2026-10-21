using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ComputerysModdingUtilities;
using HarmonyLib;
using UnityEngine;

// The recorder only observes what this client already sees (like screen recording), so it
// does not change matchmaking compatibility with players who don't have it installed.
[assembly: StraftatMod(isVanillaCompatible: true)]

namespace Streplay
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "1nf.Streplay";
        public const string Name = "Streplay";
        public const string Version = "1.0.0";  // keep in sync with Streplay.csproj and thunderstore/manifest.json

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // General
        internal static ConfigEntry<string> ReplayFolder;
        internal static ConfigEntry<float> MaxFolderSizeGB;
        // Recording
        internal static ConfigEntry<bool> RecordEnabled;
        internal static ConfigEntry<float> SampleRate;
        internal static ConfigEntry<bool> ShowRecIndicator;
        internal static ConfigEntry<KeyboardShortcut> MarkerKey;
        internal static ConfigEntry<bool> AutoMarkKills;
        internal static ConfigEntry<bool> AutoMarkDeaths;
        internal static ConfigEntry<float> MarkerKillGrace;
        // Highlights
        internal static ConfigEntry<float> HighlightChain;
        internal static ConfigEntry<string> HighlightWeaponWeights;
        internal static ConfigEntry<float> HighlightPreroll;
        internal static ConfigEntry<float> HighlightPostroll;
        internal static ConfigEntry<string> NonMapScenes;
        internal static ConfigEntry<string> IgnoredSoundClips;
        // Playback
        internal static ConfigEntry<KeyboardShortcut> BrowserKey;
        internal static ConfigEntry<float> MarkerPreroll;
        internal static ConfigEntry<bool> ReplayEffects;
        internal static ConfigEntry<bool> ReplaySounds;
        internal static ConfigEntry<string> EffectAllowlist;
        internal static ConfigEntry<string> SeekRestoreEffects;
        internal static ConfigEntry<string> EffectDenylist;
        internal static ConfigEntry<string> KeepEnabledScripts;
        internal static ConfigEntry<float> FreeCamSpeed;
        internal static ConfigEntry<float> MouseSensitivity;
        internal static ConfigEntry<float> DefaultFov;
        // Render
        internal static ConfigEntry<int> RenderWidth;
        internal static ConfigEntry<int> RenderHeight;
        internal static ConfigEntry<int> RenderFps;
        internal static ConfigEntry<bool> RenderMotionBlur;
        internal static ConfigEntry<int> RenderSubFrames;
        internal static ConfigEntry<float> RenderShutter;
        internal static ConfigEntry<bool> RenderAdaptiveSubFrames;
        internal static ConfigEntry<BlurWeighting> RenderBlurWeighting;
        internal static ConfigEntry<int> RenderMaxSubFrames;
        internal static ConfigEntry<int> RenderSubFramesPerUpdate;
        internal static ConfigEntry<RenderFormat> RenderFormatSetting;
        internal static ConfigEntry<int> RenderCrf;
        internal static ConfigEntry<bool> RenderAudio;
        internal static ConfigEntry<bool> RenderMuteSoundPass;
        internal static ConfigEntry<string> RenderFolder;
        internal static ConfigEntry<string> FfmpegPath;
        internal static ConfigEntry<bool> SlowMoKills;
        internal static ConfigEntry<bool>[] HudShow;
        internal static ConfigEntry<float> SlowMoSpeed;
        internal static ConfigEntry<float> SlowMoHold;
        internal static ConfigEntry<float> SlowMoEase;
        // Debug
        internal static ConfigEntry<KeyboardShortcut> DumpKey;

        internal static GameObject Host;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Streplay");
            ReplayFolder = Config.Bind("General", "ReplayFolder", defaultFolder, "Where replays (.strp + .strp.json) are saved.");
            MaxFolderSizeGB = Config.Bind("General", "MaxFolderSizeGB", 20f,
                new ConfigDescription("When the replays in ReplayFolder take more than this, the oldest rounds without any of your markers are deleted " +
                    "after each round (rounds with markers you placed are always kept; renders are never touched). 0 = never delete.",
                    new AcceptableValueRange<float>(0f, 10000f)));

            RecordEnabled = Config.Bind("Recording", "Enabled", true, "Automatically record every round you play or spectate.");
            SampleRate = Config.Bind("Recording", "SampleRate", 60f,
                new ConfigDescription("Snapshots per second. Higher = smoother slow-mo, bigger files.", new AcceptableValueRange<float>(10f, 144f)));
            ShowRecIndicator = Config.Bind("Recording", "ShowRecIndicator", true, "Show a small REC dot while recording.");
            MarkerKey = Config.Bind("Recording", "MarkerKey", new KeyboardShortcut(KeyCode.F8), "Drop a marker on the current moment.");
            AutoMarkKills = Config.Bind("Recording", "AutoMarkKills", true, "Automatically drop a marker when you get a kill.");
            AutoMarkDeaths = Config.Bind("Recording", "AutoMarkDeaths", true, "Automatically drop a marker when you die.");
            MarkerKillGrace = Config.Bind("Recording", "MarkerKillGrace", 8f,
                new ConfigDescription("Seconds after a kill during which the marker key flags that kill (even if the next take has started) instead of the current moment. 0 = off.",
                    new AcceptableValueRange<float>(0f, 30f)));
            NonMapScenes = Config.Bind("Recording", "NonMapScenes", "MainMenu,EmptyScene,VictoryScene,LoadingScene",
                "Scenes that are never recorded (comma separated).");
            IgnoredSoundClips = Config.Bind("Recording", "IgnoredSoundClips", "VOICE",
                "Sounds whose clip name contains any of these are not recorded (comma separated). VOICE is the proximity voice chat.");

            HighlightWeaponWeights = Config.Bind("Highlights", "WeaponWeights", "Bayshore=0.5",
                "Score multipliers per weapon, comma separated (Weapon=factor). Below 1 pushes a weapon's kills down the list, above 1 up. " +
                "The default tones down the Bayshore, whose launch makes airborne long-range no-scopes routine. Weapons named here also don't get " +
                "the airborne / movement-speed bonus when the factor is below 1.");
            HighlightChain = Config.Bind("Highlights", "MultiKillWindow", 4f,
                new ConfigDescription("Kills at most this many seconds apart count as one multi-kill highlight.", new AcceptableValueRange<float>(0.5f, 15f)));
            HighlightPreroll = Config.Bind("Highlights", "ClipBefore", 3f,
                new ConfigDescription("Seconds of lead-in before the first kill of a highlight clip.", new AcceptableValueRange<float>(0f, 20f)));
            HighlightPostroll = Config.Bind("Highlights", "ClipAfter", 2f,
                new ConfigDescription("Seconds after the last kill of a highlight clip.", new AcceptableValueRange<float>(0f, 20f)));

            BrowserKey = Config.Bind("Playback", "BrowserKey", new KeyboardShortcut(KeyCode.F10), "Open the replay browser (main menu only).");
            MarkerPreroll = Config.Bind("Playback", "MarkerPreroll", 5f, "Seconds before a marker to start playback when jumping to it.");
            ReplayEffects = Config.Bind("Playback", "ReplayEffects", true, "Re-play recorded effects (muzzle flashes, tracers, impacts, explosions, ragdolls).");
            ReplaySounds = Config.Bind("Playback", "ReplaySounds", true,
                "Re-play recorded sounds that don't come from effect RPCs (hit dings, your own jump/landing/slide, menu and HUD sounds).");
            EffectAllowlist = Config.Bind("Playback", "EffectAllowlist",
                "FX,Effect,Sound,Sfx,SFX,Clip,Trail,Explosion,Explode,BreakGlass,Impact,Blood,Muzzle,Beam,Spark",
                "RPCs whose name contains any of these are re-played during playback (comma separated).");
            SeekRestoreEffects = Config.Bind("Playback", "SeekRestoreEffects", "ExplodeForAll,SpawnVFX:20",
                "Effect RPCs whose results stay in the world (ragdolls, bullet holes). After a seek they're rebuilt as they'd be at that moment. " +
                "Name[:max age in seconds], comma separated.");
            EffectDenylist = Config.Bind("Playback", "EffectDenylist", "Despawn,ReturnMenu,Kill,Score,Round,Scene,Server",
                "RPCs whose name contains any of these are never re-played, even if allowlisted (comma separated).");
            KeepEnabledScripts = Config.Bind("Playback", "KeepEnabledScripts", "",
                "Game script type names to leave running in the replay map (comma separated). Everything else from the game's code is paused.");
            FreeCamSpeed = Config.Bind("Playback", "FreeCamSpeed", 8f, "Free camera base speed (units/second).");
            MouseSensitivity = Config.Bind("Playback", "MouseSensitivity", 2f, "Free camera mouse sensitivity.");
            DefaultFov = Config.Bind("Playback", "DefaultFov", 90f, "Free camera starting field of view.");
            RenderWidth = Config.Bind("Render", "Width", 1920,
                new ConfigDescription("Output width in pixels (independent of the game window).", new AcceptableValueRange<int>(16, 8192)));
            RenderHeight = Config.Bind("Render", "Height", 1080,
                new ConfigDescription("Output height in pixels.", new AcceptableValueRange<int>(16, 8192)));
            RenderFps = Config.Bind("Render", "Fps", 60,
                new ConfigDescription("Output frames per second of video.", new AcceptableValueRange<int>(1, 1000)));
            RenderMotionBlur = Config.Bind("Render", "MotionBlur", true,
                "Film-style motion blur: each output frame is the average of several sub-frames spread over the shutter.");
            RenderSubFrames = Config.Bind("Render", "SubFrames", 16,
                new ConfigDescription("Sub-frames averaged per output frame when motion blur is on. More = smoother blur, slower render.", new AcceptableValueRange<int>(2, 256)));
            RenderShutter = Config.Bind("Render", "ShutterAngle", 360f,
                new ConfigDescription("Shutter angle in degrees (180 = film look, 360 = blur across the whole frame).", new AcceptableValueRange<float>(1f, 360f)));
            RenderBlurWeighting = Config.Bind("Render", "BlurWeighting", BlurWeighting.Gaussian,
                "Equal: every sub-frame counts the same (a film camera, hard-edged streaks). Gaussian: the copies fade in and out " +
                "towards the ends of the shutter, the smooth frame-blended look of HLAE, RSMB or blur (use it with a 360 degree shutter).");
            RenderAdaptiveSubFrames = Config.Bind("Render", "AdaptiveSubFrames", true,
                "With motion blur on, frames where the camera moves fast (flicks) get extra sub-frames so the blur is a smooth smear " +
                "instead of separate copies of the image. SubFrames is then the minimum.");
            RenderSubFramesPerUpdate = Config.Bind("Render", "SubFramesPerUpdate", 4,
                new ConfigDescription("Motion blur sub-frames drawn per game frame. Above 1, the extra ones re-pose the replay and redraw the cameras " +
                    "without a whole engine frame (much faster); particles and physics then step once per game frame instead of every sub-frame. 1 = the old way.",
                    new AcceptableValueRange<int>(1, 32)));
            RenderMaxSubFrames = Config.Bind("Render", "MaxSubFrames", 128,
                new ConfigDescription("Most sub-frames a single frame can get with AdaptiveSubFrames.", new AcceptableValueRange<int>(2, 512)));
            RenderFormatSetting = Config.Bind("Render", "Format", RenderFormat.H264,
                "H264 / H265 (.mp4, high bitrate), ProRes4444 (.mov) or PngSequence. Video formats need ffmpeg; without it PNG frames are written.");
            RenderCrf = Config.Bind("Render", "Crf", 14,
                new ConfigDescription("H.264/H.265 quality (constant rate factor; lower = better and bigger, 0 = lossless).", new AcceptableValueRange<int>(0, 51)));
            RenderAudio = Config.Bind("Render", "Audio", true,
                "Write the replay's sound for the rendered range (audio.wav next to PNG frames, muxed into video files).");
            RenderMuteSoundPass = Config.Bind("Render", "MuteSoundPass", true,
                "Silence the speakers while a render records the sound in real time (it's still recorded).");
            RenderFolder = Config.Bind("Render", "OutputFolder", "",
                "Where renders are written. Empty = <ReplayFolder>\\renders.");
            SlowMoKills = Config.Bind("Render", "SlowMoKills", false,
                "Ease into slow motion around each of your kills in the rendered range (and back out). The clip gets longer by the slowed part.");
            SlowMoSpeed = Config.Bind("Render", "SlowMoSpeed", 0.3f,
                new ConfigDescription("Playback speed at the kill when SlowMoKills is on (relative to the render speed).", new AcceptableValueRange<float>(0.05f, 1f)));
            SlowMoHold = Config.Bind("Render", "SlowMoHold", 0.6f,
                new ConfigDescription("Replay seconds around the kill that stay fully slowed.", new AcceptableValueRange<float>(0f, 5f)));
            SlowMoEase = Config.Bind("Render", "SlowMoEase", 0.35f,
                new ConfigDescription("Replay seconds spent easing into and out of the slow motion.", new AcceptableValueRange<float>(0.05f, 3f)));
            HudShow = new ConfigEntry<bool>[HudReplay.Parts.Length];
            for (int i = 0; i < HudReplay.Parts.Length; i++)
                HudShow[i] = Config.Bind("HUD", "Show" + HudReplay.Parts[i].key, HudReplay.PartDefault(HudReplay.Parts[i].key),
                    $"Show the game's {HudReplay.Parts[i].label.ToLowerInvariant()} in My view and in renders.");
            FfmpegPath = Config.Bind("Render", "FfmpegPath", "",
                "ffmpeg.exe (or the folder containing it). Empty = look in <ReplayFolder>\\ffmpeg, the BepInEx plugin folders (an ffmpeg package), then PATH.");

            DumpKey = Config.Bind("Debug", "DumpKey", new KeyboardShortcut(KeyCode.F11),
                "Write a diagnostic dump (the scene and every networked object) to the replay folder, to attach to bug reports.");

            EnsureReplayFolder();

            // Each patch group on its own: a game update that breaks one only loses that feature, not the plugin.
            var harmony = new Harmony(Guid);
            TryPatch("effect capture", () => RpcCapture.Install(harmony));
            TryPatch("sound capture", () => AudioCapture.Install(harmony));
            TryPatch("pause menu suspension", () => harmony.PatchAll(typeof(GameSuspend)));
            TryPatch("kill detection", () => harmony.PatchAll(typeof(KillHook)));
            TryPatch("replay log noise filter", () => harmony.PatchAll(typeof(ReplayNoise)));

            // Own host object so we survive any scene changes regardless of what happens to BepInEx's manager.
            Host = new GameObject("Streplay");
            Host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(Host);
            Host.AddComponent<Recorder>();
            Host.AddComponent<ReplayPlayer>();
            Host.AddComponent<ReplayBrowser>();
            Host.AddComponent<DebugDump>();
#if STREPLAY_AUTOTEST
            Host.AddComponent<AutoTest>();
#endif

            Log.LogInfo($"{Name} {Version} loaded. Replays: {ReplayFolder.Value}");
        }

        static void TryPatch(string what, Action patch)
        {
            try { patch(); }
            catch (Exception e) { Log.LogError($"Could not hook {what} (a game update?); that part of Streplay is off: {e.Message}"); }
        }

        // The configured folder (quotes trimmed) if it can be created; otherwise %LOCALAPPDATA%\Streplay, so a bad path,
        // an offline OneDrive or a blocked Documents folder doesn't stop recording altogether.
        static void EnsureReplayFolder()
        {
            string wanted = (ReplayFolder.Value ?? "").Trim().Trim('"');
            try
            {
                if (wanted.Length == 0) throw new ArgumentException("empty path");
                wanted = Path.GetFullPath(wanted);
                Directory.CreateDirectory(wanted);
                if (wanted != ReplayFolder.Value) ReplayFolder.Value = wanted;
                return;
            }
            catch (Exception e)
            {
                string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streplay");
                Log.LogError($"Can't use the replay folder '{wanted}' ({e.Message}). Using {fallback} instead; change General.ReplayFolder to fix it.");
                try { Directory.CreateDirectory(fallback); ReplayFolder.Value = fallback; }
                catch (Exception e2) { Log.LogError($"Can't create {fallback} either: {e2.Message}. Recording will fail."); }
            }
        }

        internal static bool ContainsAny(string s, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return false;
            foreach (var part in csv.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0 && s.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}
