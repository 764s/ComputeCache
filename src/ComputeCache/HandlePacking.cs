using System;

namespace ComputeCache
{
    /// <summary>
    /// Handle 位布局编解码：
    ///   位 31..27  bank          (BankKind)
    ///   位 26..3   offsetPlusOne (1..16,777,215)
    ///   位  2..0   flags         (预留)
    /// Packed == 0 即 invalid。
    /// </summary>
    internal static class HandlePacking
    {
        public const int OffsetBits = 24;
        // offsetPlusOne 的最大值（24 位全 1）。
        public const uint MaxOffsetPlusOne = (1u << OffsetBits) - 1u; // 16,777,215

        public static uint Pack(BankKind bank, int offset, uint flags)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset 不可为负。");
            uint offsetPlusOne = (uint)offset + 1u;
            if (offsetPlusOne > MaxOffsetPlusOne)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset 超出 24 位编码上限。");
            return ((uint)bank << 27) | (offsetPlusOne << 3) | (flags & 0x7u);
        }

        public static BankKind Bank(uint packed)
        {
            return (BankKind)((packed >> 27) & 0x1Fu);
        }

        public static int Offset(uint packed)
        {
            return (int)(((packed >> 3) & 0xFFFFFFu) - 1u);
        }

        public static uint Flags(uint packed)
        {
            return packed & 0x7u;
        }
    }
}
