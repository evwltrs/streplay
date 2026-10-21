using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Streplay
{
    // Observe-only kill signal for markers. Every weapon's kill path ends in a KillShockWave method on the killer's own
    // client (guns on FirstPersonController, rifles/beams/charge guns on Weapon, and grenades, projectiles, mines, props
    // and bubbles their own), so all of them are hooked, found by name. The game's kill counter is hooked as well for
    // the few paths that call it directly (hand grenades); it's tiny enough for the JIT to inline into its callers,
    // which skips a hook, so it can't be the only signal. One kill firing both counts once (Recorder.OnLocalKill).
    internal static class KillHook
    {
        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(KillHook).GetMethod(nameof(Kill), BindingFlags.Static | BindingFlags.NonPublic));
            var asm = typeof(FirstPersonController).Assembly;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            int hooked = 0;
            foreach (var type in types)
            {
                var m = type.GetMethod("KillShockWave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m == null || m.IsAbstract || m.ContainsGenericParameters) continue;
                try { harmony.Patch(m, postfix: postfix); hooked++; }
                catch (Exception e) { Plugin.Log.LogWarning($"Kill detection: couldn't hook {type.Name}.KillShockWave: {e.Message}"); }
            }
            harmony.Patch(AccessTools.Method(typeof(Settings), nameof(Settings.IncreaseKillsAmount)), postfix: postfix);
            Plugin.Log.LogInfo($"Kill detection on {hooked} weapon kill paths");
        }

        static void Kill()
        {
            if (!ReplayPlayer.Active) Recorder.Instance?.OnLocalKill();
        }
    }
}
