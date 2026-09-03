# Majo Runtime 插件化改造计划（修订版）

## 结论

原计划方向正确，但缺少注册时序、能力域、并发实例、资源所有权和失败回滚定义。当前修订先冻结底层 SPI，再按垂直切片增加能力；允许 breaking change，但每个阶段必须有 fake/headless 验收。

原计划的主要风险：插件命令可能在 Catalog seal 后注册；Camera modifier 以每个 View 单实例保存而无法叠加；任意自定义 `via` 可能被错误当成 fade；`IActorVisual` 只是孤立接口，没有接入 Actor 生命周期和 Operation。

## 已实施基线

- `IMajoPlugin` 提供 `ConfigureServices`、`Configure(PluginRegistrationContext)` 和异步 `Initialize/Reset/Shutdown`。
- 插件在 service graph build 前配置；Host 初始化后异步初始化插件；失败时逆序回滚已初始化插件。
- 插件可注册 Command、Transition Driver、Camera Modifier 和 Actor factory 配置。
- 插件支持显式 `Dependencies`，初始化按依赖拓扑排序，Reset/Shutdown 逆序执行；缺失依赖和循环依赖在初始化前以类型化异常失败。
- 插件注册在 bootstrap seal 后拒绝追加，重复插件 ID 使用类型化注册异常；Transition Driver 对重复 ID 默认拒绝，避免静默覆盖。
- Actor factory 配置同时应用于主 ActorManager 和 Direction 临时 ActorManager，避免插件 actor 在两个渲染路径行为不一致。
- 插件 reset/shutdown 会继续清理其余插件并聚合异常；初始化失败会包含当前插件在内逆序回滚，避免部分初始化资源泄漏。
- `DirectionTransitionId` 替代封闭 enum；`TransitionDomains` 区分 View、Stage、Screen、Actor。
- Transition registry 拒绝意外重复 ID，支持按 domain 检查能力。
- Camera modifier 支持同一 View 的多个实例，delta 平移/旋转求和、缩放相乘；单个实例结束不会清除其他实例。
- 内置 modifier 使用 `IFrameClock`；Reduce Motion 保持 duration/wait 语义。
- 瞬态 cue 已改为 `ITransientCueHandlerRegistry`：`TransientDirectionCueExecutor` 只负责 handler 分发、operation 聚合、barrier 等待和取消清理；`CameraFx`、`Flash`、`Curtain` 各自使用独立 handler，插件可注册新的 transient cue 类型。

## 稳定 SPI

插件所有注册必须发生在 Runtime `Initialized` 前；运行时开始后禁止添加命令、factory 或 driver。插件 ID 必须唯一，建议使用反向域名。

Transition Driver 只负责视觉过渡，不负责 durable state、Operation 生命周期或 Yarn 解析。Actor transition 只有在拥有明确 source/target actor surface 和终态语义后才开放，禁止通用 fallback 为 fade。

Camera modifier 是有限 transient effect，不写入 Game State，不修改 base Camera Pose，必须自行清理 delta，并通过 FrameClock 采样。持续跟随和自动构图应单独建 Camera Driver。

逻辑 `IActor` 不应依赖 Sprite2D。渲染能力应逐步拆成 `IActorAppearanceCapability`、`IActorAnimationCapability`、`IActorBoundsCapability`，Godot Node 仅存在于集成层。`FactoryData` 暂时兼容，新插件应使用带 `FactoryId + schemaVersion` 的独立 Resource。

## 后续实施顺序

### Phase 1：契约和注册时序（已完成）

统一 Plugin、Transition、Modifier、Actor factory registry 的 ID、重复策略、seal 和 diagnostic；插件依赖声明和拓扑排序已落地。Bootstrap 旧回调暂时保留为兼容入口，后续迁移后删除。

验收：插件 Command 进入 Yarn catalog；初始化失败完整回滚；初始化后注册、重复 ID、缺失/循环依赖都失败。

### Phase 2：Camera Modifier（部分完成）

`CameraModifierRegistry`、`CameraFxCue`、叠加 delta、FrameClock、Reduce Motion 已完成。旧的 `camera shake` 命令、`ShakeCue`、`IDirectionShakeManager` 和 `GodotDirectionShakeManager` 已删除，所有镜头效果统一走 `camera fx` + `ICameraModifierManager`。瞬态 cue handler registry 也已完成。下一步补齐实例级 Operation ID/Group、cancel/complete/skip，并将方向 cue 字符串分支改为 schema-driven provider；覆盖叠加、取消竞态、Reset/Shutdown、Reduce Motion 和 headless。

注意：当前静态 command binder 仍会拒绝未知命名参数；插件可注册自己的 command/schema，但不能动态向内建 `camera fx` 参数类追加字段。若要实现真正 schema-driven 的可扩展 `via` 参数，下一阶段应增加参数 schema/catch-all bag，并在 Yarn catalog 生成前完成校验。

### Phase 3：Transition Domain

`via` 使用 domain-aware lookup；View/Stage/Screen 共用 envelope，但 backend 各自管理 capture、surface 和终态；ShaderMaterial、rule texture、capture texture 明确所有权和释放；不支持的 domain 立即报 authoring error。

### Phase 4：Actor Capability

移除 `ICharacterActor` 的 Sprite 专属 API；Character/LayeredCharacter 的 appearance、bounds、transform 委托 visual adapter；factory 提供 capability 集合和 definition schema validator；增加 FakeActorVisual 与非 Sprite 测试 actor。

当前 actor factory 扩展已接入主 ActorManager 和 Direction 临时 ActorManager；visual capability 仍未实现，不能把现有 `IActor` 直接宣称为 Live2D/Spine SPI。

### Phase 5：State、资源和诊断

插件 state contributor 声明稳定 key、schema version、scope、migration；资源通过 `IResourceManager` lease 管理；诊断输出 plugin/provider/version/lifecycle/operations/leases；版本不匹配在启动阶段失败。

### Phase 6：可选高级能力

按需实现 Camera Rig/Driver、Follow/Focus、多输出 Compositor、Live2D/Spine adapter；运行时热卸载暂不作为目标。

## 非目标

- 不默认程序集扫描，保持显式注册以兼容 AOT/export。
- 不把 Godot 类型带入 Direction Core、State Core 或 modifier 算法。
- 不让 shader driver 隐式决定存档状态。
- 不为旧 `cameraMove` 等命令增加 alias，除非单独提供迁移工具。

## 验证

```sh
dotnet build 'Majo Rebuild.csproj' --no-restore
dotnet test tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj --no-restore
./tools/test.sh --fast
```
