using System;
using System.Collections;
using System.IO;
using CupheadOnline.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Scripted end-to-end testing without touching the real keyboard. Set CUPHEAD_ONLINE_SCRIPT to a file of
    /// commands (one per line, '#' comments). On the host the script plays player 1 (Rewired player 0); on the
    /// client it replaces local sampling, so its presses travel the real network path and become player 2.
    ///
    ///   wait SEC                     sleep (realtime)
    ///   waitscene NAME [TIMEOUT]     host: active scene loaded; client: mirror of that scene loaded
    ///   waitp2 [TIMEOUT]             host only: player 2 has joined
    ///   anykey [TIMEOUT]             title screen "press any button" (P1 keyboard), retried until it loads
    ///   tap BUTTON [COUNT]           press and release a CupheadButton (Jump, Shoot, Accept, MenuDown, ...)
    ///   tapuntil BUTTON SCENES [T]   keep tapping until a matching scene is loaded
    ///   hold BUTTON SEC              hold a button
    ///   move X Y SEC                 hold the movement stick
    ///   level NAME [MODE]            host: load a boss level directly (Levels enum, e.g. Veggies; mode Easy/Normal/Hard)
    ///   hudweapon NAME               host: show the HUD weapon-switch icon (slides inside a UGUI Mask) for Weapon NAME
    ///   secondary NAME               host: give both players Weapon NAME as secondary weapon (in memory, not saved)
    ///   invincible                   host: keep the game's debug invincibility enabled, including new player spawns
    ///   keeprunning                  host: dismiss test-window/controller pauses during a scripted regression
    ///   scene NAME                   host: load a scene directly (only use with an initialized game)
    ///   dialog TEXT                  host: show literal regression text using the map's speech bubble
    ///   gameover                     host: display the current boss's game-over card without killing players
    ///   shot NAME                    save a screenshot to CUPHEAD_ONLINE_SHOTS/ROLE_NAME.png
    ///   burst NAME COUNT FPS         COUNT screenshots at FPS, starting on the next 10 s wall-clock boundary
    ///   align [PERIOD]               wait for the next PERIOD-second wall-clock mark (default 30) to sync two instances
    ///   burstbg NAME COUNT FPS       capture like burst, starting now, while the script keeps running
    ///   menu on|off                  show or hide the F8 online menu
    ///   netdrop SEC                  drop all network traffic, both ways, for SEC seconds (simulates a lost link)
    ///   waitdrop [TIMEOUT]           wait until the session notices the link is down and starts reconnecting
    ///   waitlink [TIMEOUT]           wait until the session is connected again (after a drop)
    ///   expect paused|running        host: check whether the game is paused (e.g. by the partner-disconnected prompt)
    ///   down BUTTON / up BUTTON      press / release a button and keep it that way
    ///   stick X Y                    set the movement stick and keep it there
    ///   state                        log scene / player / mirror state
    ///   dump                         log every camera, renderer, canvas and UI graphic
    ///   fxdump                       log particle systems, sprite/UI masks and per-renderer material state
    ///   quit                         exit the game
    /// </summary>
    public static class TestDriver
    {
        public static VirtualPad HostPad;
        public static int KeyboardPulse;
        public static float CaptionFailureUntil;
        public static bool NativeButtonTest;
        public static KeyCode[] PhysicalTestKeys = new KeyCode[0];
        public static readonly bool ReadOnlySaves = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_TEST_READONLY") == "1";
        private static bool keepInvincible, keepRunning;

        private static PadState pad;
        private static string role = "offline";

        private static MonoBehaviour runnerBehaviour;
        private static string signalDirectory;
        private static ushort rememberedSerial;

        public static void Start(MonoBehaviour runner)
        {
            runnerBehaviour = runner;
            string path = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_SCRIPT");
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                Plugin.Log.LogError("[test] script not found: " + path);
                return;
            }
            signalDirectory = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path))), "signals");
            runner.StartCoroutine(Run(File.ReadAllLines(path)));
        }

        public static void Update()
        {
            if (KeyboardPulse > 0) KeyboardPulse--;
            if (keepInvincible && !PlayerStatsManager.DebugInvincible) PlayerStatsManager.DebugToggleInvincible();
            if (keepRunning && PauseManager.state == PauseManager.State.Paused) PauseManager.Unpause();
        }

        private static void Log(string text)
        {
            Plugin.Log.LogInfo("[test:" + role + "] " + text);
        }

        private static IEnumerator Run(string[] lines)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Session.Role == Role.None && Time.realtimeSinceStartup < deadline) yield return null;
            if (Session.Role == Role.Host)
            {
                role = "host";
                HostPad = new VirtualPad();
                pad = HostPad.Latest;
            }
            else
            {
                role = Session.Role == Role.Client ? "client" : "offline";
                RemoteInput.Scripted = new PadState();
                pad = RemoteInput.Scripted;
            }
            Log("script started (" + lines.Length + " lines)");
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Log("> " + line);
                switch (a[0])
                {
                    case "nativebuttons":
                        // Keep the real SampleLocal -> packet -> host path. Inject only physical keyboard
                        // values below action mapping, while forcing the old mapped-action path to fail.
                        HostPad = null;
                        RemoteInput.Scripted = null;
                        NativeButtonTest = true;
                        Log("Native mapped-button regression enabled; native Dash/Switch actions forced neutral");
                        break;
                    case "physical":
                    {
                        string[] names = a[1].Split('+');
                        KeyCode[] keys = new KeyCode[names.Length];
                        for (int i = 0; i < keys.Length; i++) keys[i] = (KeyCode)Enum.Parse(typeof(KeyCode), names[i]);
                        for (int i = 0; i < (int)F(a, 2, 1); i++)
                        {
                            PhysicalTestKeys = keys;
                            yield return new WaitForSecondsRealtime(.12f);
                            PhysicalTestKeys = new KeyCode[0];
                            yield return new WaitForSecondsRealtime(.18f);
                        }
                        break;
                    }
                    case "signal":
                        Directory.CreateDirectory(signalDirectory);
                        File.WriteAllText(Path.Combine(signalDirectory, Path.GetFileName(a[1])), "ready");
                        break;
                    case "waitsignal":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 2, 60f);
                        string marker = Path.Combine(signalDirectory, Path.GetFileName(a[1]));
                        while (!File.Exists(marker) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.1f);
                        Log(File.Exists(marker) ? "PASS signal " + a[1] : "FAIL signal " + a[1]);
                        break;
                    }
                    case "wait":
                        yield return new WaitForSecondsRealtime(F(a, 1, 1f));
                        break;
                    case "waitscene":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 2, 60f);
                        while (!InScene(a[1]) && Time.realtimeSinceStartup < until) yield return null;
                        Log(InScene(a[1]) ? "PASS scene " + a[1] : "FAIL timed out waiting for scene " + a[1] + " (now " + SceneManager.GetActiveScene().name + ")");
                        break;
                    }
                    case "rememberscene":
                        rememberedSerial = HostSync.CurrentSerial;
                        break;
                    case "waitreload":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 1, 60f);
                        while ((HostSync.CurrentSerial == rememberedSerial || SceneLoader.CurrentlyLoading) && Time.realtimeSinceStartup < until) yield return null;
                        Log((HostSync.CurrentSerial != rememberedSerial && !SceneLoader.CurrentlyLoading ? "PASS " : "FAIL ") + "scene reload " + rememberedSerial + " -> " + HostSync.CurrentSerial);
                        break;
                    }
                    case "waitp2":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 1, 20f);
                        while (!P2Joined() && Time.realtimeSinceStartup < until) yield return null;
                        Log(P2Joined() ? "PASS player 2 joined" : "FAIL player 2 did not join");
                        break;
                    }
                    case "anykey":
                    {
                        // The title only accepts P1 after its intro animation, so keep pulsing until a load starts.
                        string from = SceneManager.GetActiveScene().name;
                        float until = Time.realtimeSinceStartup + F(a, 1, 30f);
                        while (!SceneLoader.CurrentlyLoading && SceneManager.GetActiveScene().name == from && Time.realtimeSinceStartup < until)
                        {
                            KeyboardPulse = 2;
                            yield return new WaitForSecondsRealtime(0.5f);
                        }
                        Log(SceneLoader.CurrentlyLoading || SceneManager.GetActiveScene().name != from ? "PASS anykey accepted" : "FAIL anykey ignored");
                        break;
                    }
                    case "tap":
                    {
                        int id = Button(a[1]);
                        int count = (int)F(a, 2, 1f);
                        for (int i = 0; i < count && id >= 0; i++)
                        {
                            pad.Presses[id]++;
                            yield return new WaitForSecondsRealtime(0.25f);
                        }
                        break;
                    }
                    case "tapuntil":
                    {
                        // tapuntil BUTTON SCENEPATTERN [TIMEOUT]: e.g. page through a cutscene until the level loads
                        int id = Button(a[1]);
                        float until = Time.realtimeSinceStartup + F(a, 3, 90f);
                        while (id >= 0 && !InScene(a[2]) && Time.realtimeSinceStartup < until)
                        {
                            pad.Presses[id]++;
                            yield return new WaitForSecondsRealtime(1.2f);
                        }
                        Log(InScene(a[2]) ? "PASS reached " + a[2] : "FAIL never reached " + a[2]);
                        break;
                    }
                    case "hold":
                    {
                        int id = Button(a[1]);
                        if (id < 0) break;
                        pad.Held |= 1u << id;
                        pad.Presses[id]++;
                        yield return new WaitForSecondsRealtime(F(a, 2, 0.5f));
                        pad.Held &= ~(1u << id);
                        break;
                    }
                    case "move":
                        pad.Axes[0] = F(a, 1, 0f);
                        pad.Axes[1] = F(a, 2, 0f);
                        yield return new WaitForSecondsRealtime(F(a, 3, 0.5f));
                        pad.Axes[0] = 0f;
                        pad.Axes[1] = 0f;
                        break;
                    case "level":
                    {
                        try
                        {
                            if (a.Length > 2) Level.SetCurrentMode((Level.Mode)Enum.Parse(typeof(Level.Mode), a[2], true));
                            SceneLoader.LoadLevel((Levels)Enum.Parse(typeof(Levels), a[1], true), SceneLoader.Transition.Fade);
                        }
                        catch (Exception ex) { Log("FAIL level " + a[1] + ": " + ex.Message); }
                        break;
                    }
                    case "scene":
                        SceneLoader.LoadScene(a[1], SceneLoader.Transition.Fade, SceneLoader.Transition.Fade, SceneLoader.Icon.None);
                        break;
                    case "dialog":
                        try
                        {
                            SpeechBubble bubble = SpeechBubble.Instance;
                            if (bubble == null) { Log("FAIL no SpeechBubble in this scene"); break; }
                            CupheadMapCamera mapCamera = CupheadMapCamera.Current;
                            if (mapCamera != null) bubble.basePosition = mapCamera.GetComponent<Camera>().ViewportToWorldPoint(new Vector3(0.75f, 0.15f, 1f));
                            string normalized = (string)HarmonyLib.AccessTools.Method(typeof(SpeechBubble), "GetNormalizedText")
                                .Invoke(bubble, new object[] { line.Substring(a[0].Length).Trim() });
                            bubble.StartCoroutine((IEnumerator)HarmonyLib.AccessTools.Method(typeof(SpeechBubble), "show_cr")
                                .Invoke(bubble, new object[] { SpeechBubble.Mode.Text, normalized, null }));
                        }
                        catch (Exception ex) { Log("FAIL dialog: " + (ex.InnerException ?? ex).Message); }
                        break;
                    case "hudweapon":
                        try
                        {
                            Weapon weapon = (Weapon)Enum.Parse(typeof(Weapon), a[1], true);
                            foreach (LevelHUDPlayer hud in UnityEngine.Object.FindObjectsOfType<LevelHUDPlayer>())
                                HarmonyLib.AccessTools.Method(typeof(LevelHUDPlayer), "OnWeaponChanged").Invoke(hud, new object[] { weapon });
                        }
                        catch (Exception ex) { Log("FAIL hudweapon " + a[1] + ": " + ex.Message); }
                        break;
                    case "secondary":
                        try
                        {
                            Weapon weapon = (Weapon)Enum.Parse(typeof(Weapon), a[1], true);
                            PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerOne).secondaryWeapon = weapon;
                            PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerTwo).secondaryWeapon = weapon;
                        }
                        catch (Exception ex) { Log("FAIL secondary " + a[1] + ": " + ex.Message); }
                        break;
                    case "invincible":
                        keepInvincible = true; // new player Awake resets the game's debug flag
                        if (!PlayerStatsManager.DebugInvincible) PlayerStatsManager.DebugToggleInvincible();
                        break;
                    case "keeprunning":
                        keepRunning = true;
                        break;
                    case "gameover":
                        if (LevelGameOverGUI.Current != null) LevelGameOverGUI.Current.In(false);
                        else Log("FAIL no game over UI");
                        break;
                    case "defeat":
                        foreach (PlayerId id in new[] { PlayerId.PlayerOne, PlayerId.PlayerTwo })
                        {
                            if (!PlayerManager.DoesPlayerExist(id)) continue;
                            var stats = PlayerManager.GetPlayer(id).stats;
                            if (stats.Health <= 0) continue;
                            stats.SetHealth(0);
                            HarmonyLib.AccessTools.Method(typeof(PlayerStatsManager), "OnStatsDeath").Invoke(stats, null);
                        }
                        break;
                    case "menustate":
                    case "expectdeathmenu":
                    case "expectequip":
                    {
                        var gui = LevelGameOverGUI.Current;
                        string value = gui != null ? HarmonyLib.AccessTools.Field(typeof(LevelGameOverGUI), "state").GetValue(gui).ToString() : "none";
                        var equip = gui != null ? (LevelEquipUI)HarmonyLib.AccessTools.Field(typeof(LevelGameOverGUI), "equipUI").GetValue(gui) : null;
                        bool equipVisible = equip != null && equip.gameObject.activeInHierarchy && equip.CurrentState == AbstractEquipUI.ActiveState.Active;
                        bool ready = gui != null && gui.gameObject.activeInHierarchy && value == "Ready" && !equipVisible;
                        bool expected = a[0] == "expectequip" ? equipVisible : ready;
                        Log((a[0] != "menustate" ? (expected ? "PASS " : "FAIL ") : "") + "death menu=" + value + " active=" + (gui != null && gui.gameObject.activeInHierarchy) + " equip=" + equipVisible + " serial=" + HostSync.CurrentSerial);
                        break;
                    }
                    case "captionfault":
                        CaptionFailureUntil = Time.realtimeSinceStartup + F(a, 1, 3f);
                        Log("Injecting caption metadata failure for input isolation check");
                        break;
                    case "equip":
                        keepRunning = false;
                        PauseManager.Unpause();
                        if (AbstractEquipUI.Current != null) AbstractEquipUI.Current.StartCoroutine(AbstractEquipUI.Current.ShowPauseMenu());
                        else Log("FAIL no equip UI");
                        break;
                    case "controls":
                    {
                        if (AbstractEquipUI.Current != null) AbstractEquipUI.Current.gameObject.SetActive(false);
                        PauseManager.Unpause();
                        var mapper = Cuphead.Current.controlMapper;
                        Canvas canvas = mapper.GetComponentInChildren<Canvas>(true);
                        CupheadUICamera camera = UnityEngine.Object.FindObjectOfType<CupheadUICamera>();
                        if (canvas != null && camera != null) canvas.worldCamera = camera.GetComponent<Camera>();
                        mapper.showPlayers = true;
                        mapper.Reset(); mapper.Open();
                        break;
                    }
                    case "controlsclose":
                        Cuphead.Current.controlMapper.Close(false);
                        break;
                    case "glyphletter":
                        foreach (MapUIInteractionDialogue dialogue in UnityEngine.Object.FindObjectsOfType<MapUIInteractionDialogue>())
                            foreach (CupheadGlyph glyph in dialogue.GetComponentsInChildren<CupheadGlyph>(true))
                                ((UnityEngine.UI.Text)HarmonyLib.AccessTools.Field(typeof(CupheadGlyph), "glyphChar").GetValue(glyph)).text = a[1];
                        break;
                    case "glyph":
                        MapUIInteractionDialogue.Create(new AbstractUIInteractionDialogue.Properties(""),
                            Map.Current.players[(int)F(a, 1, 0f)].input, new Vector2(F(a, 2, 0f), F(a, 3, 0.7f)));
                        break;
                    case "bind":
                    {
                        CupheadButton action = (CupheadButton)Enum.Parse(typeof(CupheadButton), a[1], true);
                        KeyCode key = (KeyCode)Enum.Parse(typeof(KeyCode), a[2], true);
                        var maps = Rewired.ReInput.players.GetPlayer(0).controllers.maps;
                        var binding = maps.GetFirstElementMapWithAction(Rewired.ControllerType.Keyboard, (int)action, true);
                        bool changed = binding != null && binding.controllerMap.ReplaceElementMap(binding.id, (int)action, Rewired.Pole.Positive, key, Rewired.ModifierKeyFlags.None);
                        PlayerManager.ControlsChanged();
                        Log((changed ? "PASS" : "FAIL") + " local binding " + action + "=" + CupheadInput.InputDisplayForButton(action, 0).text);
                        break;
                    }
                    case "glyphassert":
                    {
                        int player = (int)F(a, 1, 1f), found = 0;
                        string expected = a[2];
                        foreach (MapUIInteractionDialogue dialogue in UnityEngine.Object.FindObjectsOfType<MapUIInteractionDialogue>())
                            foreach (CupheadGlyph glyph in dialogue.GetComponentsInChildren<CupheadGlyph>(true))
                            {
                                if (glyph.rewiredPlayerId != player) continue;
                                found++;
                                var field = HarmonyLib.AccessTools.Field(typeof(CupheadGlyph), expected.Length > 1 ? "glyphText" : "glyphChar");
                                var text = (UnityEngine.UI.Text)field.GetValue(glyph);
                                Log((text.gameObject.activeInHierarchy && text.text == expected ? "PASS" : "FAIL") + " glyph player=" + player + " expected=" + expected + " actual=" + text.text + " active=" + text.gameObject.activeInHierarchy);
                            }
                        if (found == 0) Log("FAIL no glyph for player " + player);
                        break;
                    }
                    case "glyphdump":
                        foreach (MapUIInteractionDialogue dialogue in UnityEngine.Object.FindObjectsOfType<MapUIInteractionDialogue>())
                            foreach (CupheadGlyph glyph in dialogue.GetComponentsInChildren<CupheadGlyph>(true))
                            {
                                string label = CupheadInput.InputDisplayForButton(glyph.button, glyph.rewiredPlayerId).text;
                                Log("glyph player=" + glyph.rewiredPlayerId + " action=" + glyph.button + " display='" + label + "'");
                                foreach (UnityEngine.UI.Text text in glyph.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                                    Log("glyph text=" + text.name + " active=" + text.gameObject.activeInHierarchy + " value='" + text.text + "'");
                            }
                        break;
                    case "inventory":
                        foreach (Weapon weapon in Enum.GetValues(typeof(Weapon)))
                        {
                            if (!weapon.ToString().StartsWith("level_weapon_", StringComparison.Ordinal)) continue;
                            foreach (PlayerId id in new[] { PlayerId.PlayerOne, PlayerId.PlayerTwo })
                                if (!PlayerData.Data.IsUnlocked(id, weapon)) PlayerData.Data.Gift(id, weapon);
                        }
                        break;
                    case "shot":
                        Shot(a.Length > 1 ? a[1] : "shot");
                        yield return null;
                        break;
                    case "down":
                    {
                        int id = Button(a[1]);
                        if (id >= 0) { pad.Held |= 1u << id; pad.Presses[id]++; }
                        break;
                    }
                    case "up":
                    {
                        int id = Button(a[1]);
                        if (id >= 0) pad.Held &= ~(1u << id);
                        break;
                    }
                    case "stick":
                        pad.Axes[0] = F(a, 1, 0f);
                        pad.Axes[1] = F(a, 2, 0f);
                        break;
                    case "align":
                    {
                        // align [PERIOD]: wait for the next multiple of PERIOD seconds of wall-clock time (default 30)
                        long period = (long)(F(a, 1, 30f) * TimeSpan.TicksPerSecond);
                        long nowTicks = DateTime.UtcNow.Ticks;
                        DateTime target = new DateTime(nowTicks - nowTicks % period + period, DateTimeKind.Utc);
                        while (DateTime.UtcNow < target) yield return null;
                        Log("aligned at " + target.ToString("HH:mm:ss"));
                        break;
                    }
                    case "burstbg":
                        // burstbg NAME COUNT FPS: like burst, but starts now and runs while the script continues
                        runnerBehaviour.StartCoroutine(Burst(a.Length > 1 ? a[1] : "burst", (int)F(a, 2, 30f), F(a, 3, 10f), DateTime.UtcNow));
                        break;
                    case "menu":
                        CupheadOnline.UI.Overlay.Visible = a.Length < 2 || a[1] != "off";
                        break;
                    case "netdrop":
                        Connection.TestDropUntil = DateTime.UtcNow.AddSeconds(F(a, 1, 15f)).Ticks;
                        break;
                    case "waitdrop":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 1, 30f);
                        while (!Session.Reconnecting && Time.realtimeSinceStartup < until) yield return null;
                        Log(Session.Reconnecting ? "PASS reconnecting (" + Session.Status + ")" : "FAIL no drop noticed: " + Session.Status);
                        break;
                    }
                    case "waitlink":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 1, 60f);
                        while (Session.State != LinkState.Connected && Time.realtimeSinceStartup < until) yield return null;
                        Log(Session.State == LinkState.Connected ? "PASS link up (" + Session.Status + ")" : "FAIL link still down: " + Session.Status);
                        break;
                    }
                    case "expect":
                    {
                        bool paused = PauseManager.state == PauseManager.State.Paused;
                        bool want = a.Length > 1 && a[1] == "paused";
                        Log((paused == want ? "PASS " : "FAIL ") + "game is " + (paused ? "paused" : "running") + " (status: " + Session.Status + ")");
                        break;
                    }
                    case "burst":
                    {
                        // burst NAME COUNT FPS: frames start on the next 10-second wall-clock boundary, so a host and a
                        // client on the same machine capture the same moments (for side-by-side comparisons).
                        string name = a.Length > 1 ? a[1] : "burst";
                        int count = (int)F(a, 2, 30f);
                        float interval = 1f / Mathf.Max(1f, F(a, 3, 10f));
                        DateTime now = DateTime.UtcNow;
                        DateTime start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(10 - now.Second % 10);
                        while (DateTime.UtcNow < start) yield return null;
                        for (int k = 0; k < count; k++)
                        {
                            DateTime due = start.AddSeconds(k * interval);
                            while (DateTime.UtcNow < due) yield return null;
                            Shot(name + "_" + k.ToString("000"));
                            yield return null;
                        }
                        Log("burst " + name + " done");
                        break;
                    }
                    case "state":
                        Log(State());
                        break;
                    case "expectinput":
                    {
                        uint dash = (uint)F(a, 1, 0f), weapon = (uint)F(a, 2, 0f);
                        bool correct = RemoteInput.Remote.Latest.Presses[7] == (byte)dash && RemoteInput.Remote.Latest.Presses[5] == (byte)weapon &&
                            RemoteInput.Remote.FixedDownCount(7) == dash && RemoteInput.Remote.FixedDownCount(5) == weapon;
                        Log((correct ? "PASS " : "FAIL ") + RemoteInput.Summary(true));
                        break;
                    }
                    case "expecthostinput":
                    {
                        uint dash = (uint)F(a, 1, 0), weapon = (uint)F(a, 2, 0);
                        VirtualPad input = RemoteInput.ButtonPadFor(Rewired.ReInput.players.GetPlayer(0), 7);
                        bool correct = input != null && input.Latest.Presses[7] == (byte)dash && input.Latest.Presses[5] == (byte)weapon
                            && input.FixedDownCount(7) == dash && input.FixedDownCount(5) == weapon;
                        Log((correct ? "PASS " : "FAIL ") + "host mapped input/physics " + dash + "/" + weapon);
                        break;
                    }
                    case "musicstate":
                        Log(MusicSync.Summary());
                        break;
                    case "silencemusic":
                        MusicSync.StopLocalSourcesForTest();
                        Log("Stopped local music sources for recovery regression");
                        break;
                    case "retry":
                        SceneLoader.ReloadLevel();
                        break;
                    case "retrymenu":
                        if (LevelGameOverGUI.Current != null)
                            HarmonyLib.AccessTools.Method(typeof(LevelGameOverGUI), "Retry").Invoke(LevelGameOverGUI.Current, null);
                        else Log("FAIL no game over UI for retry");
                        break;
                    case "resolution":
                        Screen.SetResolution((int)F(a, 1, 960f), (int)F(a, 2, 540f), false);
                        yield return new WaitForSecondsRealtime(1f);
                        break;
                    case "dump":
                        DebugDump.Dump("script");
                        break;
                    case "fxdump":
                        DebugDump.DumpEffects("script");
                        break;
                    case "quit":
                        Log("quit");
                        Application.Quit();
                        yield break;
                    default:
                        Log("unknown command " + a[0]);
                        break;
                }
            }
            Log("script finished");
        }

        private static IEnumerator Burst(string name, int count, float fps, DateTime start)
        {
            float interval = 1f / Mathf.Max(1f, fps);
            for (int k = 0; k < count; k++)
            {
                DateTime due = start.AddSeconds(k * interval);
                while (DateTime.UtcNow < due) yield return null;
                Shot(name + "_" + k.ToString("000"));
                yield return null;
            }
            Log("burst " + name + " done");
        }

        private static float F(string[] a, int i, float fallback)
        {
            float v;
            return a.Length > i && float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static int Button(string name)
        {
            try { return (int)(CupheadButton)Enum.Parse(typeof(CupheadButton), name, true); }
            catch
            {
                Log("unknown button " + name);
                return -1;
            }
        }

        /// <summary>Pattern: names separated by '|', each may end with '*' for a prefix match.</summary>
        private static bool InScene(string pattern)
        {
            string current;
            bool loading;
            if (Session.Role == Role.Client) { current = ClientMirror.LoadedScene; loading = ClientMirror.Loading; }
            else { current = SceneManager.GetActiveScene().name; loading = SceneLoader.CurrentlyLoading; }
            if (loading || current == null) return false;
            foreach (string option in pattern.Split('|'))
            {
                if (option.EndsWith("*") ? current.StartsWith(option.Substring(0, option.Length - 1), StringComparison.Ordinal) : current == option) return true;
            }
            return false;
        }

        private static bool P2Joined()
        {
            try { return PlayerManager.Multiplayer && PlayerManager.DoesPlayerExist(PlayerId.PlayerTwo); }
            catch { return false; }
        }

        private static void Shot(string name)
        {
            string dir = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_SHOTS");
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            string file = Path.Combine(dir, role + "_" + name + ".png");
            ScreenCapture.CaptureScreenshot(file);
            Log("screenshot " + file);
        }

        private static string State()
        {
            string s = "scene=" + SceneManager.GetActiveScene().name + " loading=" + SceneLoader.CurrentlyLoading + " paused=" + PauseManager.state +
                " menu=" + CupheadOnline.UI.Overlay.Visible + " cursor=" + Cursor.visible + "/" + Cursor.lockState;
            if (Session.Role == Role.Client) return s + " " + RemoteInput.Summary(false) + " " + ClientMirror.Summary();
            s += " multiplayer=" + PlayerManager.Multiplayer;
            s += " " + RemoteInput.Summary(true);
            for (int i = 0; i < 2; i++)
            {
                PlayerId id = (PlayerId)i;
                try
                {
                    if (!PlayerManager.DoesPlayerExist(id)) { s += " p" + (i + 1) + "=none"; continue; }
                    AbstractPlayerController p = PlayerManager.GetPlayer(id);
                    Vector3 pos = p.transform.position;
                    s += string.Format(" p{0}=({1:0},{2:0}) hp={3}", i + 1, pos.x, pos.y, p.stats != null ? p.stats.Health : -1);
                    s += " inputPlayer=" + (p.input.actions != null ? p.input.actions.id.ToString() : "null");
                    LevelPlayerWeaponManager weapons = p.GetComponent<LevelPlayerWeaponManager>();
                    if (weapons != null) s += " weapon=" + HarmonyLib.Traverse.Create(weapons).Field("currentWeapon").GetValue();
                    LevelPlayerMotor motor = p.GetComponent<LevelPlayerMotor>();
                    if (motor != null) s += " dash=" + motor.DashState + " fixedTime=" + Time.fixedTime;
                }
                catch (Exception ex) { s += " p" + (i + 1) + "=? (" + ex.GetType().Name + ")"; }
            }
            return s;
        }
    }
}
