# Camera View Transition 实现设计

## 1. 状态与范围

本文定义 `camera pose/reset ... via:<mode>` 的脚本契约和 Runtime 实现边界，重点覆盖从
Focus 构图溶解到默认构图的场景。

截至 2026-08-27，单 Camera Output 的 `cut`、`move`、`dissolve` 核心路径已经实现：包括
Yarn 参数绑定、Camera Manager 状态机、独立 Stage Viewport、冻结 source capture、Operation
终态与资源回收，以及 Fake backend、unit 和 Godot headless 验证。第 8 节列出的结构化
diagnostics、可见截图 golden smoke，以及命名 Rig/多输出能力仍属于后续工作。
此外，首版在新 dissolve 替换旧 dissolve 时会先完成旧 transition 到稳定 target；第 6.2 节
定义的“捕获当前混合输出作为新 source”仍需引入可再次捕获的 compositor output 后实现。

本阶段只处理单个 Camera Output 内的 Pose 切换：

- `cut`：立即应用目标 Pose。
- `move`：在同一个 Camera View 中插值 Pose。
- `dissolve`：保留当前 Camera 输出，同时显示目标 Pose 输出，并淡出旧输出。

命名 Camera Rig、Rig 间切换和多输出 Composition 仍按
[`camera-runtime-next-phases.md`](camera-runtime-next-phases.md) 后续实施。这里建立的 View
Transition 契约必须能被未来的 `camera switch` 和 `camera compose` 复用。

## 2. Authoring 契约

### 2.1 核心语法

```yarn
<<camera pose center:50,60 zoom:2 rotation:0 via:cut>>
<<camera pose center:50,60 zoom:2 rotation:0 via:move duration:0.5 ease:cubic-out>>
<<camera pose center:50,60 zoom:2 rotation:0 via:dissolve duration:0.6 ease:sine-in-out>>

<<camera reset via:cut>>
<<camera reset via:move duration:0.5 ease:cubic-out>>
<<camera reset via:dissolve duration:0.6 wait:true>>
```

`via` 描述如何到达命令声明的目标 Camera Pose。脚本不声明快照、临时纹理、重复 Background
或临时 Rig。

### 2.2 本场景的最终写法

```yarn
<<cg Still_490_001>>
<<camera pose center:37.384,74.816 zoom:3 rotation:10 via:cut>>
<<camera move center:44.438,71.950 duration:0.4 ease:back-out>>

// Dialogue...

<<camera reset via:dissolve duration:0.6 wait:true>>
<<wait 0.2>>
```

这表达的是“当前 Focus Shot 溶解到默认 Shot”。原版的 `Stills-Middle` 和 `Stills` 双 Actor
只是实现方式，不进入新脚本模型。

### 2.3 命令职责

| 命令 | 职责 | 是否接受 `via` |
| --- | --- | --- |
| `camera pose` | 原子声明一个或多个 Pose 属性的目标值 | 是 |
| `camera reset` | 将完整 Pose 目标设为默认值 | 是 |
| `camera move` | 连续改变 center | 否，固定为 `move` |
| `camera zoom` | 连续改变 zoom | 否，固定为 `move` |
| `camera rotate` | 连续改变 rotation | 否，固定为 `move` |
| `camera shake` | 添加瞬态输出 modifier | 否 |

`camera move/zoom/rotate` 已经是明确的连续运动动词，不再接受 `via`，避免出现
`camera move via:dissolve` 这种互相矛盾的声明。

### 2.4 默认值与校验

- `camera pose` 和 `camera reset` 默认 `via:cut`。
- `via:cut` 默认 duration 为 `0`；显式提供大于 `0` 的 duration 时报 authoring error，不静默忽略。
- `via:move` 默认使用项目 Animation Duration。
- `via:dissolve` 默认使用项目 Animation Duration。
- `via:move` 和 `via:dissolve` 允许 `duration:0`，结果立即落到目标 Pose。
- `curve` 对 `move` 控制 Pose 插值，对 `dissolve` 控制混合权重。
- `wait`、`group` 和 `operation` 沿用现有 Operation 契约；Operation 继续使用现有的可 Skip
  默认值。
- 不兼容旧的隐式行为；已有 `camera pose/reset` 脚本应显式迁移到 `via:cut` 或 `via:move`。

不根据 duration 猜测模式。以下命令必须保持不同含义：

```yarn
<<camera reset via:move duration:0.6>>
<<camera reset via:dissolve duration:0.6>>
```

