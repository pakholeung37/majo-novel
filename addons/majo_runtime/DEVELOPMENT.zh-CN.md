# 独立游戏仓库：安装、联调与升级

建议将系统仓库和游戏仓库放在同一级目录。游戏仓库提交两个 addon 的源码副本和 `majo.lock.json`；系统仓库负责框架开发，游戏仓库负责剧情、素材、配置、UI 和扩展。这样游戏克隆后可直接构建，不需要开发者本机上的系统路径、软链接或额外子模块。

管理工具需要 Python 3.10+；构建需要 Godot 4.7.2 .NET 和 .NET 8 SDK。NuGet 首次还原需要网络。下列命令在系统仓库根目录执行，`../my-game` 是你准备创建的游戏目录。

## 新建游戏

```sh
python3 addons/majo_runtime/Tools/majo.py init --project ../my-game --source . --name MyGame
cd ../my-game
dotnet build
```

然后在 Godot 中打开游戏的 `project.godot`，等第一次导入结束，运行主场景。`init` 要求空目录，创建工程、解决方案、主场景、原始示例剧情、SDK 配置、忽略规则、addon 副本和版本锁，并自动配置两个 props 引用及插件启用。不会创建 GitHub 仓库、提交或推送；你可以自行初始化游戏的 Git 仓库。

命令行中的 `--name` 使用字母开头的标识符，例如 `MyGame`；游戏显示名称以后可在 Godot 修改。将游戏自己的文件放在 `assets/`、`Scripts/`、`Scenes/` 等目录；不要修改 `addons/majo_runtime` 或 `addons/YarnSpinner-Godot`。

## 安装到已有项目

```sh
python3 addons/majo_runtime/Tools/majo.py install --project ../my-game --source .
```

已有项目需要 `project.godot` 和唯一的根 `.csproj`，使用 Godot.NET.Sdk/4.7.2、net8.0。工具补齐引用和插件配置，保留现有的其他插件，不替换游戏场景、剧情、配置和素材。框架版本/工程布局不匹配时会停止，避免猜测修改。已有 addon 未被管理时，只有文件与待安装版本相符才会接管，否则要求先处理差异。

也可以使用发布 ZIP。管理命令可从系统仓库运行；手工解压到新项目后，也可使用包内同一路径的脚本：

```sh
python3 addons/majo_runtime/Tools/majo.py install --project ../my-game --archive /path/to/majo-runtime-0.1.0-rc.2-with-yarn.zip --sha256 EXPECTED_SHA256
```

将校验值替换为发布包 `SHA256SUMS` 中的值。仅支持捆绑 Yarn 的 addon ZIP；示例 ZIP 是完整项目，直接解压到空目录使用。已有解决方案文件会保留；缺少 `.sln` 时管理器会生成与游戏工程对应的解决方案，便于后续导出。

## 同时开发系统与游戏

```sh
# 一次同步，随后在游戏目录手动构建。
python3 addons/majo_runtime/Tools/majo.py sync --project ../my-game --source .

# 持续监听系统源码变化，同步稳定后的文件，再构建游戏工程。
python3 addons/majo_runtime/Tools/majo.py watch --project ../my-game --source .
```

`watch` 默认执行 `dotnet build`，可加 `--no-build` 改为仅同步。停止运行中的游戏后再修改 C#；构建通过后重新启动游戏，不依赖 C# 热替换。游戏内 Yarn 热重载是另一项功能，仍按游戏的 HotReload 配置工作。

调试时启动游戏工程，在游戏目录中的 addon 副本设置断点；编译器的源码坐标指向这个副本。框架修复编辑系统仓库中的原文件，再由 `watch` 同步回来。若误改游戏副本，同步会报告冲突并停止；先将修复迁回系统仓库、恢复游戏 addon 的原版本，再重试。工具不做自动合并，也不静默丢弃修改。

日常联调结束后，检查游戏仓库中的 addon diff 和锁文件，将验证通过的系统快照一起提交。锁文件记录插件版本、来源提交、来源是否有未提交改动、内容总指纹和逐文件 SHA-256；同为 0.1.0 的开发快照也能区分。来源的本机路径仅用于溯源，其他开发者构建游戏时不依赖它。正式游戏版本应使用已提交的系统版本或校验过的发布 ZIP。

## 升级与回退

