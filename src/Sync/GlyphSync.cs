using System;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>The network P2 has no local Rewired map on the host. Use the client's native display strings.</summary>
    public static class GlyphSync
    {
        private static readonly InputLabels sent = new InputLabels(), remote = new InputLabels();
        private static readonly CupheadButton[] actions = (CupheadButton[])Enum.GetValues(typeof(CupheadButton));
        private static readonly FieldInfo dialogueBack = AccessTools.Field(typeof(AbstractUIInteractionDialogue), "back");
        private static readonly PropertyInfo preferredWidth = AccessTools.Property(typeof(AbstractUIInteractionDialogue), "PreferredWidth");
        private static float nextPoll;
        private static bool dirty;
        public static string LocalDevice { get; private set; }

        public static void Reset()
        {
            sent.Clear(); remote.Clear(); nextPoll = 0f; dirty = true; LocalDevice = "pending";
        }

        public static void TickClient(Connection connection)
        {
            if (Time.realtimeSinceStartup < nextPoll || Localization.Instance == null) return;
            nextPoll = Time.realtimeSinceStartup + 0.25f;
            if (Time.realtimeSinceStartup < TestDriver.CaptionFailureUntil) throw new InvalidOperationException("Injected caption failure (test driver)");
            Player player = ReInput.players.GetPlayer(0);
            var controllers = player.controllers;
            // Desktop/Steam keyboard events must not make a connected gamepad's captions oscillate. Read
            // the assigned joystick's maps directly, without changing last-active state or controller maps.
            Controller controller = controllers.joystickCount > 0
                ? controllers.GetLastActiveController(ControllerType.Joystick) ?? controllers.Joysticks[0]
                : controllers.Keyboard;
            if (controller == null) return;
            LocalDevice = controller.type + ": " + controller.name;
            foreach (CupheadButton action in actions)
            {
                if (action == CupheadButton.None) continue;
                ActionElementMap binding = controllers.maps.GetFirstElementMapWithAction(controller, (int)action, true);
                string name = binding != null ? binding.elementIdentifierName : "";
                Localization.Translation translation = string.IsNullOrEmpty(name) ? default(Localization.Translation) : Localization.Translate(name);
                string caption = ButtonCaption.Format(name, translation.text, action == CupheadButton.EquipMenu);
                Localization.CategoryLanguageFont font = translation.fonts ?? Localization.Instance.fonts[(int)Localization.language][29];
                sent.SendChanged(connection, (int)action, caption, font != null ? (int)font.fontType : -1);
            }
        }

        public static void Read(NetReader r) { dirty |= remote.Read(r); }

        public static Localization.Translation Display(CupheadButton action)
        {
            InputLabels.Label label;
            if (!remote.TryGet((int)action, out label)) return new Localization.Translation { text = "" };
            return new Localization.Translation
            {
                text = label.Text,
                fonts = Enum.IsDefined(typeof(FontLoader.FontType), label.Font)
                    ? new Localization.CategoryLanguageFont { fontType = (FontLoader.FontType)label.Font } : null
            };
        }

        public static void TickHost()
        {
            if (!Session.IsHost || !dirty) return;
            dirty = false;
            foreach (CupheadGlyph glyph in UnityEngine.Object.FindObjectsOfType<CupheadGlyph>())
            {
                if (glyph.rewiredPlayerId != 1) continue;
                glyph.Init();
                // These dialogue backgrounds are sized explicitly, rather than by a LayoutGroup. A caption
                // changing from a single letter to ENTER must resize its already-open background as well.
                AbstractUIInteractionDialogue dialogue = glyph.GetComponentInParent<AbstractUIInteractionDialogue>();
                if (dialogue == null) continue;
                RectTransform back = (RectTransform)dialogueBack.GetValue(dialogue);
                if (back != null) back.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (float)preferredWidth.GetValue(dialogue, null) + 10f);
            }
        }
    }

    [HarmonyPatch(typeof(CupheadInput), "InputDisplayForButton", new[] { typeof(CupheadButton), typeof(int) })]
    internal static class RemoteGlyphDisplay
    {
        [HarmonyPrefix]
        private static bool Prefix(CupheadButton button, int rewiredPlayerId, ref Localization.Translation __result)
        {
            if (!RemoteInput.DriveHostPlayerTwo || rewiredPlayerId != 1) return true;
            __result = GlyphSync.Display(button);
            return false;
        }
    }
}
