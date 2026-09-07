# 视觉转场与缓动统一重构实施方案

日期：2026-09-07  
状态：待实施；本文定义目标设计，不表示代码已经完成迁移。  
兼容策略：允许破坏性更改，不保留旧视觉转场 API 或名称别名。

## 1. 目标与决策

本方案承接 `direction-core-refactor-plan.md` 中已经落地的 ID / registry 方向，完成从命令、配置到实际渲染的贯通。涉及视觉转场的决策以本文为准；原文档保留其历史上下文。

明确采用以下决策：

1. 视觉渐变切换统一命名为 `dissolve`，删除视觉 `crossfade` 名称，不注册兼容别名。音频 crossfade 不在本次改名范围。
2. 视觉转场唯一描述为 `DirectionTransitionId` / `DirectionTransitionSpec`，唯一扩展入口为 `ITransitionDriverRegistry`。
3. 缓动统一使用 `Timing.EasingId` / `IEasingRegistry`。删除 Actors 和 Stage 的曲线枚举及各自的映射。
4. Direction 负责目标状态、转场编排和事务；Actors 提供内容、属性及渲染适配；共享执行器负责时间推进和 easing 采样。
5. Dissolve 统一采用完整源/目标视觉输入的双输入混合，包括 LayeredCharacter 换外观。用户已确认实验视觉效果可接受，允许替换旧差量图层渐变；不保留旧算法作为正式兼容分支。
6. 退役旧 Stage 的镜头和转场服务。迁移其仍有价值的能力和调用，再删除实现及注册，不让两套状态所有者长期并存。

本次不重写音频系统、OperationRunner、角色资源加载和工厂机制，也不扩展新的特效编辑器。允许调整它们与视觉执行器之间的接口，但不顺带迁移无关功能。

## 2. 当前代码事实

以下路径均相对于仓库根目录。

| 位置 | 当前行为 | 需要解决的问题 |
| --- | --- | --- |
| `Runtime/Actors/AnimationSpec.cs` | 定义 AnimationTransition、AnimationEase、时长和冲突策略 | 将曲线重复定义为封闭枚举 |
| `Runtime/Actors/Godot/GodotAnimationSpec.cs` | 两个枚举分别映射 Godot Tween | 第二套 easing 解释器 |
| `Runtime/Direction/Godot/GodotDirectionActorTreeBackend.cs` | ToAnimationSpec 将 EasingId 转回固定枚举；换外观只传 duration | 自定义 easing 被拒绝，外观路径丢失完整规格 |
| `Runtime/Stage/StageModels.cs` | CameraViewTransitionKind、StageTransitionKind、MotionCurve 及旧请求类型 | 与 Direction 重复建模 |
| `Runtime/Stage/Godot/GodotStageBackend.cs` | 独立镜头 Tween、截帧和遮罩；Fade / Crossfade 走同一颜色遮罩分支 | 旧 Crossfade 没有对应源/目标画面的双输入混合 |
| `Runtime/Stage/StageManagers.cs` | 独立 CameraManager、StageTransitionManager 及完成/取消处理 | 与 Direction 分别管理状态和生命周期 |
| `Runtime/Bootstrap/Registration/MajoRuntimeBootstrap.Services.ActorsAndStage.cs` | 仍注册旧服务及 ProviderRegistry | 旧体系仍会初始化，不能只删除模型文件 |
| `Runtime/Direction/Transitions/BuiltInTransitionDrivers.cs` | Fade 与 Dissolve 都降低 Surface 的 alpha | 名称不同，但实现和输入契约未区分 |
| `Runtime/Direction/Godot/GodotDirectionScreenBackend.cs` | 自行执行屏幕转场分支 | Screen 也需要接入统一驱动路径 |
| `Runtime/Direction/Godot/GodotEasingAdapter.cs` | 内置使用原生 Tween，自定义使用 registry 采样 | 统一采样时必须避免重复 easing |

上表中的 `Runtime/` 前缀完整路径是 `addons/majo_runtime/Runtime/`。

