# Majo Runtime 实施计划

状态：已确认  
目标插件：`Majo Runtime`  
安装目录：`addons/majo_runtime`  
C# 命名空间：`Majo.Runtime`

## 1. 项目目标

构建一个可复用的 Godot C# 视觉小说 Runtime 插件。插件使用 Yarn Spinner 负责编译、变量、分支、台词和选项执行，并参考 Naninovel Runtime 的服务化架构、Actor 模型、异步 Operation 语义和模块边界。

当前阶段优先证明：

1. Runtime 服务可以被显式组合、可靠初始化、重置和销毁。
2. Yarn Command 可以通过稳定的适配层驱动 Actor，而不让 Core 依赖 Yarn Spinner。
3. 动画可以按 Naninovel 语义执行：默认非等待、显式等待、并发汇合、取消和强制完成。
4. 以上行为可以通过严格的纯 C# 单元测试和 Godot headless 测试自动验证。

Runtime 将支持完整的通用视觉小说能力；类似《逆转裁判》的证词、证据、推理和辩证
模式永久由游戏项目或独立玩法插件实现，不进入 Runtime。

## 2. 已确认的技术基线

- Godot 4.7 C#。
- .NET 8。
- YarnSpinner-Godot 0.3.22。
- Yarn Spinner 3.2.0。
- Yarn Spinner 是独立前置依赖，不复制或修改其插件源码。
- Majo Runtime 启动时检查 Yarn 依赖及兼容版本，并在不兼容时报告明确错误。
- 首版公共 API 只保证 C#；场景入口使用 Godot Node、Signal 和导出属性，为未来 GDScript 包装保留空间。
- 配置入口使用 `MajoProjectConfiguration : Resource`，再引用各模块的子配置 Resource。
- 服务采用显式注册和构造函数依赖注入，不使用反射扫描自动发现。
- Yarn Command 保留一个主位置参数，其余使用 Naninovel 风格命名参数；参数模型通过
  Attribute 自描述，由通用 Binder 完成类型转换和验证。

## 3. 范围边界

### 第一阶段核心范围（M0-M5，已完成）

- Runtime 组合根和服务生命周期。
- 配置加载与验证。
- Operation 调度、等待、分组、取消和强制完成。
- 最小 Character、Background Actor 及 ActorManager。
- Yarn DialogueRunner 适配和核心 Command。
- 最小 Yarn 台词与选项闭环。
- 单元测试、Godot headless 测试和可运行示例。

### 第二阶段范围（M6-M10）

- 对话项目加载、播放、停止和会话状态。
- 键盘、鼠标和手柄输入，以及模态输入所有权。
- 可直接使用且可整体替换的视觉小说 Presenter。
- BGM、SFX、Voice 和 Voice Presenter。
- Yarn 无关的 Script 资源身份、播放位置、安全恢复边界和版本指纹。
- 可扩展的 State 快照、服务状态贡献和内存恢复事务。
- Auto 阅读与当前 Session 的 History。

### M6-M10 不包含

- 用户存档/读档产品能力：磁盘格式、槽位、缩略图、Quick Save、
  Auto Save 和 Save/Load UI。
- Rollback、time travel 和快照栈。
- Skip、快进和独立已读判定。
- 镜头、复杂转场、屏幕特效和设置 UI。
- Live2D、Spine、分层立绘、视频背景。
- 多 Yarn 脚本轨道、后台脚本循环以及 Naninovel `sync` 轨道接管。
- 运行时语言切换和完整本地化工具链。
- Command Source Generator。

证词、证据、推理和辩证玩法永久位于 Majo Runtime 之外，由游戏项目或独立玩法
插件通过 Runtime 的公开扩展接口实现。

## 4. 参考 Naninovel 后保留与替换的部分

### 保留的设计思想

- Engine/Runtime 作为组合根。
- 服务接口与明确生命周期。
- 构造函数表达服务依赖。
- 配置按模块拆分。
- Actor 与 ActorManager 分离。
- 异步命令支持非等待、等待、取消和强制完成。
- 上层 Command 依赖服务接口，而不是直接查找场景 Node。

### 由 Yarn Spinner 替换的部分

- Naninovel Script 语法、编译器和表达式求值器。
- Naninovel Command playlist 和单轨 VM 执行器。
- 分支、变量表达式、台词、选项和 node 跳转执行。
- Naninovel Command 参数编译体系。

### 仍由 Majo Runtime 负责的 Script / State 能力

- 脚本项目和 node 的稳定身份、枚举、加载、校验和版本指纹。
- 对话会话、播放位置、安全暂停点、恢复语义和播放事件。
- 系统变量、Actor、Audio 及其他 Runtime Service 的状态捕获与恢复协调。
- Session、Game、Global 和 Settings 状态作用域的边界；本阶段只实现
  Session 和内存 Game Snapshot，不实现槽位持久化。

Majo Runtime 不复制 Yarn 已经提供的脚本语言和 VM，但不再把 Naninovel
`ScriptManager`/`ScriptPlayer` 的全部职责等同于 VM 而一并丢弃。Yarn 细节仍隔离在
`Dialogue.Yarn` 和独立 Bridge 程序集内。

## 5. 模块和依赖方向

```text
Majo.Runtime.Core
├── Configuration
├── Resources
├── Operations
├── Actors
├── Input
├── Audio
├── Dialogue
├── Script
├── State
└── Dialogue.Yarn / Yarn VM Bridge

Tests
├── Unit -> Core / Operations / Actors / Commands / Script / State
└── Headless -> 完整 Godot + Yarn + Majo Runtime
```

依赖规则：

- `Core` 不依赖 Godot 场景对象和 Yarn Spinner。
- `Operations` 不依赖 Yarn Spinner。
- `Actors` 可以依赖 Godot，但不依赖 Yarn Spinner。
- `Script` 公共契约不依赖 Yarn；`Dialogue.Yarn` 和 Yarn VM Bridge 是唯一可以
  直接依赖 YarnSpinner-Godot/YarnSpinner 的 Majo 集成边界。
- `State` 只依赖稳定的 State 契约和明确注册的 Contributor，不识别 Yarn、
  Godot Node 或具体 Actor/Audio 实现。
- Script 播放快照是 State 的一个 Contributor，不反向让 Script 依赖 StateManager。
- Command 只能通过服务接口访问 Actor 和 Operation。
- 不允许修改 `addons/YarnSpinner-Godot` 来实现 Majo 功能。

建议目录：

```text
game/addons/majo_runtime/
├── plugin.cfg
├── Runtime/
│   ├── Core/
│   ├── Configuration/
│   ├── Operations/
│   ├── Actors/
│   ├── Commands/
│   ├── Dialogue/Yarn/
│   ├── Script/
│   └── State/
├── Editor/
└── Samples/

game/tests/
├── Unit/
└── Headless/

bridges/
└── YarnSpinnerStateBridge/
```

## 6. Runtime 内核契约

### 6.1 服务生命周期

服务至少具有以下生命周期：

```csharp
public interface IRuntimeService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task ResetAsync(CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}
```

最终名称可在 M1 内统一，但语义不可弱化：

- 初始化按依赖拓扑顺序执行。
- Reset 保留服务实例，只清除游戏会话状态。
- Shutdown 逆初始化顺序执行。
- 初始化中途失败时，已经成功初始化的服务必须逆序清理。
- 重复初始化、重复关闭、缺失依赖、循环依赖和重复注册必须得到确定结果或明确异常。
- Runtime Host 持有根 `CancellationTokenSource`，关闭时取消所有子操作。

### 6.2 显式注册

注册风格：

```csharp
services.AddSingleton<IFrameClock>(resolver => new GodotFrameClock(...));
services.AddSingleton<IOperationScheduler>(resolver =>
    new OperationScheduler(resolver.Get<IFrameClock>()));
```

要求：

- 注册时声明接口、实现工厂和生命周期。
- 构建阶段验证服务图。
- 解析失败信息必须包含请求服务和依赖链。
- 禁止隐藏式全场景搜索作为服务解析机制。
- 允许游戏或第三方插件在 Runtime 启动前追加或替换注册。

### 6.3 配置

`MajoProjectConfiguration` 是唯一项目入口，引用：

- `RuntimeConfiguration`
- `OperationConfiguration`
- `ActorConfiguration`
- `DialogueConfiguration`
- `ResourceConfiguration`
- `InputConfiguration`
- `DialoguePresentationConfiguration`
- `AudioConfiguration`
- `ScriptConfiguration`
- `StateConfiguration`

