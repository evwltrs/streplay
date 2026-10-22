using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet;
using FishNet.Object;
using HarmonyLib;
using UnityEngine;

namespace Streplay
{
    // A visual stand-in for a recorded networked object: either a copy of its prefab (with all
    // game logic switched off) or the matching object already present in the replay map.
    internal sealed class Puppet
    {
        public ObjectInfo Info;
        public GameObject Go;
        public bool Owned;                 // we instantiated it (vs. an object that belongs to the map)
        public Transform[] Children;       // aligned with Info.ChildPaths
        public Animator[] Anims;           // aligned with Info.AnimPaths
        public AnimatorControllerParameter[][] AnimParams;
        public PlayerHealth Health;
        public PlayerPickup Pickup;
        public GameObject HatRoot;         // where hat and cigarette are worn (hidden in this player's own view)
        public GameObject HatPrefab;       // thrown off by the death ragdoll
        public bool Seen;

        // local player's first-person arms (v2 recordings)
        public Transform[] Vm = Array.Empty<Transform>();
        public Behaviour[] VmDrivers = Array.Empty<Behaviour>();  // animators/rigs fighting the recorded pose
        public readonly List<GameObject> VmActivated = new List<GameObject>();
        public bool VmShown;
        public Transform[] Bones = Array.Empty<Transform>();   // v3 skeleton
        public ObjState CurS, CurN;
        public float CurAlpha;

        // latest applied player state (used by cameras)
        public Vector3 PovPos;
        public Rigidbody[] Bodies = Array.Empty<Rigidbody>(); // items: kept kinematic while the replay places them
        public Quaternion PovRot = Quaternion.identity;
        public int HeldRight = -1, HeldLeft = -1;
        public bool IsLocalPlayer => Info.Player != null && Info.Player.IsLocal;
        public string DisplayName => System.Text.RegularExpressions.Regex.Replace(
            Info.Player != null && !string.IsNullOrEmpty(Info.Player.Name) ? Info.Player.Name : Info.Name, "<.*?>", "");

        public void Bind()
        {
            var root = Go.transform;
            Children = Info.ChildPaths.Select(p => string.IsNullOrEmpty(p) ? root : root.Find(p)).ToArray();
            Anims = Info.AnimPaths.Select(p => (string.IsNullOrEmpty(p) ? root : root.Find(p))?.GetComponent<Animator>()).ToArray();
            AnimParams = Anims.Select(a => a != null && a.runtimeAnimatorController != null ? a.parameters : null).ToArray();
            foreach (var a in Anims)
            {
                if (a == null) continue;
                a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                a.applyRootMotion = false;
                a.speed = 0f;
            }
            Vm = Info.VmPaths.Select(p => string.IsNullOrEmpty(p) ? root : root.Find(p)).ToArray();
            if (Vm.Length > 0 && Vm[0] != null)
                VmDrivers = Vm[0].GetComponentsInChildren<Animator>(true).Cast<Behaviour>()
                    .Concat(Vm[0].GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().Namespace != null
                        && m.GetType().Namespace.StartsWith("UnityEngine.Animations.Rigging", StringComparison.Ordinal)))
                    .ToArray();
            Bones = Info.BonePaths.Select(p => root.Find(p)).ToArray();
            if (Bones.Length > 0)
            {
                // the recorded skeleton replaces animation + IK for the body
                var firstBone = Bones.FirstOrDefault(b => b != null);
                var vmRoot = Vm.Length > 0 ? Vm[0] : null;
                for (int i = 0; i < Anims.Length; i++)
                {
                    var a = Anims[i];
                    if (a == null || firstBone == null || !firstBone.IsChildOf(a.transform)) continue;
                    if (vmRoot != null && a.transform.IsChildOf(vmRoot)) continue;
                    a.enabled = false;
                }
            }
            Health = Go.GetComponent<PlayerHealth>();
            Pickup = Go.GetComponent<PlayerPickup>();
            HandPoints = FindHandPoints(Pickup);
        }