另有仍在使用的 Stage 类型：IStageAccessibility 被 Direction camera modifiers、设置投影和 bootstrap 使用；CameraShakeAxis 被命令 converter 和 modifier 使用。删除 Stage 目录前必须迁移这些依赖。

## 3. 统一语义

### 3.1 转场定义

| ID | 语义 | 输入与约束 |
| --- | --- | --- |
| `cut` | 立即采用目标状态 | duration 必须为 0，不创建动画资源 |
| `move` | 连续插值可插值属性 | 必须有合法源值和目标值；支持位置、缩放、旋转等明确声明的属性 |
| `dissolve` | 源视觉内容连续混合到目标视觉内容 | 完整源/目标视觉输入；不经过黑场；不先淡出再淡入 |
| `fade` | 对同一视觉内容进行可见度/透明度变化 | 内容身份不变，或一端为空；有实际内容替换时应使用 dissolve |

Fade 不再是“任何变化都能用”的默认替代。角色进场/离场仍可用 fade；角色换外观、背景替换、镜头画面切换使用 dissolve。若显式对内容替换请求 fade，在准备阶段报错并提示 dissolve；不静默改义。

旧 Stage 的“经过指定颜色”效果迁移为现有 Screen/curtain 状态操作的显式序列：遮罩淡入 → 在遮罩下切换目标 → 遮罩淡出。它不是 dissolve，不为兼容旧名字新建一套转场系统。WipeLeft / WipeRight 若仍有调用，改为在统一 registry 中注册具名驱动，并明确其是源/目标擦除还是遮罩擦入。

`move` 是属性插值模式，不能强迫它先截帧再执行纹理驱动。统一的是规格、能力检查、进度与生命周期；属性应用器和视觉合成器保留不同输入契约。

### 3.2 Dissolve 的呈现契约：采用已验证的整图混合

用户已运行对比实验并反馈“看起来没问题，可以切换到新实现”。本方案据此采用统一整图 Dissolve，替换 LayeredCharacter 的差量层渐变。该决定表示视觉效果可接受，不宣称新旧算法数学等价或所有输入逐像素一致；生产迁移仍待实施。

| 内容 | 目标实现 |
| --- | --- |
| 普通角色外观 | 准备完整源/目标视觉输入，替换旧视图分别淡入淡出的算法 |
| 分层角色外观 | 分别渲染源/目标图层组合，交给同一个 Dissolve 合成器；删除 exiting/entering 图层 Tween |
| 背景、View、Screen | 提供对应范围的源/目标输入，共用同一混合实现 |

#### 合成数学与输入

统一使用预乘 alpha 输入。在同一颜色空间中，设输入为 `(Ps, As)` 和 `(Pt, At)`：

```text
P = (1 - p) * Ps + p * Pt
A = (1 - p) * As + p * At
```

预乘输出使用匹配的 blend mode；若输出到普通 straight-alpha surface，则只在出口执行一次 `RGB = P / A`，A 接近零时 RGB 置零。输入若是 straight alpha，则在输入适配处预乘一次。不要对已经预乘的 viewport capture 再乘 alpha。实验中的 shader 实现可提取为生产共享 shader，但必须明确不同输入的 alpha 和颜色空间约定。

p=0/1 分别精确呈现源/目标；相同输入的中间帧保持不变。源和目标必须采用一致的尺寸、局部坐标、锚点、裁剪范围与采样方式。合成期间只绘制合成结果，隐藏对应原始内容，防止重复绘制。源冻结在起始画面；目标可使用离屏渲染输入持续更新，但不能包含本次转场层。View/Screen 捕获也不得包含自身转场输出。

#### LayeredCharacter 保留与替换的边界

