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

未来将支持完整视觉小说功能以及类似《逆转裁判》的证词、推理和辩证模式，但当前不实现这些玩法。

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
- 首版 Yarn Command 使用类型化位置参数，不实现 Naninovel 风格的命名参数解析器。

## 3. 范围边界

### 当前核心范围

- Runtime 组合根和服务生命周期。
- 配置加载与验证。
- Operation 调度、等待、分组、取消和强制完成。
- 最小 Character、Background Actor 及 ActorManager。
- Yarn DialogueRunner 适配和核心 Command。
- 最小 Yarn 台词与选项闭环。
- 单元测试、Godot headless 测试和可运行示例。

### 明确后置

- 存档、读取、按 line ID 恢复、回滚。
- 自动播放、快进、已读判定和历史记录。
- BGM、SFX、Voice。
- 镜头、复杂转场和特效。
- 自定义完整对话 UI 和设置 UI。
- Live2D、Spine、分层立绘、视频背景。
- 多 Yarn 脚本轨道、后台脚本循环以及 Naninovel `sync` 轨道接管。
- 证词、证据、推理和辩证玩法。

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

- Naninovel Script 编译器。
- ScriptManager、ScriptLoader 和主 ScriptPlayer。
- 分支、变量表达式、台词和选项执行。
- Naninovel Command 参数编译体系。

Majo Runtime 不复制上述脚本层，而是在 Yarn `DialogueRunner` 外建立适配层。

## 5. 模块和依赖方向

```text
Majo.Runtime.Core
├── Configuration
├── Operations
│   └── Actors
│       └── Commands
└── Dialogue.Yarn

Tests
├── Unit -> Core / Operations / Actors / Commands
└── Headless -> 完整 Godot + Yarn + Majo Runtime
```

依赖规则：

- `Core` 不依赖 Godot 场景对象和 Yarn Spinner。
- `Operations` 不依赖 Yarn Spinner。
- `Actors` 可以依赖 Godot，但不依赖 Yarn Spinner。
- `Dialogue.Yarn` 是唯一直接依赖 YarnSpinner-Godot 的 Majo 模块。
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
│   └── Dialogue/Yarn/
├── Editor/
└── Samples/

game/tests/
├── Unit/
└── Headless/
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
- `await`：等待命名 Operation/组；可选择强制完成。
- `cancel`：取消命名 Operation/组。

保留 Yarn 内建命令：

- `wait`：计时等待。
- `stop`：结束 Yarn 对话。由于它是 Yarn VM 特殊命令，不能复用为取消命名任务。

首版使用类型化位置参数，所有可选参数必须位于末尾。精确签名由 M4 的编译和源生成器测试确定并冻结。示意：

```yarn
<<back courtroom 0.5 false setup>>
<<char Maya happy left 0.5 false setup>>
<<await setup false>>

Maya: 准备完成了。

<<char Maya surprised center 0.35 true>>
Maya: 等一下！
```

未来可以在不改变底层服务的前提下增加 Naninovel 风格命名参数解析器。

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

### M4：Yarn Command 适配

目标：让 Yarn 通过显式 Command Adapter 驱动 Runtime。

交付：

- DialogueRunner Adapter。
- `back`、`char`、`hide`、`await`、`cancel`。
- 类型化位置参数和错误诊断。
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

### M5：最小可玩闭环与扩展证明

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

## 11. 后续 Roadmap

以下 milestone 只有在 M0-M5 稳定后开始：

### M6：视觉小说 Presenter 与输入

- 自定义台词和选项 Presenter。
- 打字机、继续输入和输入锁。
- 自动播放、快进、已读判定和历史记录。

### M7：音频、镜头和表现

- BGM、SFX、Voice 服务。
- 镜头移动、缩放和震动。
- 更多转场和可复用特效 Operation。

### M8：状态与存档

- 游戏、全局、设置三类状态。
- Actor、Yarn 变量和播放位置。
- 存档槽、自动/快速存档和缩略图。
- 按 Yarn line ID 精确恢复的独立技术设计与验证。

### M9：并发脚本轨道

- 多 DialogueRunner/Yarn VM 轨道研究。
- 命名后台脚本、循环、await 和 cancel。
- 真正的 `sync` 轨道接管语义。
- 与存档、读取和回滚集成。

### M10：推理模式和产品化

- Dialogue 与证词/证据/推理模式切换协议。
- 游戏模式栈、输入所有权和 UI 模态管理。
- 本地化、设置、标题、存读档 UI。
- 插件打包、版本迁移和兼容性测试。

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

M0-M5 只实现闭环所需能力。任何存档、音频、复杂 UI、脚本轨道或推理玩法需求进入后续 milestone，不侵入核心交付。

## 14. 完成定义

第一阶段只有同时满足以下条件才算完成：

- M0-M5 的交付项和测试门禁全部通过。
- 示例闭环在可视运行和 headless 环境均成功。
- 异步行为符合 Naninovel 已确认标准。
- Yarn Spinner 保持独立且未被修改。
- 自定义 Service、Actor 和 Command 能通过公开 API 接入。
- Core 不依赖 Yarn Spinner。
- 没有活跃 Operation、Node 泄漏、未观察异步异常或未解释的新警告。