Command arguments 中的 duration 必须改为 nullable，先保留“作者是否显式填写”的信息，再由
`via` resolver 注入对应默认值。不能沿用当前 binder 在解析前无条件写入 Animation Duration 的
方式，否则无法可靠校验 `via:cut duration:>0`。

### 2.5 术语

脚本层统一使用 `dissolve`，不同时提供 `crossfade` 别名。这里的 dissolve 是两个不透明
Camera View 的 alpha 混合；在本场景中，它与“默认图固定、Focus 图淡出”的结果相同。

`transition` 根命令仍表示场景/Appearance Transition。Camera View Transition 不复用它：

```text
camera ... via:dissolve  Camera 输出之间的转换，不包含 UI
transition ...          场景 Appearance 或全局遮罩转换
```

## 3. Domain Model

### 3.1 新模型

```csharp
public enum CameraViewTransitionKind
{
    Cut,
    Move,
    Dissolve,
}

public sealed record CameraViewTransitionSpec(
    CameraViewTransitionKind Kind,
    StageOperationSpec Operation);

public sealed record CameraPoseTransitionRequest(
    CameraPoseUpdate Target,
    CameraViewTransitionSpec Transition);
```

`CameraPoseUpdate` 继续表达部分目标；`CameraManager` 在接受请求时将它与当前 `TargetState`
合并为完整且不可变的最终 `CameraState`。

### 3.2 Manager API

建议将 Pose 入口收敛为：

```csharp
public interface ICameraManager
{
    CameraState State { get; }
    CameraState TargetState { get; }

    IOperationHandle TransitionTo(CameraPoseTransitionRequest request);
    IOperationHandle MoveTo(Vector2 center, StageOperationSpec? operation = null);
    IOperationHandle ZoomTo(float zoom, StageOperationSpec? operation = null);
    IOperationHandle RotateTo(float degrees, StageOperationSpec? operation = null);
    IOperationHandle ResetCamera(CameraViewTransitionSpec transition);
    IOperationHandle Shake(CameraShakeSpec shake);
}
```

`PoseTo` 可作为内部的 `Move` 路径，不再作为模糊的公开入口。Command Handler 只负责绑定和构造
request，不决定 capture 或 compositor 行为。

### 3.3 Backend 边界

`IStageBackend` 不应继续用一个 `AnimateCameraAsync` 同时承担所有模式。建议拆为：

```csharp
public interface ICameraViewBackend
{
    CameraState RenderedCamera { get; }

    void ApplyPose(CameraState target);

    Task MovePoseAsync(
        CameraState source,
        CameraState target,
        StageOperationSpec operation,
        CancellationToken cancellationToken);

    Task DissolvePoseAsync(
        CameraState source,
        CameraState target,
        StageOperationSpec operation,
        CancellationToken cancellationToken);

    void CompleteViewTransition(CameraState target);
    void ClearTransientViewTransition();
}
```

Core 只依赖 Camera 输出语义；Godot backend 自己管理 Viewport、capture texture 和 transition
overlay。Fake backend 不生成纹理，但必须记录 source、target、mix 和资源释放状态。

## 4. Dissolve 的精确定义

### 4.1 画面范围

source 和 target 都只包含 Camera 可见的 Stage 内容：

- Background。
- Character。
- World-space effects。

不包含：

- Dialogue UI。
- Menu、Settings、Save/Load UI。
- Curtain、Flash、Letterbox 等全局 Screen FX。
- Runtime diagnostics overlay。

因此必须先建立明确的 `CameraOutput` 渲染边界。直接截取主窗口会把 UI 一起冻结，是不合格实现。

### 4.2 帧序列

`via:dissolve` 必须按以下顺序执行：

1. 采样当前实际 Camera Pose，作为 source pose。
2. 捕获当前 CameraOutput，作为 source frame。
3. 将完整 target pose 应用到 live CameraOutput。
4. 在同一呈现帧把 source frame 放到 target output 上方，初始 alpha 为 `1`。
5. 按 curve 将 source alpha 从 `1` 动画到 `0`。
6. 完成后移除 source frame 并释放 capture lease。

目标画面全程保持 alpha `1`。因为两层都是不透明全屏输出，这与标准
`source * (1-t) + target * t` dissolve 等价。

不得先 reset Camera、下一帧再创建 overlay；否则会闪出一帧未遮盖的 target。

### 4.3 Source 是冻结输出

本阶段将 dissolve source 定义为开始时冻结的 CameraOutput，target 为 live Stage 输出。这一约定：

- 与原版静态 Focus 照片淡出一致。
- 在单 Camera backend 下可确定实现。
- 避免实现升级后脚本视觉语义变化。