1. 保留 LayerCompositionResolver、增量 appearance 表达式、targetLayers 与规范化 Appearance；从已有目标层集合解析新目标，不将表达式当作单张纹理名。
2. 保留 manifest 默认启用层；源/目标完整组合包含身体等基础层。不能用清空图层后只应用 Default 别名来重建完整默认外观，实验已发现这会漏层。
3. 保留 SortingOrder、图层 Modulate、AtlasTexture、AlphaAwareMultiply、CanvasGroup 和 LayeredComposite 的端点合成能力。Multiply 在各端点内部完成，然后混合两幅结果；不再在转场中调整差量层 alpha。
4. 删除 exiting/entering 集合驱动的逐层 Tween，以及普通角色 CrossFadeToAppearance 的重复透明度算法。Actors 提供内容和稳定状态应用，Direction/Godot 的适配器准备输入，共享 driver 完成 Dissolve。
5. group_opacity 仍表达角色整体透明度。外观切换与整体透明度可并行时，输入捕获与输出应用必须约定唯一的 opacity 所有者，不能重复乘算或无意重置为 1。
6. Finish 精确应用目标组合并释放源输入。现有局部 ChangeAppearance.Cancel 会提交目标；若保留独立的低层入口，应显式保留并测试此约定。Direction 的 Cancel/Rollback 仍由 coordinator 恢复源树，不由 shader 提交状态。
7. 允许必要的临时捕获、视口和输出节点；撤销旧“换外观不分配临时节点”约束，替换为生命周期结束后无泄漏、按操作分配或复用、禁止按帧重建的要求。

原测试中“差量层出现中间 alpha”和“绝不创建临时节点”的断言需要替换为统一合成进度、正确端点和资源清理断言。保留增量组合、ArmR3 修改不丢失、规范化外观恢复、Multiply 端点等行为测试。

本次统一效果实现，不要求抹平所有输入准备方式：角色捕获自身子树，背景适配纹理，View/Screen 捕获各自输出。它们提供统一输入契约，不再分别实现 dissolve 算法。

### 3.3 Easing 和边界值

- 唯一采样公式：`t = clamp(elapsed / duration, 0, 1)`，`e = definition.Evaluate(t)`。
- 内置和自定义 easing 都通过同一个已解析的 EasingDefinition 求值。Godot Tween 若用于驱动时钟，只配置为 Linear。
- Move 可以使用有限的越界 e，以保留 Back 等曲线的 overshoot；opacity 和 dissolve 使用 `clamp(e, 0, 1)`。
- duration=0 的非 cut 操作允许立即落到目标，不截帧、不创建 Tween；ID/能力及 easing 合法性仍须验证。
- 完成或 Finish 强制应用精确目标值，不依赖自定义曲线恰好满足 Evaluate(1)=1。
- 未注册 ID、驱动不支持当前对象、缺少输入在 Prepare 中失败；运行中异常或 NaN/Infinity 进入事务失败恢复路径，不吞掉异常。

## 4. 目标接口与模块边界

以下是实施接口草案，允许在实现中微调类型名，但不可改变所有权与职责。

### 4.1 Timing 与 Operations

保留现有 EasingId、EasingDefinition、IEasingRegistry。将可复用时间规格放在 Timing，时间推进适配放在 Operations/Godot：

```csharp
// 新增：Timing；不依赖 Godot 或 Direction。
public readonly record struct AnimationTiming(
    double DurationSeconds,
    EasingId Easing,
    AnimationTimeMode TimeMode);

// 新增：Operations/Godot，返回现有 OperationDefinition。
// 0..1 的线性时钟只在此处推进；resolvedEasing 在准备时解析。
CreateProgressOperation(
    Node owner,
    AnimationTiming timing,
    EasingDefinition resolvedEasing,
    Action<float> applyProgress,
    Action finish,
    Action cancel);
```

AnimationTimeMode 从 Actors 迁到 Timing；AnimationConflictPolicy 从 Actors 迁到 Operations。保留现有 AnimationSpec 作为低层属性动画请求，字段改为 AnimationTiming + AnimationConflictPolicy，不带视觉 Transition ID。

现有 GodotTweenOperation 的视觉调用逐步改用共享进度执行器。非视觉调用不要求本次改写，但不得让视觉路径继续使用旧枚举 overload。删除 GodotAnimationSpec 和 ToAnimationSpec 的曲线分解，GodotEasingAdapter 的职责由统一采样替代。

