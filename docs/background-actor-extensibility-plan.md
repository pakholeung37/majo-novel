# Background Actor 扩展重构草案

日期：2026-09-11  
状态：后续工作备忘，尚未实施；需要时再拆分详细方案。

## 目标与范围

让 Direction 的 `backdrop` 通过已建立的插件、Factory 和 Actor 生命周期机制创建背景，开发者可以注册自定义背景实现，无需修改核心渲染代码。

允许破坏性改造，不提供旧接口和旧存档兼容层。统一背景的注册、定义、创建和外观准备协议，同时保留背景槽、适配布局和相机投影等背景专属语义。舞台动画继续由 Direction 调度。

本轮设想不包含非纹理资源管理泛化、Spine/Live2D 接入，以及两个持续运动画面的实时 dissolve；沿用现有快照转场范围。

## 当前基础与缺口

现有 ActorFactoryRegistry、ActorFactoryCatalog、ActorFactoryContext 和 ActorManager 可作为基础。Character 与 Object 已接入 Direction 的实例创建链路。

背景仍存在两条路径：Runtime 可以创建 Background Actor，但 Direction 的 `backdrop` 直接管理纹理和背景节点。`IBackgroundActor.CurrentView` 也固定为 TextureRect，因此仅注册工厂不能替换舞台背景实现。

## 涉及模块

下表路径相对 `addons/majo_runtime/Runtime/`。

| 模块 | 主要改动 |
| --- | --- |
| `Actors/IActor.cs`、`IActorPresentation.cs`、`Actors/Godot/BackgroundActor*` | 调整背景表现契约，解除对 TextureRect 的强制依赖；内置纹理背景成为该契约的一种实现。 |
| `Actors/ActorFactoryRegistry.cs`、`Actors/Godot/ActorFactoryContext.cs`、`ActorManagerBuilder.cs`、`Bootstrap/Registration/` | 为 Direction 背景提供合适的创建环境，复用定义目录、服务和实例所有权管理。 |
| `Dialogue/Commands/Direction/BackdropCommands.cs`、`Direction/Core/DirectionState.cs` 及相关计划处理逻辑 | 明确背景槽、实例、定义、外观和布局的状态语义，将定义信息贯穿指令到提交。 |
| `Direction/Godot/GodotDirectionStageBackend.cs`、`GodotDirectionRenderHost.cs` | 将直接加载背景纹理改为准备和发布背景实例，接入背景节点及生命周期。 |
| `Direction/Camera/`、舞台与视图渲染、背景 FX 和转场 | 调整内容边界、适配、相机投影和合成逻辑，使其支持自定义背景内容。 |
| `Dialogue/Yarn/Preloading/` | 背景资源清单改由 Factory 提供，复用预加载窗口和现有纹理资源缓存。 |
| `State/DirectionStateContributor.cs`、相关恢复链路、demo 与测试 | 贯通背景恢复、回滚和清理；迁移现有用例并增加自定义背景验证。 |

## 主要技术难点

1. **布局与内容边界。** 现有适配和覆盖检查依赖纹理尺寸。需要明确 Actor 如何提供内容边界，以及 Direction 如何处理适配、背景偏移和无法按普通矩形描述的内容。
2. **背景实例的事务替换。** 区分同一实例切换外观与更换定义或 Factory；准备失败、取消和恢复时必须保留正确的旧背景，并清理候选实例。
3. **多舞台、多视图与相机合成。** 自定义节点需要正确进入背景渲染层，保持相机投影、FX 和转场源/目标画面一致。
4. **资源与节点所有权。** 共享定义和缓存不能共享可变实例状态；预加载窗口释放、转场结束、舞台销毁等操作不能提前释放仍在显示的资源。
5. **状态恢复的一致性。** 保存明确的定义身份和完整外观状态，确保恢复及回滚能够重建相同背景；不扩展到任意插件内部动画参数的通用存档。

## 工作量判断与完成标准

整体难度中等偏高，属于背景子系统的一次完整改造。主要成本在布局、相机合成和事务生命周期，注册机制本身可复用；预计规模大于单纯迁出 Layered，无需重写整个 Direction。

完成时应能通过一个自定义背景插件验证：不修改核心即可配置并用于 `backdrop`；内置背景的布局、预加载、转场、多舞台/多视图、存档恢复与回滚仍正确。后续详细设计再确定接口形状和实施顺序。
