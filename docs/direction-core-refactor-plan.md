# Direction Core 重构实施方案

## 1. 文档目的

本文档定义 `Majo.Runtime.Direction.Core` 的目标边界、目标 API、迁移顺序、兼容策略和验收标准。目标不是简单移动文件，而是将 Direction 从“状态模型、命令解析、渲染实现、运行时执行混合在一起”的形态，收敛为一个稳定的领域核心。

本方案允许在内部进行 breaking change，但要求每个阶段都能编译、可测试、可回滚。重构完成后，Direction Core 应能够在没有 Godot、没有 service locator、没有插件注册表、没有 Yarn 输入的环境下独立测试。

## 2. 当前问题与重构动机

当前 `Direction.Core` 已经承载了正确的状态模型和指令模型，但仍有几类边界问题：

- `DirectionEase` 是固定 enum，却被命令绑定、配置、Beat defaults 和 Godot Tween 同时依赖；扩展 transition 可以注册，扩展 easing 却不对称。
- `DirectionTransitionSpec.Validate()` 使用 `Enum.IsDefined(Ease)`，把可用 easing 锁死在编译期。
- `DirectionEase.Decompose()` 返回 `DirectionEaseCurve/DirectionEaseMode`，实际是 Godot Tween 的适配需求，不是 Direction 领域语义。
- `DirectionPlan` 同时包含领域结果和执行信息，例如 resource claims、completion barrier、view sequence、commit token 相关内容。
- `DirectionTransitionKind` 与可扩展的 `DirectionTransitionId` 并存；配置默认值仍使用封闭 enum，导致自定义 transition 无法成为默认策略。
- `DirectionLayoutCatalog` 将 layout 名称硬编码在 Core；如果未来开放插件 layout，需要再做一次边界迁移。
- 命令字符串转换和后端能力校验虽然位于 Core 之外，但领域类型的设计仍然为这些实现细节让路。

重构后的核心原则是：

> Direction Core 只描述 Direction 世界的状态、变化意图和领域不变量；它不负责字符串解析、能力注册、资源加载、Godot 渲染或异步执行。

## 3. 目标架构

目标依赖方向如下：

```text
Majo.Runtime.Dialogue.Commands ─────┐
Majo.Runtime.Direction.Godot ────────┼──> Direction.Execution
Majo.Runtime.Direction.Transitions ──┘          │
Majo.Runtime.Timing ───────────────────────────┘
                                                   │
                         Direction.Planning ──────┘
                                  │
                         Direction.Core
```

推荐的最终模块职责：

```text
Direction.Core
  - identifiers
  - immutable state/value objects
  - direction directives
  - transition/effect intent
  - domain validation and domain exceptions

Direction.Planning
  - sequential patch application
  - target state calculation
  - write conflict detection
  - reference validation
  - pure DirectionPlan creation

Direction.Execution
  - beat lifecycle
  - transaction prepare/commit/settle
  - cancellation and operation barriers
  - resource preparation orchestration

Timing
  - EasingId
  - EasingDefinition
  - IEasingRegistry

Direction.Transitions
  - ITransitionDriver
  - TransitionDriverRegistry
  - domain capability lookup

Direction.Godot
  - Godot surface/capture management
  - Tween and Shader adapters
  - backend-specific easing translation
```

## 4. Direction Core 的最终职责

### 4.1 保留的职责

#### 标识和值对象

保留并继续强化：

- `StageId`
- `ViewId`
- `ActorId`
- `ActorAddress`
- `LayoutId`
- `LayoutSlotId`
- `DirectionTransactionId`
- `DirectionSourceLocation`

这些类型负责规范化和基础合法性，例如非空、去除首尾空格、禁止 null character。它们不访问任何注册表或外部服务。

#### 纯状态模型

保留：

- `DirectionState`
- `StageState`
- `ViewState`
- `ActorState`
- `BackdropState`
- `ScreenState`
- `CurtainState`
- `StageEffectState`
- `StableEffectState`