1. 先停止 `watch`，提交游戏当前可用状态，阅读目标版本的 CHANGELOG 和存档兼容说明。
2. 使用目标系统 checkout 的管理工具运行 `install --archive ...`，或切换系统仓库到目标版本后运行 `sync --source .`。
3. 工具检查现有 addon 是否与锁文件一致，暂存新版本，再整体替换两个 addon；旧版本删除的源码也会移除。配置写入失败时恢复原 addon 和锁文件。游戏文件不参与替换。
4. 运行 `doctor`、构建、Godot 导入，再测游戏自己的剧情、演出、存读档与受检导出。
5. 将两个 addon 目录、`majo.lock.json` 和必要的工程配置作为同一次游戏提交。

```sh
python3 addons/majo_runtime/Tools/majo.py doctor --project ../my-game
```

`doctor` 检查文件指纹、依赖版本、工程引用和插件配置，不代替构建或游戏回归。管理器会拒绝 addon 软链接及本地补丁；Godot 生成的导入元数据和构建缓存不算业务补丁。管理器维护 `.gitattributes` 的 addon 字节保留规则，避免 Windows 换行转换破坏锁定指纹。

回退时恢复游戏仓库中同一提交的两个 addon、锁文件及工程配置，随后重新构建、导入。不要只恢复锁文件。玩家存档位于 `user://`，不属于源码回退；已有存档需要单独保留并验证。

## 游戏扩展和升级边界

角色 Factory、自定义命令、资源 Provider、UI 和宿主服务放在游戏自己的命名空间与目录，通过 Bootstrap 的 `ConfigureActorFactories`、`ConfigureCommands`、`ConfigureResourceProviders`、`ConfigureServices` 等入口注册。示例可参考系统仓库中的 background-extension demo。游戏的 Yarn 源码、Direction 伴随资源及配置归游戏仓库维护。

为游戏设置独立的应用名称、存档 StorageNamespace 和 GameBuildId。不要为了框架升级随意更改存档 namespace。框架源码可升级不代表旧存档自动兼容：当前嵌套 detour 检查点要求相同的编译程序，State Contributor 的 schema 变化也可能需要迁移。正式升级应保留真实旧存档样本，验证跨版本加载结果；本工具不会迁移玩家存档。

## 扩展的启动与退出顺序

服务先按声明的依赖顺序执行 `InitializeAsync`，全部完成后再执行实现了 `IRuntimeActivation` 的服务的 `ActivateAsync`。这个阶段可以通过传入的 resolver 访问完整服务图。命令适配器和插件在此阶段接入；插件初始化完成后 Host 才进入 `Initialized` 并发出 RuntimeInitialized。游戏应在等待 Bootstrap 的 `InitializationCompletion` 后开始播放，插件初始化期间不应开启游戏会话。

初始化与激活共用生命周期超时。激活失败也会清理已经尝试激活的服务；即使插件忽略取消、超时后才返回，也会在初始化结束后执行退出清理。退出时先按激活的逆序执行 `DeactivateAsync`，再按依赖逆序关闭服务，因此插件的关闭回调可以使用仍然存活的依赖。每个激活的退出回调只尝试一次；失败会汇总报告，其他服务仍继续关闭。

`RuntimeHost` 只处理通用生命周期，通过 `IRuntimeExecutionCoordinator` 委托演出重置、读档停止、热重载停止和画面保留。默认实现位于 `Runtime/Lifecycle`；替换该服务时，用 `ManagedResetServices` 声明已经由协调器重置的服务类型，Host 不再重复重置它们。协调器自身也只重置一次，未托管的服务仍执行普通重置。

状态管理的 `StatusChanged`、`Captured`、`BoundaryCommitted`、`Restored` 和 `Failed` 都是通知事件。单个订阅者的异常会被记录为 `state.observer_failed`，不会改变事务结果，也不会阻止后续订阅者。需要阻止提交的校验应放在状态 contributor 的预检或准备阶段。

## 自定义命令的资源预加载

需要预加载的 `RuntimeCommand<TArguments>` 可以实现 `ICommandResourceProvider<TArguments>`，在 `DescribeResources` 中从已绑定的参数返回 `CommandResourceReference` 列表。例如 `new("character", arguments.Actor, arguments.Asset.Value, arguments.Appearance.Value, arguments.Stage.GetValueOrDefault("main"))`；支持的 Kind 为 `character`、`object`、`backdrop`、`bgm`、`sfx` 和 `voice`。没有资源需求的命令无需实现此接口。

