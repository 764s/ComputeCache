using System;
using System.Runtime.CompilerServices;

namespace ComputeCache
{
    /// <summary>
    /// 分桶与对齐计算：把 unmanaged 类型按大小归入固定能级，并校验对齐。
    /// </summary>
    internal static class SlotMath
    {
        // 用于测量对齐：byte 前缀使 T 落在其自然对齐处；align = sizeof(helper) - sizeof(T)。
        private struct AlignHelper<T> where T : unmanaged
        {
#pragma warning disable CS0649
            public byte Pad;
            public T Value;
#pragma warning restore CS0649
        }

        // 八个能级（字节）。
        private static readonly int[] Energies = { 1, 4, 8, 16, 32, 64, 128, 256 };

        public static int SizeOf<T>() where T : unmanaged
        {
            return Unsafe.SizeOf<T>();
        }

        public static int AlignOf<T>() where T : unmanaged
        {
            return Unsafe.SizeOf<AlignHelper<T>>() - Unsafe.SizeOf<T>();
        }

        // 返回能级序号（0..7）；size > 256 直接拒绝。
        public static int EnergyIndex(int size)
        {
            for (int i = 0; i < Energies.Length; i++)
            {
                if (size <= Energies[i]) return i;
            }
            throw new NotSupportedException("sizeof > 256 的类型不入桶。");
        }

        // 能级序号对应的字节数。
        public static int EnergyBytes(int energyIndex)
        {
            return Energies[energyIndex];
        }

        // 由 bank 反查能级字节数。
        public static int EnergyBytesOf(BankKind bank)
        {
            return Energies[(int)bank & 0x7];
        }

        /// <summary>
        /// 计算类型 T 应入的 bank。入桶条件：sizeof(T) ≤ 能级 且 alignof(T) ≤ min(能级, 8)。
        /// </summary>
        public static BankKind BankFor<T>(bool isVector) where T : unmanaged
        {
            int size = SizeOf<T>();
            if (size > 256)
                throw new NotSupportedException("sizeof > 256 的类型不入桶。");

            int align = AlignOf<T>();
            if (align > 8)
                throw new NotSupportedException("不支持 > 8 字节对齐的类型。");

            int ei = EnergyIndex(size);
            int level = Energies[ei];
            int maxAlign = level < 8 ? level : 8;
            if (align > maxAlign)
            {
                // 对齐超出本能级上限，升级到能容纳其对齐的能级。
                while (ei < Energies.Length)
                {
                    int lv = Energies[ei];
                    int ma = lv < 8 ? lv : 8;
                    if (align <= ma) break;
                    ei++;
                }
                if (ei >= Energies.Length)
                    throw new NotSupportedException("无法为该类型找到满足对齐的能级。");
            }

            return (BankKind)(ei + (isVector ? 8 : 0));
        }
    }
}
