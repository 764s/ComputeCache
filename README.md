# ComputeCache

一个纯 C#、强类型、单线程、接近裸数组访问成本的值类型缓存 / 寄存器库。无业务语义，支持 Unity 2022.3 +。

布局在构建期定稿，热路径只用 4 字节 `Handle`，无字符串、无反射、无类型判定分支、无运行期 GC 分配。

完整设计见 [ComputeCache.md](ComputeCache.md)。

---

## 0. 适用场景

ComputeCache 解决「同一组数据在一帧 / 一次求值内被反复计算，需要按需缓存中间结果并能整批失效重算」的问题，典型用在 DSL / 规则引擎 / 行为树 / 表达式求值等高频、固定形状的计算上。

| 适合 | 不适合 |
|---|---|
| 高频中间值缓存、规则结果缓存 | 少量配置查询（用 `Dictionary` 即可） |
| 批量目标 + 逐元素命中扫描 | 引用对象缓存（应外置对象表 + `int` id） |
| 生产-宣告-消费的动态数据 | 运行期增长的列表（向量长度构建期固定） |
| 分阶段增量布局（`Extend`） | 多线程共享同一 `CacheMemory`（每线程独立内存条） |
| DSL / 代码生成顺序登记（顺序路径） | 运行时 vector 扩容 |
| rollback / replay、多 layout 复用同一内存条池 | 跨机器状态序列化（version 本地、槽为本机布局字节） |

核心取舍：**用「构建期一次性定稿布局 + 热路径零分支」换运行期速度与零分配**。如果数据形状在运行期不可预知、或主要是稀疏的键值查询，应直接用 `Dictionary`。

---

## 1. 关键概念解释

三个核心类型职责正交：

```text
CacheLayout   编译产物：分桶 / offset / 长度 / hash，不可变，可被多份内存与多线程共享
CacheMemory   通用内存条：与布局无关，16 bank 按高水位增长复用，可克隆 / 换装 / 回滚
Accessor      访问逻辑：无状态；纯静态函数，或绑定 (CacheLayout, CacheMemory) 的结构体
```

数据流：

```text
声明 → Build 定稿 CacheLayout → AllocateMemory 得 CacheMemory → Accessor(layout, memory) 按 bank+offset 读写
```

- **Handle / VectorHandle**：`Handle<T>` / `VectorHandle<T>` 均为 4 字节只读值，可自由复制、长期保存、跨线程传递（纯标识，不含数据指针）。热路径只认 handle，不碰字符串。`Packed == 0` 即 invalid。
- **Bank（能级分桶）**：类型按 `sizeof(T)` 归入固定能级（1/4/8/16/32/64/128/256 字节），scalar 与 vector 各一组，共 16 个 bank。同能级多个槽共享一块类型擦除的 `SlotN[]`，按 offset 区分；类型在访问时由泛型 `T` 决定。大于 256 字节或对齐大于 8 字节的类型被拒绝。
- **只存值类型**：约束 `where T : unmanaged`。引用对象由调用方维护对象表，缓存内只存其 `int` 索引——值语义是 `Clone` / `CopyFrom` 这类 `Array.Copy` 级快照的前提。
- **Version 失效**：每个 bank 持一个 `byte` 的 `CurrentVersion` 与平行 `version` 数组。命中条件是 `Versions[i] == CurrentVersion`；写入时把元素 version 设为当前值；`Invalidate()` 只对每个活跃 bank 做 `CurrentVersion += 1`，O(bank 数) 即让上一轮所有值整批失效，免每帧清数组。
- **高水位内存条**：`CacheMemory` 不记 layout 身份，`EnsureCapacity` 把各 bank 只增不减地长到历史最大需求；多个 layout 轮流绑定后趋于满载，可承载任意容量被满足的 layout。**换装到新 layout 后必须 `Invalidate()`**，否则会把上一 layout 的残值读成命中。
- **两条声明路径**：命名路径（`Declare`/`Lookup`，key 供人读、跨模块共享）与顺序路径（`SequenceDeclare`/`SequenceFetch`，无 key，按调用顺序登记回填，供 DSL / 代码生成），共享同一分桶规则，可在同一 builder 混用，统一 `Build`。
- **Extend（增量布局）**：基于已有 layout append-only 追加声明，旧 handle 字节级不变、永久稳定；既有内存条 `EnsureCapacity` 后即可继续承载。

