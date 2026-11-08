using System;
using CupheadOnline.Net;
using CupheadOnline.Sync;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Step(float now, bool physics)
    {
        Time.realtimeSinceStartup = Time.unscaledTime = now;
        Time.frameCount++;
        Time.inFixedTimeStep = physics;
        if (physics) Time.fixedTime += 0.02f;
    }
    private static void Input(uint sequence, uint held, byte presses, double now)
    {
        NetWriter w = new NetWriter(); w.U32(sequence);
        PadState state = new PadState { Held = held }; state.Presses[7] = presses; state.Write(w);
        RemoteInput.OnInputMessage(new NetReader(w.ToArray(), 0, w.Length), now);
    }
    private static AudioManagerComponent Audio(int count, bool playing)
    {
        var component = new AudioManagerComponent();
        for (int i = 0; i < count; i++) component.bgmSources.Add(new AudioManagerComponent.SoundGroup.Source
        {
            audio = new AudioSource { clip = new AudioClip { name = "world_" + i }, isPlaying = playing,
                loop = true, time = 15f, volume = 0.4f, pitch = 0.9f }
        });
        UnityEngine.Object.Loaded = new UnityEngine.Object[] { component };
        return component;
    }
    private static void DeliverMusic(AudioManagerComponent host, AudioManagerComponent client, bool bootstrap = false)
    {
        UnityEngine.Object.Loaded = new UnityEngine.Object[] { host };
        var sent = new MemoryTransport(); var sender = new Connection(sent);
        MusicSync.Send(sender, 1, bootstrap); sender.Flush();
        UnityEngine.Object.Loaded = new UnityEngine.Object[] { client };
        var received = new MemoryTransport(); var receiver = new Connection(received);
        receiver.OnMessage += (type, r) => { if (type == MsgType.MusicState) MusicSync.Read(r); };
        foreach (byte[] packet in sent.Sent) received.Received.Enqueue(packet);
        receiver.Receive();
    }
    private static void Main()
    {
        var pad = new VirtualPad(); pad.Reset();
        Step(1f, false); pad.Latest.Presses[7]++;
        Check(pad.ButtonDown(7) && pad.ButtonDown(7), "dash edge remains readable throughout Update");
        Step(1.01f, true);
        Check(pad.ButtonDown(7), "Update does not consume FixedUpdate dash edge");
        Step(1.03f, true);
        Check(!pad.ButtonDown(7) && pad.ButtonUp(7), "short tap synthesizes one fixed edge then releases");
        Step(8f, false); pad.Latest.Presses[5]++; pad.ObservePresses();
        Step(8.01f, true);
        Check(pad.ButtonDown(5), "fresh weapon switch survives long gap in physics polling");
        Step(9f, false); pad.Latest.Presses[7]++; pad.ObservePresses();
        Step(10f, true);
        Check(!pad.ButtonDown(7), "old menu tap expires before physics resumes");
        Step(11f, true); pad.Latest.Held = 1u << 7; pad.Latest.Presses[7]++;
        Check(pad.ButtonDown(7), "held dash press");
        Step(11.02f, true); pad.Latest.Presses[7]++;
        Check(pad.ButtonDown(7), "lost release packet does not swallow next press");
        pad.Latest.Presses[7] = 255; pad.SyncPresses(); pad.ObservePresses();
        Step(12f, true); pad.Latest.Presses[7] = 0;
        Check(pad.ButtonDown(7), "byte press counter wraps correctly");
        pad.Reset(); Step(13f, true);
        Check(!pad.AnyButton() && !pad.ButtonDown(7), "reset releases held state and old counters");
        Check(!pad.Button(-1) && !pad.ButtonDown(27) && pad.Axis(999) == 0f, "invalid action IDs are neutral");

        var menuPad = new VirtualPad(); menuPad.Reset();
        Step(14f, true); menuPad.Latest.Presses[13]++; menuPad.Latest.Presses[15]++; menuPad.ObservePresses();
        menuPad.ButtonDown(2); // gameplay polls physics, but no Update consumer has asked for menu buttons
        Step(17f, false);
        Check(!menuPad.ButtonDown(13) && !menuPad.ButtonDown(15), "old combat presses cannot replay as Accept or Equip when the death menu first polls Update");
        Step(17.1f, false); menuPad.Latest.Held = (1u << 13) | (1u << 15); menuPad.Latest.Presses[13]++; menuPad.Latest.Presses[15]++; menuPad.ObservePresses();
        menuPad.SuppressUntilRelease((1u << 13) | (1u << 15));
        Check(!menuPad.ButtonDown(13) && !menuPad.ButtonDown(15), "death-menu boundary discards a fresh A/Y from its opening animation");
        Step(17.12f, false);
        Check(!menuPad.Button(13) && !menuPad.ButtonDown(15), "holding combat A/Y cannot activate the newly ready menu");
        menuPad.Latest.Held = 0; menuPad.ObservePresses(); Step(17.14f, false); menuPad.Button(13);
        menuPad.Latest.Presses[13]++; menuPad.ObservePresses(); Step(17.16f, false);
        Check(menuPad.ButtonDown(13) && !menuPad.ButtonDown(15), "a fresh Accept after release still selects Retry");
        Step(17.18f, true); menuPad.Latest.Presses[7]++; menuPad.ObservePresses();
        menuPad.SuppressUntilRelease((1u << 13) | (1u << 15));
        Check(menuPad.ButtonDown(7), "menu A/Y gate does not discard a fresh gameplay dash");
        menuPad.Reset(); Step(17.2f, false); menuPad.Latest.Presses[15]++; menuPad.ObservePresses();
        Check(menuPad.ButtonDown(15), "reset clears menu suppression for a new session");

        Check(ButtonCaption.Format("A", null, false) == "A" && ButtonCaption.Format("Left Shoulder", null, false) == "LB" && ButtonCaption.Format("Right Trigger +", null, false) == "RT",
            "explicit gamepad maps keep native A/LB/RT caption names");
        Check(ButtonCaption.Format("Left Shift", "Левый Shift", true) == "SHIFT" && ButtonCaption.Format("Space", "Пробел", false) == "ПРОБЕЛ" && ButtonCaption.Format("", "unused", false) == "",
            "explicit keyboard captions preserve localization and unbound actions");

        RemoteInput.ResetHost(); Step(20f, false);
        Input(uint.MaxValue, 1u << 7, 1, 0);
        Input(0, 0, 2, 0.1);
        Input(uint.MaxValue, 1u << 7, 1, 0.2);
        Check(RemoteInput.Remote.Latest.Held == 0 && RemoteInput.Remote.Latest.Presses[7] == 2, "input sequence wraps and rejects reordered packets");
        Input(1, 1u << 7, 3, 0.25); RemoteInput.ReleaseIfStale(0.6);
        Check(RemoteInput.Remote.Latest.Held == 0, "stale input releases held buttons");

        var senderTransport = new MemoryTransport(); var sender = new Connection(senderTransport);
        var receiverTransport = new MemoryTransport(); var receiver = new Connection(receiverTransport);
        int delivered = 0;
        byte sampledDashPresses = 0;
        receiver.OnMessage += (type, r) => { if (type == MsgType.Input) { r.U32(); var state = new PadState(); state.Read(r); sampledDashPresses = state.Presses[7]; delivered++; } };
        RemoteInput.Scripted = null;
        Rewired.ReInput.players.Local.Held = 1u << 7; // down edge deliberately unavailable
        RemoteInput.SampleAndSend(sender); sender.Flush();
        foreach (byte[] packet in senderTransport.Sent) receiverTransport.Received.Enqueue(packet);
        receiver.Receive();
        Check(delivered == 1, "real input sampler serializes through production datagram layer");
        Check(sampledDashPresses == 1, "held transition recovers unavailable local down edge");

        RemoteInput.Install(); Session.Role = Role.Client;
        Step(21f, false);
        Rewired.ReInput.players.Local.Held = 0;
        Rewired.ReInput.players.Local.Down = 1u << 5;
        Rewired.ReInput.EndUpdate(Rewired.UpdateLoopType.Update);
        Rewired.ReInput.EndUpdate(Rewired.UpdateLoopType.Update);
        Rewired.ReInput.players.Local.Down = 0; // the edge is no longer available by LateUpdate
        senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        RemoteInput.SampleAndSend(sender); sender.Flush();
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        byte sampledSwitchPresses = 0;
        receiver.OnMessage += (type, r) => { if (type == MsgType.Input) { r.U32(); var state = new PadState(); state.Read(r); sampledSwitchPresses = state.Presses[5]; } };
        foreach (byte[] packet in senderTransport.Sent) receiverTransport.Received.Enqueue(packet);
        receiver.Receive();
        Check(sampledSwitchPresses == 1, "Rewired Update edge survives until send and is sampled once per frame");
        RemoteInput.ResetClient();
        Check(RemoteInput.Summary(false).Contains("Dash=0, Switch=0"), "new client session clears cached local controller state");
        Session.Role = Role.None;

        var bufferedTransport = new MemoryTransport(); var bufferedSender = new Connection(bufferedTransport);
        var bufferedInbox = new MemoryTransport(); var bufferedReceiver = new Connection(bufferedInbox);
        PadState bufferedState = null;
        bufferedReceiver.OnMessage += (type, r) => { if (type == MsgType.Input) { r.U32(); bufferedState = new PadState(); bufferedState.Read(r); } };
        Action sampleBuffered = () =>
        {
            Rewired.ReInput.EndUpdate(Rewired.UpdateLoopType.Update);
            RemoteInput.SampleAndSend(bufferedSender); bufferedSender.Flush();
            foreach (byte[] packet in bufferedTransport.Sent) bufferedInbox.Received.Enqueue(packet);
            bufferedTransport.Sent.Clear(); bufferedReceiver.Receive();
        };
        Session.Role = Role.Client;
        Rewired.ReInput.players.Local.Held = 1u << 7; Rewired.ReInput.players.Local.Down = 1u << 7;
        Step(22f, false); sampleBuffered();
        Rewired.ReInput.players.Local.Held = 0; // the native down buffer outlives the physical release
        Step(22.02f, false); sampleBuffered(); Step(22.04f, false); sampleBuffered();
        Check(bufferedState.Presses[7] == 1 && bufferedState.Held == 0,
            "a buffered native GetButtonDown is sent once even when it remains true after release");
        Rewired.ReInput.players.Local.Held = 1u << 7;
        Step(22.06f, false); sampleBuffered();
        Check(bufferedState.Presses[7] == 2,
            "a second physical held transition survives an overlapping native down buffer");
        RemoteInput.ResetClient(); Session.Role = Role.None;

        var localLabels = new InputLabels(); var remoteLabels = new InputLabels();
        senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        int labelChanges = 0;
        receiver.OnMessage += (type, r) => { if (type == MsgType.InputLabel && remoteLabels.Read(r)) labelChanges++; };
        localLabels.SendChanged(sender, 13, "K", 4);
        localLabels.SendChanged(sender, 13, "K", 4);
        Check(sender.PendingReliableCount == 1, "unchanged input captions do not add repeated reliable traffic");
        sender.Flush();
        foreach (byte[] packet in senderTransport.Sent) receiverTransport.Received.Enqueue(packet);
        receiver.Receive();
        InputLabels.Label label;
        Check(remoteLabels.TryGet(13, out label) && label.Text == "K" && label.Font == 4 && labelChanges == 1,
            "client button caption and font arrive through production reliable transport");
        receiverTransport.Received.Enqueue(senderTransport.Sent[0]); receiver.Receive();
        Check(labelChanges == 1, "retransmitted caption is delivered only once");
        var rebound = new NetWriter(); rebound.U8(13); rebound.I32(5); rebound.Str("ВВОД");
        Check(remoteLabels.Read(new NetReader(rebound.ToArray(), 0, rebound.Length)) && remoteLabels.TryGet(13, out label) && label.Text == "ВВОД" && label.Font == 5,
            "rebinding replaces a one-letter caption with a localized multi-character caption");
        var unbound = new NetWriter(); unbound.U8(13); unbound.I32(-1); unbound.Str("");
        Check(remoteLabels.Read(new NetReader(unbound.ToArray(), 0, unbound.Length)) && remoteLabels.TryGet(13, out label) && label.Text == "",
            "an explicitly unbound button remains empty rather than inventing a key");
        localLabels.Clear(); localLabels.SendChanged(sender, 13, "K", 4);
        Check(sender.PendingReliableCount == 2, "reconnection resends captions even when local bindings have not changed");
        remoteLabels.Clear();
        Check(!remoteLabels.TryGet(13, out label), "new session discards the previous client's button captions");
        int validMessages = sender.PendingReliableCount;
        localLabels.SendChanged(sender, 255, "K", 4); localLabels.SendChanged(sender, 13, new string('Ж', 129), 4);
        Check(sender.PendingReliableCount == validMessages, "invalid actions and oversized captions cannot produce metadata packets");

        var fields = new SnapshotFields();
        Check(fields.Accept(101, 1) == 1 && fields.Accept(100, 16) == 16,
            "late text accepts its own version after a newer position delta");
        Check(fields.Accept(99, 17) == 0 && fields.Accept(102, 16) == 16,
            "older position/text cannot overwrite newer values");
        var wrappedFields = new SnapshotFields();
        wrappedFields.Accept(uint.MaxValue, 1);
        Check(wrappedFields.Accept(0, 1) == 1, "field version comparison handles frame wrap");

        var dialogue = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++) dialogue.Append("Реплика<space=11.19853> персонажа. ");
        var dialogueWire = new NetWriter(); dialogueWire.Str(dialogue.ToString());
        senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        UiFragments.Send(sender, 7, 40, 12, dialogueWire.Buffer, 0, dialogueWire.Length); sender.Flush();
        Check(senderTransport.Sent.Count > 1, "long Cyrillic NPC rich text is fragmented instead of discarded");
        var assembly = new UiFragments(); byte[] assembled = null;
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        receiver.OnMessage += (type, r) =>
        {
            if (type != MsgType.UiFragment) return;
            ushort serial = r.U16(); uint frame = r.U32(); ushort id = r.U16();
            if (serial != 7) throw new Exception("Wrong scene");
            assembled = assembly.Read(frame, id, r) ?? assembled;
        };
        bool withinMtu = true;
        for (int i = senderTransport.Sent.Count - 1; i >= 0; i--)
        {
            withinMtu &= senderTransport.Sent[i].Length <= senderTransport.MaxDatagramSize;
            receiverTransport.Received.Enqueue(senderTransport.Sent[i]);
        }
        receiver.Receive();
        Check(withinMtu, "fragmented text stays within datagram MTU");
        Check(assembled != null && new NetReader(assembled, 0, assembled.Length).Str() == dialogue.ToString(),
            "complete NPC text reassembles unchanged after reordered packets");
        var invalid = new NetWriter(); invalid.I32(UiFragments.MaxEntry + 1); invalid.I32(0); invalid.U8(1);
        bool rejected = false;
        try { assembly.Read(41, 12, new NetReader(invalid.Buffer, 0, invalid.Length)); }
        catch (FormatException) { rejected = true; }
        Check(rejected, "oversized fragment allocation is bounded");

        Check(AssetNames.Sprite("equip_icon_weapon_charge_0001(Clone)") == "equip_icon_weapon_charge_0001",
            "atlas clone names resolve to the original packed sprite name");
        Check(AssetNames.Sprite("icon (Clone)(Clone)") == "icon" && AssetNames.Sprite("icon_0001") == "icon_0001",
            "clone normalization handles repeated/space suffixes and preserves real names");
        Check(Math.Abs(UiTextLayout.FontUnits(22, 16, 0.75f, true) * 0.75f - 16f) < 0.001f,
            "best-fit font pixel size is not scaled twice on a smaller canvas");
        Check(UiTextLayout.FontUnits(24, 36, 1.5f, true) * 1.5f == 36f,
            "best-fit font pixel size is not scaled twice on a larger canvas");
        Check(UiTextLayout.FontUnits(24, 36, 1.5f, false) == 24f && UiTextLayout.FontUnits(24, 0, 0f, true) == 24f,
            "fixed fonts and unavailable best-fit metrics use requested local size");
        Check(UiTextLayout.RectAspect(100, 40, 75, 40) == 0.75f && UiTextLayout.RectAspect(0, 40, 75, 40) == 1f,
            "text preserves non-uniform parent scale and handles empty rects");
        var layout = new UiTextLayout { HorizontalOverflow = 1, VerticalOverflow = 1, AlignByGeometry = true,
            Aspect = 0.75f, Left = 0.1f, Top = 0.2f, Right = 0.3f, Bottom = 0.4f,
            CharacterSpacing = 2.5f, ParagraphSpacing = 3f, WidthAdjustment = 0.1f, Kerning = true, ParseControls = true,
            OverflowMode = 2, VisibleCharacters = 80, VisibleWords = 12, VisibleLines = 4, Page = 2 };
        var layoutWire = new NetWriter(); layout.Write(layoutWire);
        var layoutRead = new UiTextLayout(); var layoutReader = new NetReader(layoutWire.Buffer, 0, layoutWire.Length);
        layoutRead.Read(layoutReader);
        Check(layout.Same(ref layoutRead) && layoutReader.Remaining == 0, "text wrap/margins/spacing/visibility survive serialization");
        layoutRead.HorizontalOverflow = 0;
        Check(!layout.Same(ref layoutRead), "a change in wrapping produces a layout delta");
        Check(UiTextLines.Insert("Один Два Три", new[] { 5, 9 }) == "Один \nДва \nТри",
            "native Cyrillic line starts become explicit line breaks");
        Check(UiTextLines.Insert("Один\nДва", new[] { 5 }) == "Один\nДва" &&
            UiTextLines.Insert("МЕЛКАШКА", new int[0]) == "МЕЛКАШКА",
            "existing newlines and single-line labels are preserved");

        Step(30f, false); ClientMirror.LoadedSerial = 1; MusicSync.Clear();
        Audio(80, true); senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        AudioListener.pause = false; MusicSync.Send(sender, 1); sender.Flush();
        Check(senderTransport.Sent.Count > 1, "music bootstrap splits large source lists into datagrams");
        foreach (byte[] packet in senderTransport.Sent) Check(packet.Length <= 1200, "music datagram respects transport MTU");
        var clientAudio = Audio(80, false);
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        int musicMessages = 0;
        receiver.OnMessage += (type, r) => { if (type == MsgType.MusicState) { musicMessages++; MusicSync.Read(r); } };
        // Reliable messages must reassemble in order even when complete datagrams arrive backwards.
        for (int i = senderTransport.Sent.Count - 1; i >= 0; i--) receiverTransport.Received.Enqueue(senderTransport.Sent[i]);
        receiver.Receive(); SceneLoader.CurrentlyLoading = true; MusicSync.Update();
        Check(!clientAudio.bgmSources[0].audio.isPlaying, "music waits for loader completion");
        SceneLoader.CurrentlyLoading = false; Step(30.1f, false); MusicSync.Update();
        Check(musicMessages > 1 && clientAudio.bgmSources[79].audio.isPlaying, "late join restores all music sources after reordered chunks");
        Check(Math.Abs(clientAudio.bgmSources[0].audio.time - 15.09f) < 0.001f && clientAudio.bgmSources[0].audio.volume == 0.4f,
            "music restores playback position, pitch and volume");
        MusicSync.Update(); Check(clientAudio.bgmSources[0].audio.Plays == 1, "music bootstrap is applied once");

        senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        Audio(1, true); MusicSync.Send(sender, 1); sender.Flush();
        clientAudio = Audio(1, false); clientAudio.bgmSources[0].audio.clip = null;
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        receiver.OnMessage += (type, r) => { if (type == MsgType.MusicState) MusicSync.Read(r); };
        foreach (byte[] packet in senderTransport.Sent) receiverTransport.Received.Enqueue(packet);
        receiver.Receive(); MusicSync.Update();
        Check(!clientAudio.bgmSources[0].audio.isPlaying, "deferred music asset waits for matching clip");
        clientAudio.bgmSources[0].audio.clip = new AudioClip { name = "world_0" }; MusicSync.Update();
        Check(clientAudio.bgmSources[0].audio.isPlaying, "deferred music retries when the asset arrives");

        senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        Audio(1, false); MusicSync.Send(sender, 1); sender.Flush();
        clientAudio = Audio(1, true);
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        receiver.OnMessage += (type, r) => { if (type == MsgType.MusicState) MusicSync.Read(r); };
        foreach (byte[] packet in senderTransport.Sent) receiverTransport.Received.Enqueue(packet);
        receiver.Receive(); MusicSync.Update();
        Check(!clientAudio.bgmSources[0].audio.isPlaying, "stopped host track suppresses client loader autoplay");

        senderTransport = new MemoryTransport(); sender = new Connection(senderTransport);
        Audio(1, true); MusicSync.Send(sender, 1); sender.Flush();
        clientAudio = Audio(1, false); ClientMirror.LoadedSerial = 2;
        receiverTransport = new MemoryTransport(); receiver = new Connection(receiverTransport);
        receiver.OnMessage += (type, r) => { if (type == MsgType.MusicState) MusicSync.Read(r); };
        foreach (byte[] packet in senderTransport.Sent) receiverTransport.Received.Enqueue(packet);
        receiver.Receive(); MusicSync.Update();
        Check(!clientAudio.bgmSources[0].audio.isPlaying, "old scene music bootstrap cannot start in a newer scene");

        ClientMirror.LoadedSerial = 1; Step(40f, false); MusicSync.Clear();
        var hostAudio = Audio(1, true); clientAudio = Audio(1, false);
        int resets = AudioManager.MixerResets;
        SceneLoader.CurrentlyLoading = true;
        DeliverMusic(hostAudio, clientAudio, true);
        DeliverMusic(hostAudio, clientAudio, false);
        MusicSync.Update();
        Check(AudioManager.MixerResets == resets, "music state waits while the client loader is active");
        SceneLoader.CurrentlyLoading = false; MusicSync.Update();
        Check(AudioManager.MixerResets == resets + 1 && clientAudio.bgmSources[0].audio.isPlaying,
            "periodic state preserves an unapplied initial mixer reset during slow loading");
        int plays = clientAudio.bgmSources[0].audio.Plays;
        resets = AudioManager.MixerResets;
        DeliverMusic(hostAudio, clientAudio); MusicSync.Update();
        Check(clientAudio.bgmSources[0].audio.Plays == plays && AudioManager.MixerResets == resets,
            "periodic state neither restarts healthy music nor resets the mixer");
        clientAudio.bgmSources[0].audio.Stop();
        DeliverMusic(hostAudio, clientAudio); MusicSync.Update();
        Check(clientAudio.bgmSources[0].audio.isPlaying && clientAudio.bgmSources[0].audio.Plays == plays + 1,
            "periodic state recovers client music that stopped without another PlayBGM event");

        clientAudio.bgmSources[0].audio.Stop(); clientAudio.bgmSources[0].audio.clip = null;
        clientAudio.bgmSources[0].audio.Deferred = new DeferredAudioSource
            { OnInitialize = () => clientAudio.bgmSources[0].audio.clip = new AudioClip { name = "world_0" } };
        DeliverMusic(hostAudio, clientAudio); MusicSync.Update();
        Check(clientAudio.bgmSources[0].audio.isPlaying, "retry recovery reinitializes a deferred music clip from the new scene cache");
        hostAudio.bgmSources[0].audio.ignoreListenerPause = true; AudioListener.pause = true;
        DeliverMusic(hostAudio, clientAudio); MusicSync.Update();
        Check(clientAudio.bgmSources[0].audio.ignoreListenerPause && AudioListener.pause,
            "music preserves the host's listener-pause behavior");
        AudioListener.pause = false;

        MusicSync.ResetHost(); var scheduleTransport = new MemoryTransport(); var scheduled = new Connection(scheduleTransport);
        MusicSync.SceneReady(1); SceneLoader.CurrentlyLoading = true; Step(51f, false);
        MusicSync.TickHost(scheduled, 1);
        Check(scheduled.PendingReliableCount == 0, "host waits for its own loader before capturing initial music");
        SceneLoader.CurrentlyLoading = false; MusicSync.TickHost(scheduled, 1);
        int firstMessages = scheduled.PendingReliableCount;
        Step(51.5f, false); MusicSync.TickHost(scheduled, 1);
        Check(firstMessages > 0 && scheduled.PendingReliableCount == firstMessages, "music recovery snapshots are rate limited");
        Step(52.1f, false); MusicSync.TickHost(scheduled, 1);
        Check(scheduled.PendingReliableCount > firstMessages, "host sends periodic recovery snapshots after scene ready");
        int beforeWrongScene = scheduled.PendingReliableCount;
        Step(54f, false); MusicSync.TickHost(scheduled, 2);
        Check(scheduled.PendingReliableCount == beforeWrongScene, "new scene requires a matching SceneReady before music state");
        Console.WriteLine("Sync checks: " + checks + " passed. Engine boundaries are stubs; two-game validation is still required.");
    }
}
