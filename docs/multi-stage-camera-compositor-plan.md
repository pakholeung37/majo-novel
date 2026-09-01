# Direction Transaction Runtime：Multi-Stage、Multi-View 与 Yarn API 实施方案

## 1. 文档状态

- 状态：Draft 1.0
- 目标：以 Script Writer 体验为优先，破坏性重构现有 Stage、Camera 与 Yarn staging command 执行模型
- 核心能力：显式 Beat 事务、Multi-Stage、Multi-View、Screen Layout、Backdrop/CG 分离、原子 Prepare/Commit、Rollback、Hot Reload
- Authoring 原则：inline-first、node-local、name-on-reuse、资源路径隐式解析
- 兼容策略：不以旧 staging command 或旧存档 schema 兼容为设计约束
- 实施进度（2026-09-01）：Phase A 的纯 Direction Core、Planner、Prepare/Commit 协调器、显式 Beat runtime 与 fake backend 单测已完成
- Phase B 进度（2026-09-01）：独立 StageViewport → ViewViewport → Screen 的单 Stage/单 View Godot render chain、Backdrop/CG typed texture Prepare、Pose/Cut/Move/Dissolve、Curtain backend，以及 Actor `place/leave` 的完整隐藏树 Prepare、cut root swap、actor-level move/fade/finish 已落地；`backdrop/place/leave/shot pose/shot cg/beat` 已注册到 Yarn adapter，Bootstrap 已装配 Direction service graph，真实 Yarn headless vertical slice 已覆盖显式 Beat 原子提交、单命令事务、CG/Stage 往返和 Actor transition。Phase B 完成，下一阶段进入 Multi-Stage
- Phase C 进度（2026-09-01）：Stage Registry、独立 Stage SubViewport/Backdrop/Actor tree、`ActorAddress`、可配置 Stage limit、`stage:` node-local qualification 和 compiler-facing explicit import resolver 已落地；main View 已能在多个 Stage 间顺序切换并保留 dormant Stage 状态。真实 Yarn headless 测试覆盖同 Actor ID 跨 Stage 隔离、切入/返回和失败 Prepare 清理。Phase C runtime vertical slice 完成，下一阶段进入 Multi-View/Layout
- 相关文档：
  - [`camera-runtime-next-phases.md`](camera-runtime-next-phases.md)
  - [`archive/camera-view-transition-design.md`](archive/camera-view-transition-design.md)
  - [`asset-hot-reload.md`](asset-hot-reload.md)

本文取代旧版“Stage / Camera Rig / Output / Composition 直接映射为 Yarn Command”的方案。

## 2. 结论摘要

本次改造不建设一门要求作者预先填写 Stage、Shot 和 Look 资源的 Blocking DSL。

改造后的系统分为两层：

```text
Script Writer
  backdrop / place / leave / shot / layout / beat
                         │
                         ▼
Direction Runtime
  Stage / View / Screen / DirectionPlan / CommitToken
```

作者只描述：

- 舞台环境变成什么。
- 哪个 Actor 处于什么目标状态。
- 某个 View 使用哪个 Source 和 Pose。
- Screen 如何排列多个 View。
- 哪些变化必须在同一个显式 Beat 中原子提交。

Runtime 负责：

- 资源解析和准备。
- Stage、Actor、View 与 Screen 的稳定状态。
- 事务合并、冲突检测和依赖排序。
- 动画统一启动。
- Render-ready 和无黑帧提交。
- Skip、Interrupt、Rollback、Save/Load 和 Hot Reload。

## 3. 设计原则

### 3.1 Writer 体验优先

必须满足：

1. 普通单 Stage、单 View、单 Screen 场景不需要任何声明。
2. 一次性 Stage、站位、Shot 和 CG 直接写在 Yarn body。
3. 只有复用时才建立 local mark、Shot alias 或 import。
4. Yarn 不出现资源路径、texture bounds、ready fence、CommitToken、lease 或 backend 名称。
5. staging command 不携带 `wait:false`、`group:` 和 `operation:`。
6. 多 View、Skip、Interrupt、Rollback 和 Save/Load 不要求剧本编写补偿逻辑。
7. 复杂声明必须 colocate 在使用它的 Yarn Node 中。

### 3.2 不推断时间意图

