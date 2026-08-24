# Majo Runtime 第三阶段 Roadmap（讨论稿）

状态：待讨论确认  
承接范围：`runtime-plan.md` 的 M10 完成之后  
目标阶段：M10.5、M11-M16  
主题：可分发性、可观测性、持久化玩家循环

## 1. 阶段目标

第三阶段不继续横向堆叠表现功能，而是把已经完成的 Dialogue、Script、State、
Input、Audio、Auto 和 History 基础转化为可发布、可诊断、可长期游玩的 Runtime。

本阶段优先证明：

1. 插件可以脱离当前仓库结构安装到干净的 Godot C# 项目。
2. Runtime 的异步行为可以通过结构化日志和只读诊断快照完整追踪。
3. Game、Global 和 Settings 状态可以安全、可迁移地持久化。
4. 玩家可以使用 Manual、Quick 和 Auto Save 完成完整存读档闭环。
5. Auto、Skip 和 Manual 共享唯一播放模式所有权，不形成互相竞争的控制器。
6. Rollback 只跨越明确支持回退的状态边界，不虚假承诺任意外部副作用可逆。

本阶段仍保持以下永久边界：证词、证据、推理和辩证玩法由游戏项目或独立玩法插件
实现，不进入 Majo Runtime。Runtime 只提供状态贡献、保存资格和 Rollback barrier 等
通用扩展接口。

## 2. 为什么先做 Diagnostics

当前 Runtime 已有 `IRuntimeLogger`、Godot Console 输出、Runtime 生命周期日志和
Operation 后台异常观察，但它们只构成最低限度的日志骨架：

- Logger 只接收 level、message 和 exception，没有 category、event ID 或结构化属性。
- RuntimeHost 和 OperationScheduler 之外的大部分服务没有统一使用 Logger。
- Bootstrap、Presenter 和 UI 中仍存在直接调用 `GD.PushError` 的路径。
- 没有 Session、Dialogue、Command 和 Operation 的关联 ID。
- 没有日志过滤、内存缓冲、文件 Sink、报告导出或统一故障快照。
- 没有能够回答“当前究竟在等待什么”的 Debug Inspector。

Save/Load 和 Rollback 会把一次行为扩展成跨 Dialogue、Script、State、Actor、Audio、
Input 和 Operation 的恢复事务。如果先实现持久化而后补诊断，偶发失败只能依靠零散
Godot 输出反推。因此 M11 Diagnostics 是 M12-M16 的强制架构门。

## 3. 总体顺序

```text
M10 Auto + History
    -> M10.5 第二阶段收尾与可分发性门禁
    -> M11 Diagnostics & Observability
    -> M12 Durable State Foundation
    -> M13 Save / Load Product Loop
    -> M14 Settings & Global State
    -> M15 Played Register & Skip
    -> M16 Rollback
```

依赖规则：

- M10.5 未通过前，不冻结第三阶段公共 API。
- M11 未通过前，不开始磁盘存档和 Rollback。
- M12 未通过前，不实现 Slot、Quick Save 或 Auto Save。
- M14 的 Global State 未持久化前，不实现跨周目的 Read-only Skip。
- M16 复用 M12/M13 的状态恢复事务，不建立第二套恢复系统。

## 4. M10 结束前需要冻结的语义

### 4.1 播放模式所有权

Manual、Auto 和未来 Skip 必须只有一个模式真相来源。建议稳定模型为：

```text
PlaybackMode = Manual / Auto / Skip
```

M10 可以只实现 Manual 和 Auto，但不应让 Auto 与未来 Skip 各自持有互不知情的
Input Scope、deadline 和暂停状态。如果 M10 最终保留 `IAutoManager`，M15 应通过兼容
Facade 迁移到统一的 `IPlaybackModeManager`，普通调用方不同时操作两个模式服务。

### 4.2 Session 生命周期

第三阶段固定以下默认语义：

```text
New Game       清除 Game + Session，保留 Global + Settings
Load Game      替换 Game，清除 Session，保留 Global + Settings
Return To Title
               清除 Game + Session，保留 Global + Settings
Runtime Reset  清除所有瞬态对象；具体 Game 行为由显式 Session 操作决定
```

History 属于 Session。加载 Game Slot 后清空 History，不把当前进程中后来观看的台词
错误地带入已加载的旧进度。

### 4.3 安全保存边界

