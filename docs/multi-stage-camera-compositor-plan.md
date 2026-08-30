# Multi-Stage、多镜头与镜头合成实施方案

## 1. 文档状态

- 状态：Draft 0.3
- 目标：为 Majo Runtime 增加命名 Stage、多 Camera Rig、Output Routing 和 Composition
- 基线：复用现有 Actor、Camera State、Operation、State 和 Yarn Command 体系
- 相关文档：
  - [`camera-runtime-next-phases.md`](camera-runtime-next-phases.md)
  - [`archive/camera-view-transition-design.md`](archive/camera-view-transition-design.md)

## 2. 目标

系统需要支持：

1. 同时维护多个互相隔离的 Stage。
2. 每个 Stage 维护多个持久命名 Camera Rig。
3. Camera Rig 在不可见时仍可设置和保存 Pose。
4. Output 可以在 Camera Rig 之间切换。
5. 多个 Output 可以同时显示并参与布局、裁剪、叠加和过渡。
6. Stage、Camera Rig、Output 和 Composition 分别保存稳定状态。
7. 保持现有单 Stage、单 Camera 脚本兼容。

本方案不包含具体项目的镜头设计、资源组织和脚本改编规则。

## 3. 核心模型

```text
Stage Registry
├── Stage A
│   ├── Actors
│   ├── Effects
│   ├── Camera Rig A1
│   └── Camera Rig A2
│
└── Stage B
    ├── Actors
    ├── Effects
    └── Camera Rig B1
              │
              ▼
        Output Router
              │
              ▼
         Composition
              │
              ▼
       Screen Effects / UI
```

### 3.1 Stage

Stage 是 Actor 和 Stage Effect 的独立容器，负责：

- Actor 归属与查询。
- 参考分辨率和坐标空间。
- 资源准备与释放。
- Camera Rig 归属。
- 生命周期和稳定状态。

Stage 不负责：

- 决定自己是否显示。
- 决定使用哪台 Camera。
- 管理最终画面布局。
- 管理 Screen Effect 或 UI。

### 3.2 Camera Rig

Camera Rig 是 Stage 内的持久命名视图状态，负责：

- 保存 CurrentPose 和 TargetPose。
- 执行 move、zoom、rotate、pose 和 shake。
- 在没有 Output 引用时保留状态。
- 被多个 Output 引用时提供一致的 Pose。

Camera Rig 不负责：

- 自己的屏幕位置。
- 分屏形状。
- Output 层级和透明度。
- Stage Actor 生命周期。

### 3.3 Output

Output 是最终画面中的一个渲染区域，负责：

- 引用一个 Camera Rig。
- 控制区域、层级、透明度和可见性。
- 应用裁剪和适配策略。
- 参与 Camera Switch 和 Output Transition。

Output 不拥有 Stage 或 Actor。切换 Output Source 不修改源或目标 Camera Rig 的 Pose。

### 3.4 Composition

Composition 是一组 Output 的稳定布局，负责：

- Output 集合。
- Output 的矩形或裁剪形状。
- Output 层级。
- Output Source Routing。
- Composition 切换和过渡。

复杂布局应定义为资源 preset，脚本只选择 preset 和 source。

## 4. 当前基线与缺口

当前 Runtime 已具备：

- Actor 创建、销毁、状态变更和 Operation。
- Camera `center/zoom/rotation` 状态。
- Camera `pose/move/zoom/rotate/reset/shake`。
- 单 Camera Output 的 `cut/move/dissolve`。
- 独立 Stage 渲染边界和 Camera Output。
- Actor 与 Stage 稳定状态存档。
- Operation 的 wait、skip、complete、cancel、group 和 curve。

当前缺口：

- Actor Host 和 Stage Surface 只有一个。
- Actor ID 没有 Stage 作用域。
- Camera Manager 只保存一个 Camera State。
- Camera Transform 直接作用于唯一 Stage Root。
- Output 只有一个稳定 Live Output。
- 没有稳定 Output Routing。
- 没有多 Output Composition。

## 5. Stage Registry

### 5.1 数据模型