- 不存在隐式 Beat。
- 相邻 staging command 不自动合并。
- Beat 必须显式 `begin/end`。
- Beat 外的单条 staging command 各自作为单指令事务执行。
- 顺序演出使用多个 Beat 或明确的等待边界，不由 Runtime 猜测。

### 3.3 状态与机制分离

- Stage、View 和 Screen 是持久状态。
- Shot 是一次 `ViewPatch` 或可选复用 recipe，不是持久 Camera 对象。
- Camera 是 Stage Source 的 backend 实现机制，不进入普通 Yarn API。
- Layout 是 Screen 的作者层动作词；`ScreenState` 不机械地映射为 `screen` Command。
- Blocking 仅指 Actor 在 Stage 中的状态，不作为整个 Runtime 的总称。

### 3.4 精确 Pose 优先

第一版 Shot 只支持明确 Pose：

- `center`
- `zoom`
- `rotation`
- `via`
- `over`
- `ease`

第一版不包含 `focus`、`framing`、Actor bounds 驱动的自动构图、自动多人构图或 Follow Driver。这些能力不得成为第一版数据模型、事务模型或 backend 接口的前提。

### 3.5 资源寻址保持隐式

Yarn 写语义 ID：

```yarn
<<backdrop Background_001_001>>
<<place Ema look:"Fearful1,ArmR1">>
<<shot cg Still_490_001>>
```

内部转换：

```text
semantic ID
  ↓
typed ResourceKey
  ↓
IResourcePathResolver / Actor Manifest / LayerCompositionResolver
  ↓
backend resource
```

Direction Runtime 不建立第二套资源路径规则。

## 4. 领域模型

### 4.1 总体关系

```text
Stage Registry
├── Stage: main
│   ├── Backdrop
│   ├── Actors / Blocking
│   └── Stage Effects
│             │
│             └── Camera Backend ────┐
│                                    │
├── Stage: broadcast                 │
│   ├── Backdrop                     │
│   └── Actors                       │
│             │                      │
│             └── Camera Backend ────┤
│                                    ▼
CG Resource ─────── Plate Backend → View Registry
                                     │
                                     ▼
                                  Screen
                                     │
                                     ▼
                              Screen Effects / UI
```

### 4.2 Stage

Stage 是一份独立、可同时存在的故事空间状态，拥有 Backdrop、Stage-scoped Actor instances、Actor Blocking、参考坐标空间和 Stage effects。Stage 不拥有 Camera Pose、CG 或最终 Screen Layout。

```csharp
public readonly record struct StageId(string Value);

public sealed record StageState(
    StageId Id,
    BackdropState? Backdrop,
    IReadOnlyDictionary<ActorId, ActorState> Actors,
    StageEffectState Effects);

public readonly record struct ActorAddress(
    StageId StageId,
    ActorId ActorId);
```

同一个 Actor ID 可以同时存在于多个 Stage，并保存完全独立的状态。

### 4.3 Backdrop

Backdrop 是 Stage 环境，不是普通 Actor，也不是 CG。

```csharp
public sealed record BackdropState(
    ResourceKey Resource,
    BackdropFit Fit,
    Color Tint);
```

规则：

- 同一 Stage 的所有 View 观察同一个 Backdrop。
- 改变 Backdrop 不销毁或隐藏 Actor。
- 切走 View 不改变 Backdrop。
- Backdrop 资源由 typed Resolver 解析。
- cover、bleed 和 transition 属于 backend 细节。

### 4.4 View

View 是一条持续存在的观察通道。

```csharp
public readonly record struct ViewId(string Value);

public abstract record ViewSource;
public sealed record StageViewSource(StageId StageId) : ViewSource;
public sealed record CgViewSource(ResourceKey Resource) : ViewSource;

public sealed record ViewPose(
    Vector2 Center,
    float Zoom,
    float RotationDegrees);

public sealed record ViewState(
    ViewId Id,
    ViewSource Source,
    ViewPose Pose);
```

View 的稳定状态拥有 Source 和目标 Pose，不拥有 Actor、Backdrop、屏幕矩形或 Layout slot。CurrentPose、transition clock 和中间 render state 属于运行中的事务，不写入 `DirectionState`。未被 Screen 引用的 View 仍保留逻辑状态，但不必持续占用渲染资源。

### 4.5 Shot

Shot 是一次 View 改变，不是另一个 Registry 实体。

