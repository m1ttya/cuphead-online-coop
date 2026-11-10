using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>Only enabled by the explicit nativebuttons command in an isolated test script.</summary>
    [HarmonyPatch(typeof(Rewired.Keyboard), "GetKey", new[] { typeof(KeyCode) })]
    internal static class TestPhysicalButtons
    {
        [HarmonyPostfix]
        private static void Postfix(KeyCode keyCode, ref bool __result)
        {
            if (!TestDriver.NativeButtonTest) return;
            foreach (KeyCode key in TestDriver.PhysicalTestKeys)
                if (key == keyCode) { __result = true; return; }
        }
    }
}