配置要求：

- Godot Inspector 可编辑。
- 启动前集中验证必填引用、重复 Actor ID、无效资源路径和版本要求。
- 测试可以直接构造配置，不要求读取磁盘 Resource。

## 7. 异步 Operation 标准

### 7.1 对外与对内边界

- Yarn Command 返回 `Task`，使 DialogueRunner 能在需要时等待。
- Presenter 按 YarnSpinner-Godot 接口使用 `YarnTask`。
- Runtime 内部不暴露裸后台 Task，而通过 `IOperationHandle` 管理。
- 帧等待和动画时间通过 `IFrameClock`/Godot Tween，不使用 `Task.Delay` 猜测游戏帧时序。

### 7.2 Operation 契约

```csharp
public interface IOperationHandle
{
    string? Id { get; }
    OperationState State { get; }
    Task Completion { get; }

    void Cancel();
    void Complete();
}
```

建议状态：

```text
Created -> Running -> Completed
                  -> Completing -> Completed
                  -> Cancelling -> Cancelled
                  -> Faulted
```

语义：

- Cancel：终止操作并执行清理，不保证到达目标状态。
- Complete：立即应用目标最终状态，然后正常完成。
- Fault：异常必须通过 `Completion` 传播并由调度器记录。
- Runtime Reset/Shutdown：取消所有仍在运行的 Operation，并等待清理结束。
- 活跃命名 Operation ID 重复时拒绝启动并报告错误。
- 已结束 Operation 必须从活跃注册表移除。

### 7.3 Naninovel 异步行为

- 所有可耗时 Command 默认 `wait=false`。
- `wait=true` 时 Yarn 等待该 Operation 的 `Completion`。
- 不等待的 Operation 由 Scheduler 持有，不能成为无人观察的 Task。
- 多个 Operation 可以绑定同一个组 ID，`await` 等待组内全部操作。
- `await` 可选择普通等待或先 `Complete()` 再等待。
- `cancel` 取消命名 Operation 或组。
- `sync` 名称保留给未来脚本轨道同步，不用于普通动画等待。

当前阶段不声称覆盖 Naninovel 的独立 ScriptTrack、后台脚本循环、轨道接管或异步轨道序列化。

## 8. 最小 Actor 模型

### 8.1 通用 Actor

最小接口负责：

- 稳定唯一 ID。
- Appearance。
- Visible。
- Position。
- Tint/Opacity。
- 立即设置状态。
- 通过 Operation 改变上述状态。

每个可动画属性只允许一个当前写入者。发生同属性冲突时，遵循 Naninovel 风格：旧动画先完成到目标状态，再启动新动画，避免状态落在不可预测的中间值。

### 8.2 Character

首版只支持：

- 普通 2D Texture。
- Appearance 到 Texture 的映射。
- `left`、`center`、`right` 三个预设位置。
- 显示、隐藏、位置和透明度 Tween。

### 8.3 Background

首版只支持：

- 单个全屏 2D Texture 背景。
- Appearance 切换。
- 简单淡入、淡出或交叉淡化。

### 8.4 ActorManager

负责：

- 按 ID 创建、查询、枚举和移除 Actor。
- 使用配置定义解析资源。
- 保证 Actor ID 唯一。
- 管理 Character 和 Background 的场景宿主节点。
- Reset/Shutdown 时结束动画并清理 Actor。
- 允许第三方注册新的 Actor 类型，而不修改 Core。

## 9. Yarn 适配和首批 Command

Command 通过 `DialogueRunner.AddCommandHandler` 显式注册，不依赖 `[YarnCommand]` 自动扫描。

首批命令语义：

- `back`：设置背景 Appearance 和淡化时长。
- `char`：创建或修改 Character 的 Appearance、位置、可见性和动画时长。
- `hide`：隐藏指定 Actor。
- `await`：等待命名 Operation/组，或开始一个收集 Operation 的等待块；可选择强制完成。
- `endAwait`：结束等待块，并等待块内追踪的 Operation。
- `cancel`：取消命名 Operation/组。

保留 Yarn 内建命令：

- `wait`：计时等待。
- `stop`：结束 Yarn 对话。由于它是 Yarn VM 特殊命令，不能复用为取消命名任务。

Actor ID 保留为主位置参数，其余参数使用不区分大小写的 `name:value` 形式，避免较长
Command 依赖参数顺序。Command 名的规范拼写使用 camelCase。示意：

```yarn
<<await>>
<<back backdrop appearance:courtroom duration:0.5 operation:back-fade>>
<<char Maya appearance:happy position:left duration:0.5 operation:maya-left>>
<<endAwait>>

Maya: 准备完成了。

<<char Maya appearance:surprised position:center duration:0.35 wait:true operation:maya-center>>
Maya: 等一下！
```

命名目标仍可独立汇合，例如 `<<await group:setup>>`；使用
`<<await operation:maya-center complete:true>>` 会先强制完成目标再继续。

## 10. Milestone 计划

### M0：插件骨架和测试跑道（已完成）

目标：建立可持续实现和验证的最小工程结构。

交付：

- `addons/majo_runtime` 插件骨架。
- Runtime 模块目录和命名空间。
- Yarn 依赖版本检查。
- 独立纯 C# 单元测试项目。
- Godot headless 测试入口和统一测试脚本。
- 测试超时、日志和退出码约定。

验收：

- 插件能在空测试场景初始化和关闭。
- `dotnet build`、`dotnet test` 和 Godot headless 命令全部可重复运行。
- 故意失败的 headless 断言返回非零退出码。
- 不产生新增编译警告；现有 Yarn 源生成器警告使用明确白名单处理。

实施记录（2026-08-21）：

- 已建立 `Majo.Runtime` 插件组合根、Core 元数据和隔离在 `Dialogue/Yarn` 下的 Yarn 依赖检查。
- 已建立 xUnit 单元测试项目，以及覆盖真实 Godot 场景树初始化/关闭的 headless 测试。
- `tools/test.sh` 统一执行非增量构建、单元测试、Godot headless 导入、集成测试和故意失败探针，并为各阶段设置确定超时。
- 门禁通过：6 个单元测试全部通过，headless 生命周期测试通过，故意失败探针以非零状态退出；构建仅保留已明确白名单的 Yarn `SYSLIB1220` 警告。

### M1：Runtime 服务内核（已完成）

目标：实现不依赖 Yarn 的稳定组合根。

交付：

- Service Registry、Resolver、Runtime Host。
- 显式注册和构造函数注入。
- 依赖图验证与初始化顺序。
- Initialize、Reset、Shutdown。
- 根 CancellationToken、日志和错误类型。
- `MajoProjectConfiguration` 及基础配置验证。

单元测试：

- 正常依赖顺序和逆序关闭。
- 缺失依赖、重复注册和循环依赖。
- 初始化异常后的逆序清理。
- 重复调用生命周期方法。
- Reset 和 Shutdown 的取消传播。
- 第三方服务追加与替换。

Headless 验收：

- Runtime Node 进入场景树时初始化服务。
- 场景退出时所有服务只关闭一次。
- 失败配置给出可定位错误且不残留 Node。

实施记录（2026-08-21）：

- 已实现显式 Singleton 注册、替换、Resolver、依赖声明，以及缺失依赖和循环依赖的构建期验证。
- Runtime Host 按稳定拓扑顺序初始化和 Reset，按逆序 Shutdown；初始化失败和关闭失败均继续逆序清理并聚合带服务类型的错误。
- Host 持有 Runtime/Session 两级取消生命周期，并通过内建 `IRuntimeLifetime` 支持构造工厂显式注入；Reset 替换 Session，Shutdown 取消两级 Token。
- 已建立 Inspector 可编辑的 `MajoProjectConfiguration` 和四类子配置，集中验证必填引用、数值、Actor ID、资源路径及 Yarn 版本要求。
- 门禁通过：16 个纯 C# 单元测试全部通过；Godot headless 覆盖有效启动、Reset、节点退出单次 Shutdown、配置诊断和失败节点清理。
- 2026-08-22 加固：生命周期等待采用硬 deadline；超时后取消并持续观察底层 Task；场景树退出时 `RuntimeShutdown` 恰好发出一次，完成状态由 `ShutdownCompletion` 表达。

