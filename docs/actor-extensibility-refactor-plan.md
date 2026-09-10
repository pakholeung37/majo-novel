# Actor 扩展机制重构方案

日期：2026-09-10  
状态：实施方案，尚未修改运行时代码  
目标：以 LayeredCharacter 从核心迁出为验证，建立语义一致、无需修改核心即可添加角色实现的扩展机制。

## 1. 范围与决策

本方案允许破坏性改造，不提供旧注册回调、旧配置路径或旧命名空间的兼容层。以仓库内调用点、demo、测试一起迁移为交付单位。旧存档不作为验收对象；当前版本生成的存档和回滚必须正确。

本轮完成：

1. 一份封闭的 Factory 注册目录，同时服务普通 ActorManager、预加载和 Direction 的角色树。
2. 明确每个 Factory 的创建环境、资源和节点所有权。
3. 分离通用 Actor 行为与纹理实现，提供可直接派生的角色基类。
4. 自定义角色使用自己的 Resource 配置；核心不解析 layered manifest、图层表达式或材质约定。
5. Direction 统一舞台动画调度，删除 Actor 自身的动画队列和 Change* API。
6. LayeredCharacter 迁到 `demos/manosaba/Actors/Layered/`，由 manosaba 显式安装插件。
7. 支持一份角色资源创建多个独立实例，贯通角色命令、预加载、保存和恢复。
8. 完整外观状态与增量请求分离，修正迁移必需的状态语义。

用户已确认：动画入口一步到位统一；只迁出 Layered，不解决非纹理资源问题；本轮支持资源与实例身份分离。

本轮不做实时 dissolve、Spine/Live2D SDK、非纹理预加载泛化、非纹理角色完整接入验证、任意模型参数存档、插件分发系统。现有 Layered 快照转场行为保持。动画统一复用 Direction 已有槽位、事务和完成语义，不另建调度器。

## 2. 代码调查与实际约束

以下路径均相对仓库根目录；结论来自当前源码静态检查，不代表已运行测试。

| 代码位置 | 已有行为 | 对本次重构的影响 |
| --- | --- | --- |
| `Runtime/Actors/ActorManager.cs`（下文 Runtime 均指 `addons/majo_runtime/Runtime`） | 按字符串 FactoryId 注册；初始化前可替换；负责 pending 创建、销毁、恢复候选 | 保留实例管理职责，不另建一套 ActorManager |
| `Runtime/Plugins/IMajoPlugin.cs` | 保存 `Action<ActorManager>`；Configure 在服务图构建前，Initialize 在服务初始化后 | 插件注册应声明构建函数，不能等 Initialize 时再补注册 |
| `Runtime/Bootstrap/Registration/MajoRuntimeBootstrap.Services.ActorsAndStage.cs` | 创建内置 Factory 后，调用插件回调及 Bootstrap 回调 | 普通 Manager 和预加载使用这条路径 |
| `Runtime/Bootstrap/Registration/MajoRuntimeBootstrap.Services.Direction.cs` | 为指定 root 再创建 Factory；只有插件回调，没有 Bootstrap 的 Actor 回调；纹理 loader 建立 retained scope | 当前两个入口不对称；不能把普通 Manager 的 Factory 实例直接复用到 Direction |
| `Runtime/Direction/Godot/GodotDirectionActorTreeBackend.cs` | 每个新 binding 创建独立 root 和 Manager；创建、恢复、失败时销毁这一组 | Factory 实例可能很多，不能在构造函数里进行昂贵加载或订阅全局事件 |
| `Runtime/Actors/IActor.cs`、`IActorPresentation.cs` | ICharacterActor 之外还有 transform/appearance/visibility 三个接口；Binding 运行时逐个转换 | 实现角色的完整契约不直观，应提供聚合接口 |
| `Runtime/Actors/Godot/ActorBase.cs` | 生命周期、状态门控、操作队列与 TextureLoader、ApplyTexture 混在一起 | 非纹理角色被迫接收不需要的依赖；Layered 已通过覆盖和空 ApplyTexture 绕开 |
| `Runtime/Actors/Godot/CharacterActor.cs` | sealed，Sprite2D 专用；与 Layered 重复站位、scale、rotation 逻辑 | 改名为 TextureCharacterActor，抽出 CharacterActorBase |
| `Runtime/Actors/Godot/IActorTextureLoader.cs` | ResourceActorTextureLoader 可创建独立保留作用域 | 必须传递当前环境的 loader；不能用全局 loader 替换 scoped loader |
| `Runtime/Actors/Godot/ActorBase.cs:ReleaseVisualPreparation` | 扫描 Sprite2D / TextureRect 来保留纹理 | 此策略只能属于纹理支持组件，不能作为所有角色的资源释放策略 |
| `Runtime/Dialogue/Yarn/Preloading/YarnWindowPreloader.cs` | Factory 返回带 ResourceType 的条目，但后续固定使用 Texture2D 做解析、Exists 和估算 | 记录为后续限制，本轮不修改类型解析链路 |
| `Runtime/Resources/ResourceManager.cs:ResolvePreloadEntry` | 已有按 ResourceType 和 LocaleChannel 解析预加载条目的逻辑 | 未来非纹理预加载可复用，本轮不新增相关资源层 API |
| `Runtime/State/BuiltInStateContributors.cs:ActorStateContributor` | 保存 FactoryId、TargetAppearance、目标变换等；恢复要求 FactoryId 精确匹配 | 移动类名不应改变 layered-character 的资源身份；新契约必须维护稳定外观状态 |
| `Runtime/State/DirectionStateContributor.cs` | 保存 Direction ActorState，含 Kind / DefinitionId / Appearance，没有任意模型状态载荷 | 角色参数与精确动画进度不能假称已自动支持 |
| `Runtime/Actors/ActorManager.cs:BindActor` | Manager 绑定状态门控后要求 Actor 实现 IStateMutationParticipant | 只实现 ICharacterActor 的手工实现可能直到运行时才失败 |
| `Runtime/Actors/Godot/GodotActorAppearanceTransition.cs` | internal，支持 actor-local 的两路快照和 prepared commit | 同程序集移动能编译不等于公开扩展边界正确 |

