using System.Collections.Generic;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>Reliable, sparse metadata for the remote player's actual button captions.</summary>
    public sealed class InputLabels
    {
        public struct Label
        {
            public string Text;
            public int Font;
        }

        private readonly Dictionary<int, Label> labels = new Dictionary<int, Label>();

        private static bool Valid(int action, string text)
        {
            return action >= 0 && action < PadState.ActionCount && action != 9 && action != 10 && action != 17 &&
                action != 24 && action != 25 && text != null && text.Length <= 128;
        }

        public bool TryGet(int action, out Label label) { return labels.TryGetValue(action, out label); }
        public void Clear() { labels.Clear(); }

        private bool Set(int action, string text, int font)
        {
            if (!Valid(action, text)) return false;
            Label previous;
            if (labels.TryGetValue(action, out previous) && previous.Text == text && previous.Font == font) return false;
            labels[action] = new Label { Text = text, Font = font };
            return true;
        }

        public void SendChanged(Connection connection, int action, string text, int font)
        {
            text = text ?? "";
            if (!Set(action, text, font)) return;
            NetWriter w = new NetWriter(64);
            w.U8((byte)action); w.I32(font); w.Str(text);
            connection.SendReliable(MsgType.InputLabel, w);
        }

        public bool Read(NetReader r)
        {
            int action = r.U8(), font = r.I32();
            return Set(action, r.Str(), font);
        }
    }
}
