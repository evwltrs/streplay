using HarmonyLib;

namespace Streplay
{
    // Observe-only: the game's own kill and death counters (the stats screen's), the one call every kill path makes
    // (guns, rifles, beams, grenades, projectiles, mines, props) and every death, and only on the local player's own
    // client, so they are the "you got a kill" / "you died" signals for markers.
    [HarmonyPatch]
    internal static class KillHook
    {
        [HarmonyPatch(typeof(Settings), nameof(Settings.IncreaseKillsAmount))]
        [HarmonyPostfix]
        static void Kill()
        {
            if (!ReplayPlayer.Active) Recorder.Instance?.OnLocalKill();
        }

        [HarmonyPatch(typeof(Settings), nameof(Settings.IncreaseDeathsAmount))]
        [HarmonyPostfix]
        static void Death()
        {
            if (!ReplayPlayer.Active) Recorder.Instance?.OnLocalDeath();
        }
    }
}
