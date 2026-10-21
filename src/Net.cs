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
        static readonly Func<NetworkObject, ulong> GetSceneId =
            AccessTools.MethodDelegate<Func<NetworkObject, ulong>>(AccessTools.PropertyGetter(typeof(NetworkObject), "SceneId"));

        public static ulong SceneId(NetworkObject nob) => nob != null ? GetSceneId(nob) : 0UL;

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
