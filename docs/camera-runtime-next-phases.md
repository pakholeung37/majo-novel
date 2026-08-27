# Camera Runtime 后续实施方案

## 1. 文档状态

本文记录 Camera Runtime 第一阶段之后的后续设计，供未来实施使用。本文不是当前功能说明；除“已完成基线”明确列出的能力外，其余内容均尚未实现。

设计原则：

- Yarn 对外统一使用 `camera <verb>`。
- Camera Rig、画面输出和合成布局是不同领域对象。
- 精确几何状态使用 `pose`，基于主体自动构图使用 `focus`。
- 稳定状态与 shake、view transition 中间帧等瞬态状态严格分离。
- 不为旧的 `cameraMove`、`cameraZoom` 等扁平命令提供兼容层。

## 2. 已完成基线

当前 Runtime 已具备：

- 层级 Runtime Command Path 和 Yarn 根命令分派。
- `camera pose`、`camera move`、`camera zoom`、`camera rotate`、`camera reset`、`camera shake`。
- Camera center、zoom、rotation 的原子 Pose Operation。
- 参考场景百分比坐标：左下角为 `0,0`，中心为 `50,50`，允许越界。
- Linear、Sine、Cubic、Quint 和 Back motion curve。
- 不同 Camera 属性并行动画，同属性动画从当前实际帧替换。
- Camera TargetPose 存档、Skip 终态和 Reduce Motion 基础语义。
- 单 Camera Output 的 `via:cut|move|dissolve`。
- 独立于 UI 和 Global Screen FX 的 Stage Viewport、冻结帧 capture 与单 source compositor。
- Dissolve 的 Operation 终态、Reset/Shutdown 回收，以及 unit 和 Godot headless 覆盖。

当前尚未具备命名 Camera Rig、输出路由、切换、自动构图、持续跟踪和多视图合成。

单 Camera Pose 的 `via:cut|move|dissolve` 脚本契约、已实现范围和剩余验证见
[`camera-view-transition-design.md`](camera-view-transition-design.md)。该能力是命名 Rig 和多输出
Compositor 的前置阶段；后续必须复用同一个 View Transition spec。

## 3. 目标架构

```text
StageWorld
   │
   ├── CameraRig: main
   ├── CameraRig: closeup
   └── CameraRig: overhead
             │
             ▼
       CameraOutput
             │
             ▼
        Compositor
             │
             ▼
   Global Screen FX / Curtain / UI
```

### 3.1 CameraRig

Camera Rig 是持久的命名虚拟机位。每个 Rig 独立保存 Pose、TargetPose 和 Driver；切走后不销毁，再次切回时保持原机位。

建议模型：

```csharp
public sealed record CameraRigState(
    string Id,
    CameraPose CurrentPose,
    CameraPose TargetPose,
    CameraDriverState Driver);
```

Rig 的逻辑更新和实际渲染需要分离。不可见 Rig 可以继续求值 follow driver，但不应占用 SubViewport。

### 3.2 CameraDriver

Driver 表达持续驱动，不应伪装成永不结束的 Operation：

```csharp
public abstract record CameraDriverState;

public sealed record StaticCameraDriver : CameraDriverState;

public sealed record FollowCameraDriver(
    IReadOnlyList<string> ActorIds,
    CameraFraming Framing,
    Vector2 Offset,
    float Damping,
    Rect2 DeadZone) : CameraDriverState;
```

### 3.3 CameraOutput

Output 是最终合成画面中的一个区域：

```csharp
public sealed record CameraOutputState(
    string Id,
    Rect2 NormalizedRect,
    string SourceCameraId,
    CameraAspectMode AspectMode,
    int ZIndex);
```

默认只有占满画面的 `main` output。分屏或画中画才创建额外 output。

### 3.4 CameraComposition

Composition 描述多个 output 的布局及层级：

```csharp
public sealed record CameraCompositionState(
    string Id,
    IReadOnlyList<CameraOutputState> Outputs);
```

复杂 Composition 应主要由资源定义，Yarn 只负责激活和过渡。

## 4. 第二阶段：命名 Rig 与 Director

### 4.1 命令