未来需要两个持续更新的 live Rig 同时混合时，使用 `camera switch ... via:dissolve`，由双 Rig
Compositor 实现；不要静默改变本阶段 Pose dissolve 的冻结 source 契约。

## 5. Godot 渲染设计

### 5.1 目标节点结构

```text
StageRenderSurface
├── StageViewport
│   └── StageWorld
│       ├── Backgrounds
│       ├── Characters
│       └── WorldEffects
│
├── CameraOutputCompositor
│   ├── LiveOutput          TextureRect(StageViewportTexture)
│   └── TransitionSource    TextureRect(captured source), initially hidden
│
├── GlobalScreenLayer
│   ├── Curtain
│   ├── Overlay / Flash
│   └── Letterbox
│
└── Presentation / UI
```

单画面也通过 `LiveOutput` 呈现，避免普通播放和 dissolve 使用不同渲染路径。

### 5.2 Capture Lease

capture texture 必须由显式 lease 管理：

```csharp
public interface ICameraOutputCapture : IDisposable
{
    Texture2D Texture { get; }
    Vector2I Size { get; }
}
```

- 每次 dissolve 最多持有一个 source capture。
- Complete、Skip、Cancel、Reset、Load、Shutdown 和 backend fault 都必须释放 lease。
- 窗口 resize 期间旧 capture 保持开始时的尺寸，由 compositor 按当前 output rect 拉伸；下一次
  transition 使用新尺寸。
- 捕获失败时命令 fault 并直接清理，不得留下半透明 overlay。

实现可以先使用一次性的 Viewport texture copy；不允许通过隐藏 UI 一帧后截取主窗口来实现。

### 5.3 Camera 与 Shake 的层级

长期顺序应为：

```text
Camera Pose render
    → View transition composition
    → Camera output modifiers (shake)
    → Global Screen FX
    → UI
```

这样 dissolve source 不会冻结某个随机 shake offset。首期若尚未迁移 shake，开始 dissolve 前必须
完成或清除 active shake，并记录 diagnostic；不能把 shake 偏移烘焙进 source frame 后继续对
target 重复施加。

## 6. Operation 与冲突规则

### 6.1 资源通道

```text
camera/pose/center
camera/pose/zoom
camera/pose/rotation
camera/output/transition
camera/output/modifier/shake
```

- `move/zoom/rotate` 继续只占用各自 Pose 属性通道，允许不同属性并行。
- `pose via:move` 占用请求中出现的属性通道。
- `pose/reset via:cut` 占用并立即提交目标属性。
- `pose/reset via:dissolve` 是完整 View 转换，独占全部 Pose 属性和
  `camera/output/transition`。

Dissolve 即使只声明 center，也必须先把部分 update 解析为完整 target pose，再渲染完整目标 View。

### 6.2 新命令打断 Dissolve

- 新 `via:dissolve`：捕获当前已经混合后的 CameraOutput 作为新 source，再切换到新 target，禁止闪回。
- 新 `via:cut` 或 Pose motion：先完成当前 dissolve 到它的 target、释放 source，再执行新命令。
- 新 shake：作用在 compositor 输出之后，不打断 dissolve。
- 全局 `transition`：首期与 Camera dissolve 串行，避免两个 capture/overlay 系统交叉；未来统一
  Compositor 后再允许明确的并行层级。

### 6.3 Operation 终态

| 行为 | 结果 |
| --- | --- |
| 正常完成 | target pose 成为 `State` 和 `TargetState`，source capture 释放 |
| Complete | 立即显示 target，source capture 释放 |
| Skip | 与 Complete 相同 |
| 显式 Cancel | 落到 target，source capture 释放 |
| 被新 dissolve 替换 | 当前混合输出成为新 source，不闪回 |
| Reset / Load / Shutdown | 清除 transition，释放 capture，直接应用稳定目标状态 |

Dissolve 的中间混合帧不能表示成一个 Camera Pose，因此 Cancel 不采用“冻结中间状态”。把 Cancel
定义为落到 target，可以保证 Camera 稳定状态、存档和后续命令一致。

### 6.4 State 更新时机

- 请求被接受时立即计算并写入 `TargetState`。
- source capture 完成后，backend 应用完整 target pose。
- dissolve 结束时将 `State` 提交为 target。
- transition 期间 `RenderedCamera` 返回 target pose；当前混合权重属于 compositor transient state，
  不伪装成 Camera Pose。

## 7. Save、Skip 与 Accessibility

