using System;
using System.Collections.Generic;

namespace ComputeCache
{
    /// <summary>
    /// 无状态访问逻辑，两形态等价：
    ///   静态：Accessor.Op(...)，标量带 CacheMemory，向量另带 CacheLayout（取 Length）。
    ///   结构体：new Accessor(layout, memory) 绑定一次，调用省去前缀内存参数。
    /// </summary>
    public readonly struct Accessor
    {
        private readonly CacheLayout _layout;
        private readonly CacheMemory _memory;

        /// <summary>绑定 (layout, memory)；断言 memory 各 bank 容量 ⊇ layout（廉价恒开，非身份校验）。</summary>
        public Accessor(CacheLayout layout, CacheMemory memory)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            int[] need = layout.Capacities;
            for (int b = 0; b < 16; b++)
            {
                if (need[b] > 0 && memory.Lengths[b] < need[b])
                    throw new InvalidOperationException("内存条容量不足以承载该 layout（需先 EnsureCapacity）。");
            }
            _layout = layout;
            _memory = memory;
        }

        /// <summary>转发 memory.Invalidate()，开新会话。</summary>
        public void Invalidate()
        {
            _memory.Invalidate();
        }

        // ======================= 标量（静态形态）=======================

        public static T Get<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            if (d.Versions[bank][off] != d.Current[bank])
                throw new KeyNotFoundException("标量未命中。");
            return SlotAccess.ElementRef<T>(d, bank, off);
        }

        public static bool TryGet<T>(CacheMemory d, Handle<T> h, out T v) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            if (d.Versions[bank][off] == d.Current[bank])
            {
                v = SlotAccess.ElementRef<T>(d, bank, off);
                return true;
            }
            v = default(T);
            return false;
        }

        public static void Set<T>(CacheMemory d, Handle<T> h, in T v) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            SlotAccess.ElementRef<T>(d, bank, off) = v;
            d.Versions[bank][off] = d.Current[bank]; // 写值即标记命中
        }

        public static ref T GetRef<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            d.Versions[bank][off] = d.Current[bank]; // 取即标记命中（按写意图）
            return ref SlotAccess.ElementRef<T>(d, bank, off);
        }

        public static bool IsValid<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            return d.Versions[bank][off] == d.Current[bank];
        }

        public static void MarkInvalid<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            // 置为与当前 version 不同的值即 miss。
            d.Versions[bank][off] = (byte)(d.Current[bank] - 1);
        }

        // ======================= 向量（静态形态）=======================

        public static T Get<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            // 单点 Get 不查 version，仅返回槽内现值。
            return SlotAccess.ElementRef<T>(d, bank, off + i);
        }

        public static void Set<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i, in T v) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            SlotAccess.ElementRef<T>(d, bank, off + i) = v;
            d.Versions[bank][off + i] = d.Current[bank];
        }

        public static ref T GetRef<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            d.Versions[bank][off + i] = d.Current[bank];
            return ref SlotAccess.ElementRef<T>(d, bank, off + i);
        }

        public static bool TryGet<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i, out T v) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            if (d.Versions[bank][off + i] == d.Current[bank])
            {
                v = SlotAccess.ElementRef<T>(d, bank, off + i);
                return true;
            }
            v = default(T);
            return false;
        }

        public static bool IsValid<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            return d.Versions[bank][off + i] == d.Current[bank];
        }

        public static void MarkInvalid<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            d.Versions[bank][off + i] = (byte)(d.Current[bank] - 1);
        }

        public static int GetCount<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            return d.Counts[bank][off];
        }

        public static void SetCount<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int count) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)count > (uint)len) throw new ArgumentOutOfRangeException(nameof(count), "Count 须在 [0, Length]。");
            d.Counts[bank][off] = count;
        }

        public static Span<T> GetSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int count = d.Counts[bank][off];
            return SlotAccess.SpanOf<T>(d, bank, off, count); // [0, Count)
        }

        public static Span<T> GetFullSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            return SlotAccess.SpanOf<T>(d, bank, off, len); // [0, Length)
        }

        /// <summary>批量命中扫描重载：一次取出整段 data / version / 当前 version。</summary>
        public static void GetFullSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h,
            out Span<T> values, out Span<byte> versions, out byte current) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            values = SlotAccess.SpanOf<T>(d, bank, off, len);
            versions = new Span<byte>(d.Versions[bank], off, len);
            current = d.Current[bank];
        }

        public static void MarkValidRange<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int start, int count) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            CheckRange(start, count, len);
            byte cur = d.Current[bank];
            byte[] ver = d.Versions[bank];
            for (int k = 0; k < count; k++) ver[off + start + k] = cur;
        }

        public static void MarkInvalidRange<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int start, int count) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            CheckRange(start, count, len);
            byte miss = (byte)(d.Current[bank] - 1);
            byte[] ver = d.Versions[bank];
            for (int k = 0; k < count; k++) ver[off + start + k] = miss;
        }

        private static void CheckRange(int start, int count, int len)
        {
            if (start < 0 || count < 0 || start + count > len)
                throw new ArgumentOutOfRangeException(nameof(start), "区间越界。");
        }

        // ======================= 结构体形态（绑定 layout/memory 的转发）=======================

        public T Get<T>(Handle<T> h) where T : unmanaged { return Get(_memory, h); }
        public bool TryGet<T>(Handle<T> h, out T v) where T : unmanaged { return TryGet(_memory, h, out v); }
        public void Set<T>(Handle<T> h, in T v) where T : unmanaged { Set(_memory, h, v); }
        public ref T GetRef<T>(Handle<T> h) where T : unmanaged { return ref GetRef(_memory, h); }
        public bool IsValid<T>(Handle<T> h) where T : unmanaged { return IsValid(_memory, h); }
        public void MarkInvalid<T>(Handle<T> h) where T : unmanaged { MarkInvalid(_memory, h); }

        public T Get<T>(VectorHandle<T> h, int i) where T : unmanaged { return Get(_layout, _memory, h, i); }
        public void Set<T>(VectorHandle<T> h, int i, in T v) where T : unmanaged { Set(_layout, _memory, h, i, v); }
        public ref T GetRef<T>(VectorHandle<T> h, int i) where T : unmanaged { return ref GetRef(_layout, _memory, h, i); }
        public bool TryGet<T>(VectorHandle<T> h, int i, out T v) where T : unmanaged { return TryGet(_layout, _memory, h, i, out v); }
        public bool IsValid<T>(VectorHandle<T> h, int i) where T : unmanaged { return IsValid(_layout, _memory, h, i); }
        public void MarkInvalid<T>(VectorHandle<T> h, int i) where T : unmanaged { MarkInvalid(_layout, _memory, h, i); }

        public int GetCount<T>(VectorHandle<T> h) where T : unmanaged { return GetCount(_layout, _memory, h); }
        public void SetCount<T>(VectorHandle<T> h, int count) where T : unmanaged { SetCount(_layout, _memory, h, count); }
        public Span<T> GetSpan<T>(VectorHandle<T> h) where T : unmanaged { return GetSpan(_layout, _memory, h); }
        public Span<T> GetFullSpan<T>(VectorHandle<T> h) where T : unmanaged { return GetFullSpan(_layout, _memory, h); }
        public void GetFullSpan<T>(VectorHandle<T> h, out Span<T> values, out Span<byte> versions, out byte current) where T : unmanaged
        {
            GetFullSpan(_layout, _memory, h, out values, out versions, out current);
        }
        public void MarkValidRange<T>(VectorHandle<T> h, int start, int count) where T : unmanaged { MarkValidRange(_layout, _memory, h, start, count); }
        public void MarkInvalidRange<T>(VectorHandle<T> h, int start, int count) where T : unmanaged { MarkInvalidRange(_layout, _memory, h, start, count); }
    }
}
