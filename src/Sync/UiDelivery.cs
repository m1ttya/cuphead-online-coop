using System;
using System.Collections.Generic;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>Version fields independently: a late text fragment must survive newer position-only deltas.</summary>
    public sealed class SnapshotFields
    {
        private readonly uint[] frames = new uint[32];
        private uint present;

        public uint Accept(uint frame, uint mask)
        {
            uint accepted = 0;
            for (int bit = 0; bit < frames.Length; bit++)
            {
                uint flag = 1u << bit;
                if ((mask & flag) == 0 || ((present & flag) != 0 && (int)(frame - frames[bit]) < 0)) continue;
                frames[bit] = frame;
                present |= flag;
                accepted |= flag;
            }
            return accepted;
        }
    }

    /// <summary>Bounded assembly of reliable UI entries larger than one datagram (NPC rich text is often several KB).</summary>
    public sealed class UiFragments
    {
        public const int MaxEntry = 64 * 1024;
        private sealed class Entry { public uint Frame; public byte[] Data; public int Received; }
        private readonly Dictionary<ushort, Entry> entries = new Dictionary<ushort, Entry>();

        public void Clear() { entries.Clear(); }

        public static void Send(Connection connection, ushort serial, uint frame, ushort id, byte[] data, int start, int length)
        {
            if (length > MaxEntry) throw new ArgumentException("UI entry exceeds " + MaxEntry + " bytes");
            for (int offset = 0; offset < length; offset += 900)
            {
                NetWriter w = new NetWriter(1000);
                w.U16(serial); w.U32(frame); w.U16(id); w.I32(length); w.I32(offset);
                w.Bytes(data, start + offset, Math.Min(900, length - offset));
                connection.SendReliable(MsgType.UiFragment, w);
            }
        }

        public byte[] Read(uint frame, ushort id, NetReader r)
        {
            int total = r.I32(), offset = r.I32(), count = r.Remaining;
            if (total <= 0 || total > MaxEntry || offset < 0 || count <= 0 || count > total || offset > total - count)
                throw new FormatException("Invalid UI fragment range");
            Entry entry;
            entries.TryGetValue(id, out entry);
            if (entry != null && (int)(frame - entry.Frame) < 0) return null;
            if (offset == 0)
            {
                if (entry == null && entries.Count >= 64) throw new FormatException("Too many pending UI entries");
                entry = new Entry { Frame = frame, Data = new byte[total] };
                entries[id] = entry;
            }
            if (entry == null || entry.Frame != frame || entry.Data.Length != total || entry.Received != offset)
                throw new FormatException("Out-of-sequence reliable UI fragment");
            for (int i = 0; i < count; i++) entry.Data[offset + i] = r.U8();
            entry.Received += count;
            if (entry.Received != total) return null;
            entries.Remove(id);
            return entry.Data;
        }
    }
}