资源描述使用与执行相同的命令目录、最长命令路径匹配、参数绑定器、转换器及契约默认值，不调用命令的 `ExecuteAsync`。描述方法及所用转换器、校验器必须没有副作用，不应创建 Actor、改变变量或开始播放。描述失败只记录 `preload.command_analysis_failed` 并跳过这条命令的预加载，实际执行时仍按正式命令的错误策略处理。向前分析遇到动态参数、分支、选项和跳转时停止，执行到动态命令后再分析已经求值的参数。

Bootstrap 会将完成注册的命令目录接入默认预加载器，包括插件命令。手动构造 `YarnWindowPreloader` 或 `YarnPreloadPlanner` 时，通过 `commands` 参数传入 `services.Get<IYarnCommandAdapter>().Catalog`，或在目录就绪后调用 `SetCommandCatalog`；尚未接入目录时只分析语音行。旧的低层静态调用 `YarnPreloadPlanner.Resources(tokens)` 需要迁移为 `Resources(tokens, catalog)`，也可改用已接入目录的 `planner.GetResources(tokens)`。该源码 API 变化已声明在兼容性清单中，受管理项目升级需使用 `--allow-breaking --check` 并验证游戏自己的回归。

## Yarn 会话与热重载的内部边界

`YarnDialogueBackend` 负责 Runner 装配、节点进入和展示边界连接。`YarnPlaybackSession` 持有对外的 Completion、停止阶段和推进限制；内部重放通过可嵌套的作用域抑制会话结束，读档和热重载不会因此完成原来的对话会话。`YarnCheckpointController` 独立持有 VM bridge、行入口检查点、待恢复数据和首次重放来源；`YarnLocalizedDialogue` 持有当前行、选项与语言刷新状态。这些组件是内部实现，不增加游戏侧注册入口。

热重载的 `YarnSourceMonitor` 独立拥有文件监听器、扫描计时器和扫描任务；编译接受的指纹及重置都会使旧扫描结果失效。`ShutdownAsync` 会取消并等待扫描与已开始的通知结束，再关闭其余依赖。`YarnHotReloadApplier` 只在引擎线程处理安全边界检查、版本激活、恢复事务和失败回退；服务入口负责去抖、编译代次、候选排队与主线程调度。Runner 停止及恢复后的帧等待仍保留，用于等待 Yarn 展示回调与 VM continuation 稳定。

## 资源解析、缓存与租约

`ResourceManager` 保留公共 API、provider 生命周期和预加载组装配。内部 `ResourceResolver` 负责按 provider 顺序解析路径、语言变体和缺失诊断；`ResourceCache` 在同一个锁下管理共享加载、引用计数、固定资源、预算淘汰和缓存快照。缓存的加载取消范围属于 Runtime，不属于单个调用者或对话会话。

`ResourceLease` 只持有获取结果与释放动作，不访问缓存键或 provider；终态会释放关联的取消令牌源。预加载组创建失败时会释放此前已创建的租约。重置和关闭会先取消共享加载、释放租约，再等待加载 worker 的完整退出，之后才清空缓存、重置或关闭 provider；即使 provider 暂时忽略取消，也会等待它结束。

缓存保留计数的发布与取消检查位于同一临界区；释放动作绑定取得资源时的缓存条目身份。重置前的迟到释放不会扣减同一路径新条目的引用计数，也不会触发新资源的最后租约淘汰。

## 第一条程序集边界

`Assemblies/Majo.Runtime.Core/Majo.Runtime.Core.csproj` 独立编译 `Runtime/Core`，只依赖 .NET 8，不引用 Godot、Yarn 或游戏工程。主工程通过 `MajoRuntime.props` 引用此项目并排除这批源文件的重复编译。生命周期、服务图和激活阶段测试位于 `tests/Majo.Runtime.Core.UnitTests`，可直接运行 `dotnet test tests/Majo.Runtime.Core.UnitTests/Majo.Runtime.Core.UnitTests.csproj`；这条命令不会构建游戏或加载引擎依赖。完整测试门禁仍会检查游戏、发行包和实际 PCK。

Core 命名空间与源码签名保持不变，但程序集改为 `Majo.Runtime.Core.dll`，已经编译的扩展需重新构建；兼容性清单已声明这一变化。领域模型和命令协议也已形成下述独立程序集；Godot/Yarn 适配层和无头集成测试仍在游戏工程中，尚未完成全部程序集隔离。

## 领域模型与规划器程序集

