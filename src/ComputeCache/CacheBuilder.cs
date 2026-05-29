using System;
using System.Collections.Generic;

namespace ComputeCache
{
    /// <summary>顺序路径阶段：声明趟 / 回填趟。</summary>
    public enum CacheLayoutPhase : byte { SequenceDeclare, SequenceFetch }

    /// <summary>
    /// 缓存布局构建器。命名路径（Declare/DeclareVector，按 key）与顺序路径
    /// （SequenceDeclare/SequenceFetch，按 ordinal）共享同一分桶与 offset 规则，
    /// 统一 Build 产出同一张 CacheLayout，可在同一 builder 混用。
    /// </summary>
    public sealed class CacheBuilder
    {
        // 基布局（Extend 用）：其 entry 的 bank/offset/length 锁定不变。
        private readonly List<Entry> _baseEntries = new List<Entry>();
        // 本次新声明（命名 + 顺序）的调用顺序列表；offset 在 Build 时分配。
        private readonly List<Entry> _decls = new List<Entry>();
        // 命名查重：覆盖基布局命名 + 新命名。
        private readonly Dictionary<string, Entry> _named = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // 顺序声明列表（按调用顺序）。
        private readonly List<Entry> _sequence = new List<Entry>();

        private bool _built;
        private int _fetchCursor;

        public CacheBuilder()
        {
        }

        private CacheBuilder(CacheLayout baseLayout)
        {
            // 复制基布局的全部 entry，锁定其 bank/offset/length。
            for (int i = 0; i < baseLayout.AllEntries.Length; i++)
            {
                Entry src = baseLayout.AllEntries[i];
                Entry copy = new Entry
                {
                    Key = src.Key,
                    TypeId = src.TypeId,
                    Bank = src.Bank,
                    Offset = src.Offset,
                    Length = src.Length,
                    IsVector = src.IsVector,
                    Packed = src.Packed,
                };
                _baseEntries.Add(copy);
                if (copy.Key != null)
                    _named[copy.Key] = copy;
            }
        }

        /// <summary>基于已有 layout 追加声明，产出超集新 layout（append-only）。</summary>
        public static CacheBuilder Extend(CacheLayout baseLayout)
        {
            if (baseLayout == null) throw new ArgumentNullException(nameof(baseLayout));
            return new CacheBuilder(baseLayout);
        }

        // ---------------- 命名路径 ----------------

        public void Declare<T>(string key) where T : unmanaged
        {
            DeclareNamed<T>(key, false, 1);
        }