        // Where other players see a held item (right, left). PlayerPickup only fills
        // pickupPositionOnline in OnStartClient, which never runs on a replay copy, so rebuild it
        // the same way: the ItemPosition children of its OnlinePositions object.
        public Transform[] HandPoints = Array.Empty<Transform>();
        static readonly System.Reflection.FieldInfo F_OnlinePositions = HarmonyLib.AccessTools.Field(typeof(PlayerPickup), "OnlinePositions");

        static Transform[] FindHandPoints(PlayerPickup pickup)
        {
            if (pickup == null) return Array.Empty<Transform>();
            if (pickup.pickupPositionOnline != null && pickup.pickupPositionOnline.Length > 0) return pickup.pickupPositionOnline;
            if (!(F_OnlinePositions?.GetValue(pickup) is GameObject online) || online == null) return Array.Empty<Transform>();
            var points = online.GetComponentsInChildren<ItemPosition>();
            if (points.Length == 0) points = online.GetComponentsInChildren<ItemPosition>(true);
            // Each point is glued every frame to a transform on the hand bone by SetItemPosition (a game
            // script, so it's off on puppets). Use that hand transform directly.
            return points.Select(p =>
            {
                var follow = p.GetComponent<SetItemPosition>();
                return follow != null && follow.reference != null ? follow.reference.transform : p.transform;
            }).ToArray();
        }
    }

    internal static class PuppetFactory
    {
        // Assemblies whose MonoBehaviours are game logic (movement, input, networking, managers).
        static readonly HashSet<string> LogicAssemblies = new HashSet<string>
        {
            "Assembly-CSharp", "Assembly-CSharp-firstpass", "FishNet.Runtime", "Heathen.Core", "Heathen.Steamworks",
            "Heathen.PhysKit", "LambdaTheDev.NetworkAudioSync", "LambdaTheDev.NetworkAudioSync.FishNet", "GrabbitAssembly",
            "com.rlabrecque.steamworks.net",
        };

        static readonly FieldInfo F_DisableForEnemy = AccessTools.Field(typeof(PlayerSetup), "componentsToDisableForEnemy");
        static readonly FieldInfo F_GoDisableForEnemy = AccessTools.Field(typeof(PlayerSetup), "gameObjectToDisableForEnemy");
        static readonly FieldInfo F_SetupGraphics = AccessTools.Field(typeof(PlayerSetup), "graphics");
        static readonly FieldInfo F_SetupCameras = AccessTools.Field(typeof(PlayerSetup), "cameras");
        static readonly FieldInfo F_KillCam = AccessTools.Field(typeof(PlayerHealth), "killCamScript");
        internal static readonly FieldInfo F_SpawnedRagdoll = AccessTools.Field(typeof(PlayerHealth), "spawnedRagdoll");

        // Networked objects that are part of the session, not the scene. A copy of one of these
        // hooks into SteamLobby/GameManager statics (e.g. ClientInstance.OnDisable leaves the lobby
        // and reloads the menu), so they are never recorded as visible or turned into puppets.
        static readonly HashSet<string> SessionTypes = new HashSet<string>
        {
            "ClientInstance", "PlayerManager", "GameManager", "ScoreManager", "SceneMotor", "RoundManager",
            "SteamLobby", "LobbyController", "MatchLogs", "TimerManager", "SpawnerManager", "PauseManager",
            "CompetitiveScore", "ProgressManager", "MapsManager", "SoundManager",
        };

        internal static bool IsSessionObject(GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && SessionTypes.Contains(mb.GetType().Name)) return true;
            return false;
        }

        internal static bool IsLogicScript(MonoBehaviour mb)
        {
            if (mb == null) return false;
            var type = mb.GetType();
            if (!LogicAssemblies.Contains(type.Assembly.GetName().Name)) return false;
            if (type.Namespace != null && type.Namespace.StartsWith("UnityStandardAssets", StringComparison.Ordinal)) return false;
            if (Plugin.KeepEnabledScripts.Value.Split(',').Any(s => s.Trim() == type.Name)) return false;
            return true;
        }

        // Switch off everything that would make an object think or move on its own. Renderers,
        // animators, particles, audio and Unity package scripts (e.g. animation rigging) stay on.
        internal static void Neutralize(GameObject go, Camera keepCamera)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (IsLogicScript(mb)) mb.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
            }
            foreach (var col in go.GetComponentsInChildren<CharacterController>(true)) col.enabled = false;
            foreach (var cam in go.GetComponentsInChildren<Camera>(true))
                if (cam != keepCamera && cam.targetTexture == null) cam.enabled = false;
            foreach (var l in go.GetComponentsInChildren<AudioListener>(true))
                if (keepCamera == null || l.gameObject != keepCamera.gameObject) l.enabled = false;
        }

        // Make a player prefab look the way other players see it (third-person body, no viewmodel).
        internal static void SetupAsRemotePlayer(GameObject go, KillCam sceneKillCam)
        {
            var setup = go.GetComponent<PlayerSetup>();
            if (setup != null)
            {
                if (F_DisableForEnemy?.GetValue(setup) is Behaviour[] comps)
                    foreach (var c in comps) if (c != null) c.enabled = false;
                if (F_GoDisableForEnemy?.GetValue(setup) is GameObject[] gos)
                    foreach (var g in gos) if (g != null) g.SetActive(false);
                if (F_SetupCameras?.GetValue(setup) is Camera[] cams)
                    foreach (var c in cams) if (c != null) c.enabled = false;
                if (F_SetupGraphics?.GetValue(setup) is GameObject graphics && graphics != null)
                    graphics.transform.localPosition = new Vector3(0f, -0.2f, 0f);
            }
            var health = go.GetComponent<PlayerHealth>();
            if (health != null && sceneKillCam != null && F_KillCam != null && F_KillCam.FieldType.IsInstanceOfType(sceneKillCam))
                F_KillCam.SetValue(health, sceneKillCam);
        }

        internal static readonly HashSet<string> LoggedCosmetics = new HashSet<string>();

        // Suit, cigarette and hat, the way PlayerSetup's dress RPC applies them on every client.
        internal static void ApplyCosmetics(Puppet p)
        {
            var c = p.Info.Cosmetics;
            var setup = p.Go.GetComponent<PlayerSetup>();
            var cm = CosmeticsManager.Instance;
            if (c == null || setup == null) return;
            if (cm == null) { Plugin.Log.LogWarning("CosmeticsManager not available; player skins are skipped"); return; }
            var applied = new List<string>();
            if (c.Mat >= 0 && cm.mats != null && c.Mat < cm.mats.Length && cm.mats[c.Mat] != null)
            {
                setup.mat = c.Mat;
                setup.normalMat = cm.mats[c.Mat];
                foreach (var go in setup.meshesToChange ?? Array.Empty<GameObject>())
                {
                    var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
                    if (smr != null) smr.material = cm.mats[c.Mat];
                }
                if (cm.fparmsMats != null && c.Mat < cm.fparmsMats.Length && cm.fparmsMats[c.Mat] != null)
                    foreach (var go in setup.fpArmsSuits ?? Array.Empty<GameObject>())
                    {
                        var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
                        if (smr != null) smr.material = cm.fparmsMats[c.Mat];
                    }
                applied.Add("suit " + cm.mats[c.Mat].name);
            }
            var anchor = setup.hatToWearPosition;
            if (anchor == null) return;
            p.HatRoot = anchor.gameObject;
            if (c.Cig >= 0 && cm.cigs != null && c.Cig < cm.cigs.Length && cm.cigs[c.Cig] != null)
            {
                Wear(cm.cigs[c.Cig], anchor, -1);
                applied.Add("cig " + cm.cigs[c.Cig].name);
            }
            if (!string.IsNullOrEmpty(c.Hat))
            {
                var hat = cm.hats?.FirstOrDefault(h => h != null && h.name == c.Hat);
                if (hat != null)
                {
                    Wear(hat, anchor, 18);
                    p.HatPrefab = hat;
                    applied.Add("hat " + hat.name);
                }
                else Plugin.Log.LogWarning($"Hat '{c.Hat}' not found in CosmeticsManager");
            }
            string line = $"Cosmetics for {p.DisplayName}: {(applied.Count > 0 ? string.Join(", ", applied) : "none")}";
            if (LoggedCosmetics.Add(line)) Plugin.Log.LogDebug(line); // once per player, not per life
        }

        static readonly FieldInfo F_DisableForMe = AccessTools.Field(typeof(PlayerSetup), "gameObjectsToDisableForMe");
        static readonly int P_OutlineWidth = Shader.PropertyToID("_ASEOutlineWidth");
        static readonly int P_OutlineColor = Shader.PropertyToID("_ASEOutlineColor");
        static MaterialPropertyBlock _block;

        // Player outlines, the way PlayerSetup's dress RPC draws them: a line in the player's outline colour around
        // the body (thinner around the head), sized by the outline width setting. Recordings don't keep teams, so the
        // recording player gets the team colour and everyone else the enemy colour (right for 1v1 and free-for-all).
        // Off sets the width to 0, which is how a player without outlines looks.
        internal static void ApplyOutline(Puppet p, bool on)
        {
            var setup = p.Go != null ? p.Go.GetComponent<PlayerSetup>() : null;
            if (setup == null || !(F_DisableForMe?.GetValue(setup) is GameObject[] parts)) return;
            float mult = PlayerPrefs.HasKey("OutlineWidthMultiplier")
                ? Mathf.Clamp01(PlayerPrefs.GetFloat("OutlineWidthMultiplier")) : OutlineHandler.OutlineWidthMultiplier;
            var color = p.IsLocalPlayer ? PrefColor("TeamOutlineColor", OutlineHandler.TeamColor) : PrefColor("EnemyOutlineColor", OutlineHandler.EnemyColor);
            _block ??= new MaterialPropertyBlock();
            foreach (var go in parts)
            {
                var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr == null) continue;
                smr.GetPropertyBlock(_block, 0);
                _block.SetFloat(P_OutlineWidth, on ? (go.name == "SM_Aboubi_Head00" ? 0.02f : 0.04f) * mult : 0f);
                _block.SetColor(P_OutlineColor, color);
                smr.SetPropertyBlock(_block, 0);
            }
        }

        // OutlineHandler only loads the player's colours when the settings menu first opens, so read them like it does
        static Color PrefColor(string key, Color fallback) =>
            PlayerPrefs.HasKey(key) && ColorUtility.TryParseHtmlString("#" + PlayerPrefs.GetString(key), out var c) ? c : fallback;

        internal static GameObject Wear(GameObject prefab, Transform anchor, int layer)
        {
            var go = UnityEngine.Object.Instantiate(prefab, anchor, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            if (layer >= 0) go.layer = layer;
            Neutralize(go, null);
            go.SetActive(true);
            return go;
        }

        internal static GameObject InstantiateInactive(ObjectInfo info)
        {
            var nm = InstanceFinder.NetworkManager;
            if (nm == null) return null;
            NetworkObject prefab = null;
            try
            {
                var po = nm.GetPrefabObjects<FishNet.Managing.Object.PrefabObjects>(info.CollectionId, false);
                if (po != null && info.PrefabId < po.GetObjectCount()) prefab = po.GetObject(false, info.PrefabId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Prefab lookup failed for {info.Name} ({info.CollectionId}:{info.PrefabId}): {e.Message}");
            }
            if (prefab == null || IsSessionObject(prefab.gameObject)) return null;

            var pgo = prefab.gameObject;
            bool wasActive = pgo.activeSelf;
            pgo.SetActive(false);
            GameObject go;
            try { go = UnityEngine.Object.Instantiate(pgo); }
            finally { pgo.SetActive(wasActive); }
            go.name = info.Name + " [replay]";
            return go;
        }
    }
}