这些对象必须保持 immutable，集合必须在边界处冻结。`Validate()` 只检查领域不变量和对象内部引用关系。

例如，Core 可以检查：

- main stage 和 main view 存在；
- View 引用的 Stage 存在；
- Screen slot 与 layout 的 required slots 完全匹配；
- Actor 字典 key 与 `ActorState.Id` 一致；
- 坐标、颜色、缩放、zoom、opacity 等数值有限且在合法范围内。

#### 指令和变化意图

保留：

- `StageCreatePatch`
- `StageClearPatch`
- `StageReleasePatch`
- `BackdropPatch`
- `ActorPatch`
- `ActorLeavePatch`
- `ViewCreatePatch`
- `ViewPosePatch`
- `ViewReleasePatch`
- `ScreenPatch`
- `CurtainPatch`
- `ITransientDirectionCue`

这些类型表达“想改变什么”，不表达“如何渲染或何时完成”。它们可以携带 `DirectionTransitionSpec`，但不应携带 `Tween`、`CanvasItem`、资源句柄或 operation handle。

#### 领域异常

保留 `DirectionException`、`DirectionReferenceException`、`DirectionConflictException`、`DirectionRevisionException` 等异常。异常消息可以包含稳定 ID 和 source location，但不得依赖命令名、Yarn token 或 Godot 对象。

### 4.2 明确移出的职责

以下内容不得进入最终的 `Direction.Core`：

- Godot 类型：`Tween`、`CanvasItem`、`Texture2D`、`Shader`、`Node`；
- transition driver registry 和 easing registry；
- 资源加载、预加载和资源 lease；
- command string parsing 和 command parameter schema；
- runtime service locator、operation runner、frame clock；
- backend 是否支持某个 transition/easing 的判断；
- commit、settle、cancellation、异步等待；
- 将 easing 拆成 Godot `TransitionType/EaseType` 的逻辑。

## 5. 目标领域 API

### 5.1 独立 Timing/Easing 抽象

新增 `Majo.Runtime.Timing` 命名空间：

```csharp
public readonly record struct EasingId
{
    public EasingId(string value);
    public string Value { get; }
    public bool IsEmpty { get; }
    public override string ToString() => Value;
}

public sealed record EasingDefinition(
    EasingId Id,
    Func<double, double> Evaluate,
    IReadOnlyList<string>? Aliases = null);

public interface IEasingRegistry
{
    void Register(EasingDefinition definition);
    bool TryGet(EasingId id, out EasingDefinition? definition);
    EasingDefinition GetRequired(EasingId id);
    EasingDefinition Resolve(string name);
    bool Contains(EasingId id);
    void Seal();
}
```

约束：

- `Evaluate` 输入范围为 `[0, 1]`，输出必须有限（允许 overshoot）；注册时抽样验证不能证明任意 delegate 的全区间行为，后端每次采样仍须校验有限值并将异常传给事务 settlement；
- alias 解析大小写不敏感，ID 规范化规则与其他 runtime ID 一致；
- 重复 ID 和重复 alias 默认拒绝；
- registry seal 后禁止注册；
- Core 只保存 `EasingId`，不保存 `EasingDefinition` 或 delegate。

内置 easing（`linear`、`sine-in`、`cubic-out` 等）以注册项存在，而不是 enum 的 exhaustive set。旧 `DirectionEase` 可以在迁移期作为兼容 façade。

### 5.2 TransitionSpec 改造

目标形态：

```csharp
public sealed record DirectionTransitionSpec(
    DirectionTransitionId Kind,
    double DurationSeconds,
    EasingId Easing,
    bool Skippable = true)
{
    public DirectionTransitionSpec Validate();
}
```

语义分工：

- `DirectionTransitionId`：视觉效果是什么，例如 `cut`、`fade`、`dissolve`、`wipe-clock`；
- `EasingId`：进度如何变化，例如 `linear`、`cubic-out`、`elastic-out`；
- `DurationSeconds`：持续时间；
- `Skippable`：是否允许跳过。