```csharp
public enum StageLifecycleState
{
    Prepared,
    Referenced,
    Dormant,
}

public sealed record StageState(
    string Id,
    string? DefinitionId,
    Vector2I ReferenceResolution,
    StageLifecycleState Lifecycle);

public interface IStageManager : IRuntimeService
{
    string DefaultStageId { get; }
    IReadOnlyCollection<StageState> Stages { get; }

    Task CreateAsync(StageCreateRequest request, CancellationToken token);
    Task PrepareAsync(string stageId, CancellationToken token);
    Task UnloadAsync(string stageId, CancellationToken token);
}
```

### 5.2 生命周期

```text
不存在
  ↓ create / prepare
Prepared
  ↓ 被稳定或过渡中的 Output 引用
Referenced
  ↓ 不再被 Output 引用
Dormant
  ↓ unload
不存在
```

规则：

- `Prepared` Stage 已拥有可查询的 Actor 和资源。
- `Referenced` Stage 必须持续产生有效画面。
- `Dormant` Stage 保留稳定状态，但可以暂停无必要的渲染更新。
- `unload` 先处理关联 Operation、Camera Rig 和 Output，再释放资源。
- 不能卸载仍被稳定 Output 引用的 Stage；调用方必须先切换或隐藏 Output。

### 5.3 Actor 地址

Actor 使用 Stage 作用域地址：

```csharp
public readonly record struct ActorAddress(
    string StageId,
    string ActorId);
```

Actor Manager 内部以 `ActorAddress` 作为主键。规则：

- 同一个 ActorId 可以存在于多个 Stage。
- 每个实例拥有独立的 Appearance、Position、Scale、Visibility 和 Operation。
- 未指定 `stage:` 的旧命令使用 `DefaultStageId`。
- 多 Stage 脚本应显式指定 `stage:`。

## 6. Camera Rig Registry

### 6.1 数据模型

继续使用现有 Camera State：

```csharp
public sealed record CameraRigState(
    string Id,
    string StageId,
    CameraState CurrentPose,
    CameraState TargetPose);
```

```csharp
public interface ICameraDirector : IRuntimeService
{
    string DefaultRigId { get; }
    IReadOnlyCollection<CameraRigState> Rigs { get; }

    void Create(CameraRigDefinition definition);
    void Destroy(string rigId);

    IOperationHandle Pose(
        string rigId,
        CameraPoseTransitionRequest request);

    IOperationHandle Move(
        string rigId,
        Vector2 center,
        StageOperationSpec operation);

    IOperationHandle Zoom(
        string rigId,
        float zoom,
        StageOperationSpec operation);

    IOperationHandle Rotate(
        string rigId,
        float degrees,
        StageOperationSpec operation);

    IOperationHandle Shake(
        string rigId,
        CameraShakeSpec shake);
}
```

### 6.2 规则

- Camera Rig 必须归属于一个已存在的 Stage。
- `CurrentPose` 表示当前实际状态。
- `TargetPose` 表示当前 Operation 的稳定终态。
- 没有 Output 引用的 Rig 仍可修改 Pose。
- 没有 Output 引用的 Rig 不应占用 Output 渲染资源。
- 切走后 Rig 保留 Pose。
- 删除 Rig 前必须移除所有稳定和过渡中的 Output 引用。
- 省略 `camera:` 时使用 `DefaultRigId`，保持现有脚本兼容。

## 7. Output Router

### 7.1 数据模型

```csharp
public sealed record CameraOutputState(
    string Id,
    string SourceRigId,
    Rect2 NormalizedRect,
    int ZIndex,
    float Opacity,
    float RotationDegrees,
    OutputClipShape Clip,
    CameraAspectMode AspectMode,
    bool Visible);
```

```csharp
public interface ICameraOutputRouter : IRuntimeService
{
    string MainOutputId { get; }
    IReadOnlyCollection<CameraOutputState> Outputs { get; }

    IOperationHandle Switch(CameraSwitchRequest request);
    IOperationHandle SetVisibility(OutputVisibilityRequest request);
    void ApplyStableRouting(IReadOnlyCollection<CameraOutputState> outputs);
}
```

