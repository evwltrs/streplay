using System;
using FishNet.Object;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Streplay
{
    internal static class Net
    {
        // NetworkObject.SceneId is internal in this FishNet build; it identifies objects baked into a map.
        // Looked up on first use and allowed to fail: if a FishNet update renames SceneId, map objects just aren't
        // matched by id any more (they're left as they are) instead of every recording and replay throwing.
        static Func<NetworkObject, ulong> _getSceneId;
        static bool _sceneIdLooked;

        static Func<NetworkObject, ulong> GetSceneId
        {
            get
            {
                if (_sceneIdLooked) return _getSceneId;
                _sceneIdLooked = true;
                try
                {
                    var getter = AccessTools.PropertyGetter(typeof(NetworkObject), "SceneId");
                    if (getter != null) _getSceneId = AccessTools.MethodDelegate<Func<NetworkObject, ulong>>(getter);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"NetworkObject.SceneId not found (FishNet changed?): {e.Message}"); }
                if (_getSceneId == null) Plugin.Log.LogWarning("NetworkObject.SceneId not found: map objects won't be matched in replays");
                return _getSceneId;
            }
        }

        public static ulong SceneId(NetworkObject nob) => nob != null && GetSceneId != null ? GetSceneId(nob) : 0UL;

        static Scene _ddol;

        // The DontDestroyOnLoad scene (where the game keeps its HUD). Our own host object is
        // HideAndDontSave, which takes it out of every scene, so probe with a throwaway object.
        public static Scene DdolScene()
        {
            if (_ddol.IsValid()) return _ddol;
            var probe = new GameObject("Streplay DDOL probe");
            UnityEngine.Object.DontDestroyOnLoad(probe);
            _ddol = probe.scene;
            UnityEngine.Object.Destroy(probe);
            return _ddol;
        }
    }
}