`Validate()` 只检查：

- transition ID 非空；
- easing ID 非空；
- duration finite 且非负；
- `cut` 的领域约束（duration 必须为 0）；
- 其他纯领域约束。

它不检查 transition/easing 是否已在当前 runtime 注册。注册能力属于 preflight/backend validation。

### 5.3 Plan 拆分

当前 `DirectionPlan` 应拆为纯领域计划和执行计划。

建议的领域计划：

```csharp
public sealed record DirectionPlan(
    DirectionTransactionId Id,
    DirectionState BeforeState,
    DirectionState TargetState,
    IReadOnlyList<DirectionTransitionIntent> Transitions,
    IReadOnlyList<ITransientDirectionCue> Cues);
```

建议的执行计划：

```csharp
public sealed record DirectionExecutionPlan(
    DirectionPlan DomainPlan,
    IReadOnlyList<DirectionResourceClaim> ResourceClaims,
    DirectionCompletionBarrier Completion,
    IReadOnlyList<DirectionViewTransitionSequence> ViewSequences);
```

迁移时可以先保留兼容的 `DirectionPlan` 属性，再将 execution-only 字段标记 obsolete；最终由 `DirectionTransactionCoordinator` 创建 `DirectionExecutionPlan`。

建议迁移出 Core 的类型：

- `DirectionCommitToken` → `Direction.Execution`；
- `DirectionCompletionBarrier` → `Direction.Execution`；
- `DirectionViewTransitionSequence` → `Direction.Execution` 或 `Direction.Rendering`；领域计划必须保留 `DirectionViewPoseIntent` 中的中间姿态，使执行 sequence 可从领域意图重建；
- `DirectionResourceClaim` → `Direction.Execution`/resource preparation 层。

如果短期内无法物理移动文件，至少先通过命名空间和依赖约束将它们视为 execution 类型，禁止新的 Core 类型依赖它们。

## 6. 现有类型迁移表

| 当前类型/逻辑 | 目标位置 | 处理方式 |
|---|---|---|
| `DirectionState`、各类 State | `Direction.Core` | 保留，强化 immutable 与 Validate |
| Direction identifiers | `Direction.Core` | 保留 |
| Direction patches/directives | `Direction.Core` | 保留 |
| `DirectionTransitionId` | `Direction.Core` | 保留为开放 ID |
| `DirectionTransitionKind` | Compatibility/Commands | 迁移期保留，禁止新增依赖 |
| `DirectionEase` | Compatibility/Timing | 迁移期保留，最终删除或标记 obsolete |
| `DirectionEaseCurve/Mode/Parts` | `Direction.Godot` | 移出 Core |
| `DirectionEaseExtensions.Decompose` | `Direction.Godot` | 改为 Godot adapter lookup |
| `DirectionTransitionSpec` | `Direction.Core` | `Ease` 改为 `EasingId` |
| `DirectionLayoutCatalog` | Core 或 `LayoutRegistry` | 第一阶段保留；开放扩展时迁移 registry |
| `DirectionPlanner` | `Direction.Planning` | 保持纯函数，拆出 execution metadata |
| `DirectionCommitToken` | `Direction.Execution` | 移出 Core |
| `DirectionCompletionBarrier` | `Direction.Execution` | 移出 Core |
| `DirectionResourceClaim` | `Direction.Execution` | 移出 Core |
| `DirectionViewTransitionSequence` | `Direction.Execution/Rendering` | 移出 Core |
| `GodotDirectionRendering.Configure` | `Direction.Godot` | 改为 Easing adapter |
| `CommandValueConverterRegistry` 中 Ease converter | Commands | 改为 `EasingId` + registry resolve |
| `DirectionAnimationDefaults` 中 Kind/Ease | Commands/Configuration | Kind 逐步改为 ID，Ease 改为 EasingId |

## 7. 分阶段实施步骤

### Phase 0：基线与依赖审计

