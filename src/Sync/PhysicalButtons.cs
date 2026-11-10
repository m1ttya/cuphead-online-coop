using System;
using System.Reflection;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Controller.GetButtonById and Keyboard.GetKey still read Rewired's per-loop Button.value cache.
    /// Read the source values that feed that cache instead, resolving indices through the actual controller.
    /// No controller state is reset or written here. Unsupported versions fall back to the public API.
    /// </summary>
    internal static class PhysicalButtons
    {
        private static readonly FieldInfo updater = FindUpdater();
        private static readonly FieldInfo values = updater != null ? AccessTools.Field(updater.FieldType, "buttonValues") : null;
        private static readonly FieldInfo pressures = updater != null ? AccessTools.Field(updater.FieldType, "buttonPressureValues") : null;
        private static readonly Func<ControllerType, bool> allowed = FindInputGate();

        public static bool Available { get { return updater != null && values != null && pressures != null && allowed != null; } }

        private static FieldInfo FindUpdater()
        {
            // The field name is obfuscated; its type is explicitly preserved by Rewired.
            foreach (FieldInfo field in typeof(Controller).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType.FullName == "Rewired.ControllerDataUpdater") return field;
            return null;
        }

        private static Func<ControllerType, bool> FindInputGate()
        {
            MethodInfo method = AccessTools.Method(typeof(ReInput), "IsInputAllowed", new[] { typeof(ControllerType) });
            return method != null ? (Func<ControllerType, bool>)Delegate.CreateDelegate(typeof(Func<ControllerType, bool>), method) : null;
        }

        // Also used by the explicit test driver to inject source input before the controller caches it.
        internal static bool[] SourceValues(Controller controller)
        {
            object data = updater != null ? updater.GetValue(controller) : null;
            return data != null && values != null ? values.GetValue(data) as bool[] : null;
        }

        private static bool TryRead(Controller controller, int index, out bool pressed)
        {
            pressed = false;
            if (!Available || index < 0 || index >= controller.Buttons.Count) return false;
            if (!controller.enabled || !controller.isConnected || !allowed(controller.type)) return true;
            object data = updater.GetValue(controller);
            if (data == null) return false;
            if (controller.Buttons[index].isPressureSensitive)
            {
                float[] raw = pressures.GetValue(data) as float[];
                if (raw == null || index >= raw.Length) return false;
                pressed = raw[index] > 0f;
            }
            else
            {
                bool[] raw = SourceValues(controller);
                if (raw == null || index >= raw.Length) return false;
                pressed = raw[index];
            }
            return true;
        }

        public static bool TryRead(Controller controller, ActionElementMap binding, out bool pressed)
        {
            Keyboard keyboard = controller as Keyboard;
            int index = keyboard != null ? keyboard.GetButtonIndexByKeyCode(binding.keyCode) : controller.GetButtonIndexById(binding.elementIdentifierId);
            if (!TryRead(controller, index, out pressed)) return false;
            if (keyboard == null) return true;
            bool first, second, third;
            if (!TryModifier(keyboard, binding.modifierKey1, out first) || !TryModifier(keyboard, binding.modifierKey2, out second)
                || !TryModifier(keyboard, binding.modifierKey3, out third)) return false;
            pressed &= first && second && third;
            return true;
        }

        private static bool TryModifier(Keyboard keyboard, ModifierKey modifier, out bool pressed)
        {
            pressed = true;
            if (modifier == ModifierKey.None) return true;
            KeyCode left, right;
            switch (modifier)
            {
                case ModifierKey.Control: left = KeyCode.LeftControl; right = KeyCode.RightControl; break;
                case ModifierKey.Shift: left = KeyCode.LeftShift; right = KeyCode.RightShift; break;
                case ModifierKey.Alt: left = KeyCode.LeftAlt; right = KeyCode.RightAlt; break;
                case ModifierKey.Command: left = KeyCode.LeftCommand; right = KeyCode.RightCommand; break;
                default: return false;
            }
            bool a, b;
            if (!TryRead(keyboard, keyboard.GetButtonIndexByKeyCode(left), out a)
                || !TryRead(keyboard, keyboard.GetButtonIndexByKeyCode(right), out b)) return false;
            pressed = a || b;
            return true;
        }
    }
}
