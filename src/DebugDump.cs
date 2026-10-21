using System;
using System.IO;
using System.Linq;
using System.Text;
using FishNet;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // F11: write the hierarchy + components of every networked object (live) or the whole replay
    // scene (during playback) to a text file, to help diagnose objects that don't replay right.
    internal sealed class DebugDump : MonoBehaviour
    {
        void Update()
        {
            if (!Plugin.DumpKey.Value.IsDown()) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Scene: {SceneManager.GetActiveScene().name}  replay={ReplayPlayer.Active}  time={DateTime.Now}");
                var cm = InstanceFinder.ClientManager;
                if (!ReplayPlayer.Active && cm != null && cm.Started)
                {
                    DumpCanvases(sb);
                    var local = Settings.Instance != null ? Settings.Instance.localPlayer : null;
                    var setup = local != null ? local.GetComponent<PlayerSetup>() : null;
                    if (setup != null && HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "cameras")?.GetValue(setup) is Camera[] cams)
                        for (int i = 0; i < cams.Length; i++)
                            if (cams[i] != null)
                                sb.AppendLine($"player camera[{i}] {cams[i].name} enabled={cams[i].enabled} fov={cams[i].fieldOfView} depth={cams[i].depth} " +
                                              $"clear={cams[i].clearFlags} mask={cams[i].cullingMask:X8} near={cams[i].nearClipPlane} path={Recorder.ScenePath(cams[i].transform)}");
                    foreach (var nob in cm.Objects.Spawned.Values.Where(n => n != null && n.ParentNetworkObject == null).OrderBy(n => n.ObjectId))
                    {
                        sb.AppendLine($"=== #{nob.ObjectId} {nob.name} prefab={nob.SpawnableCollectionId}:{nob.PrefabId} scene={Net.SceneId(nob)} owner={nob.IsOwner}");
                        Dump(sb, nob.transform, 1);
                    }
                }
                else
                {
                    foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                        Dump(sb, root.transform, 0);
                }
                string path = Path.Combine(Plugin.ReplayFolder.Value, $"dump_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"Wrote {path}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Dump failed: {e}");
            }
        }

        static void DumpCanvases(StringBuilder sb)
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || !c.isRootCanvas) continue;
                int graphics = c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length;
                int groups = c.GetComponentsInChildren<CanvasGroup>(true).Length;
                sb.AppendLine($"canvas {Recorder.ScenePath(c.transform)} scene={c.gameObject.scene.name} active={c.isActiveAndEnabled} " +
                              $"mode={c.renderMode} cam={(c.worldCamera != null ? c.worldCamera.name : "-")} order={c.sortingOrder} graphics={graphics} groups={groups}");
            }
            sb.AppendLine();
        }

        static void Dump(StringBuilder sb, Transform t, int depth)
        {
            if (depth > 14) return;
            var comps = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c is Behaviour b && !b.enabled ? c.GetType().Name + "(off)" : c.GetType().Name);
            sb.Append(' ', depth * 2).Append(t.gameObject.activeSelf ? "" : "[inactive] ").Append(t.name)
              .Append(" layer=").Append(t.gameObject.layer).Append("  ").AppendLine(string.Join(", ", comps));
            for (int i = 0; i < t.childCount; i++) Dump(sb, t.GetChild(i), depth + 1);
        }
    }
}
