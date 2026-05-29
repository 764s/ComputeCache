# ComputeCache
一个纯 C# 模拟内存条, 适用 DSL 场景. 支持 Unity 2022.3 +.

完整设计见 [ComputeCache.md](ComputeCache.md)。

## 启动契约（已落地）

```text
core + test 位置 : src/ComputeCache/         纯 C#，库与测试同一编译单元，可被无头工程直接编译
测试调用位置     : src/ComputeCache.Headless/ 无头 Main：Console 输出报告，失败返回非零退出码
```

## 目录结构

```text
src/ComputeCache/            核心库 + 自检/基准（同一编译单元，无 UnityEngine / NUnit 依赖）
  BankKind / Slots           16 个 bank 与类型擦除物理槽
  Handle / HandlePacking     4 字节句柄与位编码
  SlotMath                   分桶与对齐计算
  CacheBuilder               命名 + 顺序双路径声明、Extend、统一 Build
  CacheLayout                不可变布局：Lookup / GetLength / Hash / AllocateMemory
  CacheMemory                通用内存条：高水位增长 / Invalidate / Clone / CopyFrom
  Accessor                   双形态访问：标量 / 向量 / Count / Span / 批量命中
  XxHash64 / LayoutHash      稳定 layout hash
  Diagnostics                可选诊断
  Testing/                   ComputeCacheTests（功能自检）、ComputeCacheBenchmarks（基准）
  ComputeCache.asmdef        Unity 程序集定义（noEngineReferences:true，allowUnsafeCode）
src/ComputeCache.Headless/   无头薄入口（仅打印报告）
```

## 在无头环境运行自检与基准

```bash
cd src/ComputeCache.Headless
dotnet run -c Release
```

## 在 Unity 中使用

将 `src/ComputeCache/` 纳入工程（其 `ComputeCache.asmdef` 以 `noEngineReferences:true` 独立编译），
再以一个薄 MonoBehaviour 调用 `ComputeCache.Testing.ComputeCacheTests.RunAll()` 并 `Debug.Log` 报告即可。
