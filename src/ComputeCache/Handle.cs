namespace ComputeCache
{
    /// <summary>
    /// 标量句柄：4 字节只读标识，可自由复制、长期保存、跨线程传递。
    /// 长度不入 handle；类型 T 在编译期编入，杜绝错配。
    /// </summary>
    public readonly struct Handle<T> where T : unmanaged
    {
        public readonly uint Packed;
        public Handle(uint packed) { Packed = packed; }
        // Packed == 0 即 invalid。
        public bool IsValid { get { return Packed != 0u; } }
    }

    /// <summary>
    /// 向量句柄：同标量句柄，4 字节只读标识；长度由 CacheLayout 持有。
    /// </summary>
    public readonly struct VectorHandle<T> where T : unmanaged
    {
        public readonly uint Packed;
        public VectorHandle(uint packed) { Packed = packed; }
        public bool IsValid { get { return Packed != 0u; } }
    }
}