```csharp
public sealed record ViewPatch(
    ViewId ViewId,
    Optional<ViewSource> Source,
    Optional<Vector2> Center,
    Optional<float> Zoom,
    Optional<float> RotationDegrees,
    ViewTransitionSpec Transition);
```

一次性 Shot 直接产生匿名 `ViewPatch`。使用 `as:` 时，Node Direction Module 保存可复用 recipe；`shot use` 再次实例化该 recipe。

### 4.6 Screen

系统默认只有一个最终 Screen。Screen 保存 Layout 与 slot routing：

```csharp
public sealed record ScreenState(
    LayoutId Layout,
    IReadOnlyDictionary<LayoutSlotId, ViewId> Bindings);
```

Screen 不理解 Stage、Actor、CG 或 Camera，只引用 View。默认状态为 `single` Layout 的 `main` slot 绑定 `main View`。

### 4.7 CG

CG 是已经完成构图的视觉 Plate，通过 `CgViewSource` 进入 View。CG 不是 Stage Backdrop、Background Actor、特殊 Stage 或 Screen overlay。

规则：

- 全屏 CG 可以复用默认 `main View`。
- CG 与 Stage 同时显示时使用两个 View。
- 切入 CG 不修改任何 Stage 或 Actor。
- 返回 Stage 时原 Stage 状态持续存在。
- CG 的 center、zoom 和 rotation 属于 View Pose。

## 5. Stage 与 View 的建立规则

### 5.1 默认实例

每个播放 Session 自动存在 `main Stage`、`main View` 和 `main Screen`。`main View` 初始引用 `main Stage`，`main Screen` 的 `main` slot 初始绑定 `main View`。

### 5.2 建立 Stage

具名 Stage 由第一次 Stage-owned 写入建立：

```yarn
<<backdrop Background_001_001 stage:cell>>
<<place Ema stage:cell at:center>>
```

`backdrop` 和 `place` 可以定义 Stage；`leave`、`shot` 和 `layout` 只能引用已定义或 import 的 Stage。同一显式 Beat 内的定义和引用由 Planner 排序，不依赖文本顺序。

### 5.3 建立 View

具名 View 由第一次带 `view:` 的 Shot 建立：

```yarn
<<shot pose view:wide stage:cell center:50,50 zoom:1>>
```

`layout` 只能引用已经由 Shot 建立或 import 的 View。未知 View 是 authoring error，不能因拼写错误静默建立。

### 5.4 选择规则

```text
需要独立世界状态       → 新 Stage
需要独立观察通道       → 新 View
只是改变观察方式       → 同一 View 的新 Shot
只是改变最终排列       → 新 Layout
```

| 场景 | Stage | View |
|---|---:|---:|
| 同一房间顺序切换两个 Pose | 1 | 1 |
| 同一房间同时显示全景和特写 | 1 | 2 |
| 牢房与直播间顺序切换 | 2 | 1 |
| 牢房与直播间同时分屏 | 2 | 2 |
| 全屏 CG 后返回 Stage | 原 Stage 保留 | 1 |
| Stage 主画面加 CG 画中画 | 原 Stage 保留 | 2 |

## 6. Node Direction Module

Direction Module 是编译器从 Yarn Node 推导的内部产物，不是作者维护的 JSON 或外部资源。

```csharp
public sealed record NodeDirectionModule(
    string NodeId,
    long Version,
    DirectionSymbolTable Symbols,
    IReadOnlyCollection<TypedResourceReference> Resources,
    IReadOnlyCollection<DirectionSourceMap> SourceMaps);
```

它负责 local mark、local Shot alias、local Stage/View symbol、explicit import、typed resource references、Yarn source location 和 preload 候选集合。

规则：

- 未限定的具名 Stage、View、Mark 和 Shot alias 默认 Node-local。
- `main Stage/View/Screen` 是 Session 默认实例。
- 跨 Node 共享必须显式 import。
- Local alias 不写入 Save；Save 保存解析后的稳定状态。
- 没有强制 Direction declaration block。
- 一次性数据直接 inline。
- 编译器可以按值 intern 匿名 Shot，但不能要求作者为性能命名。

内部完全限定示例：

```text
Act01_Chapter01_Adv02/stages/cell
Act01_Chapter01_Adv02/views/warden
Act01_Chapter01_Adv02/marks/left
Act01_Chapter01_Adv02/shots/cell-wide
```

## 7. Yarn API

### 7.1 通用约定