首版只在 `AwaitingAdvance` 安全边界保存和压入 Rollback Snapshot。待选选项、非空
Yarn call/value stack、Command 执行中、Presenter 显示中和任意 VM 指令中间态不可保存。

调用方必须能够查询 `CanSave` 及类型化拒绝原因。首版不在不安全阶段静默等待，也不
猜测最近可用位置。

## 5. M10.5：第二阶段收尾与可分发性门禁（已完成）

目标：兑现“可复用 Godot 插件”的安装承诺，并形成第三阶段开始前的稳定基线。

交付与验收结果（已全部通过）：

- 完成 M10 Auto、History、默认 UI、可见 Demo 和全部测试门禁。
- 将 Yarn VM State Bridge 纳入实际插件发行物（`addons/majo_runtime/Bridges/YarnSpinnerStateBridge`），并通过 `MajoRuntime.props` 自动引用与编译隔离；彻底移除仓库外层的相对 `ProjectReference`。
- 建立最小 clean-consumer fixture（`tests/fixtures/clean_consumer`），只复制发行插件并单独安装兼容 YarnSpinner-Godot。
- 验证从空缓存下的 Editor 导入、C# 编译、headless 运行（覆盖 Dialogue -> Command -> State Checkpoint Capture/Restore 闭环）、Release 构建与宿主目标平台 Godot export smoke。
- 自动化负向探针通过真实 `MajoRuntimeBootstrap` 启动验证：缺失或不兼容的 Yarn 依赖抛出明确类型化异常（`RuntimeLifecycleException`）。
- 校验发行物清洁度：`addons/majo_runtime` 不包含 Demo 临时资源、测试缓存或仓库专用路径。
- 更新安装、配置、扩展、兼容性和故障排查文档（`addons/majo_runtime/README.md` 与 `docs/plugin-quickstart.md`），确保 README 所述安装步骤与自动化 clean-consumer 测试 100% 一致。
- 明确并固化 Runtime API（SemVer）、State 格式（v1 严格指纹）和 Yarn Bridge（精确版本 fail-fast）的版本策略。
- 门禁脚本 `tools/test.sh` 升级并一次性绿色通过。

## 6. M11：Diagnostics & Observability

### 6.1 目标

建立 Yarn/Godot 无关的结构化日志契约、跨服务关联上下文和只读 Runtime 诊断系统，
使开发者可以从一次报告重建关键时序，而不需要在各模块临时加入 `GD.Print`。

### 6.2 Structured Logging 契约

建议公共模型：

```text
RuntimeLogEvent
├── Timestamp
├── Level
├── Category
├── EventId
├── Message
├── Exception
├── Correlation
└── Properties

RuntimeLogCorrelation
├── RuntimeId
├── SessionId
├── DialogueSessionId
├── CommandId
└── OperationId
```

具体类型名可以在实施前调整，但语义不可弱化：

- Category 表示稳定模块边界，如 `core.lifecycle`、`dialogue`、`command`、`operation`、
  `input`、`audio`、`script` 和 `state`。
- Event ID 是稳定、可搜索的机器标识；Message 是面向人的说明，不作为测试协议。
- Properties 只允许稳定的基础值或安全字符串，不放入 Godot/Yarn 对象引用。
- Correlation 由调用链继承，使 Line -> Command -> Operation -> Completion 可以关联。
- 日志 Context 不参与游戏状态，不进入 State Snapshot。

建议 Sink：

- Godot Console Sink：开发期默认输出。
- In-memory Ring Buffer Sink：保留最近 N 条事件，供 Overlay 和失败报告读取。
- Recording Sink：纯 C# 测试确定性断言。
- JSON Lines File Sink：可选发行诊断，不阻塞 Godot 主线程。
- Composite Sink：一个 Sink 失败不能阻止其他 Sink，也不能让 Runtime 失败。

配置：

- 全局最低等级。
- 按 Category 覆盖等级。
- Ring Buffer 容量。
- 是否启用文件日志、文件数量和单文件大小上限。
- Debug/Release 默认值不同，但错误日志始终可用。

日志原则：

- 记录状态变化、边界和异常，不记录每一帧。
- 被预期的取消不以 Error 记录；意外取消和超时携带原因。
- 同一个异常在责任边界记录一次，外层只补上下文而不重复打印完整堆栈。
- 默认不记录 Dialogue Variables 值、State payload、用户输入文本或绝对用户路径。
- 日志关闭时不产生显著的每帧分配和字符串格式化开销。