### 7.2 Switch 语义

`via:cut`：

1. 准备目标 Rig 所属 Stage。
2. 应用目标 Rig 的 TargetPose。
3. 等待目标 Output 产生有效画面。
4. 原子替换 Output Source。

`via:dissolve`：

1. 保留当前 Output Source。
2. 准备目标 Source。
3. 同时渲染 source 和 target。
4. 在 Output Transition 中混合。
5. 完成后提交目标 Source 并释放临时资源。

规则：

- Switch 不修改任意 Rig 的 Pose。
- 新 switch 打断旧 dissolve 时不得闪回旧 source。
- Skip 直接提交目标 source。
- Reset、Load 和 Shutdown 清理所有瞬态 transition。

## 8. Composition

### 8.1 数据模型

```csharp
public sealed record CameraCompositionState(
    string Id,
    IReadOnlyList<CameraOutputState> Outputs);

public sealed record CompositionRequest(
    string PresetId,
    IReadOnlyList<string> SourceRigIds,
    StageOperationSpec Operation);
```

### 8.2 Preset

示例：

```yaml
id: split-overlay
outputs:
  - id: main
    source-index: 0
    rect: [0, 0, 1, 1]
    z-index: 0
    opacity: 1
    clip: rectangle

  - id: inset
    source-index: 1
    rect: [0.62, 0.08, 0.34, 0.36]
    z-index: 10
    opacity: 1
    clip: inset-angle
```

Preset 可以定义：

- Output 数量。
- Source 参数位置。
- Normalized Rect。
- ZIndex。
- Opacity。
- Rotation。
- Clip Shape。
- Aspect Mode。
- 可选装饰资源。

### 8.3 Composition 切换

- Composition Operation 只修改 Output 布局和 routing。
- Composition 不修改 Stage、Actor 或 Camera Pose。
- 过渡开始前所有目标 Output 必须 ready。
- Skip 直接提交目标 Composition。
- Cancel 默认落到目标稳定 Composition。
- Reset、Load 和 Shutdown 移除瞬态 Output，并释放租约。

## 9. Render Backend

### 9.1 目标边界

```text
Stage Surfaces
       │
       ▼
Camera Output Pool
       │
       ▼
Stage Compositor
       │
       ▼
Screen Effects / UI
```

Backend 负责：

- Stage Surface 的创建、准备、休眠和释放。
- Camera Rig Pose 到实际视图状态的映射。
- Output 资源的按需分配。
- Output Texture、Clip 和 Layout。
- render-ready 通知。
- transition capture 和资源回收。

Core 不依赖具体节点、纹理或裁剪实现。

### 9.2 资源约束

- Stage 内容只有一份，不因 Camera Output 数量而复制 Actor Tree。
- 只为可见或过渡中的 Output 分配渲染资源。
- 不可见 Camera Rig 只保存逻辑状态。
- Output 隐藏后释放对应租约。
- Transition capture 必须有明确的 Complete、Skip、Cancel、Reset、Load、Shutdown 回收路径。
- 同时活跃的 Stage 和 Output 数量由配置限制。

### 9.3 Render-ready

Backend 提供显式 ready 机制：

```csharp
public interface ICameraOutputBackend
{
    Task PrepareAsync(
        string outputId,
        string rigId,
        CancellationToken token);

    Task WaitUntilReadyAsync(
        string outputId,
        CancellationToken token);
}
```

脚本不需要等待固定帧数。Switch 和 Compose 在内部等待目标 Output ready。

### 9.4 技术验证

实施 Output Pool 前先验证：

1. 同一 Stage 内容能否由多个 Output 同时观察而不复制 Actor Tree。
2. 不同 Stage 能否保持渲染和状态隔离。
3. Control、CanvasLayer、Sprite、Layered Character 和 Effect 的排序是否一致。
4. Output resize、rotation 和 clipping 是否保持参考坐标规则。
5. Headless 环境下是否存在确定性的生命周期验证路径。