```text
stage:<id>       目标 Stage，默认 main
view:<id>        目标 View，默认 main
via:<kind>       过渡方式
over:<seconds>   过渡时长
ease:<curve>     动画曲线
join:<barrier>   commit 或 settle
```

规则：

- 资源使用 primary semantic ID，不写路径。
- 时间统一使用 `over:`，不再混用 `duration:`。
- 动画曲线统一使用 `ease:`，不再混用 `curve:`。
- 单指令事务默认 `join:commit`。
- `join:settle` 等待动画完成。
- 需要异步开始后稍后 join 的多指令演出必须使用具名 Beat。

### 7.2 `backdrop`

```yarn
<<backdrop Background_001_001>>
<<backdrop Background_025_001 stage:broadcast via:dissolve over:0.4>>
```

产生 `BackdropPatch`，修改目标 Stage 的 Backdrop。

### 7.3 `place`

```yarn
<<place Ema at:left look:"Fearful1,ArmR1">>
<<place Hiro stage:cell at:70,0 look:Angry2>>
<<place Ema at:right via:move over:0.3 ease:cubic-out>>
```

产生稀疏 `ActorPatch`。Actor 不存在时建立 Stage-scoped instance；Actor 已存在时只修改明确字段；未提供字段保持稳定目标不变。`look:` 交给 Actor Manifest 和 LayerCompositionResolver。

第一版参数：

```text
at:<mark|x,y>
look:<appearance-id>
scale:<number>
z:<integer>
via:<cut|move|fade>
over:<seconds>
ease:<curve>
```

### 7.4 `leave`

```yarn
<<leave Hiro>>
<<leave Guard stage:corridor toward:right over:0.4>>
```

`leave` 表示 Actor 真正离开 Stage。未被当前 View 拍到不是 `leave`；对未知 Actor 执行 `leave` 是 authoring error。

### 7.5 `shot pose`

```yarn
<<shot pose center:35,50 zoom:2 rotation:0 via:cut>>
<<shot pose stage:cell center:50,50 zoom:1 via:dissolve over:0.4>>
<<shot pose view:reaction stage:cell center:70,45 zoom:1.8>>
```

修改目标 View。`stage:` 存在时将 Source 改为对应 Stage；省略 Source 时保持当前 Source；未提供的 Pose 字段保持不变。新建具名 View 时必须提供 Source，缺省 Pose 使用 `ViewPose.Default`。

### 7.6 `shot cg`

```yarn
<<shot cg Still_490_001 via:cut>>
<<shot cg Still_490_001 center:37.384,74.816 zoom:3 rotation:10>>
<<shot cg Evidence_001 view:evidence>>
```

将目标 View Source 改为 CG；Stage 不被修改；Pose 作用于 Plate backend。

### 7.7 `shot use`

```yarn
<<shot pose stage:cell center:50,50 zoom:1 as:cell-wide>>
...
<<shot use cell-wide>>
<<shot use cell-wide view:preview via:dissolve>>
```

Alias 保存 ViewPatch recipe，不保存运行时 View 实例。

### 7.8 `layout`

```yarn
<<layout single main:main>>
<<layout split main:cell inset:warden via:dissolve over:0.3>>
<<layout pip main:main inset:evidence>>
```

产生 `ScreenPatch`。第一个参数是 Layout semantic ID，后续 `slot:view` 绑定必须覆盖 required slots。未知 slot、重复 slot 和未知 View 都是 authoring error。Layout preset 由 `ILayoutResolver` 解析，Yarn 不写矩形和裁剪路径。

### 7.9 `beat`

```yarn
<<beat begin id:hiro-descends over:0.3 ease:cubic-out>>
<<place Hiro at:left look:Surprised1>>
<<place Ema at:right look:Fearful1>>
<<shot pose center:55,50 zoom:1.2 via:move>>
<<beat end join:settle>>
```

异步开始：

```yarn
<<beat begin id:slow-pan over:4>>
<<shot pose center:75,50 via:move>>
<<beat end join:commit>>

Narrator: 镜头继续移动。

<<beat join slow-pan>>
```

控制命令：

```text
beat begin
beat end
beat join
beat finish
beat abort
```

Beat 不能嵌套或跨 Node。Choice、jump、stop、save 等控制流命令不能出现在开放 Beat 内。Beat 级 `over/ease` 作为命令缺省值，命令可以明确覆盖。

