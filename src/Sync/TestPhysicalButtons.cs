using HarmonyLib;
using Rewired;
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
            Player player = ReInput.players.GetPlayer(0);
            foreach (ControllerMap map in player.controllers.maps.GetAllMaps())
            {
                if (map.controllerType != ControllerType.Keyboard || !map.enabled) continue;
                foreach (ActionElementMap binding in map.AllMaps)
                    if (binding.enabled && binding.keyCode == keyCode && (TestDriver.StuckButtonMask & (1u << binding.actionId)) != 0)
                    { __result = true; return; }
            }
            foreach (KeyCode key in TestDriver.PhysicalTestKeys)
                if (key == keyCode) { __result = true; return; }
        }
    }

    /// <summary>Inject at the source read boundary, independently of real-device polling and fixed-step order.</summary>
    [HarmonyPatch(typeof(PhysicalButtons), "SourceValues", new[] { typeof(Controller) })]
    internal static class TestPhysicalSource
    {
        [HarmonyPostfix]
        private static void Postfix(Controller controller, bool[] __result)
        {
            Keyboard keyboard = controller as Keyboard;
            if (!TestDriver.NativeButtonTest || keyboard == null) return;
            bool[] values = __result;
            if (values == null) return;
            foreach (ControllerMap map in ReInput.players.GetPlayer(0).controllers.maps.GetAllMaps())
            {
                if (map.controllerType != ControllerType.Keyboard || !map.enabled) continue;
                foreach (ActionElementMap binding in map.AllMaps)
                {
                    if (!binding.enabled || (binding.actionId != 5 && binding.actionId != 7)) continue;
                    int index = keyboard.GetButtonIndexByKeyCode(binding.keyCode);
                    if (index < 0 || index >= values.Length) continue;
                    bool pressed = false;
                    foreach (KeyCode key in TestDriver.PhysicalTestKeys) if (key == binding.keyCode) pressed = true;
                    values[index] = pressed;
                }
            }
        }
    }
}