### 6.3 首批事件覆盖

| 模块 | 必须记录的边界 |
| --- | --- |
| Core | Runtime 和逐服务 Initialize/Reset/Shutdown、耗时、失败与清理结果 |
| Resource | resolve、provider 选择、cache miss、加载失败；成功加载默认 Debug |
| Dialogue | Session start/stop/complete/fault、node、line、options 和 Presenter 等待边界 |
| Command | 绑定、开始、完成、取消、失败以及来源 line ID |
| Operation | start、wait、complete、cancel、fault、ID/group 和耗时 |
| Input | Scope/Muter acquire/release、modal owner 变化；逐输入分发默认 Debug |
| Audio | BGM/Voice/SFX 开始停止、Voice 抢占、资源缺失和 fade 终态 |
| Script | phase 变化、checkpoint capture/preflight/restore 和拒绝原因 |
| State | capture/restore 阶段、Contributor 顺序、preflight 和破坏性阶段失败 |
| Auto/History | 模式、暂停原因、deadline、History open/close 和 Voice replay 失败 |

### 6.4 Runtime Diagnostics

新增 Yarn/Godot-free 的只读诊断边界：

```text
IRuntimeDiagnostics
├── CaptureSnapshot()
├── RecentEvents
└── ExportReportAsync(...)

IDiagnosticContributor
├── Key
└── Capture()
```

默认诊断快照至少包含：

- Runtime 状态、版本、兼容依赖和已初始化服务。
- 当前 Session、Dialogue Project/Node/Line、Script phase 和安全快照可用性。
- 活跃 Command、Operation ID/group/state/elapsed time。
- Input Scope/Muter 数量、modal owner 的安全名称和允许动作。
- Actor 的稳定 ID、appearance 和可序列化终态。
- BGM tracks、SFX 数量、当前 Voice 和 mixer 状态。
- StateManager 阶段、最近 checkpoint 和最近拒绝原因。
- Playback mode、暂停原因、History open 状态和条目数量。
- 最近结构化日志，不包含敏感 payload。

默认 Debug Overlay：

- 只在 Debug build 或显式配置下启用。
- 通过独立 `majo_debug` Input Action 打开；使用 M7 Modal Scope。
- 只读展示，不允许任意设置变量、跳转 Yarn VM 或修改 State。
- 支持复制摘要和导出完整诊断报告。
- PackedScene 可整体替换；关闭后准确释放输入所有权和 Node。

### 6.5 M11 严格验收

- 纯 C# 测试验证等级过滤、Category 覆盖、Context 继承、Ring Buffer 淘汰和 Sink 隔离。
- 每种意外异步故障恰好产生一条责任日志，并保留完整关联链。
- 一个 Dialogue Session 的报告可以重建 Line、Command、Operation 和 Presenter 的先后顺序。
- Headless 测试失败时自动附带诊断快照及最近事件，不依赖截图。
- Reset/Shutdown 报告 Operation、Input Handle、Audio Player 和临时 Node 残留。
- Diagnostics 公共 DTO 不引用 Yarn、Godot Node、Resource 或其他场景对象。
- Overlay 反复打开、关闭、Reset 和 Shutdown 后没有输入或 Node 泄漏。

## 7. M12：Durable State Foundation

### 7.1 目标

把 M9.6 的内存 Game Snapshot 扩展为稳定、可替换且可迁移的磁盘持久化基础，但仍不
提供 Save/Load UI 和具体 Slot 产品行为。

### 7.2 分层

```text
IStateManager
    -> StateSnapshot
        -> IStateSerializer
            -> StateEnvelope bytes
                -> IStateStorage
                    -> user:// implementation
```

- `IStateManager` 继续负责服务状态 Capture/Preflight/Restore。
- `IStateSerializer` 只负责稳定格式、Envelope 和迁移，不识别 Slot UI。
- `IStateStorage` 只负责读取、写入、列举、删除和原子替换，不识别 Runtime Service。
- SlotManager 在 M13 组合以上三层，不反向进入 State Core。

### 7.3 State Envelope

至少包含：

- Envelope format version。
- Runtime version、game build ID 和创建时间。
- Scope：Game、Global 或 Settings。
- StateSnapshot 和 Contributor schema 元数据。
- Script project/node/line 和 program fingerprint 摘要。
- 可选展示元数据与缩略图引用；不混入 Contributor payload。
- 完整性校验，区分损坏文件和不兼容文件。

