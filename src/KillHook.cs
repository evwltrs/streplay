using HarmonyLib;

namespace Streplay
{
    // Observe-only: the local player's kill shockwave plays on every kill, whatever the weapon, and only on the
    // killer's own client, so it is the "you got a kill" signal for markers.
    [HarmonyPatch]
    internal static class KillHook
    {
        [HarmonyPatch(typeof(FirstPersonController), nameof(FirstPersonController.KillShockWave))]
        [HarmonyPostfix]
        static void KillShockWave()
        {
            if (!ReplayPlayer.Active) Recorder.Instance?.OnLocalKill();
        }
    }
}