### 2.1 Layered 实际包含哪些项目约定

Layered 的非通用部分包括：`layered_character.json` 命名、CompositionMap、层路径和增量表达式、atlas region、pivot / PPU、排序、Multiply、从材质字符串解析的遮罩规则。`LayeredCharacterMasking` 根据可见图层重建遮罩 shader；这些不是核心 CharacterActor 的标准。

应整体迁出这些规则，不把它们拆散后改名放回通用接口。通用部分只包括：角色生命周期、舞台坐标、外观准备/提交、预加载资源声明，以及资源作用域。

### 2.2 当前异步创建并不天然等于 Dormant 安全

IActorFactory 有多组同步、异步和 creationMode 默认实现。Dormant 默认行为是在 `CreateAsync` 返回后才 SetVisible(false)。Layered 的异步实现会跨帧构建和预热，最后 SetVisible(true)，再由外层隐藏。Direction 的隐藏 parent 有额外保护，普通 Manager 则不能只依赖这层保护。

因此新 Factory 契约必须明确：Dormant 在整个创建期间都不公开到可见场景，不能只保证返回时 Visible=false。

## 3. 目标结构

```text
Bootstrap / IMajoPlugin.Configure
              |
              v
       ActorFactoryRegistry（注册后 Seal）
              |
       按当前创建环境构建 Factory
         /                         \
普通 ActorManager                  Direction binding Manager
预加载资源发现                     对应的隐藏 root、scoped loader
         \                         /
        ActorDefinition + IActorFactory
                      |
             ICharacterActor / ISceneActor
                      |
            GodotCharacterActorBase
              /                 \
TextureCharacterActor       Manosaba LayeredCharacterActor
                              外部模型 Actor（后续）
```

这里的“普通 Manager”仍负责低层创建、即时赋值和 Yarn 预加载查询，不再提供独立的舞台动画调度；它不与 Direction binding 共用 Actor 实例。共享的是注册目录、定义规则、资源缓存，实例和作用域仍隔离。

## 4. Factory 注册与创建上下文

### 4.1 注册声明和 Factory 实例分开

新增 `ActorFactoryRegistry`，登记 FactoryId、适用环境、构建函数和可选服务依赖。构建函数返回一个绑定当前环境的 IActorFactory，不返回 Actor。

拟定 API（设计草图，具体声明以实现时编译结果为准）：

```csharp
context.ActorFactories.Register(
    LayeredActorIds.Factory,
    ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
    factoryContext => new LayeredCharacterActorFactory(factoryContext));
```

- 注册目录使用 Ordinal 字符串比较，重复 ID 默认失败；替换必须调用明确的 Replace API。
- 内置默认注册先执行，插件 Configure 后执行，项目级 ConfigureActorFactories 最后执行，之后 Seal。
- 删除 `Action<ActorManager>` 注册回调，以及 PluginRegistrationContext 中的 ActorFactoryConfigurations 列表。
- Bootstrap 新回调接收 registry，与插件使用完全相同的登记 API。
- Seal 后不得注册或替换；创建 Manager 时执行已保存的构建函数，不能重新运行插件 Configure。
- 验证构建结果非 null，且 FactoryId 与登记 ID 相同。不得共享捕获固定 root 的 Factory 单例。
- 单独 new ActorManager 的单元测试仍可注入 Factory 集合；它是低层测试入口，不成为第二套 Bootstrap 注册入口。

### 4.2 ActorFactoryContext

新增 Godot 侧 `ActorFactoryContext`，至少包含：

| 字段 | 含义与所有权 |
| --- | --- |
| Domain | Runtime 或 Direction，创建后不变 |
| CharacterRoot / BackgroundRoot | 当前环境挂载点；Direction 没有 BackgroundRoot，访问错误应明确失败 |
| TextureLoader | 当前环境的纹理 loader，可能拥有 retained scope；Factory 借用，不能 Dispose |
| Resources | IResourceManager，共享服务；本轮沿用现有资源能力，不扩展非纹理预加载 |
| FrameClock | 当前运行时时钟，借用 |
| AppearanceTransitions | 公开的外观准备服务；不启动操作、不管理动画时间，进度由 Direction 驱动 |
| CharacterPositions | CharacterPositionMap，统一场景百分比、基线偏移和预设 |
| ActorSettings / ResourceSettings | 创建所需的只读配置快照；不要让 Factory 修改全局 Resource |
| Services | 只读 IServiceResolver，供 SDK 适配器、项目缓存等额外依赖使用 |
| Logger | 诊断；错误应包含 FactoryId、ActorId、当前 Domain |

不传 Bootstrap Node 本身作为万能依赖。Factory 的角色节点必须落在当前 root 内；相机/FX 层级由 Direction 管理，Factory 不自行 Reparent 到全局舞台。

额外服务的初始化顺序不能靠 Get 调用碰巧成功：注册声明允许列出依赖服务类型，创建普通 IActorManager 服务和 Direction 后端服务时，都把注册目录声明的依赖并入现有依赖数组。内置依赖仍显式声明。禁止 Factory 依赖 IActorManager / Direction 后端自身，服务图遇环应在构建期失败。

服务图 Build 前 registry 已 Seal；Factory 的构建函数在服务实例创建时执行，不能在插件 Configure 阶段解析服务。插件 Initialize 仍可做业务初始化，但不能作为 Factory 首次可用的前提。

配置快照与 CharacterPositionMap 要同时迁移：当前 map 保存的是可变 ActorConfiguration 引用，新 map 改为接收只读站位设置，避免上下文声称只读但坐标转换仍受外部配置修改影响。创建 Godot 节点、读取/写入 CanvasItem、创建材质必须在 Godot 主线程；异步只表示可等待，不代表可以在 Task.Run 中构建场景。Layered 现有纯 JSON 解析可以留在后台，Node 构建继续由主线程执行。

