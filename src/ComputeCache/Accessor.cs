using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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

        /// <summary>转发 memory.Invalidate()，使所有缓存槽失效并开始新的会话周期。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invalidate()
        {
            _memory.Invalidate();
        }

        // ======================= 标量（静态形态）=======================

        /// <summary>读取标量缓存值；若未命中（version 不匹配）则抛出 KeyNotFoundException。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            if (d.Versions[bank][off] != d.Current[bank])
                throw new KeyNotFoundException("标量未命中。");
            return SlotAccess.ElementRef<T>(d, bank, off);
        }

        /// <summary>尝试读取标量缓存值；命中时返回 true 并输出值，未命中时返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>写入标量缓存值并将对应槽标记为已命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set<T>(CacheMemory d, Handle<T> h, in T v) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            SlotAccess.ElementRef<T>(d, bank, off) = v;
            d.Versions[bank][off] = d.Current[bank]; // 写值即标记命中
        }

        /// <summary>获取标量缓存槽的可写引用，同时将该槽标记为已命中（按写入意图）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetRef<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            d.Versions[bank][off] = d.Current[bank]; // 取即标记命中（按写意图）
            return ref SlotAccess.ElementRef<T>(d, bank, off);
        }

        /// <summary>检查标量句柄在当前会话中是否有效（version 与当前周期匹配）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            return d.Versions[bank][off] == d.Current[bank];
        }

        /// <summary>将标量句柄标记为无效（version 置为与当前周期不匹配的值）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MarkInvalid<T>(CacheMemory d, Handle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            // 置为与当前 version 不同的值即 miss。
            d.Versions[bank][off] = (byte)(d.Current[bank] - 1);
        }

        // ======================= 向量（静态形态）=======================

        /// <summary>按下标读取向量中的单个元素；不检查 version，仅做越界校验。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            // 单点 Get 不查 version，仅返回槽内现值。
            return SlotAccess.ElementRef<T>(d, bank, off + i);
        }

        /// <summary>按下标写入向量中的单个元素并将该槽标记为已命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i, in T v) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            SlotAccess.ElementRef<T>(d, bank, off + i) = v;
            d.Versions[bank][off + i] = d.Current[bank];
        }

        /// <summary>获取向量中指定下标元素的可写引用，同时将该槽标记为已命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetRef<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            d.Versions[bank][off + i] = d.Current[bank];
            return ref SlotAccess.ElementRef<T>(d, bank, off + i);
        }

        /// <summary>尝试按下标读取向量元素；命中时返回 true 并输出值，未命中时返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>检查向量中指定下标的槽在当前会话中是否有效。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            return d.Versions[bank][off + i] == d.Current[bank];
        }

        /// <summary>将向量中指定下标的槽标记为无效。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MarkInvalid<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)i >= (uint)len) throw new IndexOutOfRangeException("向量下标越界。");
            d.Versions[bank][off + i] = (byte)(d.Current[bank] - 1);
        }

        /// <summary>获取向量的当前计数（逻辑长度），即实际使用的元素数量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetCount<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            return d.Counts[bank][off];
        }

        /// <summary>设置向量的当前计数（逻辑长度），count 须在 [0, Length] 范围内。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetCount<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int count) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            if ((uint)count > (uint)len) throw new ArgumentOutOfRangeException(nameof(count), "Count 须在 [0, Length]。");
            d.Counts[bank][off] = count;
        }

        /// <summary>获取向量前 Count 个元素的 Span，即 [0, Count) 区间。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> GetSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int count = d.Counts[bank][off];
            return SlotAccess.SpanOf<T>(d, bank, off, count); // [0, Count)
        }

        /// <summary>获取向量全部 Length 个元素的 Span，即 [0, Length) 区间。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> GetFullSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h) where T : unmanaged
        {
            int bank = (int)HandlePacking.Bank(h.Packed);
            int off = HandlePacking.Offset(h.Packed);
            int len = m.LengthOfPacked(h.Packed);
            return SlotAccess.SpanOf<T>(d, bank, off, len); // [0, Length)
        }

        /// <summary>批量命中扫描重载：一次取出整段 data / version / 当前 version，用于高效遍历。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>将向量指定区间 [start, start+count) 内的所有槽标记为已命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>将向量指定区间 [start, start+count) 内的所有槽标记为无效。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>读取标量缓存值（绑定形态）；未命中时抛出异常。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(Handle<T> h) where T : unmanaged { return Get(_memory, h); }
        /// <summary>尝试读取标量缓存值（绑定形态）；命中返回 true，未命中返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet<T>(Handle<T> h, out T v) where T : unmanaged { return TryGet(_memory, h, out v); }
        /// <summary>写入标量缓存值（绑定形态）并标记命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(Handle<T> h, in T v) where T : unmanaged { Set(_memory, h, v); }
        /// <summary>获取标量缓存槽的可写引用（绑定形态），同时标记命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetRef<T>(Handle<T> h) where T : unmanaged { return ref GetRef(_memory, h); }
        /// <summary>检查标量句柄是否有效（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid<T>(Handle<T> h) where T : unmanaged { return IsValid(_memory, h); }
        /// <summary>将标量句柄标记为无效（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkInvalid<T>(Handle<T> h) where T : unmanaged { MarkInvalid(_memory, h); }

        /// <summary>按下标读取向量元素（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(VectorHandle<T> h, int i) where T : unmanaged { return Get(_layout, _memory, h, i); }
        /// <summary>按下标写入向量元素（绑定形态）并标记命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(VectorHandle<T> h, int i, in T v) where T : unmanaged { Set(_layout, _memory, h, i, v); }
        /// <summary>获取向量指定下标元素的可写引用（绑定形态），同时标记命中。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetRef<T>(VectorHandle<T> h, int i) where T : unmanaged { return ref GetRef(_layout, _memory, h, i); }
        /// <summary>尝试按下标读取向量元素（绑定形态）；命中返回 true，未命中返回 false。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet<T>(VectorHandle<T> h, int i, out T v) where T : unmanaged { return TryGet(_layout, _memory, h, i, out v); }
        /// <summary>检查向量指定下标的槽是否有效（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid<T>(VectorHandle<T> h, int i) where T : unmanaged { return IsValid(_layout, _memory, h, i); }
        /// <summary>将向量指定下标的槽标记为无效（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkInvalid<T>(VectorHandle<T> h, int i) where T : unmanaged { MarkInvalid(_layout, _memory, h, i); }

        /// <summary>获取向量的当前计数（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetCount<T>(VectorHandle<T> h) where T : unmanaged { return GetCount(_layout, _memory, h); }
        /// <summary>设置向量的当前计数（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCount<T>(VectorHandle<T> h, int count) where T : unmanaged { SetCount(_layout, _memory, h, count); }
        /// <summary>获取向量前 Count 个元素的 Span（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> GetSpan<T>(VectorHandle<T> h) where T : unmanaged { return GetSpan(_layout, _memory, h); }
        /// <summary>获取向量全部 Length 个元素的 Span（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> GetFullSpan<T>(VectorHandle<T> h) where T : unmanaged { return GetFullSpan(_layout, _memory, h); }
        /// <summary>批量命中扫描重载（绑定形态）：一次取出整段数据、版本号与当前版本号。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetFullSpan<T>(VectorHandle<T> h, out Span<T> values, out Span<byte> versions, out byte current) where T : unmanaged
        {
            GetFullSpan(_layout, _memory, h, out values, out versions, out current);
        }
        /// <summary>将向量指定区间的槽标记为已命中（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkValidRange<T>(VectorHandle<T> h, int start, int count) where T : unmanaged { MarkValidRange(_layout, _memory, h, start, count); }
        /// <summary>将向量指定区间的槽标记为无效（绑定形态）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkInvalidRange<T>(VectorHandle<T> h, int start, int count) where T : unmanaged { MarkInvalidRange(_layout, _memory, h, start, count); }
    }
}
