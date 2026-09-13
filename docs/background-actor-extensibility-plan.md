# Background Actor 扩展重构实施方案

创建：2026-09-11  
更新：2026-09-13  
状态：P0–P6 已完成；已修订背景解析为“显式定义优先、缺省按背景资源约定加载”，详见 [脚本说明](script-writing.md#自定义背景与实时转场)。后续已统一实时视觉输入，原 §5.3 快照范围由实时转场取代（见 [实施说明](live-visual-transition-implementation.md)）。本文确定本轮范围、契约和验收门槛；阶段完成状态见末尾清单，具体进度见 [实施进度](background-actor-extensibility-progress.md)。  
代码核对基线：`5e2f51a`。以下路径除特别说明外，相对仓库根目录。

## 1. 目标与范围

让 Direction 的 `backdrop` 通过已有插件注册、Factory、定义目录和 Actor 生命周期创建背景。开发者注册一个自定义背景 Factory、配置定义后，即可通过同一条 `backdrop` 命令显示，不修改核心渲染代码。

本轮交付：

- 内置纹理背景与自定义二维背景共用创建、外观准备、布局和资源持有协议。
- 保留每个 Stage 一个主背景槽，以及 Cover/Contain/Stretch、显式 placement、distance、背景 FX、多 View 投影。
- 同一背景定义切换外观复用 Actor；更换定义或 Factory 创建候选 Actor，事务成功后替换。
- 预加载由实际 Factory 提供清单；存档和回滚通过定义身份与完整外观重建背景。
- 删除 Direction 直接加载背景纹理和依赖 `TextureRect` 的内容分支。

允许破坏性改造，不提供旧命令解析或旧存档兼容层。现有脚本、配置、测试和示例统一迁移。

本轮不做：多背景槽命令、任意三维或无限画布、非纹理资源预加载泛化、Spine/Live2D 适配、插件任意内部动画状态存档、无缝打断合成链。动态背景在稳定显示与转场中持续更新；源、目标内容在消费者释放前保留各自动画。

[实时视觉过渡设计草案](live-visual-transition-design-draft.md)后续可以接管视觉输入更新策略。本方案不依赖其先实施，也不把 Driver 放进背景插件。

## 2. 重构前基线（历史迁移清单）

| 入口 | 当前行为 | 本轮改动 |
| --- | --- | --- |
| `Actors/IActor.cs` 下的 `IBackgroundActor` | `CurrentView` 固定为 `TextureRect`，另有可变 Slot | 改为通用内容准备协议；槽由宿主管理 |
| `Actors/Godot/BackgroundActor.cs` | 加载纹理，订阅容器 Resize，自行 Cover 布局 | 只准备本地坐标内容和边界；适配统一交给宿主 |
| `Bootstrap/Registration/MajoRuntimeBootstrap.Services.ActorsAndStage.cs` | 内置 Background Factory 仅注册 Runtime 域 | 注册 Runtime 与 Direction 两域 |
| `Bootstrap/Registration/MajoRuntimeBootstrap.Services.Direction.cs` | `CreateDirectionActorManager` 的背景根传入 null | 提供背景挂载环境，并复用 retained texture scope |
| `Actors/Godot/ActorManagerBuilder.cs` | 依据域创建全部已注册 Factory，共享编译定义 | 所有 Direction 创建环境都提供有效根，避免新增注册使普通角色创建失败 |
| `Dialogue/Commands/Direction/BackdropCommands.cs` | 主参数转为 `ResourceKey`，提交 `BackdropState` | 主参数改为定义 ID，外观解析交给共享定义解析器 |
| `Direction/Godot/GodotDirectionStageBackend.cs` | `PrepareBackdropAsync` 直接加载目标纹理；`PreparedBackdrop` 持有源/目标纹理 | 改为候选背景 Binding、内容租约和事务组件 |
| `Direction/Godot/GodotDirectionRenderHost.cs` | 发布纹理；布局、覆盖检查直接读纹理尺寸 | 发布背景呈现 Binding；从内容边界计算布局 |
| `Dialogue/Yarn/Preloading/` | `backdrop` 被解析为资源路径，并直接生成 Texture2D 条目 | 解析定义、外观和 Stage，再向解析后的 Factory 查询 |
| `State/DirectionStateContributor.cs` | SchemaVersion 为 5，读取 3/4/5；背景只存 Resource、Fit、Tint、Placement、Distance | 新 schema 存定义/Factory/外观，明确拒绝旧格式 |

上述 `Actors/` 等短路径均位于 `addons/majo_runtime/Runtime/`。目前背景过渡已把每个 View 的源、目标背景 ViewportTexture 交给 Driver；可复用这个合成层，但这并不等于已有任意动态 Actor 的输入与资源持有协议。

## 3. 状态与命令：先明确身份

### 3.1 槽、定义、实例的边界

- 槽身份固定为 `(StageId, main)`。保留 `StageState.Backdrop` 单值，不改成多槽字典。
- `DefinitionId` 指向共享的不可变 `ActorDefinition`，决定 Factory 和配置数据。
- `FactoryId` 是解析后真实 Factory 身份；写入稳定状态，用于恢复时发现配置变化。
- Actor 实例属于一个 Stage 的主背景 Binding。相同定义在两个 Stage 显示时创建两个实例，禁止共享可变节点或外观状态。
- 候选 Binding 使用独立 Manager 和隐藏根，允许新旧实例暂时使用相同局部 Actor ID `main`；全局身份由 Stage、Binding generation 和槽共同限定。
- `IBackgroundActor.Slot/SetSlot` 从 Actor 契约移除。Runtime 独立使用背景时也由宿主维护槽映射，不能把 Direction 舞台状态写进插件实例。

拟定稳定状态：

```csharp
public sealed record BackdropState(
    string DefinitionId,
    string FactoryId,
    string Appearance,
    BackdropFit Fit,
    DirectionColor Tint)
{
    public BackdropPlacement? Placement { get; init; }
    public float Distance { get; init; } = 1;
}
```

`ResourceKey` 不再进入背景领域状态。纹理路径、默认外观、外观到资源的映射属于 FactoryData。`Appearance` 必须是已解析的完整值，不能把增量外观指令存入快照。状态检查拒绝空身份、非法 Fit/Tint/Placement/Distance。

### 3.2 命令形状与默认规则

目标脚本示例（实施后生效）：

```text
<<backdrop Court appearance:Day stage:main>>
<<backdrop Court appearance:Night>>
<<backdrop PaintedRoom at:50,50 origin:0.5,0.5 scale:1 distance:far>>
```

主参数是背景定义 ID；新增 `appearance:`，保留现有 Stage、transition、at、origin、scale、distance 参数。转场参数继续使用 `TransitionArguments` 的既有语法。

确定以下规则，命令执行、beat 规划与预加载不得各自推断：

1. 显式定义优先，且其 Factory 必须支持 Direction；无定义时使用内置背景 Factory，按原背景目录与 ID 解析资源，不要求新增配置。显式定义错误不触发回退。
2. 未提供 appearance 时始终使用 Factory 的默认外观；默认外观缺失时报定义错误。该规则不依赖当前显示状态。
3. 支持增量外观的 Factory 使用相同定义的前一完整外观作为基线；换定义时以目标默认外观为基线。默认实现只支持完整外观替换。
4. 同一定义且未提供 distance 时保留前值，换定义时重置为 1。判断依据由旧 Resource 改为 DefinitionId + FactoryId。
5. placement 保持当前命令行为：任一 at/origin/scale 被提供时，用默认值补齐其余字段；三者均省略时为 null，使用 Fit。不会隐式继承旧 placement。
6. Fit、Tint 保留领域/API 能力，命令默认 Cover、White；本轮不额外扩大脚本参数面。
7. `backdrop fx` 继续定位 Stage 的主背景槽，与定义 ID 无关。保留 `fx` 为命令保留字并在定义用于 backdrop 时检查。
8. 背景清空仍由领域 patch 的 null 表示；本轮不引入新 clear/hide 语法。

不要在命令层通过当前已提交状态解析同一个 beat 中后续外观。新增 `BackdropIntent` 或等价未解析 patch，由规划器按该 beat 的工作状态顺序解析，最终产出完整 `BackdropState`；沿用现有冲突路径 `stage/{stageId}/backdrop` 和完成屏障。

### 3.3 共享定义解析

从 `ActorManager.ResolveDefinition` 提取可无实例调用的解析服务，建议命名 `ActorDefinitionResolver`。目录仍使用 `ActorFactoryCatalog`，不建立第二套背景目录。

解析结果至少含编译定义、Factory 身份、初始/完整外观。背景通过 ResolveBackdropAppearance 共用显式定义优先与逻辑资源键回退；Character/Object 的既有约定解析保持原行为。默认外观与纯外观解析能力放在 Factory 元数据侧，现有 Actor 外观解析可委托给它，避免预加载为了推断资源创建节点。

为预加载提供按目标域解析和查询的入口。不能直接使用 Runtime 域 Manager 判断 Direction 专用背景插件是否存在。实现可为预加载建立有效但隐藏的 Direction 工厂环境，复用注册的 Factory 元数据方法；只允许构造 Factory，不创建 Actor，且随预加载服务释放环境。

## 4. 背景内容契约

### 4.1 只准备内容，不执行转场

新增 `Actors/IBackgroundPresentation.cs`，接口职责如下。名称为本方案拟定，落地时可按现有命名习惯微调，生命周期语义不得省略。

```csharp
public interface IBackgroundActor : IActor, IGodotActorView,
    IStateMutationParticipant
{
    Task<IPreparedBackgroundAppearance> PrepareBackgroundAppearanceAsync(
        string resolvedAppearance, CancellationToken cancellationToken);
}

public interface IPreparedBackgroundAppearance : IAsyncDisposable
{
    string ResolvedAppearance { get; }
    IBackgroundContent Target { get; }
    void Commit();
    void Rollback();
    void ConfirmCommit();
}

public interface IBackgroundContent
{
    CanvasItem Root { get; }
    Rect2 LocalBounds { get; }
    BackgroundCoverage Coverage { get; }
    IDisposable Retain();
}
```

P2 已实现这些契约，并为 Runtime 宿主增加 `CurrentContent` 与同步 `ContentChanged` 通知；提交失败后的回滚会再次通知。`IStateMutationParticipant` 使用现有 State 命名空间；不要求背景实现角色的 transform、visibility animation 或 appearance transition 接口。

契约要求：

- `SceneNode` 是 Actor 生命周期根，必须位于 Factory 获得的挂载根内；插件禁止挂到全局 SceneTree。内容 Root 可以是 Node2D 或 Control 等 CanvasItem。
- 本地内容保持原始尺寸与原点，不自行执行 Stage Cover、相机变换或背景 FX。
- LocalBounds 必须有限、宽高为正，且对一次准备后的内容不可变；切换尺寸通过重新准备外观处理。允许非零或负坐标原点。
- Coverage 取 `OpaqueRectangle` 或 `Unknown`。非矩形、透明内容可提供保守包围盒，但只能声明 Unknown，不能把包围盒当作完全覆盖的证明。
- Prepare 返回时资源已加载、候选节点可渲染，但旧稳定外观和画面不变。屏幕首帧就绪由 Godot 渲染宿主在布局和隔离渲染后确认。
- Commit 同步、无资源 I/O，将候选内容与 Actor.Appearance 原子切换；Rollback 恢复旧内容和外观。二者不能通过再次异步加载实现。
- ConfirmCommit 表示整笔 Direction 事务已确认。旧内容只有在确认、回滚权利结束且所有消费者解除引用后才能回收。
- Dispose 在未提交时销毁候选；提交但未确认时回滚；已确认时结束准备对象但保留 Actor 的稳定目标。重复释放必须幂等。
- 内容租约覆盖节点及其资源；Actor.Destroy 先禁止新租约，实际内容销毁延后到已有租约释放。宿主正常路径仍必须先解绑消费者再销毁 Actor。
- 稳定时内部动画可以继续更新；本轮不要求 Root 的动画时间、物理或随机数支持精确恢复。

`IActor.SetAppearance` 保留同步稳定设置用途；Direction 路径必须走准备对象，禁止先改活动 Actor 再截图补救。内置实现将纹理加载和租约下沉到内容对象，删除自身容器 Resize/Cover 逻辑。插件不接收 Driver，不实现 dissolve shader。

### 4.2 背景挂载和工厂环境

增加包含 CharacterRoot 与 BackgroundRoot 的宿主参数对象，供 `ActorFactoryContext` 和创建 Manager 的委托共同使用。首期保留 BackgroundRoot 为 Control，尺寸由宿主设置；插件可在其下创建 Node2D/Control 内容，不受 TextureRect 限制。

`ActorManagerBuilder` 当前会构造同域所有 Factory。因此修改必须覆盖所有 Direction Manager 创建点，包括普通角色 Binding：每个环境提供有效背景根，不能只在背景创建点补根，也不能继续依赖访问时抛异常的 null 根。

每个背景 Binding 拥有独立隐藏挂载根、Manager 和 retained texture scope，复用 `GodotDirectionActorTreeBackend.Binding` 的所有权模式，但单独实现 `GodotDirectionBackdropBinding`，不把背景塞进 `StageState.Actors`。

绑定创建顺序：

1. 创建隐藏环境和 retained scope，初始化 Manager。
2. 用局部 Actor ID `main`、显式 DefinitionId、解析后的 FactoryId 和 Dormant 模式创建。
3. 检查实际实例实现 `IBackgroundActor`，Factory 身份匹配，SceneNode 属于该环境。
4. 准备完整目标外观，取得内容租约，验证边界和挂载限制。
5. 交给呈现宿主布局并准备首帧；完成后才能成为可提交候选。
6. 任一步失败，按反向顺序清理，保留原异常并聚合清理异常。

工厂在主线程操作 Godot 节点；异步加载恢复后需遵守现有 Godot 调度机制。取消后继续返回的迟到结果也必须释放，不能重新发布。

## 5. 布局、投影与合成

### 5.1 一处负责布局

扩展 `DirectionLayoutCalculator`，输入 Stage 参考尺寸、LocalBounds、Fit 和 Placement，输出内容到 Stage 的变换、裁剪矩形与可见边界。移除对 `TextureRect.StretchModeEnum` 的核心依赖，纹理只是内容的一种实现。

设内容边界为 B，尺寸为 b，Stage 尺寸为 S：

- Cover：统一缩放 `max(S.x/b.x, S.y/b.y)`，居中，裁剪到 Stage 矩形。
- Contain：统一缩放 `min(S.x/b.x, S.y/b.y)`，居中；空白保持现有 Stage 底色。
- Stretch：分别缩放两个轴使其填满 Stage。
- 显式 placement：优先于 Fit。anchor 为 `B.position + origin * B.size`，按 placement.Scale 缩放，使该 anchor 落在 StageCoordinates 解析的 at 位置。
- 所有分支都减去本地边界原点；禁止仅把 textureSize 替换成 bounds.Size 而丢掉 bounds.Position。

裁剪由宿主提供固定矩形容器或必要的离屏表面。纹理 Cover 的现有效果包含裁剪，不得让自定义节点缩放后溢出原背景区域。窗口/参考分辨率变化时重算布局，不重新创建 Actor。

覆盖检查使用投影后的实际可见边界。OpaqueRectangle 执行现有 uncovered-edge 检查；Unknown 只报告无法证明覆盖，不伪报完全覆盖，不因透明孔洞误判为矩形填充。预期会出现的 Contain 留边应与现有告警策略一致。

### 5.2 分离内容宿主与输出表面

`GodotDirectionStageSurface` 中的内容 TextureRect 替换为背景内容宿主和源内容宿主；View 上用于显示合成纹理的 TextureRect 可以保留。移除的是内容类型限制，不是所有 TextureRect。

- 活动内容进入 BackgroundVisibilityLayer，源内容进入 BackgroundTransitionVisibilityLayer；递归处理 CanvasItem，并约束插件新增节点继承正确层。插件自建 CanvasLayer 或绕过宿主渲染层不属于本轮支持范围，准备时给出明确错误。
- 候选内容在独立隔离表面准备，不能因为“隐藏父节点”导致无法获取首帧，也不能泄漏到活动 View。
- 每个 View 保留自己的背景投影；距离来自各自 source/target BackdropState。一个 Stage 的 Actor 只模拟一次，多镜头不克隆 Actor。
- BackdropFxRoot 和 `ActorFxSlot.Backdrop` 继续归槽所有。更换 Actor 不重复注册 FX，也不重复叠加 Tint/Opacity。
- Actor 稳定基础 Tint/Opacity 在 Direction 管理下设为单位值，BackdropState.Tint 由宿主一次性应用；插件内部颜色属于内容自身，不能再应用 Stage Tint。

### 5.3 转场语义（实时输入修订）

Cut 直接发布已就绪目标。非 Cut 保留旧、新内容的实时离屏输出，再经各 View 的背景投影交给公共 `GodotVisualTransition` 与现有 Driver 混合。内容一份、投影多份；源与目标在转场期间均可持续更新。

准备等待 GPU 首帧，输入使用统一预乘 alpha 约定；输入不得采到自身过渡输出。原内容与合成输出不能重复绘制。无源或无目标使用透明输入，Stage 底色单独保持。

转场期间新增或移除 View（包括实时镜头的临时旧 View）同步接入/退出背景混合，并继承当前进度。窗口尺寸变化重算投影，不重建内容。

旧版本这里规定冻结两路内容，现已删除该默认行为。Freeze 仅由调用方显式请求，例如恢复遮罩；背景转场不创建快照。插件在租约持有期间必须维持内容的绘制与动画，不能因外观确认而停止旧内容。内部动画状态精确回溯仍不属于本轮协议。

## 6. 事务、打断与所有权

`GodotDirectionStageBackend` 新增背景 Binding 管理；`PreparedBackdrop` 改为持有旧 Binding、候选内容/Binding、布局、实时输入和 Driver 资源。所有能失败的加载、节点验证、首帧准备尽量在 Prepare 完成，Commit 不等待 I/O。

| 变化 | Prepare | Commit / 确认后的处理 |
| --- | --- | --- |
| 状态完全相同且无显式转场 | 复用当前呈现 | 不创建内容 |
| 仅 Fit/Tint/Placement/Distance 改变 | 保留同一内容，准备目标布局与必要快照 | 发布布局；失败恢复原布局 |
| 同定义/Factory，外观改变 | 在同一 Actor 准备可回滚候选内容 | 同步提交外观；旧内容待确认且租约归零后释放 |
| 定义或 Factory 改变 | 新建 Dormant Binding | 原子换绑定；旧 Binding 等回滚权利与转场消费者结束后释放 |
| 目标为空 | 持有旧内容，准备透明目标 | 清空槽；旧实例按相同规则释放 |

事务顺序：

1. 以 generation 标记准备，持有稳定旧 Binding 和内容。
2. 完成候选准备、布局、首帧和 Driver 准备；任一失败，释放候选，活动背景不变。
3. 在 `DirectionCommitToken` 下发布内容和完整状态，保留同步回滚能力。
4. 整笔事务 `ConfirmCommit` 后才允许退休旧稳定引用；即使零时长转场先 settle，也不能提前销毁旧背景。
5. 动画完成或 Finish 时发布目标稳定内容、解绑 Driver/输入消费者，随后释放过渡租约。
6. Rollback 恢复旧 Binding、布局、FX 绑定和 Actor.Appearance，停止时钟，清理候选；不能只恢复领域 JSON。

连续替换沿用“先完成当前视觉过渡，再开始下一次”的策略。协调器必须先 settle 同路径操作，再准备下一笔的源；不能在准备后才调用旧 Complete，使下一笔持有过时源画面。已确认事务的历史动画进度不恢复，回滚到本笔开始时的稳定源。

Commit 中途失败也调用 Rollback。generation 保护所有迟到回调；旧操作 Dispose 不得清空新操作的呈现。Finish、Rollback、Dispose 和 Stage 删除的重复调用均需安全。

释放顺序：停止回调 → 解绑 Driver 与 View 消费者 → 释放视觉输入/内容租约 → Manager Shutdown/Actor.Destroy → 删除挂载根 → 释放 retained scope。单项失败继续清理其余资源，最终聚合错误。活动内容保持自己的持有引用，不能依赖预加载窗口或已结束的准备对象存活。

Reset/Shutdown/Stage 删除同时遍历活动过渡和背景 Binding。当前 StageBackend 的 reset/shutdown 主要清理角色树，迁移时必须显式补齐背景，不依赖 RenderHost 清纹理作为 Actor 销毁的替代。

## 7. 预加载与资源

`BackgroundActorFactory.GetPreloadResources` 返回解析后外观用到的纹理条目，包括 OverridePath；不能沿用其当前默认空实现。自定义背景 Factory 可返回多张纹理，例如分层插画的远景和前景。

`YarnPreloadPlanner` 为 backdrop 提取定义、appearance、stage；继续排除 `backdrop fx`。`YarnWindowPreloader` 使用第 3 节的共享解析服务，以 Direction 域查询实际 Factory，删除 `BackdropId.ToResourceKey` 的直接纹理分支。

要求：

- 静态已知定义/外观使用与执行相同的默认规则。增量外观无法确定前态时，只预加载可证明需要的资源；运行时准备负责正确性，不凭空猜测。
- 保留纹理本地化、fallback、override、去重、预算和 pinned 资源排除行为。
- 首期只接受 Texture2D 预加载条目；其它类型明确记录不支持的诊断并跳过，禁止强行按 Texture2D 查询。插件非纹理加载暂由自身准备方法管理。
- 预加载失败仍可按现有策略回退正常加载；执行时显式定义的 Factory 或外观错误必须报错；无定义背景缺失资源时报告资源加载错误。
- 预加载窗口退出不能释放显示中或转场中的资源。两个 Stage 共用纹理可以共享缓存，但使用独立租约和内容实例。
- locale 变化的重准备继续走同一事务路径；稳定状态保存逻辑定义和外观，不保存本地化后的物理路径。

## 8. 存档、恢复与迁移

`DirectionStateContributor.SchemaVersion` 从基线 5 升为 6；若实施时其他变更已占用版本号，采用下一未使用版本。移除该 contributor 对旧 3/4/5 的读取分支，并以清晰版本错误拒绝旧 Direction 记录；其它 contributor 的版本策略不在本轮改造范围。

背景 payload 保存 DefinitionId、FactoryId、完整 Appearance、Fit、Tint、Placement、Distance。禁止序列化节点、Viewport、租约、FactoryData 或临时 generation。

恢复分两步：先验证全部背景定义存在、Factory 身份一致、域支持、外观可解析；再通过正常候选 Binding 路径准备各 Stage。全部准备成功后参与同一恢复事务。某个 Stage 失败时撤销已准备目标，保持恢复前画面和状态。

同一个定义 ID 被配置成另一个 Factory 时，恢复明确失败，不能使用新 Factory 悄悄解释旧存档。对于不同 FactoryData 但同身份的资产版本变化，本轮不新增内容哈希兼容保证。

Direction 背景只由 DirectionStateContributor 保存；私有 Binding Manager 不注册成全局状态来源，避免重复保存。Runtime 独立 Actor 路径同步迁移背景契约与测试。

迁移清单：

- 搜索所有 `BackdropState` 构造、Resource 访问、BackdropId 和命令依赖注入，替换领域与命令路径。
- 普通背景保留原脚本 ID 与资源目录，无需定义映射；只有自定义 Factory 或外观映射需要显式定义。
- 更新所有旧存档 fixture、回滚用例、命令生成器/API 快照和测试 Fake。
- 更新 `docs/script-writing.md`、示例配置和插件示例说明；公开说明新命令与 schema 断点。
- 仅在确认无调用者后删除 BackdropId 或其旧转换方法，以及 RenderHost 的旧发布纹理接口，避免永久保留双路径。

## 9. 实施阶段与退出条件

阶段按依赖顺序完成。每阶段保持可编译；短期内部适配代码必须标记并在 P6 删除，不作为发布兼容层。

| 阶段 | 主要交付与文件 | 退出条件 |
| --- | --- | --- |
| P0 基线与迁移清单 | 枚举脚本背景和存档 fixture；记录当前纹理背景 GPU 基线；核对现有调用者 | Cover/Contain/Stretch、placement、distance、转场和多 View 均有可复现基线 |
| P1 定义与领域状态 | ActorDefinitionResolver、Factory 纯外观解析；BackdropState/intent/规划器/命令；Yarn 引用解析 | 同 beat 多次 backdrop、默认值、缺失定义及错误 Factory 单测通过；生成命令可编译 |
| P2 内容与创建环境 | IBackgroundPresentation、内置 BackgroundActor、FactoryContext、Builder、Bootstrap、Runtime 宿主 | 两域可创建；普通角色 Manager 不因背景根缺失失败；失败准备不改旧外观 |
| P3 舞台接入与布局 | GodotDirectionBackdropBinding、StageBackend、StageSurface、RenderHost、LayoutCalculator | Cut 路径完全使用 Actor；自定义 Node2D/Control 内容正确适配、裁剪、投影和 FX |
| P4 事务与转场 | PreparedBackdrop、实时输入准备、generation、确认/回滚与清理 | dissolve/wipe、快进、连续替换、失败回滚、Stage 删除均通过；无残留节点或租约 |
| P5 预加载与状态恢复 | YarnWindowPreloader、Factory 清单、DirectionStateContributor/Restore、示例配置 | 预加载/运行资源一致；新 schema 往返和跨 Stage 失败恢复正确；旧格式明确拒绝 |
| P6 插件验收与清理 | 新示例插件、脚本迁移、文档、删除旧纹理分支与临时适配 | 下述验收矩阵通过；没有需要修改核心的背景特例 |

P1 的状态变更需同步修改序列化结构以保持编译，完整恢复语义与验证在 P5 完成；不能发布中间阶段。P2/P3 优先跑通自定义背景 Cut 的最小纵向链路，发现挂载或布局问题后先修复再扩转场。

预计规模中等偏高，主要成本是事务呈现与布局迁移。以阶段退出条件控制进度，不用“注册完成”代替整体完成。

## 10. 测试与验收矩阵

### 10.1 单元测试

优先扩展已有 `ActorFactoryRegistryTests`、`ActorManagerTests`、`DirectionPatchTests`、`DirectionRuntimeTests`、`CompositeDirectionTransactionTests`、`BackdropPlacementTests`、`DirectionLayoutCalculatorTests` 和 `YarnPreloadPlannerTests`。

| 类别 | 必须覆盖 |
| --- | --- |
| 解析 | 显式自定义 Factory 优先、Direction-only 注册、缺失定义、错误域、缺失默认外观、完整外观解析 |
| 状态 | 同 beat 依序解析、换定义重置 distance、placement 默认值、null 清空、Factory 身份恢复约束 |
| 布局 | 三种 Fit、placement 优先、非零/负原点、横竖图、非法边界、Unknown coverage |
| 事务 | Prepare 失败、Commit 中途失败、确认前完成、同实例外观回滚、换定义回滚、迟到取消结果、重复释放 |
| 预加载 | 默认/显式外观、多纹理、override、本地化、未知增量、非纹理诊断、无 Actor 实例创建 |
| 状态恢复 | 新 schema 往返、旧 schema 拒绝、第二 Stage 准备失败时第一个候选清理 |

### 10.2 Godot 集成与 GPU 验证

扩展 `tests/headless/HeadlessTestRunner.SpatialBackdrop.cs`、`ActorSlots.cs`、`Actors.cs`、`Preloading.cs` 和 Direction 相关测试。Headless 用于状态、挂载和生命周期；视觉验收必须在真实渲染环境读取 GPU 输出。

- 内置背景与 P0 基线比较：三种 Fit、显式 placement、缩放/距离、窗口尺寸变化、背景 FX 和多 View。
- 转场采样 0、0.5、1：dissolve/wipe 的端点、预乘 alpha、透明内容、无重复绘制、无旧帧或黑帧。
- 两个 View 观察同一 Stage，以及两个 Stage 使用相同背景定义：投影独立、实例独立、纹理可共享。
- 背景与镜头转场重叠、连续背景切换、快进、rollback、restore、Stage 删除、runtime shutdown。
- 对取消与错误分别注入“纹理加载后”“首帧前”“提交中”“确认前”故障，检查活动背景、节点数、scope/租约数。
- 重复切换至少 100 次，等待异步清理和 QueueFree 后，临时节点、快照、租约数回到基线；缓存只允许保留已声明的稳定缓存项。

新增验收插件建议放在 `demos/background-extension/`：注册 `sample.composite-background`，使用普通 Node2D 下多个 Sprite2D/Polygon2D 组合背景，提供 Day/Night 两种尺寸或原点不同的外观，并返回多纹理预加载清单。至少一个透明/非矩形外观声明 Unknown coverage；可附简单动画验证稳定显示支持及快照转场语义。插件只能依赖公共契约，不引用 GodotDirectionRenderHost 等 internal 类。

验证命令按现有入口执行：

```sh
bash tools/test.sh --unit
bash tools/test.sh --fast
bash tools/quality.sh --check
bash tools/test.sh --full
```

阶段开发使用相关单元/集成测试；最终执行完整 gate。上述脚本不能替代真实 GPU 的视觉比较；P0/P6 分别保存截图、关键帧和资源统计至 `artifacts/background-actor/`，记录 Godot 版本、渲染后端与运行命令。

## 11. 完成定义

- [x] P0：现状与视觉基线记录完成。
- [x] P1：定义、命令和状态身份贯通。
- [x] P2：背景内容契约与两域 Factory 环境完成。
- [x] P3：Direction 背景显示完全通过 Actor，布局与相机结果正确。
- [x] P4：转场、提交确认、回滚和清理完整。
- [x] P5：预加载与新 schema 恢复通过验证。
- [x] P6：独立自定义背景插件验收、脚本迁移和旧路径删除完成。

最终判定：仅新增插件注册、定义配置与内容实现，即可通过 `backdrop` 显示并切换自定义背景；内置背景行为通过基线回归；两个 Stage 不共享可变实例；失败、取消、恢复和退出路径不丢旧画面、不泄漏资源。实时双画面过渡仍作为独立后续工作。