        public void DeclareVector<T>(string key, int length) where T : unmanaged
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "向量长度必须 > 0。");
            DeclareNamed<T>(key, true, length);
        }

        private void DeclareNamed<T>(string key, bool isVector, int length) where T : unmanaged
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            EnsureNotBuilt();

            BankKind bank = SlotMath.BankFor<T>(isVector);
            string typeId = typeof(T).AssemblyQualifiedName;

            Entry existing;
            if (_named.TryGetValue(key, out existing))
            {
                // 完全一致的重复声明幂等；任何不一致即冲突。
                if (existing.TypeId != typeId || existing.IsVector != isVector || existing.Length != length)
                    throw new InvalidOperationException("key \"" + key + "\" 的重复声明与既有声明冲突。");
                return;
            }

            Entry entry = new Entry
            {
                Key = key,
                TypeId = typeId,
                Bank = bank,
                IsVector = isVector,
                Length = length,
            };
            _named[key] = entry;
            _decls.Add(entry);
        }

        // ---------------- 顺序路径 ----------------

        public void SequenceDeclare<T>() where T : unmanaged
        {
            SequenceDeclareCore<T>(false, 1);
        }

        public void SequenceDeclareVector<T>(int length) where T : unmanaged
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "向量长度必须 > 0。");
            SequenceDeclareCore<T>(true, length);
        }

        private void SequenceDeclareCore<T>(bool isVector, int length) where T : unmanaged
        {
            EnsureNotBuilt();
            BankKind bank = SlotMath.BankFor<T>(isVector);
            Entry entry = new Entry
            {
                Key = null,
                TypeId = typeof(T).AssemblyQualifiedName,
                Bank = bank,
                IsVector = isVector,
                Length = length,
            };
            _sequence.Add(entry);
            _decls.Add(entry);
        }

        public Handle<T> SequenceFetch<T>() where T : unmanaged
        {
            Entry e = NextFetch<T>(false, 1);
            return new Handle<T>(e.Packed);
        }

        public VectorHandle<T> SequenceFetchVector<T>() where T : unmanaged
        {
            Entry e = NextFetch<T>(true, -1);
            return new VectorHandle<T>(e.Packed);
        }

        private Entry NextFetch<T>(bool isVector, int expectLength) where T : unmanaged
        {
            if (!_built) throw new InvalidOperationException("必须先 Build 再 SequenceFetch。");
            if (_fetchCursor >= _sequence.Count)
                throw new InvalidOperationException("SequenceFetch 越界：回填次数超过声明次数。");

            Entry e = _sequence[_fetchCursor];
            string typeId = typeof(T).AssemblyQualifiedName;
            if (e.TypeId != typeId || e.IsVector != isVector)
                throw new InvalidOperationException("SequenceFetch 的 T / scalar-vector 与声明趟不一致。");
            if (expectLength > 0 && e.Length != expectLength)
                throw new InvalidOperationException("SequenceFetch 长度与声明趟不一致。");

            _fetchCursor++;
            return e;
        }

        // ---------------- 构建 ----------------

        public CacheLayout Build()
        {
            EnsureNotBuilt();

            // 各 bank 的高水位计数，从基布局容量起算。
            int[] counts = new int[16];
            for (int i = 0; i < _baseEntries.Count; i++)
            {
                Entry b = _baseEntries[i];
                int end = b.Offset + (b.IsVector ? b.Length : 1);
                int idx = (int)b.Bank;
                if (end > counts[idx]) counts[idx] = end;
            }

            // 按调用顺序在各 bank 内顺序分配 offset，物化 packed handle。
            for (int i = 0; i < _decls.Count; i++)
            {
                Entry e = _decls[i];
                int idx = (int)e.Bank;
                e.Offset = counts[idx];
                counts[idx] += e.IsVector ? e.Length : 1;
                e.Packed = HandlePacking.Pack(e.Bank, e.Offset, 0u);
            }

            // 汇总全部 entry：基布局在前，新声明在后（顺序稳定）。
            List<Entry> all = new List<Entry>(_baseEntries.Count + _decls.Count);
            all.AddRange(_baseEntries);
            all.AddRange(_decls);

            Dictionary<string, Entry> named = new Dictionary<string, Entry>(StringComparer.Ordinal);
            Dictionary<uint, int> vectorLength = new Dictionary<uint, int>();
            // 热路径长度表：按 bank 容量分配，段首 offset 处填声明长度。
            int[][] segLen = new int[16][];
            for (int b = 0; b < 16; b++) segLen[b] = new int[counts[b]];
            for (int i = 0; i < all.Count; i++)
            {
                Entry e = all[i];
                if (e.Key != null) named[e.Key] = e;
                if (e.IsVector) vectorLength[e.Packed] = e.Length;
                // 标量段长记 1，向量段长记声明长度，供单点边界检查。
                segLen[(int)e.Bank][e.Offset] = e.IsVector ? e.Length : 1;
            }

            ulong hash = LayoutHash.Compute(all, counts);

            CacheLayout layout = new CacheLayout(all.ToArray(), named, vectorLength, counts, segLen, hash);
            _built = true;
            _fetchCursor = 0;
            return layout;
        }

        private void EnsureNotBuilt()
        {
            if (_built) throw new InvalidOperationException("Build 之后不可再声明。");
        }
    }
}
