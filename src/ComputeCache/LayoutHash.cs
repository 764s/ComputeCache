using System.Collections.Generic;
using System.Text;

namespace ComputeCache
{
    /// <summary>
    /// 计算稳定的 layout hash：覆盖 key / typeId / bank / offset / length / isVector 及各 bank 容量。
    /// </summary>
    internal static class LayoutHash
    {
        public static ulong Compute(List<Entry> entries, int[] capacities)
        {
            List<byte> buf = new List<byte>(256);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                WriteString(buf, e.Key);
                WriteString(buf, e.TypeId);
                buf.Add((byte)e.Bank);
                WriteInt(buf, e.Offset + 1); // 与 offsetPlusOne 对齐
                WriteInt(buf, e.Length);
                buf.Add(e.IsVector ? (byte)1 : (byte)0);
            }
            // 各 bank 容量纳入 hash。
            for (int b = 0; b < capacities.Length; b++)
                WriteInt(buf, capacities[b]);

            return XxHash64.Compute(buf.ToArray(), 0UL);
        }

        private static void WriteString(List<byte> buf, string s)
        {
            if (s == null)
            {
                // 用长度 -1 表示 null，区别于空串。
                WriteInt(buf, -1);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            WriteInt(buf, bytes.Length);
            buf.AddRange(bytes);
        }

        private static void WriteInt(List<byte> buf, int value)
        {
            uint u = unchecked((uint)value);
            buf.Add((byte)(u & 0xFF));
            buf.Add((byte)((u >> 8) & 0xFF));
            buf.Add((byte)((u >> 16) & 0xFF));
            buf.Add((byte)((u >> 24) & 0xFF));
        }
    }
}