目标：在改代码前固定现状。

工作项：

1. 建立 `Direction.Core` 类型清单和反向依赖清单；
2. 为现有 Direction、Command、Transition、Configuration 测试建立稳定基线；
3. 增加架构检查脚本或 CI grep，禁止 Core 新增 Godot/Commands/Execution 引用；
4. 明确当前公共 API 中哪些属于兼容面。

验收：

- `dotnet build 'Majo Rebuild.csproj' --no-restore` 通过；
- `dotnet test tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj --no-restore` 通过；
- 生成一份可追踪的类型迁移清单。

### Phase 1：先建立 Timing SPI

目标：在不改变 DirectionSpec 的情况下引入可注册 easing。

工作项：

1. 新增 `Majo.Runtime.Timing`、`EasingId`、`EasingDefinition`、`IEasingRegistry` 和默认 registry；
2. 将现有 `DirectionEase` 内置值注册为 built-in easing；
3. 为每个旧名称建立 alias；
4. 在 `MajoRuntimeBootstrap` 和 `PluginRegistrationContext` 中暴露 `Easings`；
5. 增加自定义 easing 的 fake/headless 测试；
6. 暂时保留 `DirectionEase` converter 和旧配置。

验收：插件可以注册 `elastic-out`，registry seal 后注册失败，重复 ID/alias 被拒绝。

### Phase 2：TransitionSpec 改用 EasingId

目标：解除 Core 对固定 easing enum 的依赖。

工作项：

1. 将 `DirectionTransitionSpec.Ease` 改为 `EasingId Easing`；
2. 提供兼容构造函数，将旧 `DirectionEase` 映射为 built-in `EasingId`；
3. 将 `DirectionBeatDefaults`、`TransitionArguments`、`TimingArguments` 改为 `EasingId`；
4. 将 `CommandParameterGroups` 的 ease 参数类型改为 `EasingId`；
5. `CommandValueConverterRegistry` 不再硬编码 `DirectionEase`，改为绑定环境中的 easing resolver/converter；
6. `DirectionTransitionSpec.Validate()` 删除 `Enum.IsDefined(Ease)`。

验收：旧命令字符串和新插件 easing 均能绑定；未知 easing 在 binding/preflight 阶段失败，而不是 Core 构造失败。

### Phase 3：移除 Godot 细节出 Core

目标：让 Core 完全不包含 Godot-specific easing decomposition。

工作项：

1. 删除/迁移 `DirectionEaseCurve`、`DirectionEaseMode`、`DirectionEaseParts`；
2. 将 `DirectionEaseExtensions.Decompose()` 改为 `GodotEasingAdapter`；
3. 内置 Godot easing 使用 adapter table 映射到 Tween；
4. 自定义 easing 使用 `Evaluate(t)` 的采样路径或后端声明的不支持错误；
5. 在 `GodotDirectionRendering` 中注入 `IEasingRegistry`，禁止从 enum 反射解析 Godot 类型。

验收：Core 项目/测试不需要引用 Godot；Godot backend 可执行内置 easing 和至少一个自定义 easing。

### Phase 4：收敛 Transition ID 与配置

目标：让 transition 的开放 ID 在命令、策略和配置中保持一致。

工作项：

1. `DirectionAnimationDefaults` 中的 `DirectionTransitionKind` 改为 `DirectionTransitionId`；
2. 配置序列化使用稳定字符串 ID；
3. 内置 `cut/move/fade/dissolve` 通过 built-in ID 提供兼容常量；
4. `DirectionCommandPolicy` 统一通过 transition registry 做 domain-aware capability check；
5. 明确默认 transition 未注册时的启动期错误信息。

验收：自定义 transition 可以成为某个命令的默认值；不支持当前 domain 的 transition 在 catalog/preflight 阶段被发现。

### Phase 5：拆分 Domain Plan 与 Execution Plan

目标：使纯领域规划结果可被编辑器、测试和历史系统复用。

工作项：

