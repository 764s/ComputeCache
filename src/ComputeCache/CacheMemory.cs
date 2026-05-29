using System;

namespace ComputeCache
{
    /// <summary>
    /// 与布局无关的通用内存条：16 个 bank 槽位（S1..S256 / V1..V256），
    /// 每槽一条类型擦除 SlotN[] + 平行 version + count。按高水位只增不减增长复用。
    /// 单线程使用；跨线程并发须每线程独立内存条 + Clone 传值。
    /// </summary>
    public sealed class CacheMemory
    {
        // 每 bank：Data（SlotN[]，类型擦除存为 Array）、Versions、Counts（段首 offset 存）、CurrentVersion。
        internal readonly Array[] Data = new Array[16];
        internal readonly byte[][] Versions = new byte[16][];
        internal readonly int[][] Counts = new int[16][];
        internal readonly byte[] Current = new byte[16];
        internal readonly int[] Lengths = new int[16]; // 各 bank 已分配槽数

        // bank 创建序号，用于错开各 bank 的 version 初值（回绕点）。
        private int _bankSeedIndex;

        // version 初值扰动：CurrentVersion_init = (bankSeedIndex * 37) % 255 + 1，恒 ≥ 1。
        // 37 与 255 互质，使各 bank 的回绕点错开，避免一次 Invalidate 多 bank 集体 Array.Clear 的尖峰。
        private const int BankSeedMultiplier = 37;
        private const int MaxVersionValue = 255;

        public CacheMemory()
        {
        }

        /// <summary>每 bank 长度 ≥ 该 layout 需求；Array.Resize 增长，满载即 no-op。</summary>
        public void EnsureCapacity(CacheLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            int[] need = layout.Capacities;
            for (int b = 0; b < 16; b++)
            {
                int req = need[b];
                if (req <= 0) continue;        // 该 layout 不用此 bank：未分配 tier 保持 null
                if (Lengths[b] >= req) continue; // 已满足：高水位 no-op

                if (Data[b] == null)
                {
                    // 首次分配：建数组并按 bank 创建序扰动 version 初值。
                    Data[b] = SlotArray.Create(b, req);
                    Versions[b] = new byte[req];
                    Counts[b] = new int[req];
                    int seed = (_bankSeedIndex * BankSeedMultiplier) % MaxVersionValue + 1; // 恒 ≥ 1
                    _bankSeedIndex++;
                    Current[b] = (byte)seed;
                }
                else
                {
                    // 非破坏增长：旧区 version / 数据原样保留，新区零填充天然 miss。
                    Data[b] = SlotArray.Resize(b, Data[b], req);
                    byte[] v = Versions[b]; Array.Resize(ref v, req); Versions[b] = v;
                    int[] c = Counts[b]; Array.Resize(ref c, req); Counts[b] = c;
                }
                Lengths[b] = req;
            }
        }

        /// <summary>各 active bank CurrentVersion += 1，旧值整批失效。</summary>
        public void Invalidate()
        {
            for (int b = 0; b < 16; b++)
            {
                if (Data[b] == null) continue;
                byte cur = Current[b];
                if (cur == MaxVersionValue)
                {
                    // 回绕：清空该 bank version，重置为 1，避免与残留值假命中。
                    Array.Clear(Versions[b], 0, Versions[b].Length);
                    Current[b] = 1;
                }
                else
                {
                    Current[b] = (byte)(cur + 1);
                }
            }
        }

        /// <summary>逐 bank 拷贝，跳过未分配 tier。</summary>
        public CacheMemory Clone()
        {
            CacheMemory dst = new CacheMemory();
            dst._bankSeedIndex = _bankSeedIndex;
            for (int b = 0; b < 16; b++)
            {
                if (Data[b] == null) continue;
                int len = Lengths[b];
                dst.Data[b] = SlotArray.CloneInto(b, Data[b]);
                dst.Versions[b] = (byte[])Versions[b].Clone();
                dst.Counts[b] = (int[])Counts[b].Clone();
                dst.Current[b] = Current[b];
                dst.Lengths[b] = len;
            }
            return dst;
        }

        /// <summary>逐 bank 整体拷回，跳过未分配 tier；目标容量不足即拒绝。</summary>
        public void CopyFrom(CacheMemory src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            for (int b = 0; b < 16; b++)
            {
                if (src.Data[b] == null) continue; // 跳过源未分配 tier
                int len = src.Lengths[b];
                if (Data[b] == null || Lengths[b] < len)
                    throw new InvalidOperationException("CopyFrom 目标 bank 容量不足。");

                Array.Copy(src.Data[b], 0, Data[b], 0, len);
                Array.Copy(src.Versions[b], 0, Versions[b], 0, len);
                Array.Copy(src.Counts[b], 0, Counts[b], 0, len);
                Current[b] = src.Current[b];
            }
        }
    }
}
