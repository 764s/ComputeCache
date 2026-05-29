namespace ComputeCache
{
    /// <summary>
    /// 一条声明的最终布局信息。命名声明带 Key；顺序声明 Key 为 null。
    /// 一经 Build 即不可变。
    /// </summary>
    internal sealed class Entry
    {
        public string Key;       // 命名路径的 key；顺序路径为 null
        public string TypeId;    // 类型身份：AssemblyQualifiedName
        public BankKind Bank;
        public int Offset;       // bank 内槽偏移（向量为段首）
        public int Length;       // 标量为 1，向量为声明长度
        public bool IsVector;
        public uint Packed;      // 物化后的 handle 编码
    }
}