### M2：Operation 调度内核（已完成）

目标：覆盖核心 Naninovel 异步命令语义。

交付：

- `IOperationHandle`、OperationContext、OperationScheduler。
- Cancel 与 Complete 双控制通道。
- 命名、分组、等待、取消和强制完成。
- `IFrameClock` 及测试用可控时钟。
- Reset/Shutdown 清理。

单元测试：

- 默认非等待不会阻塞调用方。
- 显式等待阻塞到完成。
- 多 Operation 并发与组汇合。
- Cancel 和 Complete 产生不同最终状态。
- 异常传播、重复 ID、已完成项移除。
- Reset 后没有运行项和无人观察异常。
- 所有等待测试都有确定超时。

Headless 验收：

- 真实 Godot 帧推进 Operation。
- Tween 完成、取消和强制完成后的属性正确。
- Runtime 退出期间启动的 Operation 被可靠清理。

实施记录（2026-08-21）：

- 已实现 `OperationDefinition`、`OperationContext`、`IOperationHandle` 状态机和 `IOperationScheduler`，`Start` 默认立即返回，由 `Completion` 提供显式等待边界。
- 已实现类型化 ID/Group 目标、并发汇合、Cancel/Complete 双控制通道、活跃 ID 冲突检查，以及终态发布前的同步索引移除。
- 后台 Operation 的取消和异常始终由 Scheduler 观察；Reset/Shutdown 停止接收新工作、取消活跃项并等待清理，新 Session 可继续调度。
- Bootstrap 默认注册 Godot `IFrameClock` 和 Scheduler；`GodotTweenOperation` 支持自然完成、保留中间状态的取消，以及立即应用最终属性的强制完成。
- Runtime Host 保留调用方同步上下文，Godot 服务生命周期和 Tween 回调不会因 `ConfigureAwait(false)` 漂移到线程池。
- 门禁通过：24 个纯 C# 单元测试全部通过；Godot headless 覆盖真实帧、Tween 三种终态，以及 Runtime 退出前和退出过程中创建的 Operation 清理。

### M3：最小 Actor 系统（已完成）

目标：用最简单的角色和背景验证 Actor 抽象。

交付：

- Actor、Character、Background 接口和实现。
- ActorManager。
- Actor/Appearance 配置 Resource。
- left/center/right 位置预设。
- Texture 加载、显示、隐藏、移动和淡化。
- 属性动画冲突策略。

单元测试：

- Actor 增删查和唯一 ID。
- 无效定义、Appearance 和资源路径错误。
- 立即状态与动画目标状态一致。
- 相同属性动画冲突时旧操作先完成。
- Reset 后 Actor 和 Operation 状态干净。
- 自定义 Actor 工厂注册。

Headless 验收：

- TextureRect/Sprite 节点被创建到正确层级。
- 位置和透明度 Tween 的中间、最终状态正确。
- 背景交叉淡化完成后旧资源被释放。
- 场景关闭后无残留 Actor Node。

实施记录（2026-08-21）：

- 已实现 `IActor`、Character、Background、`IActorFactory` 和纯管理层 `ActorManager`，支持定义驱动的增删查、唯一 ID 与启动前工厂验证。
- Actor 配置已扩展为 Factory、默认 Appearance、多 Texture 映射和 left/center/right 位置预设；集中验证重复 ID、缺失默认项、非法路径与非有限位置。
- Character 使用 `Sprite2D`，Background 使用全屏 `TextureRect`；Bootstrap 默认建立 Background/Character 分层并注册 `IActorManager`。
- 位置、透明度、显示和隐藏由 Operation Scheduler 驱动；同一属性冲突时前一 Operation 先强制完成并到达目标，新动画随后开始。
- Background 交叉淡化并发驱动新旧 TextureRect，完成后释放旧视图；取消时恢复旧视图且不应用目标 Appearance。
- 门禁通过：30 个纯 C# 单元测试全部通过；Godot headless 覆盖纹理加载、节点层级、立即状态、Tween 中间/最终值、冲突顺序、交叉淡化释放、Reset 和场景关闭清理。

### M4：Yarn Command 适配（已完成）

目标：让 Yarn 通过显式 Command Adapter 驱动 Runtime。

交付：

- DialogueRunner Adapter。
- `back`、`char`、`hide`、`await`、`cancel`。
- 自描述的类型化命名参数和错误诊断。
- 默认非等待及 `wait=true`。
- Command 到 Operation ID/组的绑定。

单元测试：

- 参数转换和缺失参数错误。
- 每条命令只依赖服务接口。
- `wait=false` 返回时 Operation 仍被调度器持有。
- `wait=true` 返回 Task 在 Operation 完成前不结束。
- `await`、`cancel`、`complete` 路径。
- Yarn 内建 `wait`、`stop` 不被覆盖。

Headless 验收：

- Yarn 确实在等待型 Command 上暂停。
- 两个非等待动画实际重叠执行。
- `await` 后下一条台词只在全部目标动画完成后出现。
- `cancel` 与强制完成后的 Actor 最终状态不同且符合定义。
- 未处理 Command 和错误参数产生清晰失败信息。

实施记录（2026-08-21）：

- Command 参数模型、通用 Binder、执行 Dispatcher、Catalog 和 Yarn Adapter 已分层；Adapter 不认识任何具体 Command。
- 已冻结 `back`、`char`、`hide`、`await`、`endAwait`、`cancel` 的一主参数加命名参数语法；动画默认 `wait:false`。
- Adapter 随 Runtime 初始化遍历 Catalog 注册全部命令、关闭时逆序卸载，不注册 Yarn 内建 `wait` 和 VM 特殊命令 `stop`；参数错误包含具体值及完整 usage。
- Command 产生的后台动画均由 Operation Scheduler 持有；`wait=true` 直接返回 Operation Completion，`await complete` 使用强制完成通道，`cancel` 使用取消通道。
- 门禁通过：44 个纯 C# 单元测试全部通过；真实 Yarn VM headless 覆盖非等待动画重叠、组汇合、等待型命令暂停、取消/强制完成差异、错误参数与未处理命令诊断。

### M4.5：核心架构与时序加固（已完成）

目标：在 M5 冻结首阶段公共扩展 API 前，修复审查发现的时序窗口并补齐插件扩展边界。

交付：

- Operation ID/Group 在当前 Session 内保留终态记录，已结束 Operation 仍可被精确等待，Reset 后清除记录。
- Visibility/Opacity 使用同一属性顺序通道，`Show` 可以可靠接管正在执行的 `Hide`。
- `AnimationSpec` 统一时长、缓动、时间模式和同属性冲突策略；旧 duration 重载保持兼容。
- Command 获得对话级取消上下文；Yarn Stop 取消该对话拥有的 Operation，正常取消不记为命令失败。
- 公开 `ConfigureCommands`/`RuntimeCommandRegistry`，自定义 Command 不修改 Core 即可接入 Actor 和 Operation。
- Command 失败通过 C# event 和 Godot Signal 报告，并终止当前 Yarn 对话。
- Actor 定义支持 Factory 自有 payload/Resource 和工厂级验证；Texture 配置继续兼容。
- ActorManager 支持异步创建和同 ID 并发创建去重，Reset/Shutdown 取消 pending creation。
- Actor 明确区分当前画面状态和 Target 状态；Godot Node 暴露移动到独立 capability。
- 初始化失败服务参与补偿关闭；失败 Shutdown 保留未清理服务并允许重试。
- 统一门禁增加限定 Majo 源码的格式/静态检查，并连续执行三次真实 headless 时序测试。

验收：

- 零时长/已结束 Operation 可以 late-await，未知目标仍返回明确错误。
- `Hide -> Show` 最终为可见且完全不透明。
- Yarn Stop 后没有对话级活跃 Operation，也不会展示后续台词。
- 示例自定义 Command 通过公开 Registry 在真实 Yarn VM 中执行。
- Shutdown 等待超时后可以再次调用并完成剩余清理。
- 53 个纯 C# 单元测试、Godot/Yarn headless 连续三轮和失败探针全部通过。

### M5：最小可玩闭环与扩展证明（已完成）

目标：完成第一阶段验收场景，并证明架构可扩展。

示例流程：

```text
背景和角色并行动画
    -> await 汇合
    -> wait=true 单动画
    -> 台词
    -> 选项
    -> Yarn 分支
    -> 正常结束并清理
```

交付：

