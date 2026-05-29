# ComputeCache

纯 C#、强类型、单线程、接近裸数组访问成本的值类型缓存 / 寄存器库。无业务语义。

布局在构建期定稿，热路径只用 4 字节 Handle，无字符串、无反射、无类型判定分支。

```text
CacheLayout   编译产物：分桶 / offset / 长度 / hash，不可变，可被多份内存与多线程共享
CacheMemory   通用内存条：与布局无关，16 bank 按高水位增长复用，可克隆 / 换装 / 回滚
Accessor      访问逻辑：无状态；纯静态函数，或绑定 (CacheLayout, CacheMemory) 的结构体
```

```text
声明 → Build 定稿 CacheLayout → AllocateMemory 得 CacheMemory → Accessor(layout, memory) 按 bank+offset 读写
```

```csharp
var builder = new CacheBuilder();
builder.Declare<float>("Speed");
builder.DeclareVector<Hit>("Hits", length: 32);
CacheLayout layout = builder.Build();

var speed = layout.Lookup<float>("Speed");
var hits  = layout.LookupVector<Hit>("Hits");

CacheMemory memory = layout.AllocateMemory();
var acc = new Accessor(layout, memory);   // 结构体形态
acc.Invalidate();                         // 开新会话，旧值整批失效
acc.Set(speed, 10f);

Accessor.Set(memory, speed, 10f);         // 静态形态，语义等价
```

---

## 1. 存储模型

### 1.1 标准化 bank

类型按 `Unsafe.SizeOf<T>()` 归入固定能级，scalar / vector 各一组；每种 `BankKind` 在一份内存内唯一：

```text
能级(字节)  1  4  8  16  32  64  128  256      > 256 拒绝
BankKind   S1 S4 S8 S16 S32 S64 S128 S256
           V1 V4 V8 V16 V32 V64 V128 V256
```

一个 bank 是一块类型擦除的 `SlotN[]` + 平行 `version` 数组；同能级多槽共享，按 offset 区分。bank 不记逻辑类型——`Slot4` 可承 `float` 也可承 `int`，类型在访问时由 `T` 定。`enum` 按底层整型、自定义 struct 按总大小入桶；被重解释的 struct 标 `[StructLayout(LayoutKind.Sequential)]`。

`SlotN` 以内建整型为基使其对齐恰为 `min(N, 8)`（`Slot8` 及以上以 `long` 铺满 → 对齐 8），故 `Unsafe.As<SlotN, T>()` 在 `alignof(T) ≤ 对齐` 时恒安全。

> 入桶条件：`sizeof(T) ≤ 能级` 且 `alignof(T) ≤ min(能级, 8)`；大槽容小类型，反之拒绝；不支持 `> 8` 字节对齐类型。

### 1.2 类型擦除与强类型分层

```text
物理层：bank = SlotN[]，无逻辑类型，同能级共池
API 层 ：声明绑定 T；Handle<T> 把 T 编进编译期
```

`Lookup<U>` 与声明 T 不符、或 `Handle<float>` 调 `Get<int>`，被类型系统 / 查询校验拦截。同槽多类型解释须各声明一格。

### 1.3 只存值类型

`where T : unmanaged`。引用对象由调用方维护对象表，缓存内只存其 `int` 索引——值语义是 `Array.Copy` 克隆的前提。

---

## 2. Handle

`Handle<T>` / `VectorHandle<T>` 均 4 字节只读值，可自由复制、长期保存、跨线程传递（纯标识）。同一 layout 出厂的 handle 适用于任意容量被其满足的 `CacheMemory`。

```text
位 31..27  bank          (BankKind)
位 26..3   offsetPlusOne (1..16,777,215)
位  2..0   flags         (预留)
Packed == 0 即 invalid
```

长度不入 handle，由 `CacheLayout` 持有。

---

## 3. 声明与构建

两条路径共享同一分桶与 offset 规则，产出同一张 `CacheLayout`，可在同一 builder 混用（命名按 key、顺序按 ordinal，各自计数，共占同一批 bank/offset）。

### 3.1 命名路径

