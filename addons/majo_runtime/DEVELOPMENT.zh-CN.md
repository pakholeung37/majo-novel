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