- 最小演示 Yarn 剧本和测试资源。
- 可见运行场景。
- 无图形界面的测试 Presenter/事件探针。
- 示例自定义 Service。
- 示例自定义 Actor 或 Actor 工厂。
- 示例自定义 Command。
- 插件安装、配置、运行和测试文档。

最终验收：

- 演示场景在本机正常运行。
- 同一闭环在 headless 下自动验证时序和状态。
- 新增示例扩展不修改 Core 源码。
- 所有单元和 headless 测试连续运行多次无偶发失败。
- Runtime 结束后没有活跃 Operation、残留测试 Node 或未观察异常。

实施记录（2026-08-22）：

- 新增 `demo/M5Demo.tscn` 可见主场景及可点击台词/选项 Presenter；项目默认启动场景已指向该演示。
- 新增独立的 M5 Yarn 项目，单一剧本覆盖背景和角色并行动画、组汇合、等待型动画、台词、选项、分支与正常结束；资源层完成后迁至 `assets/scripts/main.yarnproject`。
- 所有扩展均位于插件目录外的 `Majo.Demo`：`M5CaseService`、`M5PortraitActorFactory` 和 `demo_route` Command 未修改 Runtime Core。
- `M5DialogueProbe` 记录对话、Node、Line 和 Option 事件，并能确定性选择分支；真实 Yarn VM headless 同时断言 Actor 最终状态和事件顺序。
- 关闭验收会检查自定义 Service 接收到 Runtime cancellation、Actor Node 被释放、Operation 为零以及 Runtime/Runner/外部 Host 无残留。
- 主场景支持 `--m5-auto`，严格门禁会额外运行一次主场景装配 smoke test，再连续运行三轮完整 headless suite 与失败探针。
- 56 个纯 C# 单元测试全部通过；M5 Service 的生命周期、状态重置和输入验证具有独立单测。
- 新增插件安装、配置、运行、扩展和测试快速指南；M0-M5 首阶段闭环已形成可复用插件基线。

### M5.1：约定式 Runtime 资源层（已完成）

目标：消除 Demo 与 Actor 定义中的物理资源硬编码，并为后续音频、UI、状态模块
提供统一的可扩展资源边界。

约定：

```text
Root = res://assets
Character = characters/<actor-id>/<appearance>
Background = backgrounds/<actor-id>/<appearance>
Scripts = scripts/<project-id>
```

交付与验收：

- `IResourceManager` 作为标准 Runtime Service 参与 Initialize、Reset、Shutdown。
- `ResourceKey` 与 scoped typed loader 隔离逻辑 ID 和 Godot 物理路径。
- provider registry 支持稳定优先级、重复检测与项目外扩展。
- Godot provider 解析导入源扩展和原生资源扩展；Yarn 项目与纹理均使用无扩展名 ID。
- 同一资源的并发异步加载去重，成功结果缓存，Reset/Shutdown 清空缓存。
- Actor appearance 映射降级为可选严格 override；无映射时按约定解析。
- M5 的 Actor、背景和 Yarn 资源迁入 `res://assets`，Demo 配置不再含资源路径。
- 62 个纯 C# 单元测试覆盖资源键、优先级、override、缓存、并发和类型失败；
  Godot smoke/headless 覆盖真实 SVG 与 Yarn 导入资源。

### M5.2：可扩展 Command Catalog（已完成）

目标：消除中央 Parser、Adapter 分支和全局 Command Service 随命令数量线性膨胀的问题。

交付与验收：

- `RuntimeCommand`/`CommandParameter` Attribute 让每个参数模型自描述名称、主参数、
  必填项、默认值、范围和 Usage。
- `CommandBinder` 通用处理命名参数、类型转换、默认动画时长、未知/重复参数、枚举和
  参数互斥集合；自定义类型通过 Converter Registry 扩展。
- `RuntimeCommandCatalog` 是 Majo Command 名称的唯一真源，统一检测 Yarn 保留名、
  大小写冲突、重复注册和初始化后注册。
- `IRuntimeCommandModule` 支持按 Actor、Playback 及未来 Audio/Camera/UI 功能域组织并
  原子注册一组 Command，模块失败不会留下部分注册。
- `RuntimeCommandDispatcher` 统一执行绑定、异常归一、对话取消和 await scope 上下文；
  Yarn Adapter 只遍历 Catalog 注册和逆序卸载，不包含具体 Command 分支。
- 内建六条 Command、Demo 和 headless 自定义 Command 使用同一类型化注册路径。
- Actor 执行收敛到 `IActorCommandService`，等待和取消 Handler 直接依赖 Operation
  capability，不再使用随所有 Command 增长的全局 `IRuntimeCommandService`。
- 运行时元数据使用显式注册和一次性缓存，不做程序集扫描；待 API 稳定后再以独立
  milestone 引入 Source Generator，替换反射绑定并生成注册模块和编译期诊断。

## 11. 后续 Roadmap

M6-M10 构成第二阶段 Runtime Roadmap；M9.5 和 M9.6 是在 M10 开始前必须
通过的架构门。M10 完成后停止继续扩张范围，重新审查是否需要磁盘
存档、Rollback、镜头或其他独立阶段；推理玩法不进入 Runtime Roadmap。

### 11.1 Naninovel State / Script 职责复盘

先前计划把 `ScriptManager`、`ScriptLoader` 和主 `ScriptPlayer` 整体归类为“由
Yarn Spinner 替换”。对照 Naninovel 源码后，这个结论过度合并了脚本语言、
资源管理、播放协调和状态恢复四类不同职责。

| Naninovel 能力 | 真实职责 | Majo 处理 |
| --- | --- | --- |
| Script 编译/表达式/Command playlist | 将 NaniScript 转为可执行内容 | 已由 Yarn compiler 和 VM 替换，不复制 |
| `ScriptManager` | 管理脚本资源路径、加载器、临时脚本和 endpoint | M5.1/M6 只完成约定加载；M9.5 补身份、目录和版本指纹 |
| `ScriptLoader` | 按轨道预加载/释放脚本引用的资源 | 当前由 ResourceManager 和 Command 按需加载；保守/乐观/懒加载策略延后 |
| `ScriptPlayer` / main track | 管理播放位置、执行事件、输入等待、Auto/Skip 和状态快照 | M6 已实现会话外观；M9.5 抽出唯一播放真相和恢复契约，M10 再实现 Auto |
| 额外 `ScriptTrack` | 并行脚本、循环和轨道独立序列化 | 不等同于 Operation 组；本阶段仍延后 |
| Played register | 跨周目记录已执行位置，服务于 read-only Skip | 延后到 Skip/Global State 阶段，不与 Session History 混合 |
| `StateMap` + `IStatefulService` | 让服务自主贡献 Game/Global/Settings 状态 | M9.6 实现显式 Contributor Registry 和稳定 key/schema，不使用程序集类名作存档 key |
| `StateManager` | 在安全播放边界协调完成操作、重置、捕获和有序恢复 | M9.6 先做内存快照与恢复事务，为未来存档和 Rollback 复用 |
| SlotManager / QuickSave / AutoSave / thumbnail | 状态快照的持久化产品层 | 延后，不阻塞 M10 |
| Rollback stack / recovery rollback | 密集快照、回退和脚本变更恢复 | 延后；M9.5 只提供版本不匹配的明确拒绝和诊断 |

前置顺序确认为：

```text
M5.1 Resources + M6 Dialogue Session
    -> M9.5 Script Runtime Foundation
    -> M9.6 State Snapshot Foundation
    -> M10 Auto + Session History
```

M10 并非因“保存历史记录”而需要磁盘存档，而是需要先统一播放阶段、
安全边界和 Session 状态所有权。否则 Auto、History、`DialoguePlayer` 和未来 Save/Load
会各自维护一套 current line / waiting / resume 状态。

### 11.2 Yarn 隔离标准

Yarn Spinner 是唯一剧本后端和外部前置依赖，但普通游戏代码不应依赖具体 Yarn 类型。

稳定公共 API 位于：

```text
Majo.Runtime.Dialogue
├── IDialoguePlayer
├── IDialogueVariables
├── IDialoguePresenter
├── DialogueLine / DialogueOption
├── DialogueStartRequest
└── DialogueSessionState
```

M9.5/M9.6 新增的 Yarn-free 公共边界位于：