key 供人读、配置、跨模块共享公共槽。

```csharp
public sealed class CacheBuilder
{
    void Declare<T>(string key)                   where T : unmanaged;
    void DeclareVector<T>(string key, int length) where T : unmanaged;
    CacheLayout Build();
    static CacheBuilder Extend(CacheLayout baseLayout);
}
```

`Build`：分桶 → 每 bank 内顺序分配 offset（scalar 占 1 槽，vector 占 length 连续槽）→ 物化 packed handle → 记 bank 容量 → 计算稳定 layout hash（xxHash64，覆盖 key / typeId / bank / offset / length / isVector 及各 bank 容量）→ 冻结。多模块可分别声明后统一 `Build`。

冲突即抛：同 key 不同 T / scalar-vector / length；`length ≤ 0`、`sizeof > 256`、`Build` 后再声明均拒绝。完全一致的重复声明幂等。类型身份取 `AssemblyQualifiedName`。

### 3.2 顺序路径

无 key、无参，按调用顺序登记、同序回填，供 DSL / 代码生成。声明与回填两趟遍历同一结构，中间一次 `Build`。

```csharp
public enum CacheLayoutPhase : byte { SequenceDeclare, SequenceFetch }

public sealed class CacheBuilder
{
    void SequenceDeclare<T>()                 where T : unmanaged;
    void SequenceDeclareVector<T>(int length) where T : unmanaged;
    Handle<T>       SequenceFetch<T>()        where T : unmanaged;
    VectorHandle<T> SequenceFetchVector<T>()  where T : unmanaged;
}
```

第 i 次 `SequenceDeclare` 登记第 i 条 entry，第 i 次 `SequenceFetch` 回第 i 个 handle（cursor++，无 lookup、无 string）。两趟顺序须一致；`T` / scalar-vector / length 不符、`Build` 前 fetch、越界 fetch 均抛。

```csharp
interface INode { void VisitCache(CacheBuilder ctx, CacheLayoutPhase phase); }

CacheLayout Compile(INode[] nodes)
{
    var ctx = new CacheBuilder();
    foreach (var n in nodes) n.VisitCache(ctx, CacheLayoutPhase.SequenceDeclare);
    CacheLayout layout = ctx.Build();
    foreach (var n in nodes) n.VisitCache(ctx, CacheLayoutPhase.SequenceFetch);
    return layout;
}
```

节点 `SequenceDeclare` 趟登记、`SequenceFetch` 趟回填并自存 `Handle`，身份即 visit 顺序。

### 3.3 增量构建 Extend

基于已有 `CacheLayout` 追加声明，产出超集新 layout：

```csharp
var builder2 = CacheBuilder.Extend(layout);
builder2.Declare<bool>("NewFlag");
CacheLayout layout2 = builder2.Build();      // 旧 key 的 handle 在 layout2 字节级不变
```

```text
append-only：已有槽 bank/offset/length 不变 → 旧 handle 永久稳定；新槽追加到对应 bank 末尾
禁止：改已有槽形状、删槽（offset 只增不减）
```

旧 handle 字节不变，持有者无需重取。`layout2` 容量是 `layout` 的超集，既有内存条 `EnsureCapacity(layout2)` 即可继续承载（高水位增长，见 §5）。

---

## 4. CacheLayout

```csharp
public sealed class CacheLayout
{
    ulong Hash { get; }

    Handle<T>       Lookup<T>(string key)        where T : unmanaged;   // 缺失 / 类型不符抛异常
    VectorHandle<T> LookupVector<T>(string key)  where T : unmanaged;
    bool TryLookup<T>(string key, out Handle<T> h)             where T : unmanaged;
    bool TryLookupVector<T>(string key, out VectorHandle<T> h) where T : unmanaged;
    int  GetLength<T>(VectorHandle<T> h)         where T : unmanaged;

    CacheMemory AllocateMemory();
}
```

不可变，可并发读；查询非热路径，做完整校验。顺序声明的匿名槽不参与 `Lookup`，只经 `SequenceFetch` 取。`AllocateMemory` 新建一条内存条并 `EnsureCapacity(this)` 冷启动；可分配任意多份，亦可让既有内存条 `EnsureCapacity` 后复用。