### 7.1 Save / Load

存档只保存 `TargetState`，不保存：

- source capture。
- dissolve mix。
- transition overlay。
- operation handle。

在 dissolve 中存档后读取，直接显示 target pose。这与 Skip/Complete 的稳定终态一致。

### 7.2 Reduce Motion

- `via:dissolve` 保留原 duration；它不包含空间运动。
- `via:move` 继续将 Back 等 overshoot curve 降级为单调 curve。
- Reduce Motion 不改变 wait、group 或 Yarn 控制流。
- 若未来增加“减少转场”设置，可把 dissolve 的视觉输出降级为 cut，但 Operation 仍等待原 duration。

## 8. Diagnostics

至少增加：

- `camera.transition.capture_failed`
- `camera.transition.replaced`
- `camera.transition.resource_released`
- `camera.transition.invalid_duration`
- `camera.transition.serialized_with_stage_transition`
- `camera.transition.shake_completed_for_capture`

Diagnostics snapshot 应显示：

```text
Camera State
Camera TargetState
Active View Transition kind
Transition source size
Transition mix
Capture lease active/released
```

## 9. 测试设计

### 9.1 Command 与 Binder

- `via:cut|move|dissolve` 正确绑定。
- 未知 via 报错并列出允许值。
- `cut duration:>0` 报 authoring error。
- `camera move/zoom/rotate` 拒绝 `via`。
- `pose/reset` 缺省为 cut。

### 9.2 Manager Unit Tests

- partial pose 与 TargetState 合并为完整 target。
- cut 立即提交稳定状态。
- move 保持属性级并发和替换语义。
- dissolve 独占完整 Pose 和 output transition 通道。
- Complete、Skip、Cancel 都落到 target 并释放 capture。
- dissolve 替换 dissolve 时从当前 composite 开始，无 source 闪回。
- Save 只序列化 TargetState。
- Reduce Motion 不改变等待时间。

### 9.3 Fake Backend

Fake backend 记录严格调用顺序：

```text
CaptureSource
ApplyTargetPose
ShowSourceOverlay
AnimateSourceAlpha
ReleaseSource
```

任何退出路径都断言 `ActiveCaptureCount == 0`。

### 9.4 Godot Headless 与可见 Smoke

- Headless：Viewport resize、Reset、Load、Shutdown 和宿主节点提前释放不泄漏 capture。
- 可见 smoke：Focus shot 到 default shot 全程没有黑帧、UI 冻结或一帧 target 闪现。
- 截图点至少包括 mix `0`、`0.5`、`1`。
- Manosaba 本场景作为 golden fixture：Focus 构图结束后，0.6 秒 dissolve 到默认构图。

## 10. 实施状态与顺序

1. 已完成：增加 `CameraViewTransitionKind`、request/spec 和 Command 参数校验。
2. 已完成：将 `CameraManager` 的 target 解析与 `cut/move/dissolve` 调度分离。
3. 已完成：增加 Fake Camera View backend 和核心 Operation tests。
4. 已完成：建立独立于 UI/Global FX 的 `StageViewport` 与 `CameraOutput`。
5. 已完成：实现单 source capture 与 overlay compositor，并覆盖 teardown 回收。
6. 部分完成：实现 dissolve 正常完成、Skip、Cancel、替换时稳定落点和终态清理；精确捕获
   被替换 transition 的当前混合帧待 compositor 二次输出实现。
7. 已完成：dissolve 开始前完成 active shake；未来再把 shake 迁到 CameraOutput modifier 层。
8. 部分完成：Godot headless 和 Manosaba fixture 已加入；可见截图 golden smoke 待补。
9. 已完成：现有示例脚本迁移到显式 `via`。
10. 待实现：混合帧替换、结构化 transition diagnostics，以及让命名 Rig 的 `camera switch`
    和 Composition 复用同一 transition spec。

## 11. 验收标准

- Yarn 只声明目标 Pose 和 `via:dissolve`，不出现重复 Background、capture 或临时 Rig。
- Focus 到 default 的 0.6 秒过渡与原版双 Background 淡出视觉一致。
- Camera dissolve 不冻结或淡出 Dialogue UI。
- 任意完成、打断、Skip、Load、Reset 和 Shutdown 路径无 capture 泄漏。
- 新 dissolve 打断旧 dissolve 时无闪回或黑帧。
- 稳定 Camera state 始终是可序列化 Pose，不包含混合中间帧。
- 本阶段的 transition abstraction 可直接被未来 `camera switch` 和 `camera compose` 复用。