```text
Majo.Runtime.Script
├── IScriptManager / IScriptPlayback
├── ScriptId / ScriptDescriptor
└── ScriptPlaybackSpot / ScriptCheckpoint

Majo.Runtime.State
├── IStateManager / IStateContributor
├── StateScope / StateContributorRegistry
└── StateSnapshot / StateRecord
```

Yarn 细节隔离在：

```text
Majo.Runtime.Dialogue.Yarn
├── YarnDialogueBackend
├── YarnPresenterAdapter
├── YarnCommandAdapter
└── YarnLineConverter
```

依赖方向固定为：

```text
Game / Default UI -> Majo Dialogue API -> Majo Script API -> Yarn Adapter -> YarnSpinner-Godot
Majo State Core <- Script State Contributor -> Majo Script API
```

Runtime 不为 Ink、Ren'Py 或其他 VM 建立通用剧本抽象。需要原生 LineProvider、Yarn
Presenter 或调试能力的高级用户可以显式引用 `Majo.Runtime.Dialogue.Yarn` 的
Integration Builder；基础安装和使用流程不暴露该入口。

### 11.3 M6：Dialogue Session 与 Yarn 隔离（已完成）

目标：让 Runtime 完整拥有对话项目加载和会话生命周期，移除 Demo 对 YarnProject 和
DialogueRunner 的手工控制。

公共入口建议：

```csharp
await dialogue.StartAsync(new DialogueStartRequest(
    ProjectId: "main",
    StartNode: "Start"));

await dialogue.StopAsync();
```

状态机：

```text
Idle -> Loading -> Running -> Stopping -> Idle
                -> Faulted
```

确认的所有权规则：

- Runtime 默认创建、持有并释放 Yarn Backend 与 DialogueRunner。
- 对话必须由 `StartAsync` 显式启动；Runtime 初始化后不自动播放。
- `DialogueConfiguration` 提供 `main` 和 `Start` 作为默认项目与节点，但调用方可以覆盖。
- 普通宿主不直接传入 DialogueRunner；测试通过 Fake Backend，原生 Yarn 定制通过高级
  Integration Builder。
- Integration Builder 产生的 Backend 仍由 Runtime 管理生命周期，避免双重释放。

交付：

- `IDialoguePlayer`：Start、Stop、状态查询和会话事件。
- `IDialogueVariables`：Yarn 原生 string、bool、number 的 Yarn 无关访问和变化事件。
- Yarn 无关的 Line、Option、会话和错误 DTO。
- 当前 Project ID、Node、最近 Line ID 和选项阶段。
- Started、LinePresented、OptionsPresented、Completed、Stopped、Failed 事件。
- 按 `res://assets/scripts/<project-id>` 约定加载项目。
- Stop、Reset 和 Shutdown 取消当前对话及其拥有的 Operation。
- M5 Demo 改为只调用 `IDialoguePlayer`，不读取 YarnProject 或操作 DialogueRunner。

非目标：

- 默认可视 UI、打字机、Auto、History 和音频。
- 通用多剧本 VM 抽象。
- 对话状态持久化和 line ID 恢复。

严格验收：

- 重复启动、运行中再次启动、加载失败、命令失败、主动停止、Reset 和 Shutdown 都有
  明确状态及类型化错误。
- Stop 后不存在对话拥有的活跃 Operation、Presenter 等待或残留 Node。
- 架构测试保证 `Majo.Runtime.Dialogue` 公共签名不引用 Yarn/YarnSpinner 类型；仅
  `Majo.Runtime.Dialogue.Yarn` 可以引用。
- Fake Backend 单元测试覆盖状态机和竞态；真实 Yarn VM headless 覆盖项目加载、事件
  顺序、变量、正常完成、停止和失败清理。

实施记录（2026-08-23）：

- 新增 Yarn 无关的 `IDialoguePlayer`、`IDialogueVariables`、Presenter Context、Line、
  Option、Markup、会话状态及类型化异常；稳定 `Majo.Runtime.Dialogue` 公共签名通过反射
  架构测试保证不引用 Yarn 类型。
- Runtime 按 `res://assets/scripts/<project-id>` 约定创建并拥有 Yarn Backend、
  DialogueRunner、变量存储和 Presenter Adapter；默认入口为 `main/Start`，初始化后不
  自动播放。
- `StartAsync` 与 `Completion` 分离启动和完成语义；公开 Loading、Running、Stopping、
  Idle、Faulted 状态，以及 Started、Completed、Stopped、Failed、Line 和 Options 事件。
- Stop 在等待型 Command 中先取消并汇合 Command/Operation，再于安全 Presenter 边界
  停止 Yarn VM；返回时没有对话拥有的 Operation、Presenter 等待或延迟 VM Continue。
- M4/M5 集成测试和可见 Demo 均迁移到 `IDialoguePlayer`；普通代码不再加载 YarnProject、
  注入或手工释放 DialogueRunner，高级 Yarn 能力隔离在 `Majo.Runtime.Dialogue.Yarn`。
- 纯 C# 单元测试覆盖默认入口、重复启动、加载期停止、自然完成、主动停止、Faulted
  恢复、Reset、选项阶段、变量类型和 API 隔离；真实 Yarn headless 覆盖约定加载、
  事件顺序、变量桥接、项目与 Command 失败、运行中停止及清理。
- 门禁通过：92 个纯 C# 单元测试、可见 Demo smoke、真实 Yarn/Godot headless 连续
  三轮和失败探针；Stop/Command 竞态另执行 20 轮 headless 压力测试且无延迟台词、
  VM Continue、Operation 或 Node 泄漏。

### 11.4 M7：Input Manager

状态：已完成。

目标：建立 Presenter、菜单和宿主玩法共享的输入所有权系统，避免各 Node 直接争抢
Godot 输入。

交付：

- `IInputManager`、`IInputHandle` 和 Owner-based Input Scope/Muter。
- 键盘、鼠标和手柄 Backend；触屏后置。
- `majo_` 前缀的默认 InputMap Action；只补充缺失项，不覆盖宿主已有绑定。
- 同帧激活去重、启用/禁用和模态输入接管。
- Fake Input Backend，使输入仲裁不依赖 SceneTree 即可单元测试。

默认逻辑动作：

```text
advance
cancel
auto
history
menu
optionUp
optionDown
confirm
```

严格验收：

- 后取得的模态 Scope 可以按允许列表接管动作，释放后准确恢复之前所有权。
- 一个确认输入不会同时完成打字机并跳过整行。
- Reset、Shutdown 或 Owner 销毁后没有遗留 Muter/Scope。
- 键鼠和手柄在真实 Godot headless 输入注入下产生相同逻辑动作序列。

实施记录（2026-08-23）：

- 新增 Yarn 无关的 `IInputManager`、`IInputHandle`、`IInputBackend`、逻辑 Action、设备
  类型与激活事件；Presenter 和玩法层不需要直接处理 Godot `_Input`。
- 普通 Scope 按后取得者优先拥有匹配动作；Modal Scope 形成 allowlist 边界并阻止其余
  动作穿透；Muter 可屏蔽全部或指定动作，释放或禁用后确定恢复。
- Scope 与 Muter 均绑定 Owner；同一 Owner 可统一释放，Godot Node 退出树时自动释放，
  Reset 和 Shutdown 强制清空全部 Handle 与输入订阅。
- Godot Backend 只安装缺失的 `majo_` InputMap Action，不改写宿主已有绑定；键盘、鼠标、
  手柄事件投影为同一逻辑动作，一个物理事件即使匹配重叠绑定也至多产生一个激活。
- Manager 对同动作执行同帧去重；提供全局和 Handle 级启停，配置的初始启用状态会在
  Reset 后恢复。
- `FakeInputBackend` 支持不启动 SceneTree 的输入仲裁测试；真实 Godot headless 覆盖
  InputMap 约定、宿主绑定保留、键鼠手柄注入、重叠绑定、Owner 销毁、Reset、Shutdown
  及 Node 清理。
- 门禁通过：103 个纯 C# 单元测试、可见 Demo smoke、Godot headless 导入与编译、真实
  Yarn/Godot headless 连续三轮及失败探针；仅保留 Yarn 插件既有的 `SYSLIB1220` 警告。

### 11.5 M8：Default VN Presenter 与 Yarn Markup Bridge

状态：已完成。

目标：提供可直接使用、同时可以通过 PackedScene 整体替换的传统单文本框视觉小说 UI。

