using System;
using System.Collections.Generic;

namespace ComputeCache
{
    /// <summary>
    /// 编译产物：不可变的缓存布局。可被多份内存与多线程共享（只读）。
    /// 查询非热路径，做完整校验。
    /// </summary>
    public sealed class CacheLayout
    {
        private readonly Entry[] _all;
        private readonly Dictionary<string, Entry> _named;
        private readonly Dictionary<uint, int> _vectorLength;
        private readonly int[] _capacities; // 各 bank 所需槽数
        // 热路径长度表：[bank][offset] = 段长（段首处存声明长度，非段首为 0）。
        // 以数组下标取代字典查找，使向量单点访问接近裸数组成本。
        private readonly int[][] _segLen;

        public ulong Hash { get; private set; }

        internal CacheLayout(Entry[] all, Dictionary<string, Entry> named,
            Dictionary<uint, int> vectorLength, int[] capacities, int[][] segLen, ulong hash)
        {
            _all = all;
            _named = named;
            _vectorLength = vectorLength;
            _capacities = capacities;
            _segLen = segLen;
            Hash = hash;
        }

        internal Entry[] AllEntries { get { return _all; } }

        // 各 bank 所需容量（供内存条 EnsureCapacity 与 Accessor 断言使用）。
        internal int CapacityOf(int bank) { return _capacities[bank]; }

        internal int[] Capacities { get { return _capacities; } }

        // ---------------- 命名查询 ----------------

        public Handle<T> Lookup<T>(string key) where T : unmanaged
        {
            Entry e = ResolveNamed<T>(key, false);
            return new Handle<T>(e.Packed);
        }

        public VectorHandle<T> LookupVector<T>(string key) where T : unmanaged
        {
            Entry e = ResolveNamed<T>(key, true);
            return new VectorHandle<T>(e.Packed);
        }

        public bool TryLookup<T>(string key, out Handle<T> h) where T : unmanaged
        {
            Entry e;
            if (key != null && _named.TryGetValue(key, out e) && Matches<T>(e, false))
            {
                h = new Handle<T>(e.Packed);
                return true;
            }
            h = default(Handle<T>);
            return false;
        }

        public bool TryLookupVector<T>(string key, out VectorHandle<T> h) where T : unmanaged
        {
            Entry e;
            if (key != null && _named.TryGetValue(key, out e) && Matches<T>(e, true))
            {
                h = new VectorHandle<T>(e.Packed);
                return true;
            }
            h = default(VectorHandle<T>);
            return false;
        }

        public int GetLength<T>(VectorHandle<T> h) where T : unmanaged
        {
            int len;
            if (!_vectorLength.TryGetValue(h.Packed, out len))
                throw new KeyNotFoundException("该向量句柄不属于本 layout。");
            return len;
        }

        // 内部：由 packed 取向量长度（热路径，Accessor 用）。数组下标，无字典。
        internal int LengthOfPacked(uint packed)
        {
            int bank = (int)HandlePacking.Bank(packed);
            int off = HandlePacking.Offset(packed);
            return _segLen[bank][off];
        }

        /// <summary>新建一条内存条并 EnsureCapacity 冷启动。</summary>
        public CacheMemory AllocateMemory()
        {
            CacheMemory m = new CacheMemory();
            m.EnsureCapacity(this);
            return m;
        }

        private Entry ResolveNamed<T>(string key, bool isVector) where T : unmanaged
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            Entry e;
            if (!_named.TryGetValue(key, out e))
                throw new KeyNotFoundException("未声明的 key：\"" + key + "\"。");
            if (!Matches<T>(e, isVector))
                throw new InvalidOperationException("key \"" + key + "\" 的类型 / scalar-vector 与声明不符。");
            return e;
        }

        private static bool Matches<T>(Entry e, bool isVector) where T : unmanaged
        {
            return e.IsVector == isVector && e.TypeId == typeof(T).AssemblyQualifiedName;
        }
    }
}