### 4.3 两个 Manager 使用同一构建路径

新增内部 `CreateActorManager(context)` / `ActorManagerBuilder`，负责：编译定义、按 Domain 选择登记项、构建 Factory、创建 Manager。两处 Bootstrap 代码只负责准备环境。

- Runtime 包含 character、object、background 及项目扩展。
- Direction 包含支持 Direction 的登记项，过滤 background 定义的现有行为改为按登记的 Domain 校验/过滤，不依赖 layered 特例。
- 明确配置为 Runtime-only 的 actor 被 Direction 请求时，报“不支持该环境”，不能落入图片约定加载占位图。
- 不要为每个 binding 解析所有角色的 manifest：注册时只做配置结构验证；真正资源校验在资源发现或创建所需 definition 时执行。配置结构校验不得创建节点或加载 GPU 资源。
- Factory 构造函数只能保存依赖和轻量数据。Layered 构造函数当前创建 shader material，应移至需要资源时；跨实例 manifest 缓存如果保留，应由项目服务提供纯数据缓存，不能缓存 Node、Material 可变实例或 host。

### 4.4 单一创建契约

删除 IActorFactory 多组同步/异步默认创建重载，保留一个必实现入口：

```csharp
Task<IActor> CreateAsync(
    ActorDefinition definition,
    ActorCreationMode mode,
    CancellationToken cancellationToken);
```

ActorManager 的同步 Create / GetOrCreate 调用点迁到异步；不使用 GetAwaiter().GetResult 包装异步资源创建。保留同步的定义解析、预加载资源声明和配置验证。

创建约束：

1. 节点第一次挂入树之前就设为不可见；准备完成后仅 Active 可公开，Dormant 继续隐藏。
2. 返回的 ActorId、FactoryId 必须与请求/定义一致；FactoryId 不再允许空字符串默认值。
3. 工厂创建失败时负责清理尚未交出的节点和资源；成功返回后由 Manager 负责 Destroy。
4. ActorManager 的合并创建语义不变：一个等待者取消只取消自身等待；Reset / Shutdown 才取消共享创建任务。恢复候选使用恢复 token，失败后销毁所有候选。
5. 校验 ID、FactoryId 或绑定状态门控失败，也必须 Destroy 已返回实例；普通 Create、GetOrCreate 与恢复路径统一经过同一校验/绑定辅助方法，避免某条路径泄漏。
6. 拒绝返回已被其他 Manager 管理的同一个 Actor；Factory 必须每次创建新实例。

Factory 负责应用自己配置中的初始完整外观。ActorSpawnRequest.InitialAppearance 对约定定义可参与推断；显式定义下，请求的 appearance 仍由调用方通过 Prepare/Set 或 Direction 提交应用。不要在 Factory 创建时和 Direction 提交时重复应用同一条增量表达式。创建完成后需保证初始外观、位置和 opacity 已应用；Actor 不再维护另一套动画目标值。

Factory 本身按轻量、无独占资源对象设计，本轮不新增含糊的 Factory Dispose 链。节点资源归 Actor；跨 Actor 缓存归有生命周期的服务；借用的上下文服务不由 Factory 释放。

## 5. 定义与自动识别

### 5.1 核心定义不携带纹理快捷字段

`ActorDefinition` 保留 Id、FactoryId、FactoryData 和带类型检查的 GetFactoryData。删除 DefaultAppearance、Appearances、GetAppearancePath 等直接转发到 TextureActorDefinitionData 的快捷字段和纹理构造重载。

这些成员留在 TextureActorDefinitionData 中，只有纹理实现显式获取它。所有 Factory 必须只读取自己的数据类型。

`ActorDefinitionConfiguration` 简化为：Id、FactoryId、FactoryConfiguration。移除顶层纹理专用字段。新增纹理配置 Resource，由内置纹理 Factory 自己编译成 TextureActorDefinitionData；自定义 Resource 由所属 Factory 编译/验证。

为避免编译器再出现 FactoryId switch，登记项提供可选的配置编译函数；默认把 FactoryConfiguration 作为 FactoryData 透传。配置编译不依赖 host 或已初始化的服务。内置 texture 注册提供专用编译器，Layered 可以直接使用其配置 Resource。

配置有效性分两层：全局检查 ID、重复、Factory 存在和 Domain；Factory 检查自己的配置类型与字段。文件存在性、manifest 解析等资源验证延后到按需发现/创建，不能在每个 Direction Manager.Initialize 中全量重复执行。

### 5.2 约定解析保留，职责不扩大

保留显式定义优先于 IConventionActorFactory。Layered 的自动发现仍由插件实现，注册后才识别 `characters/<id>/layered_character.json`。

本轮不引入优先级竞赛：没有 RequiredFactoryId 时，如果多个约定 Factory 都命中，明确报歧义并要求显式定义；不能由注册顺序静默选择。RequiredFactoryId 存在时只允许相应身份，不能让另一个探测器先命中导致错误结果。下面的资源 `<id>` 指 DefinitionId/模块资源 ID，不再指实例别名。

内置 character/background 的纯纹理 fallback 是最后一步，不参与有格式标记的探测竞争。SceneObjectFactory 的 `objects/<asset>.json` 当前可以转发到其他 Factory，但仍固定产生 TextureActorDefinitionData；本轮不将它宣传为任意模型定义入口。该转发继续要求目标 Factory 接受纹理定义，其他类型明确报配置类型错误；通用对象格式扩展另做。

插件未注册但定义显式引用它：初始化时报缺失 Factory。没有显式定义、只有 layered 文件时，核心已无法知道这是 layered，仍会走图片 fallback；这不是“正确识别缺失插件”。manosaba 必须显式安装插件，并有专门测试，不能依赖核心扫描 layered 标记来诊断。

### 5.3 一份角色资源，多个角色实例

统一三种身份，不混用：