1. 从 `DirectionPlan` 移出 `DirectionCommitToken`、completion、resource claims、view sequences；
2. 将 `DirectionPlanner` 的纯状态变换和 execution metadata 生成拆开；
3. `DirectionTransactionCoordinator` 接受领域计划并生成执行计划；
4. 更新 fake backend 和 transaction tests；
5. 为 domain plan 增加序列化/快照测试（如果当前产品需要存档或 editor preview）。

验收：不启动 runtime、不创建 Godot 对象也能完成 plan 和冲突验证；执行层仍保留原有 commit/settle/cancel 语义。

### Phase 6：清理兼容层

目标：完成 API 收敛。

工作项：

1. 将 `DirectionEase`、`DirectionTransitionKind` 标记 obsolete；
2. 删除生产代码中对旧 enum 的新引用；
3. 提供配置/脚本文档迁移说明；
4. 删除兼容构造函数（在一个明确的 breaking release 中）；
5. 更新 plugin quickstart、command architecture 文档和示例。

验收：核心公开 API 中不再出现封闭 easing enum；所有扩展点通过 ID + registry 工作。

## 8. 注册与验证时序

启动时序必须固定为：

```text
built-in registration
  -> plugin registration
  -> registry validation
  -> command schema validation
  -> transition/easing capability preflight
  -> seal all registries
  -> attach immutable binding environment
  -> build catalog
  -> runtime initialize
```

禁止：

- catalog 暴露后才发现 easing 缺失；
- command invocation 第一次执行时才注册或查找新的 converter；
- runtime initialized 后追加 easing/transition/layout；
- backend 静默把未知 easing 降级为 linear。

错误边界应保持清晰：

| 错误 | 所属阶段 |
|---|---|
| duration 为 NaN、ID 为空、状态引用断裂 | Core/domain validation |
| 字符串无法解析为已注册 easing | command binding |
| transition/easing 未注册或 domain 不支持 | catalog/preflight |
| backend 创建 Tween/Shader 失败 | execution/backend |
| command policy 选择了不允许的 transition | policy/execution |

## 9. 测试矩阵

### Core/headless

- State 的所有引用不变量；
- patch 顺序应用和 target state；
- 重复写入冲突与 source location；
- transition spec 数值校验；
- 自定义 `DirectionTransitionId` 和 `EasingId` 不依赖 Godot；
- domain plan 的确定性（相同输入得到相同结果，除 transaction ID 外）。

### Timing/Registry

- 内置 easing ID 和 alias；
- 自定义 easing 注册、解析、重复拒绝；
- seal 后拒绝修改；
- `Evaluate(0)`、`Evaluate(1)` 和有限值契约；
- 未知名称返回带候选项的诊断。

### Command

- 旧 easing 名称兼容；
- 新 easing 名称绑定；
- nullable/default/presence 语义；
- 未知 easing 在 binding 阶段失败；
- catalog build 在 converter/capability 缺失时失败。

### Godot/fake backend

- 内置 easing 映射到 Tween；
- 自定义 easing 的采样路径；
- 不支持的 backend capability 明确失败；
- transition driver 与 easing 独立组合；
- skip/cancel/reset/shutdown 不泄漏 Tween、texture 或 operation。

### Compatibility

- `DirectionEase.BackOut` 等旧 API 映射到 `EasingId("back-out")`；
- 旧配置可以加载并规范化为新 ID；
- 旧命令 alias 行为保持不变；
- 新插件只依赖新 SPI 时不需要引用旧 enum。

## 10. 风险与处理策略

### 风险：自定义 easing 无法高效映射到 Godot Tween

处理：adapter 优先使用原生 Tween；无法映射时使用可控采样路径。为每个 backend 提供 `Supports(EasingId)`，不做静默 linear fallback。

### 风险：配置资源的 enum 序列化不兼容

处理：迁移期同时接受 enum 和 string，加载后统一转成稳定 ID；保存时使用字符串。提供一次性配置迁移工具或启动期诊断。

