using System;
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

    // Two game methods throw harmlessly on a replay's offline copies (the map's kill cam being switched off, and a player
    // copy's PlayerHealth start-up that expects a live match). The exceptions only clutter the log, so they're dropped,
    // but only while a replay is open; in normal play the game behaves exactly as without the mod.
    [HarmonyPatch]
    internal static class ReplayNoise
    {
        [HarmonyPatch(typeof(KillCam), "OnDisable")]
        [HarmonyFinalizer]
        static Exception KillCamOnDisable(Exception __exception) => ReplayPlayer.Active ? null : __exception;

        [HarmonyPatch(typeof(PlayerHealth), "Awake___UserLogic")]
        [HarmonyFinalizer]
        static Exception PlayerHealthAwake(Exception __exception) => ReplayPlayer.Active ? null : __exception;
    }
}