| 身份 | 示例 | 用途 |
| --- | --- | --- |
| InstanceId（现 Actor.Id） | `ema_left`、`ema_right` | 场景中的独立角色；Direction 以 `(StageId, InstanceId)` 定位 |
| DefinitionId | `Ema` | 项目角色定义，决定 FactoryId 和配置；保存与恢复必须携带 |
| 模块 AssetId | `Ema`，也可与定义名不同 | Layered manifest 的源资源身份，属于 FactoryData；核心不解释 |

角色脚本新增可选 `asset:`，与 object 命令采用同一用词，含义是引用定义：

```yarn
<<character ema_left asset:Ema appearance:Surprised1>>
<<character ema_right asset:Ema appearance:Fearful1>>
<<character ema_left appearance:Surprised1>>
```

两个实例独立保存图层集合、外观、变换、显隐和转场状态，共享只读 manifest 与 atlas 资源。位置由现有 at/move 语法设置；示例只展示身份。

具体改动：

1. CharacterCommand.Arguments 新增 Asset，将其传入 ActorPatch.DefinitionId；第一次未提供时使用 ActorId 作为默认 DefinitionId。提交到 DirectionState 后 DefinitionId 始终是明确值。
2. 同一已存在实例省略 asset 表示保留定义；显式给不同 asset 报身份冲突。隐藏不会释放逻辑身份，隐藏后再次显示不能偷偷换资源。当前没有通用角色删除命令，本轮不顺带新增：换定义需用不同实例 ID，或在清空相应 stage/runtime 状态后重新创建。
3. 配置目录按 DefinitionId 索引。编译的定义和创建解析结果都明确携带 DefinitionId；供 Factory 创建的 ActorDefinition.Id 是 InstanceId，新增 DefinitionId 属性保留来源，FactoryData 是可复用的只读资源定义。
4. ActorManager.ResolveDefinition 按 `request.DefinitionId ?? request.ActorId` 查目录/探测资源，将实例 Id 与定义 Id 一起传给 Factory；不得因资源别名使用 `request.ActorId` 拼资源目录。
5. IConventionActorFactory 以 DefinitionId 定位资源。TextureActorDefinitionData 的 ResourceId 显式来自资源定义；LayeredConfiguration 包含 AssetId 与 ManifestPath，manifest.Id 和 AssetId 比较。允许多个定义引用同一 manifest，但每个定义的 AssetId 必须与源资源一致。
6. IActor 暴露必需的只读 DefinitionId。已创建及 pending 请求都校验 FactoryId 和 DefinitionId：同实例并发请求不同定义立即失败，不能合并到同一个 pending task。
7. `CanCreate(string actorId, string factoryId)` 替换为接收完整 ActorSpawnRequest 的验证入口；普通 ActorStateContributor 记录 DefinitionId，preflight/PrepareReplacement 都用实例 Id + DefinitionId + RequiredFactoryId。Direction 恢复沿用 ActorState.DefinitionId，禁止回落到别名目录。
8. 若创建请求未显式提供 DefinitionId 且实例已存在，应沿用已有定义；首次创建才默认到 ActorId。此规则用于普通 Manager，与 Direction 保持一致，不能让更新 `ema_left` 的请求误判为换成 `ema_left` 资源。
9. 内置 TextureCharacter 的默认定义也走同样解析规则，不仅让 Layered 支持实例别名。SceneObject 已有 DefinitionId 路径一并迁移校验，避免同一术语两套含义。

资源声明与实例创建必须使用同一个 definition resolver，不允许预加载仍按实例 Id 猜目录。第 8 节规定静态与运行时预加载的身份传递方式。

## 6. 角色接口和基类

### 6.0 Direction 是舞台动画的唯一调度者

职责按“谁决定目标、谁推进时间、谁呈现”划分：

- DirectionState 保存已提交的目标变换、显隐和完整外观；DirectionAnimationSlot 负责进度、冲突、完成、skip 和取消。
- Actor 只负责即时呈现与资源/外观准备；IPreparedActorAppearance 负责准备结果及可由外部驱动的进度，不自行启动 operation 或计时。
- 删除 IActor 中的 TargetPosition、TargetOpacity、TargetVisible、TargetAppearance；删除具体 Actor 的对应字段及 targetLayers 作为持久目标副本的用途。需要的下一组图层放在单次 prepared 对象内。
- 外观准备接收明确的源完整外观和目标请求/规范值，源取自本次 Direction 事务的逻辑状态；不能回头读取已删除的 Actor.TargetAppearance。准备期间保留旧视觉，提交/完成/取消各自的语义沿用 Direction 当前事务流程。
- 普通 ActorStateContributor 只捕获即时值；有动画的舞台必须由 DirectionStateContributor 保存逻辑目标。不能在删 Target* 后把动画中间帧误当作舞台存档目标。
- 移除 ActorBase 的操作字典、sequencing、opacity tween、CancelProperty 等；保留保护生命周期的 epoch/generation 或等价机制，防止迟到的 prepared 提交写入已销毁实例。
- 从 Character/Layered/SceneObject/Background 的构造参数和 FactoryContext 删除仅为上述调度提供的 Runner/Easings/TransitionRegistry 依赖。Direction 后端仍保留自身的 runner、easings、drivers，不影响全局 IOperationRunner 服务。
- 所有旧 Change* 测试改为经 Direction 断言同等可观察行为；即时赋值测试继续使用 Set*。不能只删除原来的取消/冲突测试而不在统一入口补覆盖。

这是本轮必须完成的职责统一，不留下旧动画 API 或可选兼容路径。模型内部的自主动作（例如未来的眨眼）不等于舞台位置/显隐/外观的第二套调度器，本轮不设计该内部动画协议。

### 6.1 一个完整的舞台角色契约

新增 `ISceneActor` 聚合：IActor、IGodotActorView、IActorTransformTarget、IActorAppearanceTarget、IActorVisibilityTarget、IStateMutationParticipant。ICharacterActor 继承 ISceneActor，再提供角色站位能力；SceneObject 也实现 ISceneActor。

