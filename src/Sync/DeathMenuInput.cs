using System;
using System.Reflection;
using HarmonyLib;

namespace CupheadOnline.Sync
{
    /// <summary>A combat A/Y must be released and pressed again before selecting Retry or Equipment.</summary>
    [HarmonyPatch(typeof(LevelGameOverGUI), "Update")]
    internal static class DeathMenuInput
    {
        private static readonly FieldInfo state = AccessTools.Field(typeof(LevelGameOverGUI), "state");
        private static LevelGameOverGUI previous;
        private static bool wasReady;

        public static void Reset() { previous = null; wasReady = false; }

        [HarmonyPrefix]
        private static void Prefix(LevelGameOverGUI __instance)
        {
            if (!RemoteInput.DriveHostPlayerTwo) { Reset(); return; }
            bool ready = Convert.ToInt32(state.GetValue(__instance)) == 1; // native State.Ready
            if (ready && (__instance != previous || !wasReady))
                RemoteInput.Remote.SuppressUntilRelease((1u << (int)CupheadButton.Accept) | (1u << (int)CupheadButton.EquipMenu));
            previous = __instance;
            wasReady = ready;
        }
    }
}