架构决策：

- 默认台词显示以内建集成方式组合 Yarn `LinePresenter`、`IAsyncTypewriter`、
  `MarkupParseResult` 和 `ActionMarkupHandler`；Majo 不重新实现打字机、Markup 解析、
  HurryUp/NextLine Token 或台词淡入淡出。
- `IDialoguePlayer`、Yarn-free Line/Option/Markup DTO、项目加载、会话状态和安全停止继续由
  M6 提供；`IDialoguePresenter` 保留为可选的 Yarn-free 扩展入口，不要求默认 UI 通过它
  重写 Yarn 已有能力。
- Runtime 内部允许原生 Yarn Presenter 与 Majo 投影 Adapter 同时挂载；原生类型只存在于
  `Majo.Runtime.Dialogue.Yarn` 集成层，不进入普通游戏代码或稳定公共 API。
- 不使用 Yarn `LineAdvancer`。默认输入桥只消费 M7 `IInputManager`，一次 Advance 根据
  当前显示阶段选择 HurryUp 或 NextLine，避免同一物理输入在一帧内触发两者。
- Options 可以复用 Yarn 的 OptionItem/Button/焦点机制，但导航、确认和取消的所有权必须
  由 M7 Modal Scope 管理，不能绕过输入仲裁直接轮询 Godot Input。

交付：

- `IDialoguePresenter` 和 Yarn 无关的 Presentation Context。
- 基于 Yarn 原生 Presenter 能力组合默认角色名、RichText 正文和选项 UI。
- 可配置打字速度和两阶段继续：首次 Advance 完成立即显示，第二次进入下一行。
- Presenter 只消费 `IInputManager`，不直接散落处理 Godot `_Input`。
- Theme、字体和 Presenter PackedScene override。
- Text 与 Options 内部职责分离，选项支持键鼠和手柄焦点导航。
- Yarn Adapter 直接复用 Yarn 的 markup parser、Typewriter 和 Action Markup Handler，
  同时将解析结果投影为不可变的 Majo facade；不重新解析或发明新的 markup 语法。
- 普通自定义 markup handler 通过 Majo 扩展接口注册；原生 Yarn handler 使用高级入口。

非目标：

- 聊天气泡、多 TextPrinter、按角色切换 Printer、Auto 和 History。

严格验收：

- HurryUp、NextLine 和取消 Token 在 Majo Presentation Context 中保持 Yarn 原语义。
- 打字过程中连续输入、同帧输入、取消、选项出现和 Presenter 被释放均不会越过内容。
- 未知 markup 保留信息且不破坏文本；已注册 handler 按确定顺序执行。
- 默认 PackedScene 可直接运行，自定义 PackedScene 可以替换而不修改 Runtime 源码。
- 架构测试保证默认 Presenter 组合复用 Yarn Typewriter/Markup 实现，Majo Runtime 中不出现
  第二套逐字计时器或 Markup parser。

实施记录（2026-08-23）：

- 新增可直接运行的传统单文本框 PackedScene，组合 Yarn 原生 `LinePresenter`、
  `LetterTypewriter`、`OptionsPresenter` 与 `OptionItem`；默认开启并支持整体 PackedScene、
  Theme、Font、字号、打字速度和淡入淡出配置。
- 默认组合不注册 `LineAdvancer`。M7 line Scope 将同一逻辑 Advance 严格映射为
  `HurryUp -> NextLine` 两阶段；同帧去重保证一次物理输入不能同时触发两阶段。
- 选项使用 M7 Modal Scope 管理上下、下移、确认与取消边界；原生 Button 的直接提交信号
  被断开，焦点与最终 `InvokeOptionSelected` 均由输入所有权层驱动。
- 新增 Yarn-free `IDialoguePresentation`、`IDialogueMarkupHandler` 和确定顺序 Registry；
  Yarn `MarkupParseResult` 直接投影为不可变 Majo DTO，保留未知 attribute、位置、长度和
  typed properties，不重新解析 markup。
- Runtime 可以安全并存 Yarn-free observer Presenter 与原生 Yarn Presenter；原生注册入口
  只位于 `Majo.Runtime.Dialogue.Yarn`，普通 API 的反射测试保证不暴露 Godot/Yarn 类型。
- 真实 Godot/Yarn headless 覆盖自定义 PackedScene、原生 LetterTypewriter、HurryUp token、
  同帧双输入、键盘选项分支、line/options Stop 取消、markup 顺序以及同步 UI 释放；verbose
  退出无 ObjectDB/Resource 泄漏。
- 门禁通过：105 个纯 C# 单元测试，完整 headless 测试正文通过；仅保留 Yarn 插件既有的
  `SYSLIB1220` 编译警告。

### 11.6 M9：Audio Runtime

状态：已完成。

目标：建立独立、可替换、符合 Operation 时序的 BGM、SFX 和 Voice 服务。

确认的播放模型：

- BGM 支持命名多轨、淡入淡出和交叉淡化。
- SFX 可以重叠播放，并可按命名实例控制。
- Voice 默认单通道；新 Voice 中断上一条 Voice。

资源约定：

```text
res://assets/audio/bgm/<id>
res://assets/audio/sfx/<id>
res://assets/audio/voice/<locale>/<line-id>
```

交付：

- `IAudioManager`、Audio Backend、Audio Configuration 和分类资源 Loader。
- Master、BGM、SFX、Voice 音量与静音。
- 所有淡化接入 Operation 的 wait、group、cancel 和 complete 语义。
- Voice Presenter 根据 line ID 自动解析资源；line metadata 可以严格 override。
- Audio Command 按 arguments 与 handler colocate，并通过独立 Command Module 注册。
- Fake Audio Backend 支持确定性时序单元测试。

严格验收：

- BGM 交叉淡化、SFX 重叠、Voice 抢占、取消和强制完成的终态准确。
- Reset/Shutdown 释放全部 AudioStreamPlayer 和 Operation。
- Headless 不依赖物理音频设备，通过 Backend、Node 和事件状态验证。

实施记录（2026-08-23）：

- 新增 Yarn/Godot 无关的 `IAudioManager`、`IAudioBackend`、`IAudioClipLoader`、播放
  DTO 和事件；默认 Godot Backend 只在隔离层持有 `AudioStreamPlayer`。
- BGM 以命名 Track 管理，同 Track 换曲执行双 Player 交叉淡化；SFX 以命名 Instance
  支持同资源重叠；Voice 保持单通道并在新语音开始前确定停止旧语音。
- Master、BGM、SFX、Voice 音量和静音即时投影到所有活跃 Player；音频配置同时提供
  默认淡化时长、Voice Locale 及三类资源前缀。
- 所有淡入、淡出和交叉淡化均由 `OperationScheduler` 持有，支持 ID、Group、Yarn
  `await`、`cancel` 和强制 `complete`；取消交叉淡化恢复旧 Track，强制完成准确释放旧
  Player 并应用目标 Track。
- 自动 Voice Presenter 按 `audio/voice/<locale>/<line-id>` 约定解析；
  `voice:<clip-id>` metadata 是严格 override，`voice:none` 显式禁用当前行语音。
- `bgm`、`stopBgm`、`sfx`、`stopSfx`、`voice`、`stopVoice` 的参数和 Handler colocate，
  由独立 `AudioCommandModule` 原子注册；普通 Command Catalog 和 Yarn Adapter 未增加
  音频分支。
- `FakeAudioBackend` 和 `FakeAudioClipLoader` 支持无真实睡眠的确定性淡化与播放完成测试；
  114 个纯 C# 单元测试全部通过。
- 真实 Godot headless 使用生成式 AudioStream（不依赖物理音频设备）验证 Player 节点、
  BGM 交叉淡化、SFX 重叠、Voice 抢占、取消、强制完成以及 Reset/Shutdown 零残留。

### 11.7 M9.5：Script Runtime Foundation（已完成）

目标：恢复 Naninovel Script 模块中不属于“脚本语法/VM”的 Runtime 职责，在不
重新实现 Yarn 的前提下，为 State 和 M10 提供唯一、可恢复的播放真相。

边界决策：

- `IDialoguePlayer` 继续是宿主用于启动/停止对话及订阅台词的高层外观。
- 新的 `Majo.Runtime.Script` 持有 Script ID、目录、版本、播放阶段和快照契约；
  `ScriptPlayback` 投影 `DialoguePlayer` 的会话身份和当前位置，不保留第二份
  current node/line 可变状态。