### 4.2 Direction

- 保留 DirectionTransitionSpec 的 ID、duration、easing、skippable 表意；不为本次重构再新增第二种视觉规格。
- Execution 在 Prepare 中解析 easing、效果能力和实际输入，将解析结果交给 backend；Core 的 Validate 继续只检查值的不变量。
- cut、move 是执行层的特殊模式；其余视觉 ID 从唯一 registry 解析。能力检查统一封装，不能在每个 backend 重复判断一串 ID。
- driver 不自行创建 Tween，不决定提交、回滚、跳过或取消，不更新 Direction 的稳定状态。

建议将当前 ITransitionDriver.Setup(VisualTransitionContext) 调整为准备一个效果实例：

```csharp
// Direction.Transitions 的 Godot 渲染接口；不放入纯 Core。
public interface IPreparedVisualTransition : IDisposable
{
    void ApplyProgress(float progress);
}

// driver.Prepare(context) -> IPreparedVisualTransition
// context 提供统一源/目标视觉输入、输出 surface 及所需参数；不携带 Tween。
```

效果实例仅释放自己创建的资源，不释放角色永久材质或图层。源/目标输入租约和输出 Surface 由 prepared component 持有。Dispose 幂等清理，不能依赖自然 Finished。统一 dissolve 注册项使用同一个混合实现；各对象的适配器负责准备输入。自定义 shader 使用相同输入契约，并只声明经过验证的 domain 支持。

将“效果能力”与“输入是否可用”同时校验：Domains 包含 Actor 不代表已经支持角色输入。Actor、Stage、View、Screen 必须有相应适配与测试，才能声明支持。

### 4.3 Actors

- 保留 ActorManager、工厂、资源准备、位置/透明度的立即设置和具体内容渲染。
- 位置、透明度等低层动画通过共享进度执行器实现。
- Direction 的外观替换明确选择 dissolve；Actors 提供源/目标内容准备与稳定外观应用能力，Direction/Godot 适配为统一双输入。低层 ChangeAppearance 若保留，通过注入的共享呈现服务执行，不保留另一套混合实现。
- 保留 LayeredCharacter 的图层解析和端点合成；删除差量层渐变、普通角色旧视图交叉淡化及私有时间推进/曲线映射，换外观统一接入 Dissolve。
- 逐个处理 IActor/ICharacterActor 中现有 duration overload：仓库调用迁到明确规格或立即设置；删除表达视觉切换但不能携带完整规格的 API。不要为兼容继续硬编码 linear。
- Actors 不依赖 Direction 的 planner 或事务服务。适配角色视觉输入的桥接代码放在 Direction/Godot。

### 4.4 Stage 退役与共享能力搬迁

| 旧内容 | 处理方式 |
| --- | --- |
| CameraViewTransitionKind/Spec、CameraPoseTransitionRequest | 调用迁到现有 Direction view pose 指令 + DirectionTransitionSpec 后删除 |
| StageTransitionKind/Request、ProviderRegistry、IStageTransitionProvider | 调用迁到 Direction；扩展迁到唯一 driver registry 后删除 |
| MotionCurve、StageOperationSpec | 替换为共享 easing/timing 与 Direction 规格，删除 converter 和独立配置 |
| CameraManager、IStageTransitionManager、GodotStageBackend、FakeStageBackend | 能力和测试迁移完后删除；不保留转发旧服务 |
| CameraState、CameraPoseUpdate | 旧调用转换为现有 Direction View 状态与 pose patch；不在新命名空间复制旧模型 |
| CameraShakeSpec/Axis | shake 迁到现有 CameraModifierManager；Axis 搬到 Direction.Camera，旧 spec 删除 |
| IStageAccessibility、StageAccessibility | 搬到 Runtime/Accessibility 并改名 IDirectionAccessibility、DirectionAccessibility；更新设置投影和注册 |
| GodotActorHost | 单独审计 ActorManager、预加载、诊断、存档调用；不因删 Stage 自动删除整个 host |