`Assemblies/Majo.Runtime.Domain/Majo.Runtime.Domain.csproj` 只引用 Core 和 .NET 8。它编译 Direction 状态、指令、规划器和动画槽、镜头投影与覆盖计算、Actor 定义/创建请求/FX 数据，以及时间曲线。`Majo.Runtime.Domain.sources.props` 是唯一的领域源码清单：领域工程据此编译，游戏工程据此排除重复编译。编译依赖为游戏/Godot/Yarn 适配层 → Domain → Core，领域工程不引用引擎、游戏、资源实例或命令绑定器。

`DirectionPlanner` 通过 `IBackdropAppearanceResolver` 读取背景 factory 和完整外观。原来的 `ActorDefinitionResolver` 实现该契约，继续提供配置、约定路径及自定义 factory 的元数据；规划器不再获得创建 Actor 的能力。`ActorFxCue` 与 `ActorVisualDelta`、基础 `ActorDefinition` 与 Godot 纹理描述数据分开编译。后端通过 `DirectionPlan.RecordViewCommit`、`RecordActorCommit`、`TryGetActorCommit` 和 `ResolveCommitState` 交换已经准备好的提交采样，不再访问跨程序集的内部字典。

纯规划、身份/状态、投影、时间曲线和槽替换测试位于 `tests/Majo.Runtime.Domain.UnitTests`，直接引用领域工程。原测试工程保留命令、配置、状态存储、真实 metadata adapter 及 Godot/Yarn 的集成测试；混合测试按依赖拆开，原有行为断言保留。以下命令无需安装 Godot，也不会构建游戏或 Yarn：

```sh
./tools/test.sh --domain
dotnet test tests/Majo.Runtime.Domain.UnitTests/Majo.Runtime.Domain.UnitTests.csproj
```

独立 CI job 仅安装 .NET 执行这条门禁；完整门禁也包含这些测试。程序集引用测试会拒绝 Domain/Core 引入引擎或其他适配层程序集。既有命名空间与玩家存档布局保持不变，但已编译扩展须针对 `Majo.Runtime.Domain.dll` 重新构建；`DirectionPlanner` 的构造参数现在是只读元数据契约，原 `ActorDefinitionResolver` 的源码调用可直接使用。该二进制布局变化已经记录在兼容性清单中。

## 命令协议程序集

`Assemblies/Majo.Runtime.Commands/Majo.Runtime.Commands.csproj` 只引用 Core 和 .NET 8，不引用 Domain、Godot、Yarn 或游戏工程。它编译命令定义/属性、参数绑定、基础转换器及其注册、命令注册目录、资源需求描述契约、manifest 和通用等待参数组。`Majo.Runtime.Commands.sources.props` 同时用于独立编译和游戏工程的源码排除，避免同一类型重复编译。

具体演出、音频和存档命令，Direction/时间曲线转换器、Godot 向量转换器、节点访问器及读取对话会话的调度器仍在集成层；命令协议不会因此依赖它们。原有命名空间、源码签名、manifest v2 和玩家存档格式保持不变；已编译的命令扩展需要针对 `Majo.Runtime.Commands.dll` 重新构建，兼容性清单已声明此变化。

`tests/Majo.Runtime.Commands.UnitTests` 仅引用命令工程及命令生成器 analyzer。绑定、默认值、转换器、注册回滚、manifest 与生成命令的测试不再构建游戏；资源描述测试同时验证最长命令路径匹配、参数大小写、类型转换及分析不执行命令。依赖具体命令、Godot/Yarn、对话会话和演出状态的测试保留在原集成测试工程。

```sh
./tools/test.sh --independent
dotnet test tests/Majo.Runtime.Commands.UnitTests/Majo.Runtime.Commands.UnitTests.csproj
```

`--independent` 运行 Core、Domain、Commands 和生成器四个测试工程，无需安装 Godot，也不构建游戏或 Yarn；原 `--domain` 继续只运行 Core 与 Domain。仅安装 .NET 的 CI job 与完整门禁均已接入命令测试。独立 addon 消费者还会在源码安装和实际 PCK 中注册、生成、绑定及执行自己的命令，验证新 DLL 和 analyzer 随发行包正常工作。

## Godot 渲染边界与 GPU 验收

`GodotDirectionRenderHost` 保留生命周期、操作代次、渲染需求和帧等待，连接下列内部组件。它们各自持有状态和资源，不依赖宿主对象；这些拆分不增加游戏侧注册入口，也不改变 Direction 命令或存档格式。

