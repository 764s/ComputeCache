using System;

namespace ComputeCache
{
    /// <summary>
    /// xxHash64 实现，用于计算稳定的 layout hash。纯标量版本，输入为字节序列。
    /// </summary>
    internal static class XxHash64
    {
        private const ulong Prime1 = 11400714785074694791UL;
        private const ulong Prime2 = 14029467366897019727UL;
        private const ulong Prime3 = 1609587929392839161UL;
        private const ulong Prime4 = 9650029242287828579UL;
        private const ulong Prime5 = 2870177450012600261UL;

        private static ulong RotL(ulong x, int r)
        {
            return (x << r) | (x >> (64 - r));
        }

        private static ulong Round(ulong acc, ulong input)
        {
            acc += input * Prime2;
            acc = RotL(acc, 31);
            acc *= Prime1;
            return acc;
        }

        private static ulong MergeRound(ulong acc, ulong val)
        {
            val = Round(0UL, val);
            acc ^= val;
            acc = acc * Prime1 + Prime4;
            return acc;
        }

        public static ulong Compute(byte[] data, ulong seed)
        {
            int len = data.Length;
            int p = 0;
            ulong h64;

            if (len >= 32)
            {
                ulong v1 = seed + Prime1 + Prime2;
                ulong v2 = seed + Prime2;
                ulong v3 = seed;
                ulong v4 = seed - Prime1;

                int limit = len - 32;
                do
                {
                    v1 = Round(v1, ReadU64(data, p)); p += 8;
                    v2 = Round(v2, ReadU64(data, p)); p += 8;
                    v3 = Round(v3, ReadU64(data, p)); p += 8;
                    v4 = Round(v4, ReadU64(data, p)); p += 8;
                } while (p <= limit);

                h64 = RotL(v1, 1) + RotL(v2, 7) + RotL(v3, 12) + RotL(v4, 18);
                h64 = MergeRound(h64, v1);
                h64 = MergeRound(h64, v2);
                h64 = MergeRound(h64, v3);
                h64 = MergeRound(h64, v4);
            }
            else
            {
                h64 = seed + Prime5;
            }

            h64 += (ulong)len;

            while (p + 8 <= len)
            {
                ulong k1 = Round(0UL, ReadU64(data, p));
                h64 ^= k1;
                h64 = RotL(h64, 27) * Prime1 + Prime4;
                p += 8;
            }

            if (p + 4 <= len)
            {
                h64 ^= (ulong)ReadU32(data, p) * Prime1;
                h64 = RotL(h64, 23) * Prime2 + Prime3;
                p += 4;
            }

            while (p < len)
            {
                h64 ^= data[p] * Prime5;
                h64 = RotL(h64, 11) * Prime1;
                p++;
            }

            h64 ^= h64 >> 33;
            h64 *= Prime2;
            h64 ^= h64 >> 29;
            h64 *= Prime3;
            h64 ^= h64 >> 32;
            return h64;
        }

        private static ulong ReadU64(byte[] d, int i)
        {
            return (ulong)d[i]
                 | ((ulong)d[i + 1] << 8)
                 | ((ulong)d[i + 2] << 16)
                 | ((ulong)d[i + 3] << 24)
                 | ((ulong)d[i + 4] << 32)
                 | ((ulong)d[i + 5] << 40)
                 | ((ulong)d[i + 6] << 48)
                 | ((ulong)d[i + 7] << 56);
        }

        private static uint ReadU32(byte[] d, int i)
        {
            return (uint)d[i]
                 | ((uint)d[i + 1] << 8)
                 | ((uint)d[i + 2] << 16)
                 | ((uint)d[i + 3] << 24);
        }
    }
}