Direction Binding 一次转换为 ISceneActor，并验证 SceneNode 在当前 root 内。错误说明缺少完整舞台契约，而非逐个遇到 lacks transform / appearance。Background 仍保留自己的入口，不强制塞进 Character 契约。

接口不规定 Sprite2D，不要求纹理或 atlas；节点类型允许任何合适的 CanvasItem。便捷基类选用 Node2D 作为角色变换宿主，自定义渲染节点挂在其下。

### 6.2 基类分工

| 类型 | 职责 |
| --- | --- |
| GodotActorBase（由现 ActorBase 重构） | 实例身份、节点、生命周期、状态门控、即时呈现值和销毁；不依赖纹理 loader 或 OperationRunner，不拥有动画队列 |
| GodotCharacterActorBase | Node2D 变换、CharacterPositionMap、scene position / preset、scale / rotation、角色接口公共部分；外观准备由子类实现 |
| TextureAppearanceSupport（组合组件） | 纹理定义、加载、纹理资源保留和快照外观辅助；仅纹理实现与需要纹理的 Layered 使用 |
| TextureCharacterActor | 当前 CharacterActor 的 Sprite2D 具体实现 |
| BackgroundActor / SceneObjectActor | 复用纹理组件；保留各自布局/锚点语义，不为了继承树强行变成 Character |
| LayeredCharacterActor | 派生角色基类，保留图层组合、遮罩、合成 opacity 和现有快照转场 |

不新增 TextureActorBase 与 CharacterActorBase 两条必须同时继承的分支，避免 C# 单继承冲突。TextureAppearanceSupport 是组合，不是第二个 Actor。

通用基类构造函数显式接收初始 appearance，不再从 definition 猜测纹理默认值；不在基类构造函数中调用派生类外观方法。

### 6.3 必须保留的行为细节

- Position 是舞台局部像素；ScenePosition 是场景百分比。通过 CharacterPositionMap 转换一次，不能漏掉或重复加 ScenePositionOffset。
- SceneNode 是稳定宿主，换外观不换它。Direction 外层 root 承担 FX modifier，Actor 不在自己的 Position 中累加这层偏移。
- 正常 scale 约束仍是正有限值；整体 opacity 和 tint 不等同于把 alpha 逐层乘一遍。Layered 的 CanvasGroup 合成透明度保持独立实现。
- 持久写入经过 PersistentWrite，保留 StateMutationGate；框架驱动的每帧外观采样与游戏持久参数需要区分，不能把任意模型内部帧更新自动当作存档写入。
- 所有舞台动画由 Direction 调度。IActor / ICharacterActor 和具体类删除 ChangePosition、ChangeOpacity、ChangeAppearance 全部重载，删除 StartSequencedOperation、propertyOperations 和相应 Runner/Easings 依赖。即时 Set* 仅用于呈现适配、初始化和恢复；业务侧调用 Direction。
- Destroy 幂等、使迟到的外观提交失效、清理 Actor 自有资源和节点；新增受保护 OnDestroy 钩子。Direction 在销毁前终止其槽位并释放 prepared 对象，Actor 不再取消自己的动画操作。不能只实现 IDisposable 指望 Manager 自动调用——Manager 当前调用的是 Destroy。
- 释放准备对象必须幂等；资源释放异常不应跳过剩余必要清理。

## 7. 外观、准备与状态语义

### 7.1 增量请求和完整状态必须区分

LayerCompositionResolver 的 Apply 支持基于原有层集合修改，Canonicalize 则输出可独立重建的完整表达式。二者用途不同：

- 脚本 appearance 参数可以是别名或增量表达式。
- Actor.Appearance 表示已应用的完整外观；存档中的稳定 appearance 是 Direction 已提交的完整目标。
- Prepare 阶段在 prepared 对象保存 ResolvedAppearance；Commit 将其写入 DirectionState；Finish 使 Actor.Appearance 到达目标。删除 Actor.TargetAppearance，不让 Actor 成为第二份目标状态源。
- 恢复读取的是完整值，不能相对新实例默认状态再次解释为最后一条增量命令。

当前普通 ActorStateContributor 捕获 TargetAppearance（本轮将改为即时状态），但 Direction 保存自己的 ActorState.Appearance；源码中 CommitActors 在新建和空间更新时写入，不能据此推断所有外观更新都会回填规范值。

本轮增加 prepared appearance 的规范目标属性，例如 `ResolvedAppearance`；即时切换也通过无可见副作用的解析/准备获得这个值。Direction 在提交成功时写入完整 appearance，保留纯状态层对字符串内部格式的不知情。

实现时统一 CommitActors 的字段合并：外观提交不能被后续 ApplySpace 的整条 ActorState 覆盖，空间更新也不能被外观回填覆盖。应使用一个合并辅助方法，或在本次角色各项提交结束后统一写入最终记录。

针对同一 beat 内连续的增量外观请求，先核对 plan 中 intent 的保留/合并语义，再在 prepare 中按语义顺序解析为完整目标；不能将每条请求都相对同一个旧状态解析。加测试覆盖后再宣布 Layered 迁移完成。

### 7.2 转场只调整公开边界

提供公开的 `IActorAppearanceTransitionService`，薄封装当前 PrepareForCommitAsync 的快照逻辑。删除仅用于旧 ChangeAppearance 的 CreateOperation 路径；服务不持有 OperationRunner。内部 capture pool、临时节点管理和 shader driver 仍可保持 internal。

服务约定：输入节点是转场独占的临时内容；清理会释放它们，不能传正在使用的模型宿主。Layered 继续创建独立 composite。这个接口明确描述快照用途，不宣称通用动态模型转场已经实现。

自定义 Actor 可以自行实现 IPreparedActorAppearance；不支持某个转场时在 Prepare 阶段明确失败，不静默 cut。实时输入类型、实时模型生命周期本轮不设计。

### 7.3 存档范围