### 7.10 `mark`

```yarn
<<mark witness at:37,0>>
<<mark presenter stage:broadcast at:50,40>>
<<place Ema at:witness>>
```

Mark 属于 Stage 坐标空间，默认 Node-local。

### 7.11 瞬态 Cue

```yarn
<<shake strength:8 axis:x over:0.4>>
<<flash intensity:0.8 over:0.15>>
<<curtain open over:1.2>>
```

高频 Cue 直接使用动词，不要求 `cue` 包装层。Cue 在 Beat 中只在 Commit 成功后触发，不进入稳定 DirectionState。

## 8. Directive、State 与 Plan

### 8.1 Directive

```csharp
public interface IDirectionDirective;
public interface IDurableDirectionPatch : IDirectionDirective;
public interface ITransientDirectionCue : IDirectionDirective;

public sealed record BackdropPatch(...) : IDurableDirectionPatch;
public sealed record ActorPatch(...) : IDurableDirectionPatch;
public sealed record ViewPatch(...) : IDurableDirectionPatch;
public sealed record ScreenPatch(...) : IDurableDirectionPatch;
public sealed record ShakeCue(...) : ITransientDirectionCue;
public sealed record FlashCue(...) : ITransientDirectionCue;
```

Yarn staging command 不直接调用 Stage、Actor 或 Camera service，只产生 Directive。

### 8.2 DirectionState

```csharp
public sealed record DirectionState(
    IReadOnlyDictionary<StageId, StageState> Stages,
    IReadOnlyDictionary<ViewId, ViewState> Views,
    ScreenState Screen,
    StableEffectState Effects,
    long Revision);
```

Runtime 同时维护 CurrentState 和 TargetState。Save、Skip 和新 Beat merge 以 TargetState 为主；新 transition 从当前实际帧接管。

### 8.3 Plan

```csharp
public sealed record DirectionPlan(
    DirectionTransactionId Id,
    DirectionState BeforeState,
    DirectionState TargetState,
    IReadOnlyList<DirectionTransition> Transitions,
    IReadOnlyList<ITransientDirectionCue> Cues,
    IReadOnlyList<ResourceClaim> ResourceClaims,
    DirectionCompletionBarrier Completion);
```

```text
Directives
  ↓
Resolve scope and aliases
  ↓
Merge sparse patches
  ↓
Validate conflicts and references
  ↓
Build complete TargetState
  ↓
Derive resource claims and dependencies
  ↓
DirectionPlan
```

### 8.4 合并与冲突

同一 Beat 中，不同字段可以合并；同一路径被写入两次默认报错。`leave` 与同一 Actor 的 `place` 冲突；`shot cg` 与同一 View 的 Stage Source 写入冲突；`layout` 写入两次冲突。禁止使用“最后一条获胜”。

稳定路径：

```text
stage/<stage>/backdrop
actor/<stage>/<actor>/presence
actor/<stage>/<actor>/position
actor/<stage>/<actor>/appearance
actor/<stage>/<actor>/scale
actor/<stage>/<actor>/z
view/<view>/source
view/<view>/center
view/<view>/zoom
view/<view>/rotation
screen/layout
screen/slot/<slot>
effect/curtain
```

## 9. 事务执行模型

### 9.1 阶段

```text
Collect → Plan → Prepare → Commit → Transition → Settle
```

### 9.2 Prepare

Prepare 必须无可见副作用，负责解析资源、加载 Backdrop/CG/Appearance、建立隐藏 Stage backend、申请 View lease、准备 dissolve source/target、验证 Layout capacity 并等待目标 View ready。

```csharp
public sealed record PreparedDirectionPlan(
    DirectionPlan Plan,
    IReadOnlyList<IPreparedDirectionResource> Resources,
    IReadOnlyList<IRenderReadyFence> ReadyFences);
```

任一 Prepare 失败时，BeforeState 保持不变，释放临时资源，并且不触发 Cue。

### 9.3 Commit

Commit 是同步、短暂、不可 await 的可见状态切换点：

```csharp
public interface IDirectionCommitCoordinator
{
    CommittedDirectionTransaction Commit(
        PreparedDirectionPlan plan,
        DirectionCommitToken token);
}
```

Commit 同时发布 Actor、Backdrop、View Source/TargetPose、Screen Layout/bindings，启动 transition clock，更新 DirectionState revision，并允许 Cue 开始。