### 风险：Plan 拆分扩大改动面

处理：先引入 `DirectionExecutionPlan` façade，保留旧属性转发；待 coordinator、fake backend 和测试迁移后再删除旧字段。

### 风险：插件注册时序不一致

处理：所有 registry 统一由 bootstrap seal；PluginRegistrationContext 只暴露 registration-only API；增加“initialized 后注册失败”的集成测试。

### 风险：transition/easing 语义被混淆

处理：文档、类型名和命令帮助明确区分 `via` 与 `ease`；transition driver 不携带 easing registry；easing 不决定视觉效果类型。

## 11. 非目标

本次重构不包含：

- 运行时热加载或热卸载插件；
- 自动程序集扫描；
- 为所有后端实现任意曲线的硬件级插值；
- 重写 Direction transaction 的提交协议；
- 在没有实际需求前引入通用 layout compositor；
- 把 Direction Core 拆成独立 NuGet/package（先完成源码和依赖边界即可）。

## 12. 完成定义（Definition of Done）

重构完成必须满足：

1. `Direction.Core` 不引用 Godot、Dialogue.Commands、Execution、Transition registry 或 resource manager；
2. Core 中不存在 `DirectionEase.Decompose()`、Godot Tween enum 映射或 `Enum.IsDefined(Ease)`；
3. transition 使用开放的 `DirectionTransitionId`，easing 使用独立的 `EasingId`；
4. 插件可以在 runtime 初始化前注册自定义 transition 和 easing；
5. catalog build 阶段能发现缺失 converter、easing 和 domain capability；
6. runtime 初始化后所有相关 registry 都是只读的；
7. domain plan 可以在 headless 测试中独立生成和验证；
8. 旧命令和旧配置在兼容窗口内行为不变；
9. Godot backend 通过 adapter 执行 easing，不依赖 Core 的实现细节；
10. 完成全量 build、unit tests、fast integration tests，并更新相关架构文档。

## 13. 推荐执行顺序

实际开发建议按以下 PR 顺序推进：

1. `timing-spi`: `EasingId`、registry、built-ins、插件 registration facade；
2. `transition-spec-easing-id`: TransitionSpec/command arguments/beat defaults 迁移；
3. `godot-easing-adapter`: 移出 decomposition 和 Tween 映射；
4. `transition-id-defaults`: 配置和 policy 统一开放 transition ID；
5. `direction-plan-split`: domain plan 与 execution plan 拆分；
6. `compatibility-cleanup`: obsolete、迁移工具、文档和最终删除。

每个 PR 必须包含对应的 headless test；涉及 Godot adapter 的 PR 额外包含 fake backend 或最小场景测试。不要把所有类型移动和所有兼容删除合并成一个无法回滚的大 PR。


## 14. 实施补充与当前后端能力

- 新配置 ID 的空字符串表示未设置，此时读取旧 enum；显式字符串优先。不可用非空字段初始值覆盖旧资源中的配置。
- easing 优先级为命令参数、beat defaults、配置 defaults、linear；启动期验证配置 easing 是否注册。
- layout 属于 Screen domain，其 Move 是后端原生能力，不要求 Move driver 注册。
- Fade/Dissolve/Shader driver 通过 `VisualTransitionContext.AnimateProgress` 获取已适配进度；支持该入口的插件 driver 应声明 `SupportsCustomEasing = true`。未声明的 driver 在 prepare 阶段拒绝自定义 easing。不要直接使用原始 Tween 进度并同时声明支持。
- 角色 AnimationSpec 目前仍只支持内置枚举曲线，自定义 easing 在角色 prepare 阶段明确拒绝，不会降级为 Linear。开放角色采样需要后续迁移 Actors 动画接口。
- `DirectionPlan.ViewPoseIntents` 保留 cut → move 的中间姿态；创建执行计划时从该意图恢复 ViewSequences。纯 planner 的命名空间及执行元数据生成仍需进一步拆分，当前不应将 Phase 5 宣称为全部完成。
