#if STREPLAY_AUTOTEST
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // Development aid: if <ReplayFolder>\autotest.txt exists when the main menu loads, run its
    // commands (one per line) and delete it. Screenshots land in <ReplayFolder>\autotest\.
    //   explore <map>            solo exploration session (records like a normal round)
    //   leave                    leave the session, back to the main menu
    //   wait <seconds>
    //   open latest|<file> [t]   open a replay
    //   seek <t> | play | pause | speed <index>
    //   cam original|free|pov <n>   (pov n is 1-based, like the number keys)
    //   move <dx> <dy> <dz>      move the free camera in camera space
    //   hud on|off | ui on|off
    //   shot <name>              screenshot
    //   soundlog on|off          log every sound / restored effect during playback
    //   soundstats               log how many recorded sounds were played
    //   localsound jump|ui|rpc|loop on|off   trigger the game's own local sound paths (in a session)
    //   shockwave | die          kill shockwave / the local player's death (in a session)
    //   seekevent <rpc> [dt]     seek to the first event with that RPC name, plus dt
    //   seekview [dt]            seek to the first recorded lens-distortion kick, plus dt
    //   render <start> <end> [fps] [blur] [format] [WxH] [shutter]
    //                            render a range and wait until the output is written; blur = on|off|<sub-frames>,
    //                            format = h264|h265|prores|png (settings are restored afterwards)
    //   renderasync ...          same as render, but the script carries on (e.g. wait 3, then rendercancel)
    //   rendercancel             cancel the running render, like Esc
    //   renderpanel on|off       show the render settings panel
    //   ffmpegpath <path>|none   set Render.FfmpegPath
    //   highlights [n]           wait for the highlight scan, log the top n across all matches
    //   hltab [all]              show the browser's Highlights tab (latest match, or all)
    //   hlwatch <n>              open the n-th best highlight (all matches) like the browser's Watch button
    //   slowmo on|off [speed] [hold]   Render.SlowMoKills (+ speed, hold seconds)
    //   hudpart <key> on|off     show/hide a HUD part (Crosshair, Hitmarkers, KillFeed, Ammo, Health, Speed, FpsPing, RoundText)
    //   cam chase|orbit|victim   the third-person angles
    //   cfg <Section.Key> <v>    set a config value (remember to put it back)
    //   flag                     what the marker key does (flags a kill made in the last few seconds)
    //   nexttake                 bump ScoreManager's take index (in an exploration session)
    //   browser on|off           open / close the replay browser
    //   eventsys                 log whether the game's UI input (EventSystem) is enabled
    //   screenrec <name> [fps] | screenrec stop   record the game's own screen (UI included) to autotest\<name>.mp4,
    //                            in real time, whatever window is in front
    //   stuckgrey                leave the game's post-process profiles grey and warped, as a round ending mid kill flash
    //                            does (replays must not inherit it)
    //   exit                     close the replay
    //   quit                     quit the game
    internal sealed class AutoTest : MonoBehaviour
    {
        bool _ran;

        void Update()
        {
            if (_ran || SceneManager.GetActiveScene().name != "MainMenu" || Time.unscaledTime < 5f) return;
            string file = Path.Combine(Plugin.ReplayFolder.Value, "autotest.txt");
            if (!File.Exists(file)) return;
            _ran = true;
            string[] lines = File.ReadAllLines(file);
            File.Delete(file);
            Application.runInBackground = true;
            StartCoroutine(Run(lines));
        }

        static void Log(string s) => Plugin.Log.LogInfo("[autotest] " + s);

        IEnumerator Run(string[] lines)
        {
            string shots = Path.Combine(Plugin.ReplayFolder.Value, "autotest");
            Directory.CreateDirectory(shots);
            yield return new WaitForSecondsRealtime(2f);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                Log(line);
                IEnumerator step = null;
                try
                {
                    step = Exec(a, shots);
                }
                catch (Exception e)
                {
                    Log($"FAILED: {e}");
                }
                if (step != null) yield return step;
            }
            Log("done");
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        IEnumerator Exec(string[] a, string shots)
        {
            var rp = ReplayPlayer.Instance;
            switch (a[0])
            {
                case "wait":
                    return Wait(F(a[1]));
                case "explore":
                    SteamLobby.Instance.EnterExplorationMap(a[1]);
                    return null;
                case "leave":
                    SteamLobby.Instance.LeaveLobby();
                    return WaitForMenu();
                case "open":
                {
                    var metas = ReplayMeta.LoadAll(Plugin.ReplayFolder.Value);
                    var m = a[1] == "latest" ? metas.FirstOrDefault() : metas.FirstOrDefault(x => x.ReplayFile.StartsWith(a[1], StringComparison.OrdinalIgnoreCase));
                    if (m == null) { Log("no such replay"); return null; }
                    rp.Open(m, a.Length > 2 ? F(a[2]) : 0f);
                    return WaitForReplay();
                }
                case "seek": rp.Seek(F(a[1])); return null;
                case "play": rp.TestSetPlaying(true); return null;
                case "pause": rp.TestSetPlaying(false); return null;
                case "speed": rp.TestSetSpeed(int.Parse(a[1])); return null;
                case "cam": rp.TestSetCam(a[1], a.Length > 2 ? int.Parse(a[2]) : 1); return null;
                case "move": rp.TestMoveFree(new Vector3(F(a[1]), F(a[2]), F(a[3]))); return null;
                case "hud": rp.TestSetHud(a[1] == "on"); return null;
                case "ui": rp.TestSetUi(a[1] == "on"); return null;
                case "shot": return Shot(Path.Combine(shots, a[1] + ".png"));
                case "exit": rp.Exit(); return WaitForMenu();
                case "quit": StopRec(); Application.Quit(); return null;
                case "screenrec": if (a[1] == "stop") StopRec(); else StartRec(Path.Combine(shots, a[1] + ".mp4"), a.Length > 2 ? int.Parse(a[2]) : 60); return null;
                case "camnear": rp.TestCamNear(int.Parse(a[1]), a.Length > 2 ? F(a[2]) : 1.5f); return null;
                case "helddiag": rp.TestHeldDiag(); return null;
                case "fakehit":
                {
                    // spawn the game's own hitmarker the way Gun does on a hit
                    var weapon = Resources.FindObjectsOfTypeAll<Weapon>().FirstOrDefault(x => x.hitMarker != null);
                    if (weapon == null || Crosshair.Instance == null || PauseManager.Instance == null) { Log("no hitmarker source"); return null; }
                    var marker = Instantiate(weapon.hitMarker, Crosshair.Instance.transform.position, Quaternion.identity, PauseManager.Instance.transform);
                    DG.Tweening.ShortcutExtensions.DOPunchScale(marker.transform, a.Length > 1 && a[1] == "head" ? new Vector3(2.5f, 2.5f, 2.5f) : Vector3.one, 0.3f, 8, 2f);
                    if (a.Length > 1 && a[1] == "head") marker.GetComponent<UnityEngine.UI.Image>().color = Color.red;
                    Destroy(marker, 0.3f);
                    return null;
                }
                case "soundlog": ReplayAudio.LogEach = a[1] == "on"; return null;
                case "seekevent": Log(rp.TestSeekEvent(a[1], a.Length > 2 ? F(a[2]) : 0f)); return null;
                case "die":
                {
                    // the local player's own death, through the game's normal Explode path (ragdoll + death sting)
                    var fpc = Settings.Instance != null ? Settings.Instance.localPlayer : null;
                    var health = fpc != null ? fpc.GetComponent<PlayerHealth>() : null;
                    if (health == null) { Log("no local player"); return null; }
                    var tr = fpc.transform;
                    health.Explode(false, true, "Head_Col", tr.forward, 20f, tr.position + Vector3.up * 1.5f);
                    health.DisablePlayerObjectWhenKilled(); // what every weapon does next
                    return null;
                }
                case "seekview": Log(rp.TestSeekView(a.Length > 1 ? F(a[1]) : 0.05f)); return null;
                case "shockwave":
                {
                    var fpc = Settings.Instance != null ? Settings.Instance.localPlayer : null;
                    typeof(FirstPersonController).GetMethod("KillShockWave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(fpc, null);
                    return null;
                }
                case "soundstats": Log(rp.TestAudioStats); return null;
                case "stuckgrey": StuckGrey(); return null;
                case "render": return Render(rp, a);
                case "flag": Recorder.Instance.TestMarkerKey(); return null;
                case "cfg":
                {
                    var parts = a[1].Split('.');
                    var def = new BepInEx.Configuration.ConfigDefinition(parts[0], parts[1]);
                    if (Plugin.Instance.Config.ContainsKey(def)) { var e = Plugin.Instance.Config[def]; e.SetSerializedValue(a[2]); Log($"{a[1]} = {e.BoxedValue}"); }
                    else Log($"no config {a[1]}");
                    return null;
                }
                case "clip": rp.TestClip(F(a[1]), F(a[2])); return null;
                case "zoom": rp.TestZoom(F(a[1]), a.Length > 2 ? F(a[2]) : 0f); return null;
                case "help": rp.TestHelp(a[1] == "on"); return null;
                case "slowmo":
                    Plugin.SlowMoKills.Value = a[1] == "on";
                    if (a.Length > 2) Plugin.SlowMoSpeed.Value = F(a[2]);
                    if (a.Length > 3) Plugin.SlowMoHold.Value = F(a[3]);
                    return null;
                case "hudpart":
                {
                    int i = Array.FindIndex(HudReplay.Parts, p => p.key.Equals(a[1], StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) Plugin.HudShow[i].Value = a[2] == "on"; else Log("no such HUD part");
                    return null;
                }
                case "vcdiag": Log(rp.TestVictimDiag()); return null;
                case "hudprobe": if (a.Length > 1) HudReplay.ProbeOnRender = a[1] == "on"; else rp.TestProbe(); return null;
                case "highlights": return WaitHighlights(a.Length > 1 ? int.Parse(a[1]) : 10);
                case "hltab":
                    if (a.Length > 1 && a[1] == "rounds") ReplayBrowser.Instance.TestRoundsTab();
                    else ReplayBrowser.Instance.TestHighlightsTab(a.Length > 1 && a[1] == "all");
                    return null;
                case "hlwatch": ReplayBrowser.Instance.TestWatchHighlight(int.Parse(a[1])); return WaitForReplay();
                case "nexttake": ScoreManager.Instance.TakeIndex++; Log($"take index {ScoreManager.Instance.TakeIndex}"); return null;
                case "browser": ReplayBrowser.Instance.TestSetOpen(a[1] == "on"); return null;
                case "eventsys": Log("EventSystems: " + string.Join(", ", FindObjectsOfType<UnityEngine.EventSystems.EventSystem>(true).Select(e => $"{e.name} enabled={e.enabled}"))); return null;
                case "renderasync": { var r = Render(rp, a); StartCoroutine(r); return null; }
                case "rendercancel": rp.TestCancelRender(); return null;
                case "renderpanel": rp.TestRenderPanel(a[1] == "on"); return null;
                case "ffmpegpath": Plugin.FfmpegPath.Value = a.Length > 1 && a[1] != "none" ? string.Join(" ", a.Skip(1)) : ""; Log($"ffmpeg: {Ffmpeg.Find() ?? "not found"}"); return null;
                case "localsound": LocalSound(a[1], a.Length > 2 ? a[2] : ""); return null;
                default: Log("unknown command"); return null;
            }
        }

        // Trigger the game's own local-only sound paths on the local player:
        //   localsound jump        FirstPersonController.JumpClip (PlayOneShot outside any RPC)
        //   localsound ui          SoundManager.PlaySoundWithPitch (2D)
        //   localsound loop on|off the fall-wind loop (Play/Stop on a networked source)
        static void LocalSound(string what, string arg)
        {
            var fpc = Settings.Instance != null ? Settings.Instance.localPlayer : null;
            if (fpc == null) { Log("no local player"); return; }
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            switch (what)
            {
                case "jump":
                    typeof(FirstPersonController).GetMethod("JumpClip", flags)?.Invoke(fpc, null);
                    break;
                case "ui":
                {
                    var clips = typeof(FirstPersonController).GetField("concreteClips", flags)?.GetValue(fpc) as AudioClip[];
                    var clip = clips != null && clips.Length > 0 ? clips[0] : null;
                    if (SoundManager.Instance == null) Log("no SoundManager");
                    else if (clip == null) Log("no clip");
                    else SoundManager.Instance.PlaySoundWithPitch(clip, 1.3f);
                    break;
                }
                case "rpc":
                    // footstep through the game's own RunLocally server+observers RPC pair
                    typeof(FirstPersonController).GetMethod("PlaySoundServer", flags)?.Invoke(fpc, new object[] { 1 });
                    break;
                case "loop":
                {
                    var src = typeof(FirstPersonController).GetField("fallAudio", flags)?.GetValue(fpc) as AudioSource;
                    if (src == null) { Log("no fallAudio"); break; }
                    if (arg == "off") src.Stop();
                    else { src.volume = 1f; src.Play(); }
                    Log($"fallAudio clip={(src.clip != null ? src.clip.name : "null")} loop={src.loop} playing={src.isPlaying}");
                    break;
                }
            }
        }

        static IEnumerator Render(ReplayPlayer rp, string[] a)
        {
            var saved = (Plugin.RenderFps.Value, Plugin.RenderMotionBlur.Value, Plugin.RenderSubFrames.Value, Plugin.RenderFormatSetting.Value,
                         Plugin.RenderWidth.Value, Plugin.RenderHeight.Value, Plugin.RenderShutter.Value);
            if (a.Length > 3) Plugin.RenderFps.Value = int.Parse(a[3]);
            if (a.Length > 4)
            {
                if (a[4] == "off") Plugin.RenderMotionBlur.Value = false;
                else if (a[4] == "on") Plugin.RenderMotionBlur.Value = true;
                else { int n = int.Parse(a[4]); Plugin.RenderMotionBlur.Value = n > 1; if (n > 1) Plugin.RenderSubFrames.Value = n; }
            }
            if (a.Length > 5)
                Plugin.RenderFormatSetting.Value = a[5] == "png" ? RenderFormat.PngSequence : a[5] == "h265" ? RenderFormat.H265 : a[5] == "prores" ? RenderFormat.ProRes4444 : RenderFormat.H264;
            if (a.Length > 6)
            {
                var wh = a[6].Split('x');
                Plugin.RenderWidth.Value = int.Parse(wh[0]);
                Plugin.RenderHeight.Value = int.Parse(wh[1]);
            }
            if (a.Length > 7) Plugin.RenderShutter.Value = F(a[7]);
            rp.TestRender(F(a[1]), F(a[2]));
            yield return null;
            var began = Time.realtimeSinceStartup;
            while (rp.RenderBusy) yield return null;
            yield return null; // the finished message reaches the player on its next Update
            Log($"render finished in {Time.realtimeSinceStartup - began:0.0}s: {rp.TestRenderSummary}");
            (Plugin.RenderFps.Value, Plugin.RenderMotionBlur.Value, Plugin.RenderSubFrames.Value, Plugin.RenderFormatSetting.Value,
             Plugin.RenderWidth.Value, Plugin.RenderHeight.Value, Plugin.RenderShutter.Value) = saved;
        }

        static IEnumerator WaitHighlights(int top)
        {
            yield return null;
            float until = Time.realtimeSinceStartup + 300f;
            while (Highlights.Scanning && Time.realtimeSinceStartup < until) yield return null;
            Log("top highlights (all matches):\n" + ReplayBrowser.Instance.TestTopHighlights(top));
        }

        static IEnumerator Wait(float s) { yield return new WaitForSecondsRealtime(s); }

        static IEnumerator WaitForMenu()
        {
            float until = Time.unscaledTime + 30f;
            yield return new WaitForSecondsRealtime(1f);
            while (Time.unscaledTime < until && SceneManager.GetActiveScene().name != "MainMenu") yield return null;
            yield return new WaitForSecondsRealtime(2f);
        }

        static IEnumerator WaitForReplay()
        {
            float until = Time.unscaledTime + 60f;
            while (Time.unscaledTime < until && !ReplayPlayer.Instance.TestIsReady) yield return null;
            yield return new WaitForSecondsRealtime(1f);
        }

        // screenrec: the back buffer (game + IMGUI) read back each frame and piped to ffmpeg at a steady real-time rate;
        // a slow frame is repeated, so the video keeps the wall clock. Unlike a desktop capture it doesn't need the game
        // focused or fullscreen.
        FfmpegSink _rec;
        RenderTexture _recRT;
        int _recW, _recH, _recIndex;

        void StartRec(string path, int fps)
        {
            StopRec();
            string ffmpeg = Ffmpeg.Find();
            if (ffmpeg == null) { Log("screenrec: ffmpeg not found"); return; }
            _recRT = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            _recW = Screen.width & ~1; _recH = Screen.height & ~1; _recIndex = 0;
            _rec = new FfmpegSink(ffmpeg, path, RenderFormat.H264, _recW, _recH, fps, 14, null, "veryfast");
            Log($"screenrec {_recW}x{_recH} @ {fps} -> {path}");
            StartCoroutine(RecLoop(_rec, fps));
        }

        IEnumerator RecLoop(FfmpegSink sink, int fps)
        {
            var eof = new WaitForEndOfFrame();
            float t0 = Time.realtimeSinceStartup;
            int next = 0;
            while (_rec == sink)
            {
                yield return eof;
                if (_rec != sink) break;
                int due = (int)((Time.realtimeSinceStartup - t0) * fps);
                if (due < next) continue;
                int copies = due - next + 1;
                next = due + 1;
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_recRT);
                int sw = _recRT.width, w = _recW, h = _recH;
                AsyncGPUReadback.Request(_recRT, 0, TextureFormat.RGBA32, r =>
                {
                    if (r.hasError || _rec != sink) return;
                    var src = r.GetData<byte>();
                    for (int c = 0; c < copies; c++)
                    {
                        var buf = sink.Rent();
                        // the capture is top-down already; the sink expects Unity's usual bottom-up rows, so flip here
                        for (int y = 0; y < h; y++) Unity.Collections.NativeArray<byte>.Copy(src, y * sw * 4, buf, (h - 1 - y) * w * 4, w * 4);
                        sink.Submit(buf, _recIndex++);
                    }
                });
            }
        }

        void StopRec()
        {
            if (_rec == null) return;
            AsyncGPUReadback.WaitAllRequests();
            var sink = _rec;
            _rec = null;
            Log($"screenrec stop ({_recIndex} frames)");
            sink.Finish(false, s => Log("screenrec written " + s.OutputPath + (s.Error != null ? " error " + s.Error : "")));
            if (_recRT != null) { _recRT.Release(); Destroy(_recRT); _recRT = null; }
        }

        static void StuckGrey()
        {
            int n = 0;
            foreach (var prof in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessProfile>())
            {
                if (prof.TryGetSettings(out UnityEngine.Rendering.PostProcessing.ColorGrading g)) { g.saturation.value = -100f; n++; }
                if (prof.TryGetSettings(out UnityEngine.Rendering.PostProcessing.LensDistortion l)) l.intensity.value = -60f;
            }
            Log($"stuckgrey: {n} profiles left grey");
        }

        static IEnumerator Shot(string path)
        {
            yield return null; // let camera/HUD changes made by the previous command take effect
            yield return null;
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }
}
#endif
