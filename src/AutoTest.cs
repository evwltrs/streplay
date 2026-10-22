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
                    var metas = ReplayIndex.Load(Plugin.ReplayFolder.Value).List;
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
                case "perf": return PerfRun(F(a[1]), a.Length > 2 ? a[2] : "");
                case "togglerec": Recorder.Instance.ToggleRecording(); return null;
                case "fakefree": Disk.TestFree = F(a[1]) < 0f ? -1 : (long)(F(a[1]) * (1L << 30)); return null;
                case "failrec": Recorder.TestFailSample = true; return null;
                case "droparms": Recorder.TestDropArms = true; return null;
                case "weaponkill":
                {
                    // a non-Gun weapon's own kill path (Weapon.KillShockWave -> the game's kill counter), as a rifle kill runs it
                    var fpc = Settings.Instance != null ? Settings.Instance.localPlayer : null;
                    var wpn = Resources.FindObjectsOfTypeAll<LargeRaycastGun>().FirstOrDefault();
                    if (fpc == null || wpn == null) { Log("no local player or weapon"); return null; }
                    var keep = wpn.playerController;
                    wpn.playerController = fpc;
                    wpn.KillShockWave();
                    wpn.playerController = keep;
                    return null;
                }
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
                    else if (a.Length > 1 && a[1] == "settings") ReplayBrowser.Instance.TestSettingsTab();
                    else ReplayBrowser.Instance.TestHighlightsTab(a.Length > 1 && a[1] == "all");
                    return null;
                case "hlwatch": ReplayBrowser.Instance.TestWatchHighlight(int.Parse(a[1])); return WaitForReplay();
                case "nexttake": ScoreManager.Instance.TakeIndex++; Log($"take index {ScoreManager.Instance.TakeIndex}"); return null;
                case "browser": ReplayBrowser.Instance.TestSetOpen(a[1] == "on"); return null;
                case "menuui": MenuUi(); return null;
                case "openmenu": MenuController.Instance?.OpenGame(); return null;
                case "menubutton": ReplayBrowser.TestHideButton = a[1] == "off"; return null;
                case "slotdiag": Log(MenuSlot.Diag()); return null;
                case "menuhover": ReplayBrowser.TestHover = a[1] == "on"; return null;
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
        // Map of the main menu's visible buttons (for fitting Streplay's own button in): path, label, font, rect on
        // screen, sprite, and which game scripts drive it. Written to <ReplayFolder>\autotest\menuui.txt.
        static void MenuUi()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"screen {Screen.width}x{Screen.height}");
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || !c.isRootCanvas || !c.isActiveAndEnabled) continue;
                sb.AppendLine($"== canvas {Recorder.ScenePath(c.transform)} mode={c.renderMode} cam={(c.worldCamera ? c.worldCamera.name : "-")} scale={c.scaleFactor} order={c.sortingOrder}");
                foreach (var sel in c.GetComponentsInChildren<UnityEngine.UI.Selectable>(false))
                {
                    if (!sel.isActiveAndEnabled) continue;
                    var rt = (RectTransform)sel.transform;
                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    var cam = c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
                    Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                    var tmp = sel.GetComponentInChildren<TMPro.TMP_Text>(false);
                    var img = sel.GetComponent<UnityEngine.UI.Image>();
                    var scripts = string.Join(",", sel.GetComponents<MonoBehaviour>().Select(m => m.GetType().Name).Distinct());
                    sb.AppendLine($"  {sel.GetType().Name} {Recorder.ScenePath(sel.transform)}");
                    sb.AppendLine($"     screen x={a.x:0}-{b.x:0} y={Screen.height - b.y:0}-{Screen.height - a.y:0} (from top)  size={b.x - a.x:0}x{b.y - a.y:0}");
                    if (tmp != null) sb.AppendLine($"     text '{tmp.text}' font={tmp.font?.name} size={tmp.fontSize} color={tmp.color} style={tmp.fontStyle} align={tmp.alignment}");
                    if (img != null) sb.AppendLine($"     image sprite={img.sprite?.name} color={img.color} type={img.type}");
                    sb.AppendLine($"     scripts {scripts}");
                }
            }
            foreach (var cm in Camera.allCameras)
                sb.AppendLine($"  CAM {cm.name} depth={cm.depth} mask={cm.cullingMask:X8} rt={(cm.targetTexture ? cm.targetTexture.name + " " + cm.targetTexture.width + "x" + cm.targetTexture.height : "screen")} ortho={cm.orthographic} size={cm.orthographicSize} fov={cm.fieldOfView} rect={cm.rect} pixelRect={cm.pixelRect} pos={cm.transform.position} rot={cm.transform.eulerAngles}");
            foreach (var n in new[] { "lobbyname", "back_button", "play_button", "settings_button" })
            {
                var go = Resources.FindObjectsOfTypeAll<Renderer>().FirstOrDefault(x => x != null && x.gameObject.scene.IsValid() && x.name == n);
                if (go == null) continue;
                foreach (var cm in Camera.allCameras.Where(cm => (cm.cullingMask & (1 << go.gameObject.layer)) != 0))
                {
                    var b0 = cm.WorldToScreenPoint(go.bounds.min); var b1 = cm.WorldToScreenPoint(go.bounds.max);
                    sb.AppendLine($"  PROJ {n} via {cm.name}: ({b0.x:0},{b0.y:0},{b0.z:0.0}) - ({b1.x:0},{b1.y:0},{b1.z:0.0})");
                }
            }
            // every visible 3D part of the menu frame (the bar, the back arrow, the slots), with its box on screen
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid() || !r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) continue;
                string rpath = Recorder.ScenePath(r.transform);
                if (!rpath.StartsWith("---USER INTERFACE---", StringComparison.Ordinal)) continue;
                var cam = Camera.allCameras.Where(cm => (cm.cullingMask & (1 << r.gameObject.layer)) != 0).OrderByDescending(cm => cm.depth).FirstOrDefault();
                if (cam == null) continue;
                var bb = r.bounds;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    var c = bb.center + Vector3.Scale(bb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var sp = cam.WorldToScreenPoint(c);
                    x0 = Mathf.Min(x0, sp.x); x1 = Mathf.Max(x1, sp.x); y0 = Mathf.Min(y0, Screen.height - sp.y); y1 = Mathf.Max(y1, Screen.height - sp.y);
                }
                var mat = r.sharedMaterial;
                sb.AppendLine($"  R {rpath} [{r.GetType().Name}] cam={cam.name} layer={r.gameObject.layer} world x={bb.min.x:0}-{bb.max.x:0} y={bb.min.y:0}-{bb.max.y:0} z={bb.min.z:0}-{bb.max.z:0} lossy={r.transform.lossyScale.x:0.##} mat={(mat ? mat.name : "-")} tex={(mat && mat.mainTexture ? mat.mainTexture.name : "-")}");
            }
            // 3D menu buttons on the bar (ModelButtonHandler)
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || !mb.gameObject.scene.IsValid() || !mb.isActiveAndEnabled) continue;
                string n = mb.GetType().Name;
                if (n != "ModelButtonHandler" && n != "ModelButtonTween") continue;
                var cam = Camera.allCameras.FirstOrDefault(cm => (cm.cullingMask & (1 << mb.gameObject.layer)) != 0);
                var sp = cam != null ? cam.WorldToScreenPoint(mb.transform.position) : Vector3.zero;
                sb.AppendLine($"  3D {n} {Recorder.ScenePath(mb.transform)} layer={mb.gameObject.layer} cam={(cam ? cam.name : "-")} screen=({sp.x:0},{Screen.height - sp.y:0})");
            }
            string path = Path.Combine(Plugin.ReplayFolder.Value, "autotest", "menuui.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());
            Log("wrote " + path);
        }

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

        // frame times over a stretch, plus Streplay's own share of them and the GC activity
        static IEnumerator PerfRun(float secs, string label)
        {
            var dts = new System.Collections.Generic.List<float>();
            Perf.Reset();
            int gc0 = System.GC.CollectionCount(0);
            long alloc0 = System.GC.GetAllocatedBytesForCurrentThread();
            float end = Time.realtimeSinceStartup + secs;
            long heapGrowth = 0, lastMem = System.GC.GetTotalMemory(false);
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                dts.Add(Time.unscaledDeltaTime * 1000f);
                long mem = System.GC.GetTotalMemory(false);
                if (mem > lastMem) heapGrowth += mem - lastMem;
                lastMem = mem;
            }
            int n = dts.Count;
            float avg = dts.Average();
            dts.Sort();
            float p50 = dts[n / 2], p99 = dts[Mathf.Min(n - 1, (int)(n * 0.99f))], max = dts[n - 1];
            double sampleMs = Perf.Sample.Elapsed.TotalMilliseconds, rpcMs = Perf.Rpc.Elapsed.TotalMilliseconds, guiMs = Perf.Gui.Elapsed.TotalMilliseconds;
            long alloc = System.GC.GetAllocatedBytesForCurrentThread() - alloc0;
            Log(System.FormattableString.Invariant($"perf {label}: {n} frames in {secs:0}s, frame avg {avg:0.000} ms (fps {1000f / avg:0}), p50 {p50:0.000}, p99 {p99:0.000} (1% low fps {1000f / p99:0}), max {max:0.0}") +
                System.FormattableString.Invariant($" | recorder {sampleMs / n:0.0000} ms/frame over {Perf.Samples} samples ({(Perf.Samples > 0 ? sampleMs / Perf.Samples : 0):0.000} ms each)") +
                System.FormattableString.Invariant($" | rpc hooks {rpcMs / n:0.0000} ms/frame ({Perf.Rpcs} calls) | gui {guiMs / n:0.0000} ms/frame") +
                System.FormattableString.Invariant($" | gen0 GCs {System.GC.CollectionCount(0) - gc0}, main-thread alloc {alloc / 1024f / secs:0} KB/s") +
                " | " + (Recorder.Instance != null ? Recorder.Instance.TestTrackedSummary() : ""));
            if (Perf.Samples > 0)
            {
                long raw = Perf.Bytes.Sum();
                Log("perf " + label + " sections per sample: " + string.Join(", ", Enumerable.Range(0, Perf.Names.Length).Select(i =>
                    System.FormattableString.Invariant($"{Perf.Names[i]} {Perf.Sec[i].Elapsed.TotalMilliseconds / Perf.Samples:0.000} ms / {Perf.Bytes[i] / Perf.Samples} B / alloc {Perf.Alloc[i] / 1024f / secs:0.0} KB/s"))) +
                    System.FormattableString.Invariant($" | raw {raw / Perf.Samples} B/sample = {raw / 1024f / secs:0} KB/s before compression | gui alloc {Perf.GuiAlloc / 1024f / secs:0.0} KB/s | heap growth overall {heapGrowth / 1024f / secs:0} KB/s"));
            }
        }

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
