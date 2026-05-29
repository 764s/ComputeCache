using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ComputeCache
{
    /// <summary>
    /// 物理槽访问：按 bank 能级把类型擦除的 SlotN[] 重解释为 T 引用 / Span。
    /// </summary>
    internal static class SlotAccess
    {
        // 取 bank 内第 index 槽，重解释为 ref T（alignof(T) ≤ 槽对齐 时安全）。
        public static ref T ElementRef<T>(CacheMemory d, int bank, int index) where T : unmanaged
        {
            Array data = d.Data[bank];
            switch (bank & 0x7)
            {
                case 0: return ref Unsafe.As<Slot1, T>(ref ((Slot1[])data)[index]);
                case 1: return ref Unsafe.As<Slot4, T>(ref ((Slot4[])data)[index]);
                case 2: return ref Unsafe.As<Slot8, T>(ref ((Slot8[])data)[index]);
                case 3: return ref Unsafe.As<Slot16, T>(ref ((Slot16[])data)[index]);
                case 4: return ref Unsafe.As<Slot32, T>(ref ((Slot32[])data)[index]);
                case 5: return ref Unsafe.As<Slot64, T>(ref ((Slot64[])data)[index]);
                case 6: return ref Unsafe.As<Slot128, T>(ref ((Slot128[])data)[index]);
                default: return ref Unsafe.As<Slot256, T>(ref ((Slot256[])data)[index]);
            }
        }

        // 取 [start, start+count) 的 Span<T>。要求 sizeof(T) 恰等于该 bank 槽能级字节。
        public static Span<T> SpanOf<T>(CacheMemory d, int bank, int start, int count) where T : unmanaged
        {
            if (Unsafe.SizeOf<T>() != SlotMath.EnergyBytesOf((BankKind)bank))
                throw new InvalidOperationException("Span 取值要求 sizeof(T) 恰等于槽能级字节；欠尺寸类型只能逐元素访问。");

            Array data = d.Data[bank];
            switch (bank & 0x7)
            {
                case 0: return MemoryMarshal.Cast<Slot1, T>(((Slot1[])data).AsSpan(start, count));
                case 1: return MemoryMarshal.Cast<Slot4, T>(((Slot4[])data).AsSpan(start, count));
                case 2: return MemoryMarshal.Cast<Slot8, T>(((Slot8[])data).AsSpan(start, count));
                case 3: return MemoryMarshal.Cast<Slot16, T>(((Slot16[])data).AsSpan(start, count));
                case 4: return MemoryMarshal.Cast<Slot32, T>(((Slot32[])data).AsSpan(start, count));
                case 5: return MemoryMarshal.Cast<Slot64, T>(((Slot64[])data).AsSpan(start, count));
                case 6: return MemoryMarshal.Cast<Slot128, T>(((Slot128[])data).AsSpan(start, count));
                default: return MemoryMarshal.Cast<Slot256, T>(((Slot256[])data).AsSpan(start, count));
            }
        }
    }
}