兼容策略：

- Contributor schema 变化必须通过显式 migration registry。
- Envelope migration 和 Contributor migration 分离。
- Script checkpoint 默认要求完全一致的 Program Fingerprint。
- 游戏如需兼容剧本更新，必须显式注册旧 fingerprint + line ID 到新 checkpoint 的迁移。
- 禁止按邻近行、program counter 或相似文本自动猜测恢复位置。
- 所有迁移和 Preflight 在破坏当前 Runtime 状态前完成。

存储要求：

- 写入临时文件，flush 成功后原子替换目标文件。
- 写入失败保留上一个有效版本。
- 路径由逻辑 key 解析，不接受任意绝对路径。
- 支持取消、并发互斥和确定错误类型。
- 首版不实现云同步、加密和压缩；加密不作为防篡改承诺。

### 7.4 M12 严格验收

- Snapshot -> bytes -> Snapshot 确定 round-trip。
- 未知格式、损坏内容、checksum 错误、schema 缺失和脚本不兼容均在 Preflight 失败。
- 模拟写入中断后旧文件仍可读取。
- 同 key 并发写入不会产生交错内容或半文件。
- 自定义 Storage 和 Serializer 可以替换而不修改 StateManager。
- 诊断报告记录迁移链和拒绝原因，但不记录完整 State payload。

## 8. M13：Save / Load Product Loop

### 8.1 目标

在 M12 上建立玩家可直接使用且可替换的存读档产品能力。

公共能力：

- `ISaveSlotManager`：列举、读取元数据、保存、加载、删除和重命名。
- Manual、Quick、Auto 三种类型化 Slot，不依靠字符串前缀让调用方猜测类型。
- `CanSave`、`CanLoad` 和类型化不可用原因。
- Save/Load 状态、进度和开始/完成/失败事件。
- Slot 操作互斥；Load 期间输入被 modal scope 接管。

Slot 元数据：

- Slot ID 和类型。
- 保存时间、游戏版本和 Runtime 版本。
- 当前 Project、Node、Line ID、Speaker 和最终可见文本摘要。
- 可选玩家名称或开发者 metadata。
- 缩略图引用和兼容状态。

产品行为：

- Manual Save 只在安全边界启用。
- Quick Save 和 Auto Save 使用独立可配置轮换链。
- Auto Save 只在明确配置的安全 node/tag/line boundary 触发，不使用定时盲存。
- Load 先读取、迁移和完整 Preflight；失败不停止当前游戏。
- Load 成功开始新 Session，清空 History 和瞬态 Auto/Skip 状态。
- Global 和 Settings 不随 Game Slot 回退。
- 默认 Save/Load PackedScene 支持键鼠和手柄，并可整体替换。
- 缩略图捕获失败不破坏主存档；错误在 Slot metadata 和日志中明确显示。

非目标：

- 云存档、跨设备冲突合并。
- 任意脚本版本自动兼容。
- 存档加密、防作弊或 DRM。

### 8.2 M13 严格验收

- Manual/Quick/Auto Slot 的容量、轮换、覆盖和排序确定。
- 保存失败不会覆盖有效旧档；删除和重命名范围精确。
- 加载损坏或不兼容 Slot 不改变当前 Dialogue、Actor、Audio 或 Input 状态。
- 加载有效 Slot 恢复 Script、Variables、Actors、Background 和 BGM，当前行只重放一次。
- Load 后 History 清空，Global/Settings 保留，SFX/Voice 不错误恢复。
- 默认 UI 在空 Slot、忙碌、失败、覆盖确认和手柄导航下行为明确。
- headless 连续执行 Save -> mutate -> Load 三轮，无 Command、Operation 或 Node 泄漏。

## 9. M14：Settings & Global State

### 9.1 目标

把项目 Resource 中的默认值与玩家运行时设置分离，并正式启用 M9.6 已冻结的 Global 和
Settings Scope。

Settings 范围：

- Master/BGM/SFX/Voice 音量与静音。
- 打字速度。
- Auto 额外停留时间和是否等待 Voice。
- Skip 默认模式和可选速度。
- 默认 UI 显示偏好。
- Runtime 语言字段只冻结身份；完整热切换在后续 Localization 阶段实现。
- 宿主可通过 Settings Contributor 扩展窗口、显示或自定义玩法设置。