Direction.Core 当前对旧 Stage 的 using 和类型依赖需要清除。无用途的 using 直接删除；实际共享数据迁到合适模块。

## 5. 生命周期与状态所有权

沿用现有 DirectionTransactionCoordinator 和 IPreparedDirectionComponent 协议；共享进度执行器不成为第二个事务协调器。

| 阶段/事件 | 要求 |
| --- | --- |
| Prepare | 解析能力、加载资源、准备隐藏目标和输入；不得发布目标画面。失败释放已准备资源 |
| Commit | 按现有 coordinator 协议切换所有权并启动呈现；源资源保留到 settlement |
| 自然完成 / Finish | 精确应用目标；释放源捕获、临时输出、材质与运行中的时钟 |
| Cancel / Rollback | 执行现有 coordinator 的恢复路径；底层先停止写入，不自行把目标当成功状态提交 |
| 运行异常 | 传播到 settlement；由 coordinator 恢复一致状态；清理不能掩盖原异常 |
| 被新操作取代 | 使用现有冲突策略，串行完成或取消旧操作；旧回调不能覆盖新状态 |
| Reset / Shutdown / 场景销毁 | 幂等终止执行和释放资源，不等待已经无法触发的 Tween.Finished |

实施前以现有 coordinator 测试固定取消的可观察行为，再迁移后端。文档不授权通过改写事务语义来使新后端测试通过。Skippable 由上层 operation barrier 处理；time mode 由共享执行器处理；ReduceMotion 在准备时投影执行策略，不能写回用户持久化目标或再次引入按枚举分支的 easing 替换。

## 6. 命令、配置、扩展和资产迁移

1. 查找仓库内视觉 crossfade（C#、Yarn、tres、tscn、JSON、示例和活动文档），按真实效果迁移到 dissolve 或显式 curtain 序列；音频用法保持。
2. DirectionTransition converter 示例和能力校验使用统一命名。显式 crossfade 给出未注册错误，不做隐式别名。
3. 检查 DefaultCharacterAnimationId：现有默认 fade 不能继续用于外观替换。默认策略按操作选择：进出场 fade，内容替换 dissolve；必要时拆分该配置项，并同步 Godot Resource 和默认值解析测试。
4. 删除 DefaultStageAnimationCurve 与 MotionCurveConverter 注册；所有视觉默认 easing 统一从 EasingId 配置解析。已有配置资产必须同步迁移，不依赖 Godot 忽略未知字段。
5. 删除 ConfigureStageTransitionProviders 回调及示例，插件转场统一走现有 Direction driver 注册扩展点。新 driver 生命周期契约为破坏性更新。
6. 稳定状态恢复使用 Direction state contributor；临时纹理、进度、Tween 不进入存档。检查旧 Appearance/CameraState 是否存在存档字段；如有则更新格式版本并明确拒绝旧版本，不静默丢失字段。
7. 旧 Stage 的测试替换为新服务测试。保留对应行为断言，不通过单纯删除测试缩小验收面。

## 7. 实施阶段与完成条件

每阶段提交需能编译并通过相关测试。阶段之间允许暂时保留旧实现，但本方案最终交付不得留下两套正式入口。

### P1：固定语义与迁移清单

- 增加 cut/move/dissolve/fade 的输入合法性与默认策略测试。
- 列出所有旧 Stage 公共调用、扩展注册和配置资产；区分视觉与音频 crossfade。
- 固定取消、Finish、抢占和 ReduceMotion 的现有可观察行为。
- 输出精确迁移表，确认旧 curtain/wipe 行为由新路径承接。

完成条件：后续工作有可运行的行为基线，且没有未分配迁移目标的旧能力。

### P2：共享 timing 与属性执行

- 增加 AnimationTiming、共享 progress operation；内置/自定义统一 Evaluate。
- 迁移 Actors 的属性动画与相关接口、工厂依赖注入。
- 移除 AnimationTransition、AnimationEase、GodotAnimationSpec、ToAnimationSpec 固定映射。
- 将“不支持自定义 actor easing”的现有测试改为验证支持与中间值；保留未注册 easing 失败测试。