“同一 CommitToken”表示同一逻辑和渲染提交边界，不承诺所有 Godot 节点调用发生在同一个 CPU 指令中。

### 9.4 Transition、Finish 与 Rollback

- `join:commit` 在 Commit 完成后返回。
- `join:settle` 等待所有 transition 到达 TargetState。
- `finish` 立即提交已 Commit 事务的 TargetState。
- `abort` 撤销尚未 Commit 的事务并释放 Prepare 资源。
- `rollback` 对已 Commit 事务应用 BeforeState 恢复事务。
- `replace` 从当前实际帧接管冲突路径。
- `skip` finish 所有 skippable 事务。

Rollback 只对稳定视觉状态提供保证。已播放 SFX、已被玩家感知的 flash 等瞬态副作用不可逆。

## 10. Render Backend

```csharp
public interface IStageRenderBackend
{
    Task<IPreparedStage> PrepareAsync(
        StageState target,
        CancellationToken cancellationToken);
}

public interface IViewRenderBackend
{
    Task<IPreparedView> PrepareAsync(
        ViewState target,
        CancellationToken cancellationToken);
}

public interface IScreenCompositorBackend
{
    Task<IPreparedScreen> PrepareAsync(
        ScreenState target,
        CancellationToken cancellationToken);
}
```

Core 不依赖 Godot Node、SubViewport、TextureRect 或具体裁剪实现。

规则：

- 一个 Stage 只拥有一份 Actor Tree。
- 同一 Stage 的多个 View 不复制 Actor Tree。
- Camera backend 将 ViewPose 映射为 Stage 观察结果。
- Plate backend 直接渲染 CG，不创建 Actor Tree。
- 逻辑 View 可以在没有 render lease 时存在。
- 被 Screen 引用或参与 transition 的 View 才持有 lease。
- Reset、Load、Rollback 和 Shutdown 必须清理临时 lease/capture。

实施多 View 前必须验证同 Stage 多观察、跨 Stage 隔离、Godot layer 排序、Stage/CG 统一参考分辨率、dissolve 中断、窗口 resize 和 headless lifecycle。

## 11. Preload 与资源推导

Node Direction Compiler 收集 `backdrop`、`place look:`、`shot cg`、`layout` 和 import 的 typed resource references，生成 `DirectionPreloadManifest`。

规则：

- 作者不维护资源表。
- 静态引用在 Node 进入前预加载。
- 变量生成的动态引用在 Beat Prepare 阶段解析。
- 静态扫描可以保守多加载，但不得漏掉确定引用。
- Hot Reload 只增量准备新增资源。

## 12. State、Save/Load 与 Hot Reload

保存：

- Stage Registry、Backdrop 和 Actor target state。
- View Registry、ViewSource 和 TargetPose。
- Screen Layout 和 bindings。
- 稳定 Curtain/Screen Effect。
- DirectionState revision 和必要 provenance。

不保存 tween 中间值、shake offset、dissolve weight、capture texture、ready fence、lease、Prepare 临时资源和 Node-local alias 表。

Load 流程：

```text
清理现有 transition
  ↓
解析和验证 snapshot
  ↓
Prepare Stage / Actor / View / Screen
  ↓
原子 Commit snapshot TargetState
  ↓
恢复稳定 Effect 和 UI
```

Hot Reload 以 Node Direction Module 为编译单位。新版本必须完成解析、引用验证和资源 Prepare 后，才替换未来指令使用的 Module。需要重放当前 Beat 时，生成从当前实际状态到新版 TargetState 的新事务；失败时保留旧 Module 和旧画面。

## 13. Destructive Refactor 策略

删除或替换：

- Command bind 后立即调用 Actor/Camera service。
- `AwaitScopeCoordinator` 对已启动 Operation 的事后收集。
- staging command 的 `wait/group/operation`。
- Backdrop 作为 Background Actor。
- Still/CG 复用 Backdrop Actor。
- 唯一 Camera State 和唯一 Live Output 假设。
- `camera create/switch/compose` 作者 API。

保留并适配：

- Actor Manifest。
- LayerCompositionResolver。
- IResourcePathResolver 与 ResourceKey。
- MotionCurve/AnimationSpec 中与新 transition 契约一致的部分。
- Audio、Dialogue、UI 和非 staging commands。
- State Manager 的 contributor/preflight 思路。
- 可复用的 Godot Texture/Transition backend。