如果首选实现无法满足条件，备选实现仍必须遵守相同 Core 接口和“不复制 Actor Tree”的约束。

## 10. Yarn API

### 10.1 Stage

```yarn
<<stage create id:stage-a definition:scene-a>>
<<stage create id:stage-b definition:scene-b>>

<<back stage:stage-a actor:background appearance:default duration:0>>
<<character stage:stage-a actor:Alice appearance:Idle position:40,100>>
<<character stage:stage-b actor:Bob appearance:Idle position:60,100>>

<<stage unload id:stage-b>>
```

### 10.2 Camera Rig

```yarn
<<camera create id:wide stage:stage-a preset:wide>>
<<camera create id:closeup stage:stage-a from:wide>>
<<camera create id:reaction stage:stage-b preset:closeup>>

<<camera pose camera:closeup center:62,54 zoom:1.8 via:cut>>
<<camera move camera:wide to:75,50 duration:4 curve:sine-in-out>>
<<camera zoom camera:closeup to:2 duration:0.5 curve:sine-out>>
```

### 10.3 Output 与 Composition

```yarn
<<camera switch output:main to:closeup via:cut>>
<<camera switch output:main to:wide via:dissolve duration:0.6>>

<<camera compose preset:split-overlay sources:wide,reaction duration:0.35>>
<<camera compose preset:single sources:wide duration:0.2>>
```

## 11. Operation 通道

```text
stage/<id>/lifecycle
actor/<stage>/<actor>/position
actor/<stage>/<actor>/appearance
actor/<stage>/<actor>/visibility
rig/<id>/center
rig/<id>/zoom
rig/<id>/rotation
rig/<id>/modifier/shake
output/<id>/source
output/<id>/visibility
composition/layout
```

冲突规则：

- 同一 Rig 的不同 Pose 属性可以并行。
- 同一属性的新 Operation 从当前实际值接管。
- Output Switch 独占对应 `output/<id>/source`。
- Composition Operation 独占 `composition/layout` 和涉及的 Output 布局属性。
- Stage unload 独占该 Stage 生命周期，并在释放前处理所有关联 Operation。
- Camera shake 是瞬态 modifier，不改变 TargetPose。

## 12. State

### 12.1 保存

- Stage Registry 和各 Stage 稳定生命周期状态。
- Stage-scoped Actor 稳定目标状态。
- Camera Rig Registry 和各 Rig TargetPose。
- 稳定 Output Routing。
- 稳定 Composition。
- 现有 Curtain、Screen Effect 和 Stage Transition 状态。

### 12.2 不保存

- Tween 中间值。
- Shake offset。
- Output ready fence。
- 临时 capture texture。
- Transition 混合权重。
- Output lease。
- Operation handle。

### 12.3 恢复顺序

1. 取消 Camera、Output 和 Composition Operation。
2. 清理 transition、capture 和 Output lease。
3. 恢复 Stage Registry。
4. 恢复 Actor。
5. 恢复 Camera Rig 和 TargetPose。
6. 恢复稳定 Composition 与 Output Routing。
7. 等待可见 Output ready。
8. 恢复 Screen Effect 和 UI。

### 12.4 Schema

- `majo.actors` entry 增加 `StageId`。
- `majo.stage` 增加 Stage Registry、Rig Registry、Output Routing 和 Composition。
- 如果开发期没有旧存档兼容要求，直接拒绝旧 schema；否则单独提供迁移。

## 13. 配置

建议新增：

```csharp
public sealed class StageConfiguration
{
    public int MaxPreparedStages { get; set; } = 4;
    public int MaxActiveOutputs { get; set; } = 2;
    public string DefaultStageId { get; set; } = "main";
    public string DefaultRigId { get; set; } = "main";
    public string DefaultOutputId { get; set; } = "main";
    public string DefaultCompositionId { get; set; } = "single";
}
```

超出限制时产生 authoring error，不静默驱逐仍被引用的 Stage 或 Output。

## 14. 实施阶段

### Phase A：Stage Registry 与 Actor Scope

交付：

