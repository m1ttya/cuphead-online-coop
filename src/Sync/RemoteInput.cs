using System;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Wire format of one controller state. Action ids are Cuphead's Rewired action ids (CupheadButton values).
    /// Each button carries a press counter so taps shorter than a network frame are never lost.
    /// </summary>
    public sealed class PadState
    {
        public const int ActionCount = 27;            // CupheadButton.Swap == 26 is the highest real action id
        public static readonly int[] AxisActions = { 0, 1, 22, 23 }; // MoveHorizontal, MoveVertical, MenuHorizontal, MenuVertical

        public uint Held;
        public readonly byte[] Presses = new byte[ActionCount];
        public readonly float[] Axes = new float[AxisActions.Length];

        public static int AxisSlot(int actionId)
        {
            for (int i = 0; i < AxisActions.Length; i++) if (AxisActions[i] == actionId) return i;
            return -1;
        }

        public void Write(NetWriter w)
        {
            w.U32(Held);
            w.Bytes(Presses, 0, ActionCount);
            for (int i = 0; i < Axes.Length; i++)
            {
                w.I8((sbyte)Mathf.RoundToInt(Mathf.Clamp(Axes[i], -1f, 1f) * 127f));
            }
        }

        public void Read(NetReader r)
        {
            Held = r.U32();
            for (int i = 0; i < ActionCount; i++) Presses[i] = r.U8();
            for (int i = 0; i < Axes.Length; i++) Axes[i] = r.I8() / 127f;
        }
    }

    /// <summary>
    /// Turns a stream of PadStates (latest wins) into per-frame held/down/up state, the way Rewired reports it.
    /// Press counters guarantee a tap shorter than one frame/packet still produces exactly one down edge.
    /// Like Rewired, the Update and FixedUpdate loops keep separate edge state: Cuphead polls SwitchWeapon, Jump,
    /// Dash, Super, parry and Accept-to-interact with GetButtonDown inside FixedUpdate, and a press consumed by
    /// the rendered frame must still read as down in the first fixed step after it.
    /// </summary>
    public sealed class VirtualPad
    {
        public readonly PadState Latest = new PadState();
        private readonly Edges frameLoop = new Edges();
        private readonly Edges fixedLoop = new Edges();
        private readonly byte[] observedPresses = new byte[PadState.ActionCount];
        private readonly float[] pressReceivedAt = new float[PadState.ActionCount];
        private uint suppressedHeld;

        private sealed class Edges
        {
            /// <summary>Expire unpolled presses in both loops, including gameplay taps first read by a death menu.</summary>
            private const float StaleAfter = 0.25f;

            public readonly byte[] SeenPresses = new byte[PadState.ActionCount];
            public readonly bool[] Held = new bool[PadState.ActionCount];
            public readonly bool[] Down = new bool[PadState.ActionCount];
            public readonly bool[] Up = new bool[PadState.ActionCount];
            public readonly float[] PressStart = new float[PadState.ActionCount];
            public readonly uint[] DownCount = new uint[PadState.ActionCount];
            private double computedStep = -1;

            public void Reset(PadState latest)
            {
                computedStep = -1;
                Array.Clear(Held, 0, Held.Length);
                Array.Clear(Down, 0, Down.Length);
                Array.Clear(Up, 0, Up.Length);
                Array.Clear(DownCount, 0, DownCount.Length);
                Buffer.BlockCopy(latest.Presses, 0, SeenPresses, 0, SeenPresses.Length);
            }

            public void Suppress(PadState latest, uint mask)
            {
                computedStep = -1;
                for (int i = 0; i < PadState.ActionCount; i++)
                {
                    if ((mask & (1u << i)) == 0) continue;
                    SeenPresses[i] = latest.Presses[i];
                    Held[i] = Down[i] = Up[i] = false;
                }
            }

            public void Advance(PadState latest, double step, float[] receivedAt, uint suppressed)
            {
                if (step == computedStep) return;
                computedStep = step;
                float now = Time.realtimeSinceStartup;
                for (int i = 0; i < PadState.ActionCount; i++)
                {
                    // Age the press itself, not the time since physics last asked. The first fresh dash after
                    // a pause/scene load must survive even if FixedUpdate has not polled us for several seconds.
                    bool fresh = now - receivedAt[i] <= StaleAfter && (suppressed & (1u << i)) == 0;
                    bool newPress = latest.Presses[i] != SeenPresses[i] && fresh;
                    SeenPresses[i] = latest.Presses[i];
                    bool want = (suppressed & (1u << i)) == 0 && ((latest.Held & (1u << i)) != 0 || newPress);
                    bool was = Held[i];
                    Down[i] = (want && !was && fresh) || newPress;
                    Up[i] = !want && was;
                    Held[i] = want;
                    if (Down[i]) { PressStart[i] = now; DownCount[i]++; }
                }
            }
        }

        public void Reset()
        {
            Latest.Held = 0;
            suppressedHeld = 0;
            Array.Clear(Latest.Axes, 0, Latest.Axes.Length);
            frameLoop.Reset(Latest);
            fixedLoop.Reset(Latest);
            Buffer.BlockCopy(Latest.Presses, 0, observedPresses, 0, PadState.ActionCount);
            for (int i = 0; i < pressReceivedAt.Length; i++) pressReceivedAt[i] = -100f;
        }

        /// <summary>Timestamp changed counters at packet arrival, including presses made while physics is paused.</summary>
        public void ObservePresses()
        {
            suppressedHeld &= Latest.Held; // a held menu button becomes eligible only after an observed release
            for (int i = 0; i < PadState.ActionCount; i++)
            {
                if (observedPresses[i] == Latest.Presses[i]) continue;
                observedPresses[i] = Latest.Presses[i];
                pressReceivedAt[i] = Time.realtimeSinceStartup;
            }
        }

        /// <summary>Accept the current counters as already seen (don't replay old presses).</summary>
        public void SyncPresses()
        {
            Buffer.BlockCopy(Latest.Presses, 0, frameLoop.SeenPresses, 0, PadState.ActionCount);
            Buffer.BlockCopy(Latest.Presses, 0, fixedLoop.SeenPresses, 0, PadState.ActionCount);
        }

        public void SuppressUntilRelease(uint mask)
        {
            suppressedHeld |= Latest.Held & mask;
            frameLoop.Suppress(Latest, mask);
            fixedLoop.Suppress(Latest, mask);
        }

        /// <summary>The edge state for the loop that is asking: one step per rendered frame or per fixed step.</summary>
        private Edges Advance()
        {
            ObservePresses(); // also supports the scripted local test pad
            if (Time.inFixedTimeStep)
            {
                fixedLoop.Advance(Latest, Time.fixedTime, pressReceivedAt, suppressedHeld);
                return fixedLoop;
            }
            frameLoop.Advance(Latest, Time.frameCount, pressReceivedAt, suppressedHeld);
            return frameLoop;
        }

        private static bool InRange(int id) { return id >= 0 && id < PadState.ActionCount; }

        public bool Button(int id) { Edges e = Advance(); return InRange(id) && e.Held[id]; }
        public bool ButtonDown(int id) { Edges e = Advance(); return InRange(id) && e.Down[id]; }
        public bool ButtonUp(int id) { Edges e = Advance(); return InRange(id) && e.Up[id]; }
        public uint FixedDownCount(int id) { return InRange(id) ? fixedLoop.DownCount[id] : 0; }

        public float Axis(int id)
        {
            int slot = PadState.AxisSlot(id);
            return slot < 0 ? 0f : Latest.Axes[slot];
        }

        public float TimePressed(int id)
        {
            Edges e = Advance();
            return InRange(id) && e.Held[id] ? Time.realtimeSinceStartup - e.PressStart[id] : 0f;
        }

        public bool AnyButton()
        {
            Edges e = Advance();
            for (int i = 2; i < PadState.ActionCount; i++) if (e.Held[i]) return true;
            return false;
        }

        public bool AnyButtonDown()
        {
            Edges e = Advance();
            for (int i = 2; i < PadState.ActionCount; i++) if (e.Down[i]) return true;
            return false;
        }

        public bool AnyButtonUp()
        {
            Edges e = Advance();
            for (int i = 2; i < PadState.ActionCount; i++) if (e.Up[i]) return true;
            return false;
        }
    }

    /// <summary>
    /// Client side: samples the local player's controller (Rewired player 0) every frame and sends it.
    /// Host side: the received PadStates drive Rewired player 1 (Mugman) through a VirtualPad.
    /// </summary>
    public static class RemoteInput
    {
        /// <summary>When true, Rewired calls pass through untouched (used while we sample real input ourselves).</summary>
        [ThreadStatic] public static bool Sampling;

        /// <summary>True while the host should feed remote input into Rewired player 1.</summary>
        public static bool DriveHostPlayerTwo;

        /// <summary>True while a mirroring client must not let the local game see any input.</summary>
        public static bool BlockLocalGame;

        public static readonly VirtualPad Remote = new VirtualPad();
        private static readonly VirtualPad hostButtons = new VirtualPad();
        private static uint hostMapped;
        private static uint hostCached, hostSource, localCached, localSource;
        private static readonly ButtonSources hostSources = new ButtonSources(), clientSources = new ButtonSources();
        private static int hostSampleFrame = -1;
        private static float nextMappingWarning;

        /// <summary>Optional scripted pad (test driver): replaces local sampling on the client, drives P1 on the host.</summary>
        public static PadState Scripted;

        // ---- client sampling ----
        private static readonly PadState local = new PadState();
        private static uint localSeq;
        private static uint localDown;
        private static int sampledFrame = -1;
        private static float sampledAt = -100f, receivedAt = -100f;
        private static EventInfo inputUpdated;
        private static readonly Action<Rewired.UpdateLoopType> onInputUpdated = OnInputUpdated;

        public static void ResetClient()
        {
            localSeq = 0;
            localDown = 0;
            sampledFrame = -1;
            sampledAt = -100f;
            local.Held = 0;
            localCached = localSource = 0;
            clientSources.Reset();
            Array.Clear(local.Presses, 0, local.Presses.Length);
            Array.Clear(local.Axes, 0, local.Axes.Length);
        }

        /// <summary>Read edge-triggered actions in Rewired's own Update context, rather than after rendering.</summary>
        public static void Install()
        {
            try
            {
                inputUpdated = typeof(Rewired.ReInput).GetEvent("UpdateEndedEvent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (inputUpdated == null) throw new MissingMemberException("ReInput.UpdateEndedEvent");
                inputUpdated.GetAddMethod(true).Invoke(null, new object[] { onInputUpdated });
                Plugin.Log.LogInfo("Remote input sampling: Rewired UpdateEndedEvent");
                Plugin.Log.LogInfo("Physical button source bypass: " + (PhysicalButtons.Available ? "available" : "unavailable; cached API fallback"));
            }
            catch (Exception ex)
            {
                inputUpdated = null;
                Plugin.Log.LogWarning("Rewired update sampling unavailable; using frame fallback: " + ex.Message);
            }
        }

        private static void OnInputUpdated(Rewired.UpdateLoopType loop)
        {
            if (loop != Rewired.UpdateLoopType.Update) return;
            if (Session.IsClient && Scripted == null && sampledFrame != Time.frameCount) SampleLocal();
            else if (Session.IsHost && DriveHostPlayerTwo && TestDriver.HostPad == null) SampleHostButtons(true);
        }

        private static uint ReadMapped(Rewired.Player player, out uint held, out uint cached, out uint source, ButtonSources history, out uint down)
        {
            try { return MappedButtons.Read(player, out held, out cached, out source, history, out down); }
            catch (Exception ex)
            {
                // Device removal/map replacement can race a sample. Fall back to native input for this frame.
                held = cached = source = down = 0;
                history.Reset();
                if (Time.realtimeSinceStartup >= nextMappingWarning)
                {
                    nextMappingWarning = Time.realtimeSinceStartup + 5f;
                    Plugin.Log.LogWarning("Mapped button sampling: " + ex.Message);
                }
                return 0;
            }
        }

        private static void SampleHostButtons(bool inputJustUpdated = false)
        {
            // A game query in FixedUpdate can precede Rewired's Update. That early fallback must not prevent
            // the completed Update sample from observing a release later in the same rendered frame.
            if (hostSampleFrame == Time.frameCount && !inputJustUpdated) return;
            hostSampleFrame = Time.frameCount;
            uint held, down;
            hostMapped = ReadMapped(Rewired.ReInput.players.GetPlayer(0), out held, out hostCached, out hostSource, hostSources, out down);
            held &= hostMapped;
            uint edges = (down | (held & ~hostButtons.Latest.Held)) & hostMapped;
            for (int i = 0; i < PadState.ActionCount; i++)
                if ((edges & (1u << i)) != 0) hostButtons.Latest.Presses[i]++;
            hostButtons.Latest.Held = held;
            hostButtons.ObservePresses();
        }

        /// <summary>Diagnostic counters in F8 distinguish unmapped local buttons from a transport/physics problem.</summary>
        public static string Summary(bool host)
        {
            PadState state = host ? Remote.Latest : (Scripted ?? local);
            string result = (host ? "Received" : "Sent") + " input: Dash=" + state.Presses[7] + ", Switch=" + state.Presses[5];
            if (host) result += " | Physics edges: Dash=" + Remote.FixedDownCount(7) + ", Switch=" + Remote.FixedDownCount(5);
            if (host) result += " | Host local: Dash=" + hostButtons.Latest.Presses[7] + ", Switch=" + hostButtons.Latest.Presses[5];
            result += " | " + (host ? "Remote held=" : "Held=") + HeldNames(state.Held);
            if (host) result += " | Host held=" + HeldNames(hostButtons.Latest.Held);
            result += " | " + (host ? "Host" : "Local") + " controller cache=" + HeldNames(host ? hostCached : localCached);
            result += " | Source bypass=" + HeldNames(host ? hostSource : localSource);
            if (host && hasRemote) result += " | Input age=" + (Time.realtimeSinceStartup - receivedAt).ToString("0.00") + "s";
            if (!host && sampledFrame >= 0) result += " | Sample age=" + (Time.realtimeSinceStartup - sampledAt).ToString("0.00") + "s";
            return result;
        }

        private static string HeldNames(uint held)
        {
            held &= MappedButtons.Mask;
            return held == 0 ? "none" : ((held & (1u << 7)) != 0 ? "Dash " : "") + ((held & (1u << 5)) != 0 ? "Switch" : "");
        }

        public static void SampleAndSend(Connection connection)
        {
            // LateUpdate only sends the completed sample. Polling Rewired here can select a different edge context.
            PadState state = Scripted ?? (sampledFrame == Time.frameCount ? local : SampleLocal());
            if (state == null) return;
            NetWriter w = new NetWriter(48);
            w.U32(++localSeq);
            state.Write(w);
            connection.SendUnreliable(MsgType.Input, w);
        }

        private static PadState SampleLocal()
        {
            Rewired.Player player;
            try { player = Rewired.ReInput.players.GetPlayer(0); }
            catch { return null; }
            if (player == null) return null;
            Sampling = true;
            try
            {
                ClientController.EnsureAssigned(player);
                uint mappedHeld, mappedDown;
                uint mapped = ReadMapped(player, out mappedHeld, out localCached, out localSource, clientSources, out mappedDown);
                uint held = 0, down = 0;
                for (int i = 0; i < PadState.ActionCount; i++)
                {
                    if (i == 9 || i == 10 || i == 17 || i == 24 || i == 25) continue; // unused ids
                    bool direct = (mapped & (1u << i)) != 0;
                    bool pressed = direct ? (mappedHeld & (1u << i)) != 0 : player.GetButton(i);
                    if (pressed) held |= 1u << i;
                    bool edge = direct ? (mappedDown & (1u << i)) != 0 : player.GetButtonDown(i);
                    if (edge) down |= 1u << i;
                    // A held transition also recovers a press when Rewired's down edge was unavailable at sampling.
                    // Buffered GetButtonDown may remain true across frames, including after release. Do not
                    // turn that one native press into new network presses that can leak into a later menu.
                    bool rising = pressed && (local.Held & (1u << i)) == 0;
                    if (direct ? edge || rising : (edge && (localDown & (1u << i)) == 0) || rising) local.Presses[i]++;
                }
                local.Held = held;
                localDown = down;
                for (int i = 0; i < PadState.AxisActions.Length; i++)
                {
                    local.Axes[i] = player.GetAxis(PadState.AxisActions[i]);
                }
                sampledFrame = Time.frameCount;
                sampledAt = Time.realtimeSinceStartup;
            }
            finally
            {
                Sampling = false;
            }
            return local;
        }

        // ---- host application ----
        private static uint latestSeq;
        private static bool hasRemote;

        public static void ResetHost()
        {
            latestSeq = 0;
            hasRemote = false;
            receivedAt = -100f;
            Remote.Reset();
            Array.Clear(hostButtons.Latest.Presses, 0, hostButtons.Latest.Presses.Length);
            hostButtons.Reset();
            hostMapped = 0;
            hostCached = hostSource = 0;
            hostSources.Reset();
            hostSampleFrame = -1;
        }

        private static double lastInputTime;
        private static bool released;

        /// <summary>Link hiccup: don't leave Mugman running/shooting on the last packet for the whole timeout.</summary>
        public static void ReleaseIfStale(double now)
        {
            if (!hasRemote || released || now - lastInputTime < 0.25) return;
            released = true;
            Remote.Latest.Held = 0;
            Array.Clear(Remote.Latest.Axes, 0, Remote.Latest.Axes.Length);
        }

        public static void OnInputMessage(NetReader r, double now)
        {
            uint seq = r.U32();
            if (hasRemote && (int)(seq - latestSeq) <= 0) return; // stale / reordered
            Remote.Latest.Read(r);
            lastInputTime = now;
            receivedAt = Time.realtimeSinceStartup;
            released = false;
            Remote.ObservePresses();
            if (!hasRemote)
            {
                Remote.SyncPresses(); // don't replay presses from before we connected
                hasRemote = true;
            }
            latestSeq = seq;
        }

        /// <summary>Which virtual pad (if any) answers for this Rewired player right now.</summary>
        internal static VirtualPad PadFor(Rewired.Player p)
        {
            if (p == null) return null;
            if (DriveHostPlayerTwo && p.id == 1) return Remote;
            if (TestDriver.HostPad != null && p.id == 0) return TestDriver.HostPad;
            return null;
        }

        internal static VirtualPad ButtonPadFor(Rewired.Player p, int action)
        {
            VirtualPad pad = PadFor(p);
            if (pad != null) return pad;
            if (p != null && p.id == 0 && DriveHostPlayerTwo && Session.IsHost && (action == 5 || action == 7))
            {
                SampleHostButtons();
                if ((hostMapped & (1u << action)) != 0) return hostButtons;
            }
            return null;
        }
    }

    /// <summary>
    /// Rewired.Player is the single input choke point (observed via REA: Rewired_Core.dll Rewired.Player
    /// GetButton/GetButtonDown/GetButtonUp/GetAxis(int) and GetAnyButton*(); Cuphead's PlayerInput.actions
    /// and PlayerManager's join logic call these on ReInput.players.GetPlayer(0/1)).
    /// </summary>
    [HarmonyPatch]
    internal static class RewiredPatches
    {
        private static bool Block(Rewired.Player p)
        {
            return RemoteInput.BlockLocalGame && !RemoteInput.Sampling;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButton", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButton(Rewired.Player __instance, int actionId, ref bool __result)
        {
            if (TestDriver.NativeButtonTest && RemoteInput.Sampling && (actionId == 5 || actionId == 7)) { __result = false; return false; }
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.ButtonPadFor(__instance, actionId); if (pad != null) { __result = pad.Button(actionId); return false; }
            if (Block(__instance)) { __result = false; return false; }
            if (TestDriver.NativeButtonTest && (actionId == 5 || actionId == 7)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButtonDown", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButtonDown(Rewired.Player __instance, int actionId, ref bool __result)
        {
            if (TestDriver.NativeButtonTest && RemoteInput.Sampling && (actionId == 5 || actionId == 7)) { __result = false; return false; }
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.ButtonPadFor(__instance, actionId); if (pad != null) { __result = pad.ButtonDown(actionId); return false; }
            if (Block(__instance)) { __result = false; return false; }
            if (TestDriver.NativeButtonTest && (actionId == 5 || actionId == 7)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButtonUp", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButtonUp(Rewired.Player __instance, int actionId, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.ButtonPadFor(__instance, actionId); if (pad != null) { __result = pad.ButtonUp(actionId); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAxis", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetAxis(Rewired.Player __instance, int actionId, ref float __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.Axis(actionId); return false; }
            if (Block(__instance)) { __result = 0f; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAxisRaw", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetAxisRaw(Rewired.Player __instance, int actionId, ref float __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.Axis(actionId); return false; }
            if (Block(__instance)) { __result = 0f; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButtonTimePressed", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButtonTimePressed(Rewired.Player __instance, int actionId, ref float __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.ButtonPadFor(__instance, actionId); if (pad != null) { __result = pad.TimePressed(actionId); return false; }
            if (Block(__instance)) { __result = 0f; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAnyButton"), HarmonyPrefix]
        private static bool GetAnyButton(Rewired.Player __instance, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.AnyButton(); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAnyButtonDown"), HarmonyPrefix]
        private static bool GetAnyButtonDown(Rewired.Player __instance, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.AnyButtonDown(); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAnyButtonUp"), HarmonyPrefix]
        private static bool GetAnyButtonUp(Rewired.Player __instance, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.AnyButtonUp(); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        /// <summary>The title screen only accepts P1's keyboard (PlayerManager.Update: controllers.Keyboard.GetAnyButtonDown on scene_title).</summary>
        [HarmonyPatch(typeof(Rewired.Controller), "GetAnyButtonDown"), HarmonyPostfix]
        private static void ControllerAnyButtonDown(Rewired.Controller __instance, ref bool __result)
        {
            if (!__result && TestDriver.KeyboardPulse > 0 && __instance is Rewired.Keyboard) __result = true;
        }

        /// <summary>
        /// A network player has no physical controller to lose. P2 counts as disconnected only while their link is
        /// down: then the game's own "reconnect controller" prompt pauses the host's game (as in couch co-op), and
        /// it dismisses itself and unpauses once they are back (ControllerDisconnectedPrompt.Update).
        /// </summary>
        [HarmonyPatch(typeof(PlayerManager), "IsControllerDisconnected"), HarmonyPrefix]
        private static bool IsControllerDisconnected(PlayerId playerId, ref bool __result)
        {
            if (RemoteInput.DriveHostPlayerTwo && playerId == PlayerId.PlayerTwo)
            {
                __result = Session.Role == Role.Host && Session.Reconnecting && PlayerManager.Multiplayer;
                return false;
            }
            return true;
        }
    }
}
