using System.Collections.Generic;
using System.Text;

namespace CupheadOnline.Sync
{
    public static class UiTextLines
    {
        public static string Insert(string text, IList<int> starts)
        {
            StringBuilder result = null;
            int copied = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                int at = starts[i];
                if (at <= 0 || at >= text.Length || at < copied || text[at - 1] == '\n' || text[at - 1] == '\r') continue;
                if (result == null) result = new StringBuilder(text.Length + starts.Count);
                result.Append(text, copied, at - copied).Append('\n');
                copied = at;
            }
            if (result == null) return text;
            return result.Append(text, copied, text.Length - copied).ToString();
        }
    }
}