- 保持普通 ActorStateContributor 与 DirectionStateContributor 的责任分离；不会把预加载 Manager 的实例当成 Direction 舞台实例存档。普通 contributor 改为捕获即时 Appearance / Visible / Position / Opacity，并保存 DefinitionId；Direction contributor 保存统一调度的逻辑目标和 DefinitionId。同步迁移 schema 与恢复校验，不保留旧 Target* 字段适配。
- 新版本层级状态恢复要覆盖 Factory 选择、appearance 规范值、transform、可见性、opacity、tint 等当前已有字段。
- FactoryData 是资源定义，不是运行状态；不把模型当前参数塞进共享配置 Resource。
- 本轮不新增任意 Actor 状态 JSON 袋。需要持久化的模型专属参数，未来设计有版本的状态协议；测试角色只用完整 appearance 表示持久外观。
- 当前恢复路径 PrepareReplacement 创建候选后才提交；插件缺失、配置错误或创建失败不能先清掉旧舞台。保留并验证这一行为。

## 8. 资源、预加载与本地化：只做 Layered 迁移所需改动

本轮保留 YarnWindowPreloader 现有 Texture2D 加载、本地化、pinned 检查和 PNG 预算逻辑；不新增按任意 ResourceType 的解析、Exists 或 pinned API，不添加模型预算配置。ResourcePreloadEntry 的现有类型字段也不删除，避免反向收窄基础接口。非纹理资源完整接入是明确的后续工作，不是本轮验收承诺。

必须改的是资源身份传递：

- YarnPreloadReference 的 character 分支改为分别携带实例 Id、Asset（映射 DefinitionId）和 Appearance，停止将实例与外观拼成 `id/appearance` 再切字符串。
- 新增 `character ... asset:...` 的解析；窗口把 DefinitionId 传入 ActorSpawnRequest，与实际 Direction 创建一致。
- 后续 appearance 命令省略 asset 时，运行前的 required 预加载从当前 DirectionState 的 `(StageId, ActorId)` 取得定义身份；不能在普通预加载 Manager 中查舞台实例。引用需携带 stage，stage 解析复用 DirectionScopeResolver。
- 静态 look-ahead 仅在直线可证明的范围内传播 `(stage, instance) -> definition` 映射，遇到跳转/未知动态表达式则停止推断或跳过该项。绝不将未知别名当成资源目录。没有 appearance 时保持定义的初始外观语义，不擅自强加字符串 Default。
- Layered 返回基于资源定义路径的 atlas 条目；两个实例必须得到同一组资源 Key/OverridePath，以复用资源缓存。GetPreloadResources 不创建节点、不预热 GPU。
- 保留已有纹理预算与本地化断言，不借身份传递重写整个窗口计划器。

纹理扫描保留策略移到 TextureAppearanceSupport，只影响它自己使用的纹理作用域。Layered 复用它维持 atlas 行为。Actor 不 Dispose 上下文中的 TextureLoader 或 ResourceManager；Direction root 的 retained scope 由环境拥有。

准备期间旧外观资源、新外观资源和临时输入都必须存活。两个实例共享 atlas 资源，但拥有独立的图层可见集合、CanvasGroup、可变 Material 和 prepared 对象。销毁其中一个实例不能释放另一个仍在使用的资源。PreloadExistingAppearances=false 保留原含义，跨 Manager 去重仍依靠现有资源缓存/pinned 机制。

## 9. Layered 迁移清单

建议目标目录：

```text
demos/manosaba/Actors/Layered/
  LayeredCharacterPlugin.cs
  LayeredActorIds.cs
  LayeredCharacterConfiguration.cs
  LayeredCharacterActorFactory.cs
  LayeredCharacterActor.cs
  LayeredCharacterMasking.cs
  LayerCompositionResolver.cs
  LayeredCharacterManifest.cs
  Shaders/
    AlphaAwareMultiply.gdshader
    LayeredComposite.gdshader
```

迁移项：

1. 将 `Runtime/Actors/Layered/*`、三个 Godot Layered 类、Layered 配置和专用 shader 移到上述目录；命名空间改为项目侧。
2. 同步移动 `.uid`，检查 `.tres` / `.tscn` / `.cs` / 工具中的路径和类型引用，修正 shader 的硬编码 res:// 路径。不要留下旧资源转发文件。
3. 从 ActorFactoryIds 删除 LayeredCharacter；字符串 `layered-character` 由 LayeredActorIds 拥有。字符串仍保持不变是为了避免无意义的数据身份变化，不提供旧类型别名。
4. 两处 Bootstrap 删除 Layered Factory 的内置构造；Factory 注册只出现在插件 Configure。
5. ManosabaDemoController 在 AddChild(runtime) 之前调用 AddPlugin(new LayeredCharacterPlugin())。Layered 的 manifest.Id 校验改为针对资源 AssetId，不再针对实例 Id。
6. Layered Actor 改用角色基类和公开转场服务；Masking 与 manifest 等内部互相调用可以继续 internal。
7. Layered 的 BuildLayerViewsAsync、WarmAppearanceRenderingAsync 继续留在模块；Factory 负责调用，核心不增加“分层预热”接口。
8. 更新 LayerCompositionResolverTests、ActorManagerTests 中的测试命名空间和 Factory ID；通用 Manager 测试改用自定义测试 ID，不再依赖业务 Layered 常量。
9. 逐一检查 headless 的 LayeredCharacters、LayeredMasks、Preloading、RepeatedWall、DefaultPresentation、SpatialBackdrop 和 suite 入口；仅需要 Layered 的 runtime 安装插件，不在全局测试配置无条件注册。
10. 保留已有 fixture 的层、遮罩、canonical 外观和内存预算断言；不要通过删除测试消除搬迁后的错误。

核心依赖验收：`addons/majo_runtime` 不得引用任何 Layered 类型、manosaba 命名空间、layered manifest 文件名或项目 shader 路径。测试可引用项目模块，核心不可。

## 10. 实施分期与完成条件

### 10.1 主要文件改动地图