```yarn
<<camera create wide>>
<<camera create closeup from:main>>
<<camera destroy closeup>>

<<camera pose camera:wide center:50,50 zoom:1>>
<<camera move camera:closeup to:48,62 duration:0.4>>

<<camera switch to:wide via:cut>>
<<camera switch to:closeup via:dissolve duration:0.6 curve:cubic-in-out>>
```

不增加 `camera select`。显式 `camera:<id>` 可以避免并行脚本和多输出场景中的隐式编辑上下文。

省略 `camera:` 时，命令作用于 `main` output 的目标 Rig；dissolve 期间目标 Rig 指 transition
target。

### 4.2 Switch via Cut 语义

- 立即修改指定 output 的 source。
- 完成并清理该 output 上正在运行的 view transition。
- 不修改源 Rig 或目标 Rig 的 Pose。
- 被切走的 Rig 保持自身状态。

### 4.3 Switch via Dissolve 语义

- 同时渲染源 Rig 和目标 Rig。
- 在 compositor 中混合，不通过移动某个 Rig 模拟转场。
- 新 dissolve 打断旧 dissolve 时，捕获当前合成帧作为新 transition source，避免闪回。
- Skip 直接落到目标 Rig，并释放临时纹理。

### 4.4 Operation 通道

建议使用以下资源通道仲裁冲突：

```text
rig/<id>/center
rig/<id>/zoom
rig/<id>/rotation
rig/<id>/driver
rig/<id>/modifier/shake
output/<id>/source
composition/layout
```

同一 Rig 的不同 Pose 属性可以并行；同一属性的新动画从当前实际值替换。

## 5. 第三阶段：Focus、Follow 与 Hold

### 5.1 Focus

`focus` 只表达基于主体或场景点的自动构图，不作为 `pose` 的别名：

```yarn
<<camera focus target:Ema framing:closeup duration:0.5>>
<<camera focus target:Ema,Hiro framing:two-shot padding:8>>
<<camera focus point:70,35 zoom:2 duration:0.4>>
```

`target` 与 `point` 二选一。Actor 模式根据可见 bounds 计算 center 和 zoom，并支持：

- `framing:closeup|medium|wide|two-shot`
- `padding`
- `offset`
- `min-zoom`
- `max-zoom`

Focus 默认只计算一次，完成后形成静态 Pose。

### 5.2 Follow

```yarn
<<camera follow target:Ema framing:medium damping:0.25>>
<<camera follow camera:closeup target:Ema,Hiro framing:two-shot>>
```

Follow 设置持久 Driver。目标 Actor 消失时冻结在最后有效 Pose、记录 diagnostic，不自动 reset。

### 5.3 Hold

```yarn
<<camera hold>>
<<camera hold camera:closeup>>
```

Hold 的严格语义：

1. 采样当前实际 Pose。
2. 取消当前 Pose motion。
3. 移除 follow driver。
4. 将采样值同时写入 CurrentPose 和 TargetPose。

Hold 不是等待命令；等待继续使用 `await`。

## 6. 第四阶段：多输出 Compositor

### 6.1 Yarn API

简单布局：

```yarn
<<camera compose layout:split-horizontal sources:wide,closeup>>
<<camera compose layout:picture-in-picture sources:wide,closeup>>
<<camera compose layout:single sources:wide>>
```

复杂布局：

```yarn
<<camera compose preset:trial-double-view duration:0.4 curve:cubic-out>>
```

示例资源：

```yaml
id: trial-double-view
outputs:
  - id: main
    source: wide
    rect: [0, 0, 1, 1]
    z: 0
  - id: inset
    source: witness-closeup
    rect: [0.68, 0.06, 0.28, 0.30]
    z: 10
    border: trial-frame
```

### 6.2 Godot 渲染结构

当前单个 CanvasLayer Transform 无法同时渲染多个机位。目标 backend：

```text
StageWorld
├── Backgrounds
├── Characters
└── WorldEffects

CameraRenderGraph
├── SubViewport(main)  + Camera2D(wide)
├── SubViewport(inset) + Camera2D(closeup)
└── ...

StageCompositor
├── TextureRect(main viewport texture)
├── TextureRect(inset viewport texture)
└── Transition layers

GlobalScreenLayer
├── Curtain
├── Flash
├── Letterbox
└── Dialogue UI
```