- `YarnDialogueBackend` 负责把 `YarnProject`/`DialogueRunner` 投影到 Script 契约；
  公共 `Majo.Runtime.Script` 签名不出现 Yarn 类型。
- Yarn VM Bridge 只是 Yarn 3.2.0 的隔离实现细节，不是普通游戏代码可调用的 API。

公共模型：

```text
ScriptId              = ProjectId + NodeName
ScriptDescriptor      = ScriptId + ProgramFingerprint + available line metadata
ScriptPlaybackSpot    = ScriptId + stable LineId + boundary/replay semantics
ScriptPlaybackPhase   = Idle / Loading / Executing / PresentingLine /
                        AwaitingAdvance / PresentingOptions / Stopping / Faulted
ScriptCheckpoint      = format version + descriptor fingerprint + playback spot +
                        backend-neutral replay data
```

交付：

- `IScriptManager`：按逻辑 ID 解析、加载和校验 Script，产生确定的
  `ProgramFingerprint`。
- `IScriptPlayback`：暴露当前位置、播放阶段、安全快照可用性和阶段变化事件。
- `IScriptCheckpointBridge`：捕获安全行边界，并在新会话中重放当前行一次后
  从下一条 Yarn 指令继续。
- 脚本快照持久化锚点是 stable Line ID，program counter 只能用于当前进程诊断，
  不进入稳定 State 协议。
- 恢复数据要包含重现当前行所需的 resolved substitutions；不允许为了恢复行
  而从 node 开头重放 Command。
- 重复 Line ID、缺失 node/line、非安全边界、待选选项、非空 call/value stack、
  格式版本或 ProgramFingerprint 不兼容时明确拒绝，不猜测附近位置。
- 首版恢复要求 ProgramFingerprint 完全一致；脚本修改后的兼容迁移和
  recovery rollback 延后到独立 milestone。
- 保留未来 Transient Script Provider 扩展口，但本 milestone 不开放社区 modding。

Yarn VM Bridge 产品化门禁：

- 锁定 Yarn Spinner 3.2.0，启动时同时校验版本和 Bridge 内部布局。
- `InternalsVisibleTo("YarnSpinner.Tests")` 和 `UnsafeAccessor` 依赖被封装在独立程序集；
  字段或执行状态布局不匹配时 fail fast，不运行降级。
- Bridge 的快照 DTO 不直接作为 State 持久化 DTO；由 Yarn Adapter 投影为
  backend-neutral `ScriptCheckpoint`。

严格验收：

- 单元测试覆盖 Script ID、目录、指纹、快照格式、重复 Line ID 和所有拒绝路径。
- 真实 Yarn 编译结果同时验证普通行和带变量 substitution 的行；恢复后当前行
  只展示一次，文本与捕获时完全一致，下一次 Advance 进入正确后续行。
- 在脚本中插入无关指令但不改变目标行时，Bridge 仍能用 Line ID 定位；
  产品恢复则在破坏当前状态前因 ProgramFingerprint 变化而拒绝。
- Godot headless 验证 Stop -> 新 Session -> 注入 -> 重放 -> 继续 -> 正常结束，
  且无 Runner、Presenter、Operation 或延迟 Continue 残留。
- 架构测试保证 `Majo.Runtime.Script` 和普通 `Majo.Runtime.Dialogue` 公共签名不引用 Yarn。

实施记录（2026-08-23）：

- 已新增 Yarn-free 的 Script 身份、Descriptor、播放阶段、安全边界和 checkpoint v1
  契约；`ScriptPlayback` 只读取现有 Dialogue 会话真相，并由默认 Presentation 的
  display-complete/dismiss 事件确定 `AwaitingAdvance`。
- `YarnDialogueBackend` 以 compiled Program protobuf 的 SHA-256 生成精确指纹，校验
  node、全项目 Line ID 唯一性、格式、边界、指纹和锚点，再将恢复请求注入新 Session。
- 独立 Bridge 已支持 resolved substitutions 的捕获和按 Yarn 栈顺序重建；恢复使用
  stable Line ID 定位，program counter 只保留为诊断信息。
- Bootstrap 在建立 Runtime 前同时校验 Yarn 版本和 `Dialogue.vm`、`callStack`、
  `isContinuing` 内部布局；不匹配直接失败，不执行降级恢复。
- 真实 Yarn compiler 和 Godot `DialogueRunner` 已验证普通行、动态 substitution、停止旧
  Session、重放当前行一次、继续下一行和完整清理。

### 11.8 M9.6：State Snapshot Foundation（已完成）

目标：实现 Naninovel State 模块的状态贡献和有序恢复内核，但不把磁盘槽位、
存档 UI 或 Rollback 快照栈拉入第二阶段。

状态作用域：

```text
Session   仅当前 Runtime Session；Reset 清空；永不进入磁盘存档
Game      当前 playthrough 可恢复快照；本阶段只在内存中 round-trip
Global    跨 playthrough 状态；冻结契约，持久化延后
Settings  用户设置；冻结契约，持久化延后
```

公共契约：

- `IStateManager`：捕获 Game Snapshot、恢复 Game Snapshot、重置 Session，并发布开始、
  完成和失败事件。
- `IStateContributor`：显式声明稳定 `Key`、`Scope`、`SchemaVersion`、依赖项，
  以及 Capture/Preflight/Restore 操作。
- `StateSnapshot`/`StateRecord`：不可变、Yarn/Godot 无关，通过显式 key 和 schema
  索引 payload；不把 assembly-qualified type name 当作长期兼容 key。
- `StateContributorRegistry`：显式注册，检查 key 重复、缺失依赖和循环，并给出确定
  Capture/Restore 顺序。

Game Contributor 范围：

- Script Playback：`ScriptCheckpoint`。
- Dialogue Variables：Yarn string/bool/number 的完整快照，包括恢复前清理多余变量。
- Actors：Actor ID、Factory ID、Appearance、Visible、Position、Tint/Opacity 的确定终态。
- Audio：命名 BGM Track 及其可恢复属性；一次性 SFX 和当前 Voice 不恢复。
- 活跃 Operation、Input Scope/Muter、Presenter token 和 Godot Node 引用永不序列化；恢复时
  由各服务重建。

恢复事务顺序：

```text
Preflight 全部 record/schema/fingerprint/anchor
    -> 接管模态输入并停止对话
    -> 取消并汇合活跃 Operation
    -> Reset Game contributor
    -> 按依赖顺序恢复 Variables / Actors / Audio / 扩展服务
    -> 最后恢复 Script Playback 并重放当前行
    -> 释放输入所有权并发布 Restored
```

所有 Preflight 在破坏当前状态前完成。如果已开始 Restore 后 Contributor 失败，
Runtime 进入明确 Faulted 状态、保留输入阻断且不自动继续脚本；本阶段不虚假声称可对任意
外部副作用做全局原子回滚。

严格验收：

- 纯 C# 测试覆盖 Registry 顺序、稳定 key、schema 版本、缺失/未知 record、
  Preflight 不破坏当前状态、Restore 失败和 Reset 清理。
- Fake Script/Actor/Audio/Variables Contributor 验证完整捕获和恢复顺序，不使用磁盘。
- 真实 Yarn/Godot headless 在带 substitution 的等待行捕获快照，随后修改变量、
  Actor、Background 和 BGM，Restore 后验证全部回到快照状态并从正确后续行继续。
- 快照与恢复期间没有输入穿透、重复 Command、重复 Voice、活跃 Operation、
  延迟 VM Continue 或 Node 泄漏。
- State 内核和快照 DTO 的反射架构测试保证不引用 Yarn 或 Godot 类型。

实施记录（2026-08-23）：

- 已实现不可变 `StateSnapshot`/`StateRecord`、四种 Scope、稳定 key/schema、显式
  Contributor Registry，以及缺失依赖、重复 key、循环依赖和确定拓扑顺序诊断。
- 默认 Game Contributor 已覆盖 Script Playback、Dialogue Variables、Actors/Background
  和命名 BGM；变量恢复先清空，Actor 校验 ID/Factory，SFX 和 Voice 明确不进入快照。
- `StateManager` 捕获前强制完成活跃 Operation；恢复先完成全量 Preflight，再接管输入、
  停止 Dialogue、取消并汇合 Operation、逆序 Reset、正序 Restore，最后恢复 Script。