---

## 5. CacheMemory

与布局无关的**通用内存条**：16 个 bank 槽位（S1..S256 / V1..V256），每槽一条类型擦除 `SlotN[]` + 平行 version + count。它不记 layout 身份，唯一状态量是各 bank 的当前长度。

```csharp
public sealed class CacheMemory
{
    void        EnsureCapacity(CacheLayout layout); // 每 bank 长度 ≥ 该 layout 需求；Array.Resize 增长，满载即 no-op
    void        Invalidate();                       // 各 active bank CurrentVersion += 1，旧值整批失效
    CacheMemory Clone();                            // 逐 bank 拷贝，跳过未分配 tier
    void        CopyFrom(CacheMemory src);          // 逐 bank 整体拷回，跳过未分配 tier；目标容量不足即拒绝
}
```

```text
每 bank：Data[]（SlotN[]）、Versions[]（byte）、Count（向量段首）、CurrentVersion（byte）
```

**高水位复用**：`EnsureCapacity` 把每个 bank 只增不减地长到历史最大需求；多个 layout 轮流绑定后趋于满载，此后承载任意容量被满足的 layout，无需关心 layout 身份。`Array.Resize` 增长非破坏——旧区 version / 数据原样保留（活值不因扩容失效），新区零填充，而 `version 0 ≠ CurrentVersion(初值 ≥ 1)` 使新槽天然为 miss，无需额外清理。

**绑定纪律**：内存条复用 / 换装到新 layout 后**必须 `Invalidate()`**（或视为冷启动）——一次整批 version bump 即令残留数据全部 miss，语义自洽；漏调会把上一 layout 的残值读成命中。`COMPUTE_CACHE_DEBUG` 下内存条仅在调试期记最近绑定的 `layout.Hash`，换 layout 而未 `Invalidate` 即告警；该标记不入数据、不入快照，release 零成本。

**快照 / 回滚**：`Clone` / `CopyFrom` 即 `Array.Copy` 级状态进出，`SlotN[]` 原样、命中与计数全等还原，仅同一运行时内有效。二者遍历 16 槽位，非空 bank 才拷、未分配 tier 直接跳——满载条服务小 layout 时开销不虚高。预分配一组等容量内存条作回滚环，`CopyFrom` 进出零额外分配。跨线程并发的唯一方式：每线程独立内存条共享同一 `CacheLayout`，靠 `Clone` 传值。

---

## 6. Accessor

无状态访问，两形态等价：

- **静态**：`Accessor.Op(...)`，标量带 `CacheMemory`，向量另带 `CacheLayout`（取 `Length`）。
- **结构体**：`new Accessor(layout, memory)` 绑定一次，调用省去前缀内存参数，余同。

```csharp
public readonly struct Accessor
{
    Accessor(CacheLayout layout, CacheMemory memory);   // 断言 memory 各 bank 容量 ⊇ layout（廉价恒开，非身份校验）
    void Invalidate();                                   // 转发 memory.Invalidate()
}
```

下列签名为静态形态；结构体形态去掉 `CacheMemory d` / `CacheLayout m` 前缀即可（如 `acc.Set(h, v)`）。

### 6.1 标量

```csharp
T     Get<T>(CacheMemory d, Handle<T> h);             // 未命中抛异常
bool  TryGet<T>(CacheMemory d, Handle<T> h, out T v);
void  Set<T>(CacheMemory d, Handle<T> h, in T v);     // 写值并标记命中
ref T GetRef<T>(CacheMemory d, Handle<T> h);          // 零拷贝；取即标记命中（按写意图）
bool  IsValid<T>(CacheMemory d, Handle<T> h);
void  MarkInvalid<T>(CacheMemory d, Handle<T> h);
```

### 6.2 向量

`Length`（声明固定）、`Count`（有效计数）、version 命中三者正交：单点 `Get` 仅按 `i ∈ [0, Length)` 越界检查并返回槽内现值，**不查 version**（命中由 `IsValid` / `TryGet` 或批量扫描判定）——与标量 `Get` 未命中即抛取舍不同，专为热路径手动管理命中。