行为：

- Project Configuration 提供默认值，`ISettingsManager` 持有当前用户值。
- 修改立即投影到对应服务，保存失败不撤销内存设置但必须报告状态。
- Reset to Defaults 使用当前项目默认值，不硬编码 Runtime 常量。
- Settings 在 Runtime 初始化早期加载，避免先以默认音量播放再跳变。
- Global State 跨 New Game 和 Game Slot Load 保留。
- 默认 Settings UI 可整体替换，并使用 M7 Input modal ownership。

### 9.2 M14 严格验收

- Settings 跨 Runtime 重启恢复，Game Load 不覆盖 Settings。
- 音量、打字速度和 Auto delay 修改即时生效。
- 损坏 Settings 文件回退到默认值并给出明确诊断，不阻止 Runtime 启动。
- Global 与 Settings Contributor 不进入 Game Slot。
- 自定义 Settings Contributor 的依赖、迁移和失败语义与 State Core 一致。

## 10. M15：Played Register & Skip

### 10.1 Played Register

- 以 `ProjectId + stable LineId` 作为身份，不使用 Yarn instruction index。
- 只有台词实际展示完成后才标记已读。
- 存入 Global State，跨 New Game 和 Game Slot Load 保留。
- 可选记录内容 fingerprint；内容显著变化时由项目策略决定是否重新视为未读。
- History、Played Register 和 Rollback Stack 是三个独立概念，不共享存储。

### 10.2 Playback Mode

统一模式：

```text
Manual <-> Auto
Manual <-> SkipReadOnly
Manual <-> SkipAll
```

- 任意时刻只有一个主动推进模式。
- History、菜单、Save/Load、State restore 和 Debug Overlay 打开时暂停。
- `SkipReadOnly` 遇到未读台词立即回到 Manual。
- Skip 在选项处停止，不自动选择。
- 手动 Advance 与 Skip tick 同时发生时只推进一次。
- Skip 对视觉 Operation 使用 `Complete` 到目标终态，不用 `Cancel` 留在中间状态。
- Voice 默认停止或不播放；策略可配置，但不能残留后台 Voice。
- Skip 不通过提高全局 SceneTree time scale 实现，避免影响宿主玩法和物理系统。

### 10.3 M15 严格验收

- Auto、Skip 和 Manual 互斥，切换不会遗留 deadline 或 Input Scope。
- Read-only Skip 在已读连续区间推进，并精确停在第一条未读台词。
- 重复 Line ID 已在 Script 层拒绝，Played Register 不产生身份歧义。
- 选项、History、Save/Load、Debug modal 和外部玩法 barrier 均停止或暂停 Skip。
- 动画、BGM fade 和 Presenter 结束在确定终态，无延迟 Continue。
- Global State round-trip 后已读判定保持一致。

## 11. M16：Rollback

### 11.1 目标

复用 State Snapshot 和 Restore 事务提供有界的玩家回退能力，不实现通用 time travel。

### 11.2 Rollback Snapshot

- 只在 `AwaitingAdvance` 安全边界捕获。
- 使用有数量和估算内存双上限的 ring buffer。
- Snapshot 只在内存中存在，不写入普通 Game Slot。
- 超出容量按最旧优先确定淘汰，并释放关联缩略图或资源引用。
- 相邻完全等价状态可以按稳定 fingerprint 去重，但不得合并不同播放位置。

### 11.3 Contributor 能力与 Barrier

每个 Game Contributor 显式声明：

```text
SupportsPersistence
SupportsRollback
```

- 内建 Script、Variables、Actors、Background 和 BGM 支持 Rollback。
- SFX、Voice、Input Handle、Operation 和 Node 引用仍不进入 Snapshot。
- 外部玩法 Contributor 默认不支持 Rollback，除非显式声明并通过测试。
- 不可逆外部副作用建立 barrier；玩家不能跨越该 Snapshot。
- barrier 必须提供开发者诊断和可选玩家提示原因。
- Runtime 不假设证据提交、成就、网络请求或平台购买可以回退。

### 11.4 玩家语义