完成条件：角色位置与透明度使用自定义曲线成功；Finish/Cancel/抢占不回写过期状态。

### P3：生产化统一 Dissolve，替换旧外观渐变

- 从已验证的实验提取共享双输入 shader，实现生产输入租约、alpha/颜色空间适配和 prepared transition 生命周期；不将实验的 41 帧缓存采样搬入生产。
- 改造 driver registry 的实例准备契约和 ShaderTransitionDriver。
- 依次迁移 backdrop、view、screen、actor appearance/进出场；Dissolve 共用双输入混合和进度执行器。LayeredCharacter 先生成完整端点输入，再移除逐层 fades；保留图层内容能力。
- 删除 Fade/Dissolve 的重复实现和 backend 的隐式“非 move 即透明度变化”分支。

完成条件：四个 domain 有真实画面测试；端点正确、生产 Dissolve 符合统一混合契约和用户接受的实验效果；图层增量语义保留、临时资源全部清理；自定义 shader 在声明 domain 中实际可用。

### P4：旧 Stage 退役

- 将 CameraManager 直接调用迁到 Direction，抖动迁到 modifiers。
- 搬迁 accessibility 和 CameraShakeAxis；更新 bootstrap、settings projection、测试。
- 删除 Stage 旧模型、backend、manager、provider registry、配置回调和 converter。
- 审计旧 host 和 actor 状态贡献者，迁移必要引用；不扩大为无关的角色架构重写。

完成条件：运行时不注册旧 Stage 服务，新状态保存/恢复与 ReduceMotion 行为正确。

### P5：仓库迁移与最终验收

- 更新脚本、配置资源、demo、插件示例和活动文档；历史 archive 文档可保留旧术语。
- 删除无调用的旧 overload 与相应 .uid；清除不用的 using 和项目文件项。
- 执行完整测试门禁并提交视觉证据；确认已无旧视觉名称/类型运行时引用。

完成条件：第 8 节所有项目满足；清理提交不引入兼容分支。

## 8. 验证计划与交付门槛

### 8.1 单元和执行测试

优先扩展 TimingRegistryTests、ActorManagerTests、PluginAndDirectionExtensibilityTests、StageCommandTests 及现有 Direction coordinator 测试。

- 相同注册 easing 在 Actor/Stage/View/Screen 产生一致的采样结果；自定义非线性曲线在 t=0.25/0.5/0.75 的值符合公式。
- Back overshoot 对 Move 保留，对视觉混合钳制；NaN/Infinity 和抛异常的曲线按失败路径清理。
- cut 非零 duration 拒绝；其他零时长操作不分配转场资源；非法 ID 在发布前失败。
- fade 内容替换拒绝；默认外观替换解析为 dissolve；视觉 crossfade 无别名。
- driver domain 与实际输入双重校验，缺失源/目标不静默降级。
- scaled/unscaled、Skippable、Finish、Cancel、抢占、Reset、Shutdown 全部覆盖；取消后旧回调不能污染新操作。

### 8.2 Godot 画面与资源测试

扩展 `tests/headless/HeadlessTestRunner.Direction.cs`、Actors、LayeredCharacters、ScriptAndState 等现有测试。

以固定内容、固定进度采样生产共享合成器，记录 p=0、0.25、0.5、0.75、1 的像素。新实现以第 3.2 节混合公式为基准；旧图层算法仅作为历史对照，不要求新旧中间帧逐像素相同。实验作为已接受的视觉参考，生产测试不依赖其缓存帧。允许 RGBA8 量化误差，阈值在测试中明确指定。

必测场景：

