using Rewired;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Read critical buttons without depending on Rewired's cached action edges. Resolve current enabled
    /// maps on every sample: retries, controller reassignment and live rebinding must not retain old maps.
    /// Axis bindings remain native; never guess a trigger threshold or hard-code a gamepad layout.
    /// </summary>
    internal static class MappedButtons
    {
        public const uint Mask = (1u << 5) | (1u << 7);

        public static uint Read(Player player, out uint held)
        {
            held = 0;
            uint readable = 0, unsupported = 0;
            foreach (ControllerMap map in player.controllers.maps.GetAllMaps())
            {
                if (!map.enabled) continue;
                Controller controller = player.controllers.GetController(map.controllerType, map.controllerId);
                if (controller == null) continue;
                var bindings = map.AllMaps;
                for (int index = 0; index < bindings.Count; index++)
                {
                    ActionElementMap binding = bindings[index];
                    int action = binding.actionId;
                    if ((action != 5 && action != 7) || !binding.enabled) continue;
                    uint bit = 1u << action;
                    if (binding.elementType != ControllerElementType.Button) { unsupported |= bit; continue; }
                    readable |= bit;
                    Keyboard keyboard = controller as Keyboard;
                    bool pressed = keyboard != null
                        ? keyboard.GetKey(binding.keyCode) && Modifier(keyboard, binding.modifierKey1)
                            && Modifier(keyboard, binding.modifierKey2) && Modifier(keyboard, binding.modifierKey3)
                        : controller.GetButtonById(binding.elementIdentifierId);
                    if (pressed) held |= bit;
                }
            }
            return readable & ~unsupported;
        }

        private static bool Modifier(Keyboard keyboard, ModifierKey key)
        {
            return key == ModifierKey.None || keyboard.GetModifierKey(key);
        }
    }
}