单画面也应走 compositor 路径，避免单视图和多视图切换时出现渲染差异。

### 6.3 性能约束

- 只为当前可见 output 分配活跃 SubViewport。
- 不可见 Rig 继续逻辑求值，但不渲染。
- 配置最大同时输出数并在超限时产生 authoring diagnostic。
- Transition capture texture 必须具有确定的释放时机。

## 7. 存档与恢复

未来 Camera state 需要持久化：

- 全部命名 Rig。
- 每个 Rig 的 TargetPose。
- Follow driver 配置。
- 当前 Composition。
- 每个 output 的稳定 source。

不持久化：

- Tween 中间帧。
- Shake modifier。
- View transition 混合权重。
- Transition capture texture。
- Operation handle。

恢复流程：

1. 取消全部 Camera Operation。
2. 清除 transient modifier 和 transition texture。
3. 重建 Rig。
4. 恢复 Driver。
5. 恢复 Composition 和稳定 output routing。
6. 直接应用 TargetPose。

实现命名 Rig 时需要再次提升 `majo.stage` schema，并明确不提供旧 schema 迁移。

## 8. Skip、Cancel 与 Reduce Motion

### 8.1 Skip

- Pose motion：落到 TargetPose。
- Rig dissolve：切到目标 Rig。
- Compose：落到目标布局。
- Shake：清除 modifier。
- Follow：保持 Driver。

### 8.2 Cancel

- Pose motion：冻结当前实际 Pose。
- Rig dissolve：落到目标 Rig；下一次 dissolve 替换则从当前合成帧开始，不得闪回。
- Shake：清除 modifier。
- Follow：只有 hold 或新 Driver 才停止。

### 8.3 Reduce Motion

- Operation duration 和 Yarn 等待语义保持不变。
- Shake 输出零偏移，但仍等待原 duration。
- Back 等 overshoot 曲线降级为单调曲线。
- 大角度 rotation 和大范围 zoom 的进一步降级策略在 Driver 阶段集中配置。

## 9. Authoring 与导入

Camera Pose 的 center 表示观察中心，不能直接复用旧 Naninovel 背景 Actor 的 ScenePosition。导入器应在参考分辨率下进行矩阵转换：

```text
cameraViewMatrix = originalBackgroundTransform
cameraWorldMatrix = inverse(cameraViewMatrix)
```

再将结果分解为 center、zoom 和 rotation。该转换应进入独立导入工具并配套 golden fixtures，不应在 Yarn 中手工维护大量魔法数值。

## 10. 推荐实施顺序

1. 引入 `CameraRigState`、Rig registry 和默认 `main` Rig。
2. 让现有 pose/move/zoom/rotate/shake 支持显式 `camera:<id>`。
3. 实现 Camera Director、`camera switch ... via:cut` 和稳定 output routing。
4. 实现双 Rig `camera switch ... via:dissolve` 和 transition frame capture。
5. 升级 Stage state schema，覆盖多 Rig 和 output routing。
6. 实现 Actor bounds、focus framing、follow driver 和 hold。
7. 将 StageWorld 从单 CanvasLayer backend 迁移到 Camera2D/SubViewport render graph。
8. 实现 Composition preset、分屏和画中画。
9. 增加 Naninovel Camera transform 批量导入工具。

## 11. 验收标准

- 切走再切回后 Rig Pose 完整保留。
- 新 motion 打断旧 motion不跳帧。
- Dissolve 被打断不闪回源 Rig。
- Follow 目标消失时冻结并产生 diagnostic。
- Skip 后稳定状态确定且资源释放完成。
- Save/Load 不恢复 shake、半帧和 transition texture。
- 不同窗口比例下参考场景构图一致。
- 单画面和多画面使用同一渲染路径。
- Reduce Motion 不改变 Yarn 指令顺序或等待时长。
- Headless 覆盖状态和 Operation，截图 smoke 覆盖 cut、dissolve、split 和 picture-in-picture。
