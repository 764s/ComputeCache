namespace ComputeCache
{
    /// <summary>
    /// 可选诊断：活跃 bank 数、分配字节、有效槽数。用于判断缓存是否值回本。
    /// 非热路径，仅按需调用。
    /// </summary>
    public struct Diagnostics
    {
        public int ActiveBanks;     // 已分配的 bank 数
        public long AllocatedBytes; // 各 bank 数据数组合计字节（仅 SlotN 数据）
        public int AllocatedSlots;  // 各 bank 已分配槽合计

        public static Diagnostics Of(CacheMemory memory)
        {
            Diagnostics r = new Diagnostics();
            for (int b = 0; b < 16; b++)
            {
                if (memory.Data[b] == null) continue;
                r.ActiveBanks++;
                int len = memory.Lengths[b];
                r.AllocatedSlots += len;
                r.AllocatedBytes += (long)len * SlotMath.EnergyBytesOf((BankKind)b);
            }
            return r;
        }
    }
}
