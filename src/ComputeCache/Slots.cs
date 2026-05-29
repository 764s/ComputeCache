using System.Runtime.InteropServices;

namespace ComputeCache
{
    // 类型擦除的物理槽：以内建整型为基，使对齐恰为 min(N, 8)。
    // Slot8 及以上以 long 铺满 → 对齐 8，故 Unsafe.As<SlotN, T>() 在 alignof(T) ≤ 对齐 时恒安全。
    // 大于 8 字节的槽用 StructLayout.Size 补足总大小，单个 long 字段保证 8 字节对齐。

    [StructLayout(LayoutKind.Sequential)]
    internal struct Slot1 { public byte V; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Slot4 { public int V; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Slot8 { public long V; }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct Slot16 { public long V; }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct Slot32 { public long V; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct Slot64 { public long V; }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    internal struct Slot128 { public long V; }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    internal struct Slot256 { public long V; }
}
