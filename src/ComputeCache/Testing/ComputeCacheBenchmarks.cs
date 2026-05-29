using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ComputeCache.Testing
{
    /// <summary>单场景基准结果。</summary>
    public struct BenchRow
    {
        public string Scenario;
        public double BaselineMs;
        public double CacheMs;
        public double Ratio; // cache / baseline
        public long Checksum;
    }

    /// <summary>性能基准汇总报告。</summary>
    public sealed class BenchReport
    {
        public readonly List<BenchRow> Rows = new List<BenchRow>();

        public string Summary()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("场景".PadRight(28)).Append("baseline(ms)".PadLeft(14))
              .Append("cache(ms)".PadLeft(14)).Append("比值".PadLeft(10)).Append('\n');
            for (int i = 0; i < Rows.Count; i++)
            {
                BenchRow r = Rows[i];
                sb.Append(r.Scenario.PadRight(28))
                  .Append(r.BaselineMs.ToString("F3").PadLeft(14))
                  .Append(r.CacheMs.ToString("F3").PadLeft(14))
                  .Append(r.Ratio.ToString("F2").PadLeft(10))
                  .Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 性能基准：同一意图下对比「常规数组/字段读写」与「ComputeCache 读写」。
    /// 构建在计时区外；Stopwatch 预热后取多次最优；累加校验和防 DCE。
    /// </summary>
    public static class ComputeCacheBenchmarks
    {
        private const int Repeats = 5; // 取最优的轮数

        public static BenchReport RunAll(int targets, int registers, int iterations)
        {
            BenchReport report = new BenchReport();
            report.Rows.Add(ScalarSingle(registers, iterations));
            report.Rows.Add(VectorSingle(targets, iterations));
            report.Rows.Add(BatchMiss(targets, iterations));
            report.Rows.Add(BatchHit(targets, iterations));
            return report;
        }

        // 标量单点：寄存器逐个写后读。
        private static BenchRow ScalarSingle(int registers, int iterations)
        {
            // 构建（计时区外）。
            var builder = new CacheBuilder();
            for (int i = 0; i < registers; i++) builder.Declare<int>("r" + i);
            var layout = builder.Build();
            var handles = new Handle<int>[registers];
            for (int i = 0; i < registers; i++) handles[i] = layout.Lookup<int>("r" + i);
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            int[] baseline = new int[registers];

            long sum = 0;
            double baseMs = BestOf(() =>
            {
                long s = 0;
                for (int it = 0; it < iterations; it++)
                    for (int i = 0; i < registers; i++)
                    {
                        baseline[i] = i + it;
                        s += baseline[i];
                    }
                return s;
            }, out long bcheck);

            double cacheMs = BestOf(() =>
            {
                long s = 0;
                acc.Invalidate();
                for (int it = 0; it < iterations; it++)
                    for (int i = 0; i < registers; i++)
                    {
                        acc.Set(handles[i], i + it);
                        s += acc.Get(handles[i]);
                    }
                return s;
            }, out long ccheck);

            sum = bcheck + ccheck;
            return Row("标量单点", baseMs, cacheMs, sum);
        }

        // 向量单点：逐元素 Set / Get。
        private static BenchRow VectorSingle(int targets, int iterations)
        {
            var builder = new CacheBuilder();
            builder.DeclareVector<int>("V", targets);
            var layout = builder.Build();
            var v = layout.LookupVector<int>("V");
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);
            int[] baseline = new int[targets];

            double baseMs = BestOf(() =>
            {
                long s = 0;
                for (int it = 0; it < iterations; it++)
                    for (int i = 0; i < targets; i++)
                    {
                        baseline[i] = i ^ it;
                        s += baseline[i];
                    }
                return s;
            }, out long bcheck);

            double cacheMs = BestOf(() =>
            {
                long s = 0;
                acc.Invalidate();
                for (int it = 0; it < iterations; it++)
                    for (int i = 0; i < targets; i++)
                    {
                        acc.Set(v, i, i ^ it);
                        s += acc.Get(v, i);
                    }
                return s;
            }, out long ccheck);

            return Row("向量单点", baseMs, cacheMs, bcheck + ccheck);
        }

        // 批量命中（全 miss，最坏）：每帧 Invalidate + GetFullSpan 扫描回填。
        private static BenchRow BatchMiss(int targets, int iterations)
        {
            var builder = new CacheBuilder();
            builder.DeclareVector<int>("V", targets);
            var layout = builder.Build();
            var v = layout.LookupVector<int>("V");
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);

            int[] baseVals = new int[targets];
            byte[] baseVer = new byte[targets];
            byte baseCur = 1;

            double baseMs = BestOf(() =>
            {
                long s = 0;
                for (int it = 0; it < iterations; it++)
                {
                    baseCur++;
                    for (int i = 0; i < targets; i++)
                    {
                        if (baseVer[i] == baseCur) { s += baseVals[i]; continue; }
                        baseVals[i] = i + it;
                        baseVer[i] = baseCur;
                        s += baseVals[i];
                    }
                }
                return s;
            }, out long bcheck);

            double cacheMs = BestOf(() =>
            {
                long s = 0;
                for (int it = 0; it < iterations; it++)
                {
                    acc.Invalidate();
                    Span<int> vals; Span<byte> ver; byte cur;
                    acc.GetFullSpan(v, out vals, out ver, out cur);
                    for (int i = 0; i < vals.Length; i++)
                    {
                        if (ver[i] == cur) { s += vals[i]; continue; }
                        vals[i] = i + it;
                        ver[i] = cur;
                        s += vals[i];
                    }
                }
                return s;
            }, out long ccheck);

            return Row("批量命中(全 miss)", baseMs, cacheMs, bcheck + ccheck);
        }

        // 批量命中（全 hit，纯扫描）：预填后逐帧只读。
        private static BenchRow BatchHit(int targets, int iterations)
        {
            var builder = new CacheBuilder();
            builder.DeclareVector<int>("V", targets);
            var layout = builder.Build();
            var v = layout.LookupVector<int>("V");
            var mem = layout.AllocateMemory();
            var acc = new Accessor(layout, mem);

            // 预填（计时区外）。
            acc.Invalidate();
            {
                Span<int> vals; Span<byte> ver; byte cur;
                acc.GetFullSpan(v, out vals, out ver, out cur);
                for (int i = 0; i < vals.Length; i++) { vals[i] = i; ver[i] = cur; }
            }

            int[] baseVals = new int[targets];
            byte[] baseVer = new byte[targets];
            byte baseCur = 3;
            for (int i = 0; i < targets; i++) { baseVals[i] = i; baseVer[i] = baseCur; }

            double baseMs = BestOf(() =>
            {
                long s = 0;
                for (int it = 0; it < iterations; it++)
                    for (int i = 0; i < targets; i++)
                        if (baseVer[i] == baseCur) s += baseVals[i];
                return s;
            }, out long bcheck);

            double cacheMs = BestOf(() =>
            {
                long s = 0;
                for (int it = 0; it < iterations; it++)
                {
                    Span<int> vals; Span<byte> ver; byte cur;
                    acc.GetFullSpan(v, out vals, out ver, out cur);
                    for (int i = 0; i < vals.Length; i++)
                        if (ver[i] == cur) s += vals[i];
                }
                return s;
            }, out long ccheck);

            return Row("批量命中(全 hit)", baseMs, cacheMs, bcheck + ccheck);
        }

        // ---------------- 工具 ----------------

        private static BenchRow Row(string name, double baseMs, double cacheMs, long checksum)
        {
            BenchRow r = new BenchRow();
            r.Scenario = name;
            r.BaselineMs = baseMs;
            r.CacheMs = cacheMs;
            r.Ratio = baseMs > 0 ? cacheMs / baseMs : 0;
            r.Checksum = checksum;
            return r;
        }

        // 预热一次后取多轮最优耗时（ms）。
        private static double BestOf(Func<long> body, out long checksum)
        {
            checksum = body(); // 预热
            double best = double.MaxValue;
            Stopwatch sw = new Stopwatch();
            for (int r = 0; r < Repeats; r++)
            {
                sw.Restart();
                checksum += body();
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms < best) best = ms;
            }
            return best;
        }
    }
}
