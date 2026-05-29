using System.Collections.Generic;
using System.Text;

namespace ComputeCache.Testing
{
    /// <summary>单项自检结果。</summary>
    public struct TestResult
    {
        public string Name;
        public bool Pass;
        public string Message;

        public TestResult(string name, bool pass, string message)
        {
            Name = name;
            Pass = pass;
            Message = message;
        }
    }

    /// <summary>功能测试汇总报告。</summary>
    public sealed class TestReport
    {
        public readonly List<TestResult> Results = new List<TestResult>();

        public int PassCount { get; private set; }
        public int FailCount { get; private set; }
        public bool AllPassed { get { return FailCount == 0; } }

        public void Add(TestResult r)
        {
            Results.Add(r);
            if (r.Pass) PassCount++; else FailCount++;
        }

        public string Summary()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Results.Count; i++)
            {
                TestResult r = Results[i];
                sb.Append(r.Pass ? "[PASS] " : "[FAIL] ");
                sb.Append(r.Name);
                if (!r.Pass && !string.IsNullOrEmpty(r.Message))
                {
                    sb.Append(" - ");
                    sb.Append(r.Message);
                }
                sb.Append('\n');
            }
            sb.Append("----\n");
            sb.Append("通过 ").Append(PassCount).Append(" / 失败 ").Append(FailCount)
              .Append(" / 全部通过 ").Append(AllPassed ? "是" : "否");
            return sb.ToString();
        }
    }
}