| 文件/目录 | 操作 |
| --- | --- |
| `Runtime/Actors/ActorFactoryRegistry.cs` | 新增登记项、Domain、Seal、显式 Replace、依赖元数据 |
| `Runtime/Actors/Godot/ActorFactoryContext.cs`、`ActorManagerBuilder.cs` | 新增环境描述和统一构建；管理定义目录与环境筛选 |
| `Runtime/Plugins/IMajoPlugin.cs`、`Runtime/MajoRuntimeBootstrap.cs` | 替换回调类型，注入 registry，调整 Configure/Seal 顺序 |
| `Runtime/Bootstrap/Registration/MajoRuntimeBootstrap.Services.ActorsAndStage.cs`、`MajoRuntimeBootstrap.Services.Direction.cs` | 删除手写 Factory 数组，分别组装 context；登记扩展服务依赖 |
| `Runtime/Actors/IActorFactory.cs`、`IActorManager.cs`、`ActorManager.cs` | 单一异步创建、返回值验证、定义探测歧义、失败清理 |
| `Runtime/Actors/ActorDefinition.cs`、`Runtime/Configuration/ActorDefinitionConfiguration.cs`、`Runtime/Actors/Godot/ActorConfigurationCompiler.cs` | 删除纹理快捷字段，按登记项编译配置；新增纹理配置 Resource |
| `Runtime/Actors/IActor.cs`、`IActorPresentation.cs` | 聚合 ISceneActor、必需 FactoryId/DefinitionId、删除 Change*/Target*、prepared 规范外观目标 |
| `Runtime/Actors/Godot/ActorBase.cs`、`CharacterActor.cs`、`CharacterPositionMap.cs` | 重构/改名，新增角色基类和纹理组合组件 |
| `Runtime/Actors/Godot/SceneObjectActor.cs`、`BackgroundActor.cs` 及对应 Factory | 跟进基类、配置和异步创建，不改变对象锚点与背景布局 |
| `Runtime/Actors/Godot/GodotActorAppearanceTransition.cs` | 增加公开服务门面，内部实现继续隐藏 |
| `Runtime/Direction/Godot/GodotDirectionActorTreeBackend.cs`、`Runtime/Direction/Core/DirectionPlan.cs` | 完整舞台契约校验、规范外观回填、提交状态合并 |
| `Runtime/Dialogue/Commands/Direction/ActorCommands.cs`、`Runtime/Direction/Execution/DirectionPlanner.cs`、`Runtime/Direction/Core/DirectionState.cs` / `DirectionDirectives.cs` | character asset 参数、实例身份不变规则、完整定义引用 |
| `Runtime/Dialogue/Yarn/Preloading/YarnPreloadPlanner.cs`、`YarnWindowPreloader.cs` | character 实例/定义/外观/stage 分离传递，保持纹理加载实现 |
| `Runtime/State/BuiltInStateContributors.cs`、`DirectionStateContributor.cs` | 普通状态改存即时值并新增 DefinitionId；Direction 存完整逻辑目标，迁移恢复校验与 schema |
| `demos/manosaba/ManosabaDemoController.cs`、新增 `Actors/Layered/` | 显式安装模块、迁移类型与资源 |
| `tests/Majo.Runtime.UnitTests/Testing/FakeActorInfrastructure.cs`、ActorManager/ActorLifecycle/配置及插件测试、`tests/headless/` | 迁移破坏性 API 并扩充行为验证 |
| `tools/test.sh` | 新增核心独立消费 smoke，完整检查保留 |

实施时先用搜索枚举同步 Manager 调用、旧配置字段、旧 ActorBase/CharacterActor 类型和旧 Layered 路径，形成迁移清单；不要只改上述主要文件而漏掉测试工具与演示调用。

### 10.2 阶段安排

每期提交必须可构建。允许迁移期间短暂保留旧代码帮助分步修改，但最终交付删除旧 API，不发布双轨兼容设计。

### P1：统一注册与环境构建

- 新增 registry、Domain、FactoryContext 和统一 Manager builder。
- 替换两套回调；内置注册和 plugin 注册全部进入目录。
- 将依赖服务纳入 service graph；明确 runtime / direction 的作用域和挂载点。
- Layered 暂留原位置，但已从同一个 registry 构建。
- 验收：两个独立 runtime、多个 Direction binding 的 Factory 都收到正确环境；registry Seal 后拒绝修改；项目级注册在两条路径均生效。

难度：中。风险集中在服务图时序、root 和 loader 捕获。

### P2：收敛定义、创建和角色契约

- ActorDefinition / 配置去掉纹理专用快捷字段，使用登记项的配置编译函数。
- Factory 只保留必实现的异步创建入口，迁移同步 Manager 调用。
- 新增 ISceneActor、角色基类和纹理组合组件，迁移 Character、SceneObject、Background、Layered。
- Factory/Actor ID 校验、状态门控绑定、异常释放使用统一路径。
- 删除 Actor 动画调度及 Target*，把业务和测试中的动画调用迁到 Direction。新增 DefinitionId 传递、character asset 参数与 Layered 资源身份校验。
- 同一提交迁移外观准备签名和 contributor 对已删除字段的读取，确保编译和基本恢复可用；P3 再完成复杂提交合并、资源身份传播和恢复回归，不把失效的调用点留到下一阶段。
- 验收：Actor 不再引用动画运行器；同一 Layered 资源可创建两个独立实例，基础舞台操作由 Direction 驱动。

难度：中至高。删除第二套动画入口会影响接口、构造函数、状态采样和原有测试；不改变转场算法，但不能按纯文件搬迁估算。

### P3：打通统一调度、实例身份与状态恢复

- 规范外观目标在 cut 和 prepared transition 中均可取得，Direction 合并写入完整状态。
- 普通 ActorStateContributor 改存即时值及 DefinitionId；Direction 成为舞台动画唯一目标状态源。
- Yarn 预加载传递 definition/stage 身份，保留现有纹理加载、本地化和预算。
- 验收：两个实例各自连续增量换外观后可恢复；省略 asset 的后续命令仍加载正确资源；销毁一个实例不影响另一个；准备失败保留原状态。

