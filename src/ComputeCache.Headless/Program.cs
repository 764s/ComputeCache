using System;
using ComputeCache;
using ComputeCache.Testing;

namespace ComputeCache.Headless
{
    // 薄入口：仅调用 RunAll() 与基准 RunAll(...) 并打印，无任何业务逻辑。
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.WriteLine("== ComputeCache 功能测试 ==");
            TestReport report = ComputeCacheTests.RunAll();
            Console.WriteLine(report.Summary());

            Console.WriteLine();
            Console.WriteLine("== ComputeCache 性能基准 ==");
            BenchReport bench = ComputeCacheBenchmarks.RunAll(targets: 1024, registers: 64, iterations: 200);
            Console.WriteLine(bench.Summary());

            // 失败返回非零退出码。
            return report.AllPassed ? 0 : 1;
        }
    }
}
