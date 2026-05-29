using System;

namespace ComputeCache
{
    /// <summary>
    /// 按 bank 能级创建 / 扩容 / 克隆类型擦除的 SlotN[]。能级序号 = bank 低 3 位。
    /// </summary>
    internal static class SlotArray
    {
        public static Array Create(int bank, int length)
        {
            switch (bank & 0x7)
            {
                case 0: return new Slot1[length];
                case 1: return new Slot4[length];
                case 2: return new Slot8[length];
                case 3: return new Slot16[length];
                case 4: return new Slot32[length];
                case 5: return new Slot64[length];
                case 6: return new Slot128[length];
                default: return new Slot256[length];
            }
        }

        public static Array Resize(int bank, Array array, int length)
        {
            switch (bank & 0x7)
            {
                case 0: { Slot1[] a = (Slot1[])array; Array.Resize(ref a, length); return a; }
                case 1: { Slot4[] a = (Slot4[])array; Array.Resize(ref a, length); return a; }
                case 2: { Slot8[] a = (Slot8[])array; Array.Resize(ref a, length); return a; }
                case 3: { Slot16[] a = (Slot16[])array; Array.Resize(ref a, length); return a; }
                case 4: { Slot32[] a = (Slot32[])array; Array.Resize(ref a, length); return a; }
                case 5: { Slot64[] a = (Slot64[])array; Array.Resize(ref a, length); return a; }
                case 6: { Slot128[] a = (Slot128[])array; Array.Resize(ref a, length); return a; }
                default: { Slot256[] a = (Slot256[])array; Array.Resize(ref a, length); return a; }
            }
        }

        public static Array CloneInto(int bank, Array array)
        {
            return (Array)array.Clone();
        }
    }
}