- Preflight 失败不会停止或修改当前 Game；破坏性阶段失败进入 `Faulted` 并持续静音输入，
  直到 Runtime Reset/Shutdown 明确释放。
- 真实 Godot headless 已完成带 substitution 等待行、变量、角色、背景和 BGM 的内存
  round-trip，并验证当前行只重放一次、下一次 Advance 正确、无临时音频和节点泄漏。
- 全量门禁通过：127 个纯 C# 单元测试、可见场景 smoke、Godot import/编译和三轮
  headless 集成测试均通过。

非目标：

- JSON/二进制存档文件、加密、压缩、槽位命名和迁移 UI。
- Quick Save、Auto Save、应用退出时保存、缩略图和 Save/Load UI。
- Rollback 快照栈、玩家回退输入、脚本变更后的 recovery rollback。
- 恢复待选选项、非空 Yarn call/value stack 或任意 VM 指令中间态。

### 11.9 M10：Auto 与 History（已完成）

目标：补齐普通视觉小说的自动阅读和当前 Session 回看能力。

前置：M9.5 和 M9.6 必须完成。Auto 消费 `IScriptPlayback` 的播放阶段和安全
Advance 边界；History 是 State `Session` 作用域的 Contributor，明确不进入
Game Snapshot 或未来磁盘存档。

Auto 交付：

- Manual 和 Auto 两种状态及明确切换事件。
- Auto 只在 `AwaitingAdvance` 阶段启动 deadline，等待打字完成、Voice 完成
  和可配置的额外停留时间。
- 进入选项或打开 History 时自动暂停；关闭后是否恢复由进入前状态决定。
- 手动 Advance 与 Auto deadline 同时发生时只推进一次。

History 交付：

- 只记录实际展示完成的台词；以最终文本可见事件为准，不以较早的
  `LinePresented` 投递事件为准，也不记录 Command 和选项。
- 保存 line ID、角色名、最终文本和 Voice ID。
- 可配置容量和确定的淘汰顺序。
- 默认 History UI，并支持重新播放对应 Voice。
- History 仅存在于当前 Runtime Session，Reset 后清空，不写入磁盘。

严格验收：

- Fake Frame Clock、Fake Input 和 Fake Audio 验证 Auto，不使用真实睡眠。
- 无 Voice、有 Voice、Voice 被抢占、选项、History 模态和手动输入竞态均有独立测试。
- History 容量、顺序、重复 line ID、markup 最终文本和 Voice 重播行为确定。
- 完整可视 Demo 与真实 Yarn/Godot headless 覆盖 Manual、Auto 和 History 闭环。

实施记录（2026-08-23）：

- 已新增 Yarn-free `IAutoManager`，只消费 `IScriptPlayback` 阶段；deadline 只在
  `AwaitingAdvance` 创建，先等待捕获到的 Voice 完成，再使用 unscaled Frame Clock
  执行配置停留时间。Manual、Auto、Pause 和失败均有明确状态或事件。
- History 和 Options 分别暂停 Auto；关闭/选择后重新开始完整 deadline。手动 Advance
  会取消待执行 deadline，Yarn Backend 对同一展示边界的 NextLine 请求再做一次原子
  去重，因此同帧 Manual/Auto 竞争只推进一次。
- 已新增 `IHistoryManager` 和默认 History Overlay；仅在 `LineDisplayCompleted` 记录 stable
  line ID、speaker、最终可见文本和实际可用 Voice ID，按 sequence/FIFO 确定淘汰，并支持
  重复 line ID 与 Voice 重播。
- History 以稳定 key `majo.session.history` 注册为 State `Session` Contributor；Reset
  关闭并清空 History、将 Auto 恢复为 Manual，并在 Input Reset 后重新绑定两项常驻输入。
  `CaptureGameAsync` 的 record 集合不含 History。
- 可视 Demo 已改用默认 Presenter 并提供 Auto/History HUD；真实 Yarn/Godot fixture 覆盖
  Manual、History 暂停、Voice 等待、Options 暂停、容量淘汰、Voice 重播、Reset 与节点
  清理闭环。
- 全量门禁通过：136 个纯 C# 单元测试、Godot import/编译、可见场景 smoke、三轮连续
  headless 集成测试和失败探针均通过；仅保留 YarnSpinner-Godot 既有 `SYSLIB1220` 警告。

### 11.10 第二阶段完成定义（已完成）

M6-M10 只有同时满足以下条件才算完成：

- 完整 Demo 覆盖项目加载、台词、markup、选项、输入、BGM、SFX、Voice、Auto 和
  History。
- 普通游戏代码和默认 UI 不引用 Yarn 类型；Yarn 细节仅存在于隔离集成命名空间。
- Script 资源身份、播放阶段和安全快照只有一个真相来源，`DialoguePlayer`、
  Auto、History 和 State 不各自复制 current line/node 状态机。
- 内存 Game Snapshot 能恢复 Script、Variables、Actors、Background 和 BGM，而 Session History
  在 Reset 后清空且不进入 Game Snapshot。
- 默认 Presenter 可直接运行，也能通过 PackedScene 整体替换。
- 没有输入争抢、活跃 Operation、残留 Node、Presenter 等待或未观察异步异常。
- 每个 milestone 通过纯 C# 单元测试、Godot headless、可视 smoke 和失败探针。
- 核心时序 headless 连续运行三次，无偶发失败和新增未解释警告。
- Runtime 源码中不存在证词、证据、推理或辩证玩法实现。

## 12. 严格测试策略

### 12.1 纯 C# 单元测试

- 使用独立测试项目和标准 `dotnet test`。
- Core 测试不得启动 Godot SceneTree。
- 时间相关测试使用 FakeFrameClock，不使用真实睡眠。
- 每个异步测试设置上限超时。
- 对异常类型、状态变化和清理结果进行明确断言。
- 新增公共行为必须先有失败测试，再实现通过。

### 12.2 Godot headless 测试

- 使用本机 Godot Mono headless 模式。
- 测试运行器以进程退出码表示成功或失败。
- 测试真实 Node 生命周期、Signal、Tween 和 Yarn DialogueRunner。
- 测试使用事件和状态断言，不依赖截图或肉眼观察。
- 时间断言使用合理容差，同时验证事件相对顺序。
- 每个测试场景结束后检查 Node 和 Operation 泄漏。
- 任一未观察 Task 异常、超时或日志中的未预期 Error 都视为失败。

### 12.3 每个 milestone 的统一门禁

顺序执行：

1. 格式和静态检查。
2. `dotnet build`。
3. `dotnet test`。
4. Godot headless 导入/编译检查。
5. Godot headless 集成测试。
6. 重复执行关键时序测试以发现偶发问题。

只有全部通过且没有新增未解释警告，milestone 才能标记完成。

## 13. 主要风险与控制

### Yarn Command 名称冲突

`stop` 是 Yarn VM 内建命令，Majo 使用 `cancel` 管理 Operation；启动时检查其他命令重复注册。

### Task 与 Godot 生命周期

禁止使用 `Task.Run` 操作 Godot Node；所有 Godot 状态变化保持在主线程，并绑定 Runtime 取消令牌。

### 强制完成与普通取消混淆

Operation API 和测试明确区分 Cancel 与 Complete，Tween 实现必须分别处理。

### Headless 时序测试偶发失败

用信号和相对顺序断言代替固定睡眠；统一超时并记录完整事件轨迹。

### Yarn 版本升级

首版锁定当前版本；升级通过独立兼容 milestone 完成，不在核心实现中静默接受未知版本。

### 过早复制 Naninovel 功能

每个 milestone 只实现已确认的 Runtime 能力。M6-M10 只前置 Script/State 的播放与
内存快照内核，不引入磁盘存档产品层、Rollback、镜头、多脚本轨道、完整本地化
或 Source Generator；推理玩法始终由 Runtime 外部模块实现。

## 14. 完成定义

第一阶段只有同时满足以下条件才算完成：

- M0-M5 的交付项和测试门禁全部通过。
- 示例闭环在可视运行和 headless 环境均成功。
- 异步行为符合 Naninovel 已确认标准。
- Yarn Spinner 保持独立且未被修改。
- 自定义 Service、Actor 和 Command 能通过公开 API 接入。
- Core 不依赖 Yarn Spinner。
- 没有活跃 Operation、Node 泄漏、未观察异步异常或未解释的新警告。