新 vertical slice 成立后直接迁移 demo Yarn 并删除旧 staging handler，不建设长期双轨兼容桥。

### 13.1 目标代码边界

```text
Runtime/Direction/
├── Core/          IDs、State、Directive、Patch、Plan、validation
├── Execution/     Beat collector、Planner、Prepare、Commit、transaction registry
├── Rendering/     Stage/View/Screen backend interfaces、ready fence、lease contracts
└── Godot/         Stage renderer、Camera/Plate View renderer、Screen compositor

Runtime/Dialogue/Direction/
├── Yarn command adapters
├── Node Direction Compiler
├── Node-local symbol/import resolver
└── preload/source-map diagnostics
```

依赖方向必须保持：Dialogue adapter → Direction Core/Execution；Execution → Core 与 Rendering interfaces；Godot → Core/Rendering；State contributor → Direction snapshot API。Core 不依赖 Yarn、Godot Node、现有 command registry 或 save schema。

### 13.2 现有实现迁移映射

| 现有实现 | 处理方式 |
|---|---|
| `RuntimeCommandRegistry` / `RuntimeCommandDispatcher` | 保留通用注册与 Yarn 调用桥；Direction handler 只提交 Directive，不直接调用 Actor/Camera service |
| `AwaitScopeCoordinator` / `await` handlers | 不再参与 staging；若 Audio 等非 staging command 仍需要，则缩减为其私有机制，不让它进入 Direction transaction |
| `ActorCommandService` | staging 路径由 ActorPatch + Planner 取代；仅保留不属于 Direction 的职责，之后删除无调用部分 |
| `CameraCommandModule` / `ICameraManager` / `CameraManager` | 由 `shot` adapter、ViewState、Planner 和 View backend 取代 |
| `GodotStageBackend` | 拆出 Stage renderer、Stage Camera View renderer 和可复用 transition primitives；不得继续保存唯一 Camera/Output 状态 |
| `BackgroundActor` / `BackgroundActorFactory` | 删除；以 Stage-owned Backdrop renderer 取代 |
| `BuiltInStateContributors` 中 Camera/Actor staging payload | 迁移为单一 DirectionState schema 与 transactional load |
| `MajoRuntimeBootstrap` | 改为装配 Direction Core、Execution、backend pool、Yarn adapters 和 state contributor |

迁移期间允许短期编译中间态，但每个 vertical slice 合并时只能有一个 staging 执行入口。旧 handler 与新 handler 不得同时注册同一作者命令。

## 14. 实施阶段

### Phase A：Direction Core 与事务内核

交付 DirectionState、Core records、Directive、Patch merge、冲突检测、DirectionPlan、Prepare/Commit/Settle、fake backend、Finish/Abort/Rollback/Skip。

验收：Prepare 失败不改变 BeforeState；同一路径冲突提供 Yarn source location；Commit 单次递增 revision；commit/settle 可测试；Rollback 恢复完整 BeforeState。

### Phase B：单 Stage、单 View、Backdrop/CG Vertical Slice

交付默认 main Stage/View/Screen、独立 Backdrop、StageSource/CgSource、ViewPose、Stage/Plate backend，以及 `backdrop/place/leave/shot pose/shot cg/beat`。

验收：CG 切入不改变 Stage；返回时 Actor 状态保留；Backdrop/Actor/Shot 原子发布；Stage/CG dissolve 无黑帧；不读取 Actor bounds。

### Phase C：Multi-Stage

交付 Stage Registry、ActorAddress、Stage lifecycle、`stage:` scope、Node-local qualification、首次写入和 import、Stage limits，并迁移 `Adv02` 中 broadcast 的切入与返回。

验收：同 Actor ID 可存在于不同 Stage；cell/broadcast 顺序切换只需一个 View；切走后状态保持；未知 Stage 不静默创建；Stage Prepare 失败不部分发布。

实施说明：import 的解析内核在本阶段交付，Yarn import 语法由 Phase E 的 Node compiler 生成，不增加临时 runtime command。`Adv02` 的 broadcast 行为已用同构的真实 Yarn vertical slice 验证；原 demo 仍大量使用旧 `back/char/camera/curtain` staging 链路，在 Direction cue 和整段 staging 迁移完成前不做局部替换，以免同一场景同时发布两套输出。

### Phase D：Multi-View 与 Layout