- 回退恢复目标行并只重放一次，下一次 Advance 进入正确后续指令。
- Played Register 不随 Rollback 回退，因为玩家已经实际看过对应台词。
- History 默认保留本 Session 实际展示过的内容，不因 Rollback 删除。
- 回退到选项之前的安全行是允许的，但不恢复“选项正在显示”的 VM 中间态。
- Rollback 开始时关闭 Auto/Skip、History、Save/Load 和其他模态界面。
- 默认 Rollback UI 支持逐步回退和可用范围提示，可整体替换。

### 11.5 M16 严格验收

- 容量和内存淘汰顺序确定，Reset/New Game/Load Game 清空 Stack。
- 连续跨多行和分支前回退后，Variables、Actors、Background、BGM 和 Script 一致。
- 不可回退 Contributor 精确形成 barrier，失败不破坏当前状态。
- Rollback 中途 Restore 失败进入明确 Faulted 状态并保持输入阻断。
- Played Register 和 History 遵守既定非回退语义。
- 高频 push/pop 不产生未观察 Task、Operation、Input 或 Node 泄漏。

## 12. 第三阶段统一测试门禁

每个 milestone 继续执行现有统一门禁，并新增：

1. 公共 DTO 的 Yarn/Godot 隔离架构测试。
2. clean-consumer 插件安装与编译测试。
3. Structured Log 事件和 correlation 的确定性测试。
4. 故障测试自动附加诊断快照和最近日志。
5. 磁盘测试使用独立临时目录，不接触真实用户 `user://` 数据。
6. 模拟损坏、写入中断、版本不兼容和迁移失败。
7. Save/Load、Skip 和 Rollback 的核心时序 headless 连续运行三次。
8. 每次 Reset/Shutdown 检查 Operation、Input Handle、Audio Player、Presenter 和 Node 泄漏。

禁止用固定长时间 `Task.Delay` 证明时序；继续使用 Fake Frame Clock、事件边界和合理
超时。文件 I/O 测试允许真实异步磁盘操作，但必须隔离路径并可确定清理。

## 13. 第三阶段完成定义

只有同时满足以下条件才算完成：

- 插件可以从发行包安装到干净 Godot C# 项目，不依赖仓库外部 Bridge 路径。
- 任一 Dialogue/Command/Operation/State 故障都可通过一次诊断报告定位到所属 Session
  和状态边界。
- Manual、Quick 和 Auto Save 可安全 round-trip，损坏或不兼容文件不破坏当前游戏。
- Settings 和 Global State 跨进程恢复，Game Slot 不覆盖它们。
- Read-only Skip 基于稳定 Line ID 的 Played Register，并在未读台词和选项处停止。
- Rollback 只跨越明确支持的 Contributor，并遵守 barrier。
- Manual、Auto 和 Skip 只有一个播放模式真相来源。
- History 仍为 Session 状态，不进入 Game Slot；Played Register 不因 Game Load 或
  Rollback 回退。
- 默认 UI 均可直接使用，也可通过 PackedScene 整体替换。
- 所有核心路径通过纯 C#、Godot headless、可见 smoke、失败探针和重复时序测试。
- Runtime 源码仍不包含证词、证据、推理或辩证玩法实现。

## 14. 延后到第四阶段重新评估

- Camera、复杂 Transition 和 Screen FX。
- 运行时语言切换和完整本地化工具链。
- 分层立绘、Spine、Live2D 和视频 Actor。
- 资源预加载策略和更复杂的资源生命周期。
- Editor Command 检查器、Yarn 辅助诊断和可视化配置工具。
- 多 Yarn Script Track、后台脚本循环和 `sync` 轨道接管。
- 云存档、跨设备冲突合并、加密、防作弊和 DRM。
- 通用远程 Debug Console 或能够任意修改 VM 状态的调试器。

第四阶段的优先级应由真实游戏制作反馈决定，不在第三阶段预先承诺完整 Naninovel
功能对齐。

## 15. 待确认决策

在把本讨论稿标记为“已确认”前，需要确定：

1. 第三阶段首要产品目标是通用插件发行，还是服务当前实际游戏制作。
2. 剧本更新后的旧存档是否必须兼容；默认建议只允许显式迁移。
3. 外部推理玩法是否允许 Rollback；默认建议不允许，除非 Contributor 显式声明。
4. JSON Lines 文件日志是否默认只在 Development build 开启。
5. History 在 Rollback 后是否保留已实际展示内容；当前建议保留。
6. Auto Save 的触发边界使用 Yarn node/tag、项目 API，还是两者同时支持。
