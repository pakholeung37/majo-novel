# Yarn 窗口预加载

实现位于 `addons/majo_runtime`，不修改 Yarn VM、Yarn 编译器、`addons/YarnSpinner-Godot` 或作者的 `.yarn` 文件。

## 执行约定

- 首次启动等待预算内的首窗口完成；内部节点跳转在节点通知时启动预加载，在 Majo 资源命令执行或台词呈现前等待必要资源。
- `YarnPreloadPlanner` 读取当前项目的编译指令，只扫描确定的直线路径，最多 128 条指令、配置数量的台词、一个后续显式 beat。不会执行条件或调用用户函数以预测路径。
- 遇到分支、选项、跳转、返回、表达式或动态替换停止前瞻。节点入口和唯一的实际台词 ID 重新定位；只有已知直线路径中的匹配命令可以推进指令位置。重复命令不会被当成跨分支定位依据。
- 动态命令使用 Yarn 已求值的参数准备资源。已解析的命令参数不回写脚本或编译指令。
- 台词资源屏障在 Majo presenter 并发分发前执行。命令资源屏障包含在 command activity、取消及异常处理范围内。
- 显式 beat 内已经准备的资源保持 lease 到 `beat end` 提交，防止命令只是收集 patch 时资源提前被淘汰。

## 资源范围

支持 `backdrop`、显式 `character appearance:`、`bgm`、`sfx`、`voice` 和台词 ID/`voice:` metadata 对应的语音。`voice:none` 不加载。缺失资源及预加载失败回到原有消费者的错误或占位策略。

角色依赖由 actor manager/factory 提供。普通角色使用 definition 的 appearance key 和 override；分层角色列出工厂实际加载的全部 atlas。依赖查询不创建 dormant actor，不改变场景或 appearance。

贴图和语音使用对应语言与 fallback 规则，语言变化会清理并重建前瞻窗口。显式带路径的 `voice` 命令保持音频管理器的直接路径语义。

## 默认配置（Resources）

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| `EnableYarnPreloading` | `true` | 启用自动窗口预加载 |
| `PreloadVoiceLines` | `4` | 前瞻实际有配音的台词数量（跳过缺失语音及 voice:none） |
| `PreloadBudgetMiB` | `96` | 当前必要资源加推测资源的窗口估算预算 |
| `PreloadMaxResources` | `12` | 窗口数量限制；必要资源不受此限制 |
| `PreloadTextureEstimateMiB` | `32` | 未知贴图体积的保守下限 |
| `PreloadAudioEstimateKiB` | `2048` | 每个音频资源的估算体积 |

PNG 可读取源文件时，读取 24 字节头部，按 RGBA8 加完整 mip chain 估算，并取配置下限与估算中的较大值。导出后源 PNG 不可读、非 PNG、音频解码和驱动额外副本仍需实际 profiling；这个预算不代表进程或显存硬上限。

窗口一次只发起一个 provider acquisition。窗口失效不会把底层仍运行的共享 I/O 当作已取消并立即发起一批新加载；等待当前 acquisition 完成后放弃旧队列。调用者的取消等待可立即结束，后台完成后释放过期 lease。

必要资源优先，即使自身超过预算也等待加载，并记录 `preload.required_over_budget`。超预算时停止添加推测资源；例如一个大 atlas 已超预算，窗口无法降低显示该角色本身所需的内存。

切换窗口释放不再需要的 lease，共同资源复用。真正的资源回收仍遵循 ResourceManager 的 LRU 预算及 Godot/Actor/Audio 的对象持有关系。不会全局调用 `TrimCache()` 影响其他服务。

## 生命周期与边界

停止、会话完成、重置清理窗口；新项目/热重载从新编译程序重新规划，恢复台词会从实际 line ID 重定位。热重载的当前台词重放不会为了当前语音重新申请必需加载。

自定义工厂可实现 `IActorFactory.GetPreloadResources` 提供依赖；默认返回空集合。没有显式 appearance 的动态外观、未注册在 Majo 的原生 Yarn command/presenter，以及不能静态推导的资源仍由原有消费者准备。静态前瞻不保证每次使用都无等待，但 Majo 的资源命令与台词会在实际使用前等待。

## 验证

- C# 单元测试覆盖真实编译产物的窗口推进、分支/选项、动态参数、重复定位、窗口预算、重叠复用、过期加载、取消、失败重试、命令屏障与 PNG 估算。
- Godot headless `--preload-regressions` 使用实际 `Background_025_001`、`Warden/1`、`0101Adv02_Ema014`，验证启动窗口完成后消费者加载不增加 cache miss，并验证分层 atlas 发现不创建角色。
- 完整 headless suite 同时覆盖现有存档、恢复、热重载、命令、角色和演出回归。

示例：

```sh
dotnet test tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj --no-restore
/Applications/Godot_mono.app/Contents/MacOS/Godot --headless --path . res://tests/headless/HeadlessTestRunner.tscn -- --preload-regressions
```

## 语音命中与重复 atlas 加载诊断

`resource.cache_miss` 包含预加载首次读盘，不能直接用所有 resource hit/miss 计算播放命中率。资源使用前的屏障单独记录 `preload.required_ready`、`preload.required_wait` 与 `preload.required_wait_completed`（等待毫秒）；按语音路径过滤这些事件能区分就绪和使用前等待。required_ready 也可能是普通缓存复用，不单独归功于预加载。

分层角色创建时已经加载并持有全部 atlas。此后换 appearance 不再申请相同 atlas 的预加载 lease，避免 ResourceManager 缓存项被淘汰后，预加载为了重建缓存再次计入整张 atlas 并挤掉语音（Godot 自身可能仍缓存着同一对象，不一定重新读盘）。角色删除后再次创建仍会查询依赖。工厂通过 `PreloadExistingAppearances` 声明此能力；默认 true，普通逐张 appearance 贴图不会跳过预加载。

旁白不消耗语音句数，但仍受 128 指令扫描上限和控制流边界限制；不会为了凑满语音窗口无限扫描整个 node。