```csharp
T     Get<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i);   // i ∈ [0, Length)
void  Set<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i, in T v);
ref T GetRef<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i);
bool  TryGet<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i, out T v);
bool  IsValid<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i);
void  MarkInvalid<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int i);

int     GetCount<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h);
void    SetCount<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int count);   // [0, Length]
Span<T> GetSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h);                // [0, Count)
Span<T> GetFullSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h);           // [0, Length)
void    MarkValidRange<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int start, int count);
void    MarkInvalidRange<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h, int start, int count);
```

生产-消费：`GetFullSpan` 写入 → `SetCount(n)` → 下游 `GetSpan` 得 `[0, n)`。

所有 `Span` 取值（`GetSpan` / `GetFullSpan` 及 §6.3 重载）按 `sizeof(T)` 为步长，运行期强制 `sizeof(T)` 恰等于该 bank 槽能级字节，否则抛；欠尺寸类型（`sizeof(T) <` 能级）只能逐元素 `Get` / `Set`。

### 6.3 批量命中扫描（主访问模式）

固定向量 + 逐元素命中的热路径，紧凑循环内零方法调用。重载一次取出整段 data / version / 当前 version：

```csharp
void GetFullSpan<T>(CacheLayout m, CacheMemory d, VectorHandle<T> h,
                    out Span<T> values, out Span<byte> versions, out byte current);
```

```csharp
acc.GetFullSpan(hits, out var vals, out var ver, out var cur);
foreach (int idx in targets)
{
    if (ver[idx] == cur) continue;     // 命中跳过
    vals[idx] = Compute(idx);
    ver[idx]  = cur;                   // 写 version 即标记命中
}
```

```text
span 不得跨 Invalidate / CopyFrom 使用
标量无此重载
```

---

## 7. Version 失效

命中即逐槽 version 比对，免每帧清数组。version 取 `byte`：连续扫描下体积最省、缓存行与预取最优。

```text
命中 ：Versions[i] == CurrentVersion
写入 ：写 Data[i]；Versions[i] = CurrentVersion
失效 ：Invalidate() 令每 active bank CurrentVersion += 1（O(bank 数)，不触 Data / Count）
```

初值扰动 `CurrentVersion_init = (bankSeedIndex * 37) % 255 + 1`（bankSeedIndex 随 bank 创建递增）；达 255 回绕时 `Array.Clear` 该 bank version、重置为 1。扰动错开各 bank 回绕点，避免一次 `Invalidate` 多 bank 集体 `Array.Clear` 的尖峰；仅改初值，不改命中语义。初值恒 ≥ 1，故新建或扩容出的零填充槽天然 miss。

---

## 8. 内部布局

```text
ScalarBank：index = offset
VectorBank：index = offset + element     // 连续段，无二维寻址、无运行时扩容
Count     ：与 Data 分离，按段首 offset 存
scalar 即 length = 1 的段，与 vector 同构
```

---

## 9. 运行环境

```text
目标 netstandard2.1；LangVersion 9 锁定，引擎与无头两侧一致，禁用 C# 10+ 语法以免破坏任一侧编译
无 UnityEngine 引用，允许 unsafe，不依赖 Burst / Jobs
Unity 经 noEngineReferences:true 的 asmdef 引用
```

可选 `Diagnostics`（活跃 bank 数、分配字节、有效槽数、回绕次数）判断缓存是否值回本；额外运行期校验由 `COMPUTE_CACHE_DEBUG` 控制（含 §5 换装护栏），release 不付费。

---

## 10. 边界

| 适合 | 不适合 |
|---|---|
| 高频中间值缓存、规则结果缓存 | 少量配置查询（用 Dictionary） |
| 批量目标 + 逐元素命中扫描 | 引用对象缓存（用外置表 + id） |
| 生产-宣告-消费的动态数据 | 运行期增长的列表（长度固定） |
| 分阶段增量布局（Extend） | 多线程共享同一 CacheMemory |
| DSL / 代码生成顺序登记（顺序路径） | 运行时 vector 扩容 |
| rollback / replay、多 layout 复用同一内存条池 | 跨机器状态序列化（version 本地、槽为本机布局字节） |