- `IStageManager` 和 Stage Registry。
- 默认 `main` Stage。
- `ActorAddress`。
- Actor Manager 使用 Stage-scoped key。
- Stage prepare、dormant 和 unload。
- 旧命令默认路由到 `main` Stage。

验收：

- 多个 Stage 可以同时 Prepared。
- 相同 ActorId 可以存在于不同 Stage。
- Stage 状态互相隔离。
- Stage unload 不遗留 Actor、资源或 Operation。
- 现有脚本和测试保持通过。

### Phase B：Camera Rig Registry

交付：

- `ICameraDirector` 和 Rig Registry。
- 默认 `main` Rig。
- 现有 Camera Operation 支持 `camera:<id>`。
- 隐藏 Rig 的 Pose 更新和保存。
- Rig 生命周期与 Stage 生命周期关联。

验收：

- 多个 Rig 独立保存 Pose。
- 切走再切回不重置 Pose。
- 修改隐藏 Rig 不改变当前 Output。
- Stage unload 正确处理所属 Rig。

### Phase C：Output Router

交付：

- `ICameraOutputRouter`。
- 默认 `main` Output。
- Output Pool 和 lease。
- `camera switch via:cut|dissolve`。
- render-ready barrier。
- Output Routing 稳定状态。

验收：

- 首次切换不显示旧 Pose 或空白画面。
- Switch 不改变 Rig Pose。
- Dissolve 被打断不闪回。
- 隐藏 Output 后释放渲染资源。

### Phase D：Composition

交付：

- Composition Registry 和 preset loader。
- 多 Output 同时显示。
- Output rect、z-index、opacity、rotation、aspect mode。
- 矩形和自定义裁剪。
- `camera compose`。
- Composition State。

验收：

- Composition 切换不修改 Stage、Actor 或 Rig。
- 多 Output 首帧全部 ready。
- Skip、Cancel、Reset、Load、Shutdown 无资源残留。
- 不同窗口比例下布局符合 preset。

### Phase E：持久化、诊断与性能

交付：

- State schema 更新。
- Stage、Rig、Output 和 Composition diagnostics。
- 活跃资源计数。
- 配置上限与 authoring validation。
- 性能和资源回收测试。

验收：

- Save/Load 恢复稳定画面。
- 不恢复 transition 中间帧和瞬态资源。
- 日志可以定位 Stage、Rig、Output 和 Operation。
- 超出配置上限时产生可操作的错误信息。

## 15. 测试策略

### Unit

- Stage Registry、生命周期和重复 ID。
- ActorAddress、默认 Stage 和隔离。
- Rig Registry、Pose Operation 和重复 ID。
- Output Routing、Switch 和 ready 顺序。
- Composition validation 和 preset source 数量。
- Operation 冲突和终态。
- State capture、preflight 和 restore。

### Headless

- 多 Stage 创建、休眠和释放。
- 多 Rig 更新和切换。
- Output lease 分配和释放。
- Reset、Load 和 Shutdown 清理。
- 无实际像素输出时的确定性生命周期验证。

### Visual Smoke

- 同一 Stage 的多个 Camera Output。
- 不同 Stage 的多个 Camera Output。
- Cut、dissolve 和被打断的 dissolve。
- Split、overlay 和 single Composition。
- Output resize、rotation、opacity 和 clip。
- 不同窗口比例下的布局。

## 16. 首个技术切片

首个切片只验证基础依赖关系：

1. 将现有唯一 Stage 包装成默认 `main` Stage。
2. 创建第二个 Stage，并验证状态隔离。
3. Actor Manager 引入 `ActorAddress`，保持旧命令默认行为。
4. 在同一 Stage 创建 `main` 和 `aux` 两个 Camera Rig。
5. 修改隐藏 `aux` Pose。
6. 将 `main` Output 从 `main` Rig 切换到 `aux` Rig。
7. 验证切换首帧、Operation 终态和资源释放。

完成该切片后，再实现多 Output 和 Composition。这样可以先稳定 Stage、Actor、Rig 和 Routing
之间的领域关系，避免同时修改全部渲染路径。