---

## 2. 使用方式

### 2.1 基本用法

```csharp
var builder = new CacheBuilder();
builder.Declare<float>("Speed");
builder.DeclareVector<Hit>("Hits", length: 32);
CacheLayout layout = builder.Build();

Handle<float>       speed = layout.Lookup<float>("Speed");
VectorHandle<Hit>   hits  = layout.LookupVector<Hit>("Hits");

CacheMemory memory = layout.AllocateMemory();
var acc = new Accessor(layout, memory);   // 结构体形态
acc.Invalidate();                         // 开新会话，旧值整批失效
acc.Set(speed, 10f);

Accessor.Set(memory, speed, 10f);         // 静态形态，语义等价
```

`Accessor` 有两种等价形态：**静态**（`Accessor.Op(memory, ...)`）与**结构体**（`new Accessor(layout, memory)` 绑定一次后省去前缀参数）。

### 2.2 批量命中扫描（主热路径）

固定向量 + 逐元素命中的紧凑循环，零方法调用，一次取出整段 data / version / 当前 version：

```csharp
acc.GetFullSpan(hits, out var vals, out var ver, out var cur);
foreach (int idx in targets)
{
    if (ver[idx] == cur) continue;     // 命中跳过
    vals[idx] = Compute(idx);
    ver[idx]  = cur;                   // 写 version 即标记命中
}
```

> span 不得跨 `Invalidate` / `CopyFrom` 使用。

### 2.3 在无头环境运行自检与基准

核心库与测试同处 `src/ComputeCache/`（同一编译单元），无头薄入口在 `src/ComputeCache.Headless/`：

```bash
cd src/ComputeCache.Headless
dotnet run -c Release
```

会先打印功能自检报告（失败返回非零退出码），再打印性能基准。

### 2.4 在 Unity 中使用

将 `src/ComputeCache/` 纳入工程（其 `ComputeCache.asmdef` 以 `noEngineReferences:true` 独立编译），再以一个薄 `MonoBehaviour` 调用 `ComputeCache.Testing.ComputeCacheTests.RunAll()` 并 `Debug.Log` 报告即可。

### 2.5 目录结构

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

---

## 3. 性能报告

基准由 `ComputeCacheBenchmarks.RunAll(targets, registers, iterations)` 生成：同一意图下对比「常规写死数组 / 字段读写」（baseline）与「ComputeCache 读写」（cache）。构建（`Declare` / `Build` / `AllocateMemory` / `Accessor` 绑定）在计时区外；`Stopwatch` 预热后取多轮最优；累加校验和防 DCE。**比值 = cache / baseline，越接近 1 越接近裸数组**。

下表为 `targets: 1024, registers: 64, iterations: 200` 的一次参考运行（net8.0 Release，绝对耗时随机器而异，应关注比值与场景趋势）：

| 场景 | baseline (ms) | cache (ms) | 比值 |
|---|---:|---:|---:|
| 标量单点 | 0.035 | 0.439 | 12.46 |
| 向量单点 | 0.156 | 3.704 | 23.75 |
| 批量命中 (全 miss，最坏) | 0.309 | 0.282 | 0.91 |
| 批量命中 (全 hit，纯扫描) | 0.218 | 0.176 | 0.81 |

解读：

- **批量命中扫描是设计的主访问模式**，比值趋近甚至略优于 1（接近裸数组），因为 `GetFullSpan` 一次取出整段 span，热循环内零方法调用、零分支分发。
- **单点 Set / Get** 每次调用都要经 handle 解包与 bank 寻址，相对裸字段 / 裸数组有固定分发开销（数值上放大，因绝对耗时本就极小）；它服务的是偶发单点读写，而非紧密循环。
- 实践中应尽量走批量命中扫描路径，单点访问仅用于低频读写。

复现：

```bash
cd src/ComputeCache.Headless
dotnet run -c Release
```

---

## 运行环境

```text
核心库目标 netstandard2.1；LangVersion 9 锁定，引擎与无头两侧一致，禁用 C# 10+ 语法
无 UnityEngine 引用，允许 unsafe，不依赖 Burst / Jobs
Unity 经 noEngineReferences:true 的 asmdef 引用；无头工程以 net8.0 编译，同样锁 LangVersion 9
```
