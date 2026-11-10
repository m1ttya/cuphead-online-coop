using System;
using System.Collections.Generic;
using Rewired;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Dash/Switch are one-shot actions. Each physical binding has its own edges; the most recent press
    /// owns the action until it is released. An older stuck binding cannot swallow a new gamepad press,
    /// take ownership back without a real release/press, or manufacture auto-repeat.
    /// </summary>
    internal sealed class ButtonSources
    {
        private struct Key : IEquatable<Key>
        {
            public Controller Controller;
            public int Action, Element, Modifiers;
            public bool Equals(Key other)
            {
                return ReferenceEquals(Controller, other.Controller) && Action == other.Action
                    && Element == other.Element && Modifiers == other.Modifiers;
            }
            public override bool Equals(object other) { return other is Key && Equals((Key)other); }
            public override int GetHashCode() { return Controller.GetHashCode() ^ Action * 397 ^ Element * 31 ^ Modifiers; }
        }

        private sealed class State
        {
            public int Action, Seen;
            public bool Current, Previous, Joystick;
        }

        private readonly Dictionary<Key, State> states = new Dictionary<Key, State>();
        private readonly State[] owners = new State[PadState.ActionCount];
        private int sample;

        public void Reset() { states.Clear(); Array.Clear(owners, 0, owners.Length); sample = 0; }
        public void Begin() { sample++; }

        public void Add(Controller controller, ActionElementMap binding, bool pressed)
        {
            var key = new Key { Controller = controller, Action = binding.actionId,
                Element = controller is Keyboard ? (int)binding.keyCode : binding.elementIdentifierId,
                Modifiers = (int)binding.modifierKey1 | ((int)binding.modifierKey2 << 3) | ((int)binding.modifierKey3 << 6) };
            State state;
            if (!states.TryGetValue(key, out state))
            {
                state = new State { Action = binding.actionId, Joystick = controller.type == ControllerType.Joystick };
                states.Add(key, state);
            }
            if (state.Seen != sample) state.Current = false;
            state.Seen = sample;
            state.Current |= pressed; // duplicate maps of the same physical binding still produce one edge
        }

        public uint Finish(out uint down)
        {
            down = 0;
            foreach (State state in states.Values)
            {
                bool held = state.Seen == sample && state.Current;
                if (held && !state.Previous)
                {
                    uint bit = 1u << state.Action;
                    // If keyboard and gamepad arrive in the same sample, prefer the assigned gamepad.
                    if ((down & bit) == 0 || state.Joystick) owners[state.Action] = state;
                    down |= bit;
                }
                state.Previous = held;
            }
            uint result = 0;
            for (int action = 0; action < owners.Length; action++)
            {
                State owner = owners[action];
                if (owner != null && owner.Previous) result |= 1u << action;
                else owners[action] = null;
            }
            return result;
        }
    }
}
