using System;
using System.Runtime.InteropServices;

namespace ComputeCache.Testing
{
    // 测试用值类型。
    internal enum Color : byte { Red = 1, Green = 2, Blue = 7 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Hit
    {
        public int Id;
        public int Score;
        public Hit(int id, int score) { Id = id; Score = score; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vec16
    {
        public long A;
        public long B;
        public Vec16(long a, long b) { A = a; B = b; }
    }

    [StructLayout(LayoutKind.Sequential, Size = 300)]
    internal struct TooBig { public long V; }

    /// <summary>
    /// 功能自检套件：每项返回 (name, pass, message)，汇总为 TestReport。
    /// </summary>
    public static class ComputeCacheTests
    {
        public static TestReport RunAll()
        {
            TestReport report = new TestReport();

            Run(report, "命名双路径声明取回", NamedDeclareLookup);
            Run(report, "顺序路径声明回填", SequenceDeclareFetch);
            Run(report, "两路径混用各自计数共占同一 bank", MixedPaths);
            Run(report, "冲突即抛/一致幂等", ConflictAndIdempotent);
            Run(report, "非法参数与时序全拒绝", IllegalArguments);
            Run(report, "Handle 编码 Packed==0 无效/解码", HandleEncoding);
            Run(report, "类型不符 Lookup 抛/TryLookup false", TypeMismatch);
            Run(report, "标量读写/GetRef/TryGet", ScalarReadWrite);
            Run(report, "向量单点 Get 不查 version/欠尺寸 Span 抛", VectorSinglePointAndSpan);
            Run(report, "version 写入命中/整批失效/回绕", VersionLifecycle);
            Run(report, "Count vs Length/Invalidate 不动 Count", CountVsLength);
            Run(report, "批量命中扫描二次全命中跳过", BatchHitScan);
            Run(report, "Extend 旧 handle 不变/新槽可用/hash 改变", ExtendStability);
            Run(report, "AllocateMemory 多份独立", AllocateIndependent);
            Run(report, "EnsureCapacity 高水位非破坏/复用后无假命中", EnsureCapacityReuse);
            Run(report, "Clone 独立/CopyFrom 全等/容量不足拒绝/跳空 tier", CloneCopyFrom);
            Run(report, "Accessor 两形态结果一致", TwoFormsEqual);

            return report;
        }

        // ---------------- 各测试 ----------------

        private static void NamedDeclareLookup()
        {
            var builder = new CacheBuilder();
            builder.Declare<float>("Speed");
            builder.DeclareVector<Hit>("Hits", 32);
            CacheLayout layout = builder.Build();

            var speed = layout.Lookup<float>("Speed");
            var hits = layout.LookupVector<Hit>("Hits");
            Check(layout.GetLength(hits) == 32, "向量长度应为 32");

            CacheMemory mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();
            acc.Set(speed, 10f);
            Check(acc.Get(speed) == 10f, "标量读回应为 10");

            acc.Set(hits, 5, new Hit(5, 50));
            Hit h = acc.Get(hits, 5);
            Check(h.Id == 5 && h.Score == 50, "向量读回不符");
        }

        private static void SequenceDeclareFetch()
        {
            var b = new CacheBuilder();
            b.SequenceDeclare<int>();
            b.SequenceDeclareVector<float>(4);
            b.SequenceDeclare<Color>();
            CacheLayout layout = b.Build();

            Handle<int> hi = b.SequenceFetch<int>();
            VectorHandle<float> hf = b.SequenceFetchVector<float>();
            Handle<Color> hc = b.SequenceFetch<Color>();

            Check(hi.IsValid && hf.IsValid && hc.IsValid, "顺序句柄应有效");
            Check(layout.GetLength(hf) == 4, "顺序向量长度应为 4");

            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();
            acc.Set(hi, 7);
            acc.Set(hc, Color.Blue);
            acc.Set(hf, 2, 3.5f);
            Check(acc.Get(hi) == 7 && acc.Get(hc) == Color.Blue && acc.Get(hf, 2) == 3.5f, "顺序读写不符");
        }

        private static void MixedPaths()
        {
            var b = new CacheBuilder();
            b.Declare<int>("A");          // S4 offset 0
            b.SequenceDeclare<int>();      // S4 offset 1
            b.Declare<int>("B");          // S4 offset 2
            CacheLayout layout = b.Build();

            var a = layout.Lookup<int>("A");
            var bb = layout.Lookup<int>("B");
            var seq = b.SequenceFetch<int>();

            Check(HandlePacking.Bank(a.Packed) == BankKind.S4, "A 应在 S4");
            Check(HandlePacking.Offset(a.Packed) == 0, "A offset 0");
            Check(HandlePacking.Offset(seq.Packed) == 1, "顺序 offset 1");
            Check(HandlePacking.Offset(bb.Packed) == 2, "B offset 2");
        }

        private static void ConflictAndIdempotent()
        {
            var b = new CacheBuilder();
            b.Declare<int>("X");
            b.Declare<int>("X"); // 完全一致幂等
            ExpectThrows(() => b.Declare<float>("X"), "同 key 不同 T 应抛");
            ExpectThrows(() => b.DeclareVector<int>("X", 4), "scalar→vector 应抛");

            var b2 = new CacheBuilder();
            b2.DeclareVector<int>("V", 4);
            ExpectThrows(() => b2.DeclareVector<int>("V", 8), "向量长度冲突应抛");
        }

        private static void IllegalArguments()
        {
            ExpectThrows(() => new CacheBuilder().DeclareVector<int>("v", 0), "length<=0 应抛");
            ExpectThrows(() => new CacheBuilder().Declare<TooBig>("big"), "sizeof>256 应抛");

            var b = new CacheBuilder();
            b.Declare<int>("a");
            b.Build();
            ExpectThrows(() => b.Declare<int>("b"), "Build 后声明应抛");

            var b2 = new CacheBuilder();
            b2.SequenceDeclare<int>();
            ExpectThrows(() => b2.SequenceFetch<int>(), "Build 前 fetch 应抛");
            b2.Build();
            b2.SequenceFetch<int>();
            ExpectThrows(() => b2.SequenceFetch<int>(), "越界 fetch 应抛");
        }

        private static void HandleEncoding()
        {
            Handle<int> invalid = default(Handle<int>);
            Check(!invalid.IsValid && invalid.Packed == 0, "默认 handle 应无效");

            uint packed = HandlePacking.Pack(BankKind.V16, 1234, 0);
            Check(HandlePacking.Bank(packed) == BankKind.V16, "bank 解码错");
            Check(HandlePacking.Offset(packed) == 1234, "offset 解码错");
            Check(packed != 0, "有效 handle Packed!=0");
        }

        private static void TypeMismatch()
        {
            var b = new CacheBuilder();
            b.Declare<float>("F");
            var layout = b.Build();
            ExpectThrows(() => layout.Lookup<int>("F"), "类型不符 Lookup 应抛");
            ExpectThrows(() => layout.Lookup<float>("Missing"), "缺失 key 应抛");

            Handle<int> h;
            Check(!layout.TryLookup<int>("F", out h), "类型不符 TryLookup 应 false");
            Handle<float> hf;
            Check(layout.TryLookup<float>("F", out hf), "正确 TryLookup 应 true");
        }

        private static void ScalarReadWrite()
        {
            var b = new CacheBuilder();
            b.Declare<int>("N");
            var layout = b.Build();
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();
            var n = layout.Lookup<int>("N");

            int v;
            Check(!acc.TryGet(n, out v), "未写应 miss");
            acc.Set(n, 42);
            Check(acc.IsValid(n) && acc.TryGet(n, out v) && v == 42, "写后命中 42");

            ref int r = ref acc.GetRef(n);
            r = 99;
            Check(acc.Get(n) == 99, "GetRef 写回应为 99");

            acc.MarkInvalid(n);
            Check(!acc.IsValid(n), "MarkInvalid 后应 miss");
        }

        private static void VectorSinglePointAndSpan()
        {
            var b = new CacheBuilder();
            b.DeclareVector<int>("Vi", 8);
            b.DeclareVector<short>("Vs", 8); // 2 字节 → V4 槽，欠尺寸
            var layout = b.Build();
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();

            var vi = layout.LookupVector<int>("Vi");
            // 单点 Get 不查 version，返回槽内现值（默认 0）。
            Check(acc.Get(vi, 3) == 0, "单点 Get 应返回现值 0");
            ExpectThrows(() => acc.Get(vi, 8), "越界单点 Get 应抛");

            var vs = layout.LookupVector<short>("Vs");
            ExpectThrows(() => acc.GetFullSpan(vs), "欠尺寸类型 Span 应抛");
            // 但欠尺寸类型可逐元素访问。
            acc.Set(vs, 1, (short)5);
            Check(acc.Get(vs, 1) == (short)5, "欠尺寸逐元素读写应正常");
        }

        private static void VersionLifecycle()
        {
            var b = new CacheBuilder();
            b.Declare<int>("S");
            var layout = b.Build();
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            var s = layout.Lookup<int>("S");

            // 跨越 byte 回绕：每轮写后必命中，未写一轮即失效。
            for (int i = 0; i < 300; i++)
            {
                acc.Invalidate();
                acc.Set(s, i);
                Check(acc.IsValid(s) && acc.Get(s) == i, "回绕中写后应命中");
                acc.Invalidate();
                Check(!acc.IsValid(s), "未重写一轮应失效");
            }
        }

        private static void CountVsLength()
        {
            var b = new CacheBuilder();
            b.DeclareVector<int>("V", 10);
            var layout = b.Build();
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();
            var v = layout.LookupVector<int>("V");

            Span<int> full = acc.GetFullSpan(v);
            Check(full.Length == 10, "GetFullSpan 长度应为 Length");
            for (int i = 0; i < 10; i++) full[i] = i;
            acc.SetCount(v, 4);
            Span<int> span = acc.GetSpan(v);
            Check(span.Length == 4 && span[3] == 3, "GetSpan 应为 [0,Count)");

            acc.Invalidate();
            Check(acc.GetCount(v) == 4, "Invalidate 不应动 Count");
            ExpectThrows(() => acc.SetCount(v, 11), "Count 越界应抛");
        }

        private static void BatchHitScan()
        {
            var b = new CacheBuilder();
            b.DeclareVector<int>("Hits", 16);
            var layout = b.Build();
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();
            var hits = layout.LookupVector<int>("Hits");

            int computeCalls = 0;
            // 第一趟：全 miss，回填。
            ScanFill(acc, hits, ref computeCalls);
            Check(computeCalls == 16, "首趟应全部计算");

            // 第二趟：全命中跳过。
            computeCalls = 0;
            ScanFill(acc, hits, ref computeCalls);
            Check(computeCalls == 0, "二次扫描应全命中跳过");
        }

        private static void ScanFill(Accessor acc, VectorHandle<int> hits, ref int calls)
        {
            Span<int> vals; Span<byte> ver; byte cur;
            acc.GetFullSpan(hits, out vals, out ver, out cur);
            for (int idx = 0; idx < vals.Length; idx++)
            {
                if (ver[idx] == cur) continue;
                vals[idx] = idx * idx;
                ver[idx] = cur;
                calls++;
            }
        }

        private static void ExtendStability()
        {
            var b = new CacheBuilder();
            b.Declare<int>("Old");
            var layout = b.Build();
            var oldHandle = layout.Lookup<int>("Old");

            var b2 = CacheBuilder.Extend(layout);
            b2.Declare<bool>("NewFlag");
            var layout2 = b2.Build();

            var oldIn2 = layout2.Lookup<int>("Old");
            Check(oldIn2.Packed == oldHandle.Packed, "Extend 后旧 handle 字节应不变");
            Check(layout2.Hash != layout.Hash, "Extend 后 hash 应改变");

            var mem = layout2.AllocateMemory();
            var acc = new Accessor(layout2, mem);
            acc.Invalidate();
            var flag = layout2.Lookup<bool>("NewFlag");
            acc.Set(flag, true);
            acc.Set(oldIn2, 5);
            Check(acc.Get(flag) && acc.Get(oldIn2) == 5, "Extend 新旧槽均可用");
        }

        private static void AllocateIndependent()
        {
            var b = new CacheBuilder();
            b.Declare<int>("N");
            var layout = b.Build();
            var n = layout.Lookup<int>("N");

            var m1 = layout.AllocateMemory();
            var m2 = layout.AllocateMemory();
            var a1 = new Accessor(layout, m1);
            var a2 = new Accessor(layout, m2);
            a1.Invalidate(); a2.Invalidate();
            a1.Set(n, 1);
            a2.Set(n, 2);
            Check(a1.Get(n) == 1 && a2.Get(n) == 2, "多份内存应相互独立");
        }

        private static void EnsureCapacityReuse()
        {
            var b1 = new CacheBuilder();
            b1.DeclareVector<int>("V", 4);
            var small = b1.Build();

            var mem = small.AllocateMemory();
            var accS = new Accessor(small, mem);
            accS.Invalidate();
            var vs = small.LookupVector<int>("V");
            for (int i = 0; i < 4; i++) accS.Set(vs, i, 100 + i);

            // 更大的 layout（同 bank 更长）复用同一内存条。
            var b2 = new CacheBuilder();
            b2.DeclareVector<int>("V", 12);
            var big = b2.Build();
            mem.EnsureCapacity(big);

            // 扩容非破坏：旧活值留存。
            var accBig = new Accessor(big, mem);
            var vb = big.LookupVector<int>("V");
            Check(accBig.Get(vb, 0) == 100 && accBig.IsValid(vb, 0), "扩容后旧活值应留存且命中");

            // 换 layout 后必须 Invalidate，否则残值假命中；这里验证 Invalidate 后无假命中。
            accBig.Invalidate();
            Check(!accBig.IsValid(vb, 0), "Invalidate 后旧值应失效");
            // 新区天然 miss。
            Check(!accBig.IsValid(vb, 10), "新扩容区应天然 miss");
        }

        private static void CloneCopyFrom()
        {
            var b = new CacheBuilder();
            b.Declare<int>("N");
            b.DeclareVector<int>("V", 4);
            var layout = b.Build();
            var n = layout.Lookup<int>("N");
            var v = layout.LookupVector<int>("V");

            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            acc.Invalidate();
            acc.Set(n, 11);
            acc.Set(v, 0, 22);

            var clone = mem.Clone();
            var accC = new Accessor(layout, clone);
            Check(accC.Get(n) == 11 && accC.Get(v, 0) == 22, "Clone 应全等");

            // 改原，clone 不受影响。
            acc.Set(n, 99);
            Check(accC.Get(n) == 11, "Clone 应独立");

            // CopyFrom 全等恢复。
            mem.CopyFrom(clone);
            Check(acc.Get(n) == 11, "CopyFrom 应恢复到 11");

            // 容量不足拒绝。
            var b2 = new CacheBuilder();
            b2.DeclareVector<int>("V", 100);
            var bigLayout = b2.Build();
            var bigMem = bigLayout.AllocateMemory();
            var bigAcc = new Accessor(bigLayout, bigMem);
            bigAcc.Invalidate();
            ExpectThrows(() => mem.CopyFrom(bigMem), "目标容量不足应拒绝");
        }

        private static void TwoFormsEqual()
        {
            var b = new CacheBuilder();
            b.Declare<int>("N");
            b.DeclareVector<int>("V", 4);
            var layout = b.Build();
            var n = layout.Lookup<int>("N");
            var v = layout.LookupVector<int>("V");

            var mem = layout.AllocateMemory();
            mem.Invalidate();

            // 静态形态。
            Accessor.Set(mem, n, 7);
            Accessor.Set(layout, mem, v, 1, 8);
            int staticScalar = Accessor.Get(mem, n);
            int staticVector = Accessor.Get(layout, mem, v, 1);

            // 结构体形态。
            var acc = new Accessor(layout, mem);
            int structScalar = acc.Get(n);
            int structVector = acc.Get(v, 1);

            Check(staticScalar == structScalar && staticVector == structVector, "两形态结果应一致");
            Check(staticScalar == 7 && staticVector == 8, "值应为 7/8");
        }

        // ---------------- 工具 ----------------

        private static void Run(TestReport report, string name, Action test)
        {
            try
            {
                test();
                report.Add(new TestResult(name, true, ""));
            }
            catch (Exception ex)
            {
                report.Add(new TestResult(name, false, ex.GetType().Name + ": " + ex.Message));
            }
        }

        private static void Check(bool cond, string message)
        {
            if (!cond) throw new Exception("断言失败：" + message);
        }

        private static void ExpectThrows(Action action, string message)
        {
            bool threw = false;
            try { action(); }
            catch { threw = true; }
            if (!threw) throw new Exception("应抛异常但未抛：" + message);
        }
    }
}