| 组件 | 负责内容 |
| --- | --- |
| `GodotDirectionSurfaces` | Stage、View 和临时镜头的节点树、视口依赖顺序、尺寸与释放 |
| `GodotDirectionScreenComposer` | 分屏/PIP 插槽、布局插值、屏幕转场、幕布与插槽材质 |
| `GodotDirectionBackdropTransitions` | 已准备和已激活的背景转场输入；移除 View 或重置时统一释放 |
| `GodotDirectionRetainedOutput` | 恢复期间保留的独立图片、覆盖层和目标画面等待 |
| `GodotDirectionViewProjection` | 镜头状态到各渲染 pass 的投影及覆盖诊断 |
| `GodotDirectionRenderNodes` | 通用节点创建、可见层和共享输出材质 |

恢复画面保留由独立组件持有，重置 Stage/View 不会清掉覆盖层。退出时先释放保留画面和转场消费者，再释放屏幕、表面和共享材质。未初始化就释放、重复释放和 owner 已先退出场景树的路径也必须清理；不能只依赖 `TreeExiting` 回调。

在有真实显示设备的开发机上运行 `python3 tools/test-rendering.py`。默认会先编译并打开 640×360 的置顶测试窗口，执行 13 项渲染用例，保存后端设备信息、用例结果、图片和恢复统计到 `build/rendering-gpu`。用例覆盖分屏、PIP、视图需求、隐藏捕获、缩放、同帧镜头切换、动态/半透明转场、图层遮罩、重复贴图以及恢复时的转场和幕布保留。每次执行还包含 200 次状态恢复、600 次恢复画面采样和候选/节点释放检查。工具拒绝 headless 和已知软件渲染器，并要求成功标记、完整用例、图片证据与正常退出同时成立。

Apple M4 的验收命令如下；其他平台可通过 `--godot`、`--driver` 指定其实际引擎与后端。

```sh
python3 tools/test-rendering.py --method forward_plus --driver metal
python3 tools/test-rendering.py --method mobile --driver metal
python3 tools/test-rendering.py --method gl_compatibility --driver opengl3
```

2026-11-17 在 Apple M4、Godot 4.7.2 .NET 上，以上三条渲染路径各通过 13 项用例、200 次恢复和 600 次恢复画面采样。恢复循环的节点数保持 62，候选创建/释放均为 200；Metal Mobile 的 6 张恢复截图与重构前基线逐字节一致。本次原始日志、图片和统计位于 `build/render-host-refactor/final`，三个路径记录的渲染源码摘要相同。

GPU 验收与 `tools/test.sh --full` 分开运行，后者仍负责单测、无头集成、独立项目安装、导出及实际 PCK 启动。

## CI 与发布

游戏 CI 从已提交的 addon 副本构建，先运行包内 `Tools/majo.py doctor --project .`，再执行游戏自己的构建、导入和回归。不要在游戏 CI 使用开发者的 `--source` 路径或自动追踪系统 main 分支。

正式出包使用 `addons/majo_runtime/Editor/export.py`，详见插件 README。管理锁中的版本不能替代平台验收；当前本机 macOS 的 PCK 和 Last Bell 独立游戏可执行程序均已验证；Majo 的 Linux 发布门禁已在 GitHub Actions 通过，Windows 和其他架构仍需补充验证。每款游戏仍应验收自己的实际导出包。


## 候选版在线安装和受检升级

不克隆系统仓库时，可以从已发布的明确版本取得安装入口：

```sh
curl -fsSL https://github.com/pakholeung37/majo-novel/releases/download/v0.1.0-rc.2/install.py | python3 - init --version v0.1.0-rc.2 --project ./my-game --name MyGame
```

升级命令改用 `upgrade --check`，可加 `--check-command "python3 tools/dev.py check"`。它在临时游戏副本中安装候选依赖、构建、导入并执行游戏回归，全部通过才切换当前项目。失败时当前依赖与锁文件不变；通过后，在 `.majo/history` 留下旧版依赖、锁文件和宿主配置。

用包内管理器的 `rollback --list` 查看记录，`rollback --check --godot /path/to/godot` 检查并恢复上一版。回退不会覆盖剧情或素材；升级后若修改了宿主工程配置，回退会停止并要求先合并。切换后需要重新构建/导入实际项目，玩家存档不随此流程恢复或迁移。`.majo/` 应加入游戏的 `.gitignore`。