难度：中至高。这是最容易出现“注册成功、运行到一半才坏”的部分，应单独提交和测试。

### P4：迁出 Layered 并完成公开边界

- 薄封装现有快照转场服务，不改渲染算法。
- 按迁移清单移动模块，manosaba 显式安装插件。
- 增加扩展者示例：配置 Resource + Factory + Actor + Plugin；示例可以是测试 fixture，无需创建新的产品 demo。
- 做核心独立消费工程检查，防止主项目同程序集掩盖依赖方向。
- 验收：核心无需 Layered 源码即可构建；主项目启用模块后通过已有行为回归。

难度：低至中；Godot 资源路径、导出和公开性检查需要单独验证。

## 11. 验证矩阵

| 场景 | 验证内容 | 推荐层级 |
| --- | --- | --- |
| 注册与封闭 | 重复、Replace、Seal、构建结果 FactoryId 不匹配、插件缺失 | 单元测试 |
| 配置 | Resource 类型错误、显式定义优先、多个 convention 命中、required factory 约束 | 单元测试 |
| 创建 | Active / Dormant 全程可见性、跨帧取消、错误 ID、状态绑定失败、失败重试 | headless + fake factory |
| 并发 | 同 ID、同定义等待合并；同 ID、不同定义立即失败；单等待者取消不影响其他等待者；Reset 取消创建并清理 | 单元测试/现有 lifecycle 测试 |
| 场景隔离 | 同 ID 在两个 stage/runtime 中独立；Factory root 不串用；FX 不重复变换 | headless |
| 唯一动画入口 | Actor 无 Change*/Target* 与 OperationRunner；移动/显隐/外观动画均由 Direction 槽位管理，覆盖打断、skip、立即完成 | API 检查 + headless |
| 多实例 | 相同定义、不同实例、不同 appearance/位置；支持跨 stage 和独立销毁 | headless |
| 别名预加载 | asset 解析、省略 asset 的后续外观命令、stage 身份、动态表达式跳过、共享 atlas 与纹理预算 | 单元测试 + headless |
| 资源所有权 | cache eviction 后活动资源有效；取消准备释放新资源；Destroy 释放自有 leases | 资源诊断断言 |
| 状态 | Layered 两次增量修改后的 cut/转场中存档与回滚；含同 beat 多条请求、appearance 与 move 同时更新 | 集成测试 |
| 恢复失败 | 缺 Factory、资源失败、取消恢复，旧角色和舞台仍完整 | 集成测试 |
| Layered 行为 | 遮罩、Multiply、合成 alpha、排序、位置映射、转场取消、预热与 atlas 预算 | 迁移既有 headless 测试 |
| 本地化 | 普通图片语言切换不退化；自定义 Actor 重设完整 appearance 幂等 | 集成测试 |
| 打包边界 | 无 manosaba 的核心消费工程；带 Layered 的完整项目导出 | 构建/导入/导出 smoke |

现有入口：

```sh
bash tools/test.sh --unit
bash tools/test.sh --fast
bash tools/quality.sh --check
bash tools/test.sh --full
```

每阶段使用相关单元测试及必要的 headless 场景，最终再跑 full。`tools/test.sh --full` 当前执行主项目构建、Godot 导入、headless 和导出；虽然脚本有 copy_clean_addon 函数，当前执行路径没有真正创建独立消费工程。因此需要新增这项 smoke，不能把现有 full 通过当作核心独立性的证明。

独立消费 smoke 在临时目录建立最小 Godot C# 工程，只复制必要 addon 及其已声明依赖，禁止复制 demos；启动默认图片角色。随后在消费工程添加一个仅引用公开 API 的自定义 Actor 验证注册。此 smoke 验证的是源码消费边界，不能宣称已验证独立 DLL/SDK 分发。

遮罩、透明混合和转场画面另用现有渲染测试/实际 Godot 画面复核；headless 的节点与状态断言不能替代像素正确性检查。

## 12. 风险与不应遗漏的收尾

1. **配置编译与初始化重复工作**：不要把 manifest 解析移入每个 Manager 的全量 Validate；配置编译应一次完成或仅执行轻量操作。
2. **基类资源泄漏**：移走纹理扫描时检查所有调用者；把 RetainVisualPreparation 改成空操作而不迁移保留逻辑会造成转场资源寿命变化。
3. **Layered IDisposable 与 Destroy 不一致**：迁移时审查 activeOverlay、CanvasGroup、生成材质及操作的所有权，通过统一 OnDestroy 清理，不能双重释放共享 atlas。
4. **定义身份与实例身份**：不能只删 manifest.Id 校验；需要用定义中的 AssetId 校验，并贯通创建、预加载、CanCreate 与恢复。实例 ID 不再参与资源路径拼接。
5. **状态规范值被覆盖**：新建、appearance、move 分支都会影响 CommitActors，要验证最终合并结果，而非只验证 Actor 当前画面。Actor.TargetAppearance 已删除。
6. **同程序集掩盖 internal 耦合**：只移动目录不能证明第三方可接入；新模块只使用公开服务，消费 smoke 必须检查这个事实。
7. **范围控制**：本轮不解决非纹理资源类型、预算与本地化问题；不能将 Layered 验收通过写成 Spine/Live2D 资源链已完整支持。
8. **语言切换重放外观**：ActorManager 当前对可见 Actor 调用 SetAppearance(actor.Appearance)；自定义实现必须支持完整状态幂等应用，不得把同一值视为再次叠加的动作命令。

最终完成标准：Layered 通过配置、Factory、角色实现和插件注册接入，核心没有 Layered 类型判断；舞台动画只由 Direction 调度；一份角色资源可产生多个独立实例，并正确预加载、保存、恢复和销毁。未安装模块的核心可以独立构建运行。非纹理资源完整接入不属于本轮承诺。
