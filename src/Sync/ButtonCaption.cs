using System;
using System.Text.RegularExpressions;

namespace CupheadOnline.Sync
{
    /// <summary>Compact native key/gamepad names for Cuphead's small caption fields.</summary>
    public static class ButtonCaption
    {
        private static readonly Regex side = new Regex(@"\b(LEFT|RIGHT)\b");
        private static readonly Regex shoulder = new Regex(@"\s+(SHOULDER|BUMPER)\b");
        private static readonly Regex trigger = new Regex(@"\s+TRIGGER\b");
        private static readonly Regex shift = new Regex(@"\b[LR]\s+SHIFT\b");
        private static readonly Regex direction = new Regex(@"\s+[+-](?=\s|$)");

        public static string Format(string raw, string translated, bool equip)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (equip && raw.IndexOf("Shift", StringComparison.OrdinalIgnoreCase) >= 0) return "SHIFT";
            string text = (string.IsNullOrEmpty(translated) ? raw : translated).ToUpperInvariant();
            text = side.Replace(text, m => m.Value == "LEFT" ? "L" : "R");
            text = shoulder.Replace(text, "B");
            text = trigger.Replace(text, "T");
            return direction.Replace(shift.Replace(text, "SHIFT"), "");
        }
    }
}