1. 不透明红→蓝；相同画面→相同画面；验证统一混合公式，后者中途不变暗。
2. 半透明源→半透明目标，置于棋盘背景；验证预乘合成、透明边缘和颜色空间，不出现重复 alpha 乘算。
3. LayeredCharacter：完整默认身体层与源/目标组合正确；Multiply 端点与整体 group_opacity 正确；统一输出包含中间混合值；连续增量表达式、Finish、Cancel 和抢占后的 canonical Appearance 正确，临时节点全部回收。普通角色也使用相同算法，验证大小/锚点。
4. 背景、相机 view、最终 screen 的 dissolve；屏幕截帧没有递归转场层。
5. 进场/离场与相同内容的 opacity fade；同一 beat 的 move + 内容变化遵守既有冲突规则。
6. 完成、取消、异常、节点销毁后，没有遗留 Texture/Material/Viewport、可见临时层和等待不结束的 operation。
7. 多 Stage / 多 View 同时转场互不清理对方输入；新转场抢占旧转场不闪回旧画面。
8. 读档恢复稳定画面，无半途转场；ReduceMotion 仍抑制既有抖动行为。

额外记录有代表性的多角色/多视图场景中临时 viewport 数量、纹理尺寸与释放后数量。必须避免按帧重建材质或纹理；首次实现不要求给出未经测量的帧率承诺。

### 8.3 仓库命令

阶段性验证使用已有脚本：

```bash
bash tools/test.sh --fast
```

最终执行完整发布门禁（包含 Release / export 等现有检查）：

```bash
bash tools/test.sh --full
```

运行需要现有 .NET 与 Godot Mono 环境，Godot 路径可由 GODOT_BIN 指定。工具或环境失败要单独记录，不把未执行当作测试通过。

最终静态检查：

```bash
rg -n 'AnimationTransition|AnimationEase|MotionCurve|CameraViewTransitionKind|StageTransitionKind|StageTransitionProviderRegistry|IStageTransitionManager|ICameraManager|ConfigureStageTransitionProviders|ToAnimationSpec|GodotAnimationSpec' addons/majo_runtime tests demos
rg -n -i 'crossfade' addons/majo_runtime tests demos
```

第一项应无旧体系引用；第二项逐条确认只剩音频语义。另检查 GodotEasingAdapter 和原生 Tween easing 的剩余视觉调用，确保没有双重采样。最终交付附迁移后的 API 清单、测试结果和代表性画面。

## 9. 主要风险及处理

| 风险 | 处理 |
| --- | --- |
| 实验可接受但生产输入存在偏差 | 端点、alpha/颜色空间、锚点和动态目标单独验证；不将用户接受解释为数学等价 |
| 替换渐变时误删图层内容能力 | 保留 resolver、manifest 默认层、Multiply 和 CanvasGroup 的端点合成；差量渐变明确删除 |
| 离屏捕获导致成本增长 | 输入租约复用、限定捕获边界、释放后计数检查；有测量后再优化 |
| shader 仅在自然 Finished 清理 | 清理归 prepared component 的全部终止路径，效果 Dispose 幂等 |
| 注册统一但执行路径仍绕开 registry | Screen、Actor 与自定义驱动做端到端覆盖，不能只测 registry.Contains |
| 删除 Stage 破坏设置/抖动/存档 | 明确共享能力迁移表，旧服务最后删除，保留行为断言 |
| 默认 fade 改义造成脚本破坏 | 同步迁移默认策略、资源和示例；显式错误，不静默替换 |

最终完成的判断是：一个视觉意图经过统一能力解析与进度采样；各对象提供一致的源/目标视觉输入，Dissolve 只保留一个正式混合实现。新增 easing 或修正 Dissolve 不需要分别修改各 backend 的动画算法。

## 10. 实验验证与采用记录

实验：`tests/experiments/LayeredDissolveExperiment.tscn`。左侧采样实际 ChangeAppearance，右侧混合完整源/目标捕获；支持 Ema 外观、进度和背景选择。详见该目录 README。

验证结果：用户反馈“看起来没问题，可以切换到新实现，更新到文档中”。据此批准重构采用统一整图 Dissolve，包括替换 LayeredCharacter 的旧差量层渐变。用户未提供逐案例结果，不推定所有外观、进度或背景组合都已验证。

本次更新确认实现方向；生产代码尚未切换。动态目标、资源成本、整体 opacity 并行变化及事务接入属于 P3/第 8 节的剩余验收项，无需再就替换旧渐变算法本身请求批准。