---

## 11. 实现与交付

### 11.1 产物与约束

**纯 C#：核心库与测试同处一地、同一编译单元**（无 `UnityEngine` / NUnit 依赖，计时用 `System.Diagnostics.Stopwatch`）。测试为带细节的自检代码，可被无头工程与引擎两侧同样编译运行。薄调用入口独立于库。

### 11.2 核心清单

```text
CacheBuilder（命名 Declare + 顺序 SequenceDeclare / SequenceFetch，统一 Build，Extend）
CacheLayout（不可变：Lookup / GetLength / Hash / AllocateMemory）
CacheMemory（通用内存条：16 bank 高水位 Array.Resize 增长；EnsureCapacity / Invalidate / Clone / CopyFrom，跳空快照）
Accessor（双形态：静态函数 + 绑定 (layout, memory) 结构体；标量 / 向量单点 / Count / Span / 批量命中重载 / Mark）
Handle<T> / VectorHandle<T> / HandlePacking / 稳定 LayoutHash / 8 字节对齐 unmanaged struct slot
后置：Diagnostics、源生成器、Native/Burst 后端
```

### 11.3 功能测试

`static TestReport RunAll()`：每项自检返回 `(name, pass, message)`，汇总 `PassCount / FailCount / AllPassed` 与可打印摘要。覆盖：

```text
命名 / 顺序双路径声明取回；两路径混用各自计数、共占同一 bank/offset
冲突即抛 / 完全一致幂等；length ≤ 0、sizeof > 256、Build 后声明、Build 前 fetch、越界 fetch 全拒绝
Handle 编码：Packed == 0 无效；bank / offset 解码
类型不符 Lookup 抛异常；TryLookup 返回 false
标量与向量读写、GetRef（取即命中）、TryGet
向量单点 Get 取槽内现值（不查 version）；欠尺寸 T 的 Span 取值即抛
version：写入命中、Invalidate 整批失效、回绕后仍正确
Count vs Length：GetSpan = [0,Count)、GetFullSpan = [0,Length)、Invalidate 不动 Count
批量命中扫描 GetFullSpan 重载：二次扫描全命中跳过
Extend：旧 handle 字节级不变、新槽可用、hash 改变
AllocateMemory 多份相互独立
EnsureCapacity 高水位增长非破坏（旧活值留存、新区冷）；换 layout 复用 + Invalidate 后无假命中
Clone 独立、CopyFrom 全等恢复、容量不足拒绝、跳过未分配 tier
Accessor 两形态结果一致（静态 ≡ 结构体）
```

### 11.4 性能基准

`static BenchReport RunAll(int targets, int registers, int iterations)`：同一意图下对比「常规写死数组 / 字段读写」与「ComputeCache 读写」。构建（Declare / Build / AllocateMemory / Accessor 绑定）在计时区外；`Stopwatch` 预热后取多次最优；累加校验和防 DCE。

```text
标量单点    ：寄存器逐个写后读                         → 反映单点分发开销
向量单点    ：逐元素 Set / Get
批量命中(全 miss，最坏)：每帧 Invalidate + GetFullSpan 扫描回填
批量命中(全 hit，纯扫描)：预填后逐帧只读
输出每场景 baseline ms / cache ms / 比值；批量场景比值应趋近 1（近裸数组）
```

### 11.5 薄调用入口

仅调用 `RunAll()` 与基准 `RunAll(...)` 并打印，无任何逻辑。二选一（由调用位置决定）：

```text
Unity 编辑器 MonoBehaviour：ContextMenu / Start → Debug.Log 报告
无头 Main                 ：Console.WriteLine 报告，失败返回非零退出码
```

### 11.6 启动契约

拿到本文档，仅需指明两处位置即可推进：

```text
core + test 位置 : <纯 C# 目录，库与测试同处，可被无头工程直接编译>
测试调用位置     : <薄入口：Unity MonoBehaviour 或 headless Main>
```
