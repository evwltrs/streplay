using HarmonyLib;

namespace Streplay
{
    // While a replay is open the game is sitting in an offline map it didn't load itself.
    // PauseManager would then lock/unlock the cursor every frame, open its pause menu on Esc and
    // run its "returned to main menu" teardown, so it is paused for the duration of the replay
    // (and until the main menu has loaded again). Outside of replays these prefixes do nothing.
    [HarmonyPatch]
    internal static class GameSuspend
    {
        [HarmonyPatch(typeof(PauseManager), "Update")]
        [HarmonyPrefix]
        static bool PauseManagerUpdate() => !ReplayPlayer.SuspendGame;

        [HarmonyPatch(typeof(PauseManager), "Menu")]
        [HarmonyPrefix]
        static bool PauseManagerMenu() => !ReplayPlayer.SuspendGame;
    }
}