交付 View Registry、具名 View、render lease pool、ScreenState、Layout Resolver、`view:`、`layout` 和 slot binding。

验收：同 Stage 双 View 不复制 Actor Tree；未知 View 编译失败；Split/PIP 全部 ready 后提交；被打断 transition 不闪回；unbind 后释放 lease。

### Phase E：Node Compiler、Preload 与 Alias

交付 NodeDirectionModule、source map、`mark`、Shot `as/use`、import、typed preload 和 authoring diagnostics。

验收：Node-local 同名不冲突；未 import 的跨 Node 引用失败；一次性内容不要求命名；Yarn 不出现物理路径；静态资源集合可推导。

### Phase F：State、Hot Reload、诊断与性能

交付新 state schema、原子 Save/Load、Hot Reload transactional reapply、diagnostics、资源上限和清理测试。

验收：Load 不恢复中间帧；Hot Reload 失败保持旧画面；Reset/Load/Rollback/Shutdown 无资源泄漏；日志可定位 Node、Beat、Stage、Actor、View 和稳定路径。

## 15. 测试策略

### Unit

- StageId、ViewId 和 Node-local qualification。
- Sparse Patch merge 与冲突。
- Stage/View 建立和未知引用。
- Backdrop/CG ownership。
- Shot partial Pose merge。
- Layout slot validation。
- Beat begin/end/join/finish/abort。
- TargetState、BeforeState 和 revision。
- Resource claim、dependency graph 和 state schema。

### Headless

- Prepare 无可见副作用和失败清理。
- Commit 单次性和同 token transition 启动。
- Rollback、Skip、Stage dormant、View lease。
- 同 Stage 多 View 不复制逻辑 ActorState。
- Node abort、Load、Reset 和 Shutdown 清理。

### Visual Smoke

- Backdrop + Actor + Shot Pose 原子首帧。
- CG 全屏进入、Pose move、返回 Stage。
- 同 Stage wide/close 双 View。
- cell/broadcast 跨 Stage split。
- Stage + CG PIP。
- Cut、move、dissolve 和中断。
- Layout resize、rotation、opacity、clip 和不同窗口比例。

### Script Writer 验收

使用简单单人 ADV、Adv02 和真实 multi-view Trial 验证：普通场景无声明样板；一次性内容不命名；显式 Beat 不增加 wait/group/operation；broadcast 返回不重建 Actor；CG 不伪装 Backdrop；multi-view 不暴露 Output/lease；只阅读当前 Yarn Node 即可理解局部演出。

## 16. 首个技术切片

首个切片纵向贯通：

1. 建立纯 Core DirectionState、Patch、Plan 和 CommitToken。
2. 使用 fake backend 验证 Prepare 失败和原子 Commit。
3. 建立默认 main Stage/View/Screen。
4. 将 Backdrop 从 Background Actor 中分离。
5. 实现 StageSource 与 CgSource。
6. 实现 `backdrop/place/shot pose/shot cg/beat` 最小 Yarn adapter。
7. 迁移 Adv02 开场 CG Pose、Camera move 和 Curtain open。
8. 从 CG 切回默认 `main View -> main Stage`，验证 CG 演出未修改 Stage 稳定状态。
9. 验证 Skip、Rollback、Reset 和 Save/Load 稳定终态。

首个切片不包含自动 framing/bounds、具名 View、多 View Layout、Shot alias/import、自定义 clip 或旧 Command 兼容层。只有它证明事务模型、Backdrop/CG 分离和 Writer API 可行后，才进入 Multi-Stage 和 Multi-View。

## 17. 完成标准

1. Writer 可以只用 inline command 完成普通 Node。
2. Beat 原子性覆盖 Actor、Backdrop、View 和 Screen 稳定状态。
3. 相邻命令不会隐式归组。
4. Backdrop 与 CG 使用不同状态所有权和 backend。
5. Multi-Stage 和 Multi-View 可以独立组合。
6. 未知 Stage/View、资源和 Layout slot 在 Commit 前失败。
7. Prepare 失败不产生部分可见状态。
8. Save/Load、Skip、Rollback 和 Hot Reload 使用同一 DirectionState/Plan 模型。
9. Yarn 不出现物理路径、bounds、ready fence、lease、group 或 operation。
10. demo Yarn 已迁移，旧 staging handler 已删除，不存在双轨执行。
