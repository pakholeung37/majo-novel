# Majo Runtime 第四阶段 Roadmap

状态：进行中；M17、M17.5 已完成；M18、M19 核心 Runtime 已实现，正在收尾验收，下一里程碑为 M20
承接范围：第三阶段 M10.5-M15 已完成；原定 M16 Rollback 已取消  
目标阶段：M17-M22  
主题：生产化创作、表现能力与 1.0 准备

## 1. 阶段目标

第三阶段已经完成可分发、可诊断、可持久化的完整玩家循环。第四阶段不再扩展时间回退
或通用玩法系统，而是解决真实视觉小说制作进入量产后最直接的内容与表现问题：

1. 台词、Voice、字体和内容资源可以在运行时可靠切换语言；Runtime UI 本地化由项目负责。
2. 大型章节可以显式预取并释放资源，不因表现能力增长形成不可控卡顿或内存常驻。
3. Camera、场景 Transition 和 Screen FX 使用现有 Operation、Skip、State 与 Diagnostics
   语义，不建立第二套动画系统。
4. 分层立绘和视频等富媒体能力通过可选 Actor/Backend 扩展，不污染基础插件依赖。
5. 常见配置、Command 和资源错误可以在 Godot Editor 或构建门禁中提前发现。
6. 公共 API、状态兼容、性能、文档和发行包达到可发布 1.0 候选版的标准。

证词、证据、推理和辩证玩法继续永久位于游戏项目或独立玩法插件。Rollback 已取消，
不会以隐藏子任务进入任何第四阶段 milestone。

## 2. 排序依据

第四阶段按“身份先于资源、资源先于表现、运行时契约先于编辑器工具”的顺序推进：

```text
Phase 3 complete (M10.5-M15)
    -> M17 Runtime Localization & Text Accessibility
    -> M18 Resource Preload & Lifetime
    -> M19 Camera, Transition & Screen FX
    -> M20 Rich-media Actor Extensions
    -> M21 Editor Authoring & Validation
    -> M22 1.0 Readiness Gate
```

依赖规则：

- M17 先冻结 locale identity、fallback 和切换事务；M18 才能正确设计 locale-aware 资源组。
- M18 先冻结 lease、预算和释放语义；M19-M20 不各自实现互相冲突的资源缓存。
- M19 的 Stage/FX 状态与 Command 契约通过后，M20 才接入富媒体 Actor。
- M21 只消费 Runtime 已有的配置与验证契约，不在 Editor 中维护第二套真相来源。
- M22 不再加入新的大功能，只处理兼容、性能、文档、打包和发布阻断问题。

## 3. 跨里程碑必须冻结的语义

### 3.1 Locale 是 Settings，不是 Game State

- 当前 locale 属于 Settings Scope，跨 New Game 和 Load Game 保留。
- Played Register 默认继续使用 `ProjectId + stable LineId`，切换语言不把同一台词重新视为
  未读；项目仍可显式选择 locale-aware identity policy。
- History 保存实际展示时的 locale 和 resolved text，不在日后切换语言时改写旧条目。
- Game Slot 记录创建时 locale 只用于展示和诊断，Load 不覆盖用户当前 locale。

### 3.2 资源必须有明确所有权

- `ResourceKey` 继续表示稳定逻辑身份；locale、quality 或 backend variant 由显式解析上下文
  决定，不让调用方拼接路径猜测。
- 预取返回可释放的 lease/group handle；释放 handle 代表取消 Runtime 的保留意图，不保证
  Godot 立即回收仍被场景对象引用的底层资源。
- 自动淘汰只处理 Runtime 拥有且无活跃 lease 的缓存项，不扫描或释放宿主项目对象。
- 资源预算是诊断和淘汰边界，不虚假承诺跨平台精确的 GPU 内存数字。

### 3.3 表现能力复用现有 Operation

- Camera、Transition、Screen FX 和富媒体 Actor 的耗时行为统一返回 `IOperationHandle`。
- `wait`、group、Cancel、Complete、Reset、Shutdown 和 correlation 继续使用 M2/M11 语义。
- Skip 只 Complete 当前 Dialogue correlation group 中显式 `skippable` 的表现 Operation。
- 持久化只记录可重建的稳定终态；shake、粒子播放头、视频解码器、Tween 和 GPU 对象不进入
  State Snapshot。

### 3.4 可选集成不能成为基础依赖

- Spine、Live2D、特定视频解码器或第三方 Shader 只能通过独立 adapter 接入。
- `addons/majo_runtime` 的 clean-consumer fixture 在没有这些依赖时仍必须编译、运行和导出。
- 缺失可选 backend 时在 Preflight 报告类型化错误，不在播放中途产生空引用或静默降级。

## 4. M17：Runtime Localization & Text Accessibility（已完成）

### 4.1 目标

建立 Yarn/Godot 边界清晰的 locale 管理服务，使 Dialogue、Voice、Runtime UI、字体和
Settings 使用同一个当前 locale、fallback chain 与切换事务。

### 4.2 交付

- `ILocaleManager`：枚举可用 locale、当前 locale、fallback chain、切换状态和事件。
- locale catalog/provider 契约：分别解析 Yarn line、Voice、Runtime UI 文本和字体资源。
- 当前 locale 接入 `ISettingsManager`，启动早期恢复，不先显示默认语言再跳变。
- Voice 使用与当前 line 相同的 locale resolution result，避免文本与语音独立 fallback。
- Runtime 自带 History、Save/Load、Settings、Diagnostics UI 的可替换字符串 catalog。
- 字体 fallback、文本缩放和行距配置；默认 UI 在 CJK 长文本与较大字号下可滚动且不截断。
- locale 切换事务：先 preflight catalog/font，再提交；VM/Command 执行中请求切换时排队到
  最近安全 presentation boundary，不中断正在执行的 Command。
- 当前台词或选项位于稳定展示边界时，以相同 Line ID 重新 resolve 和渲染，不推进 VM、
  不新增 History、Played 或 Voice 重播记录。
- 结构化诊断记录 requested/resolved locale、fallback 来源、缺失 key 和切换阶段，但不记录
  完整本地化文本表。

### 4.3 严格验收

- 精确 locale、父级 locale、项目默认和缺失四种 fallback 结果确定且可诊断。
- 切换失败保留旧 locale、当前台词、Voice 和 Settings 已提交值，不形成半切换状态。
- 在台词、选项、Auto、Skip、History 和 Settings UI 各边界切换语言不重复推进或选项提交。
- 文本与 Voice 使用同一次 locale resolution；缺失 Voice 按配置静默跳过或报告 Warning。
- locale 切换不改变默认 Played identity，History 既有条目不被翻译或覆盖。
- Settings round-trip 后 locale、字体和文本缩放在 Runtime 启动首帧前生效。
- 至少以英文、简体中文和一个 fallback locale 完成 unit、headless 与可见 UI smoke。

### 4.4 完成结论

- Locale 切换事务已串行化；Command 执行期间排队到下一 presentation boundary，Reset 和
  Shutdown 会取消尚未提交的事务。
- catalog、Godot locale、Yarn line provider 和当前 presentation 全部应用成功后才提交
  `CurrentLocale`；失败恢复旧 locale，不写入 Settings。
- Yarn 首次展示和重渲染都记录实际 resolved locale，Voice 与 History replay 使用同一结果；
  选项重渲染保留 substitution，不推进 VM 或提交选项。
- Settings 与 Locale 双向协调，启动恢复在 Runtime 初始化完成前应用；直接 Locale API 切换也
  会写回 Settings。History 和 Save metadata 保留展示或创建时 locale。
- 默认 Settings、History、Save/Load 和 Diagnostics UI 已接入英文/简体中文 catalog，并支持
  字体 fallback、文本缩放、行距与滚动布局。
- unit、Godot headless、visible demo、clean-consumer、Release build 和目标平台 export 门禁通过。

## 4.5 M17.5：Independent Locale & Localized Resource Resolver（已完成）

### 4.5.1 决策

- Dialogue/Text locale 与 Voice locale 是两项独立 Settings，可分别即时切换；Voice 不跟随
  Dialogue。切换期间正在播放或显示的内容不承诺原子替换，下一次解析必须使用新值。
- Runtime 默认 UI 保留英文参考实现，但不读取 Runtime locale、不提供 UI string catalog；宿主
  项目自行实现 UI 本地化。
- 美术、Voice、BGM 和 SFX 共用同一 fallback 规则：精确 locale -> 父级 locale -> 对应通道的
  默认 locale -> 无 locale 的平铺资源。简单项目可以只放平铺资源。
- 美术使用 Dialogue locale；切换后可见 Actor 立即 best-effort 重载当前 appearance。新资源缺失
  或加载失败时保留旧画面，不承诺与台词重渲染原子同步。

### 4.5.2 交付与验收

- `ILocaleManager` 暴露独立的 `DialogueLocale` / `VoiceLocale`、默认值、事件、切换 API 与诊断。
- `ILocalizedResourceResolver` 统一返回 resolved path/key、resolved locale 和 fallback source；
  override path 支持 `{locale}`，最后始终尝试无 locale 路径。
- Settings、默认 Settings UI、Audio、Voice presenter、History replay、Yarn 和 Actor loader 已接入
  独立 locale 与统一 resolver；旧 `Language` 设置仅作为读取迁移入口。
- unit 与 Godot headless 已覆盖独立切换、持久化、父级/默认/平铺 fallback；clean-consumer、
  Release build 与目标平台 export 完整发布门禁已通过。

## 5. M18：Resource Preload & Lifetime

### 5.1 目标

在现有 `IResourceManager` 与 provider registry 上增加显式预取、保留、预算和释放语义，
为本地化资源、场景特效和富媒体 Actor 提供唯一资源生命周期边界。

### 5.2 交付

- 类型化 `IResourceLease` / `IResourceGroupLease`，支持完成、取消、释放和诊断状态。
- 显式 preload manifest：可由 Project、Yarn node/tag metadata 或宿主 API 声明资源组。
- locale-aware variant resolution；切换 locale 时预取新组，提交后释放旧 locale 保留组。
- 并发去重：同一 resolved key 的并发请求共享底层加载，但每个调用方持有独立 lease。
- 可配置内存预算、LRU 淘汰和 pinned group；只淘汰无 lease 的 Runtime cache entry。
- Node/章节切换策略：进入前预取、退出后释放；首版不解析未来分支并自动猜测整章资源。
- 加载进度、慢资源、cache hit/miss、预算压力和淘汰原因进入 Diagnostics。
- 默认 Loading UI 只消费进度模型，可整体替换，并使用 Input modal ownership。

### 5.3 严格验收

- 并发加载去重、单调用方取消、最后 lease 释放和失败重试的结果确定。
- locale 切换、node 进入/退出、New Game、Load Game、Reset 和 Shutdown 后无 lease 泄漏。
- 预算超限只淘汰无 lease 项；pinned 与场景正在使用的资源不会被 Runtime 误释放。
- provider 失败保留完整 logical/resolved key 和 correlation，Loading UI 可以恢复或取消。
- Fake provider 可确定测试顺序、进度和取消；Godot headless 覆盖真实 Texture、Audio、Font。
- clean-consumer 和 Release export 不包含宿主未引用的 Demo 预取 manifest。

### 5.4 当前实现进度

- `IResourceLease` / `IResourceGroupLease`、并发去重、独立取消、释放、失败重试和
  Reset/Shutdown 回收已接入 `ResourceManager`。
- 预取 manifest 支持 Dialogue/Voice locale variant、估算预算、pinned group、LRU 淘汰和
  cache snapshot 诊断；现有 `Load` / `LoadAsync` 保持兼容。
- ResourceManager unit tests 已覆盖取消竞态、预算压力、pinned 保留、locale fallback 和
  reset 后重载；Godot headless 已验证生命周期回收。

## 6. M19：Camera、Transition & Screen FX

### 6.1 目标

参考 naninovel 建立统一 Stage 表现服务，覆盖常用 2D Camera、场景转场和屏幕效果，同时保持现有
Operation、Playback、State 和 Resource 契约。

### 6.2 交付

- `ICameraManager`：位置、缩放、旋转、聚焦、reset 和可取消 shake。
- `IStageTransitionManager`：fade、crossfade、wipe 及项目自定义 transition provider。
- `IScreenEffectManager`：颜色叠加、flash、letterbox 和可替换 effect backend。
- Camera/Transition/FX Command 采用现有 vertical-slice registry、binder 与 usage metadata。
- 所有持续效果显式声明 operation ID/group、wait 和 skippable；冲突属性使用现有单写入者
  与 Complete-to-final-state 规则。
- Camera 稳定 transform、持久 screen overlay 和 Stage appearance 进入 Game State；shake、
  flash 中间帧、transition capture texture 和 backend 对象不持久化。
- Reduce Motion 设置：可把 shake/flash 等效果压缩为安全终态，不改变脚本控制流。
- 默认 2D backend 与扩展 provider 分离，复杂 Shader 不进入 Core。

### 6.3 严格验收

- 并行、等待、Cancel、Complete、Skip、Reset 和 Shutdown 后 Camera/FX 终态确定。
- 相同属性冲突不会遗留竞争 Tween；不同属性可以并行并按 group 汇合。
- Save -> mutate -> Load 恢复稳定 Stage 终态，不尝试恢复 transition 中间帧。
- locale 切换与资源预取期间触发 Transition 不会引用已释放资源。
- Reduce Motion 不改变 Yarn 指令顺序、Command 完成语义或存档结果。
- headless 测试覆盖状态与 Operation，可见 smoke 覆盖画面层级、遮罩和窗口尺寸变化。

### 6.4 当前实现进度

- 默认 Godot 2D backend、Fake backend、Camera/Transition/Screen FX manager 和自定义
  transition provider registry 已接入 Runtime service graph。
- Stage operation 复用现有 scheduler 的 wait/group/Cancel/Complete/Skip 语义，并提供
  `cameraMove`、`cameraZoom`、`cameraRotate`、`cameraReset`、`cameraShake`、`transition`、
  `overlay`、`letterbox`、`flash` 命令。
- 稳定 Camera、Screen Effect 和 Stage appearance 已纳入 Game State；headless 已覆盖
  Save/Load round-trip、Reset/Shutdown 和宿主节点提前释放的 teardown 边界。

## 7. M20：Rich-media Actor Extensions

### 7.1 目标

在不破坏现有 Character/Background Actor 的前提下，提供分层立绘和视频 Actor，并冻结
第三方动画 backend 的最小 adapter 契约。

### 7.2 交付

- Layered Character：稳定 layer/part ID、appearance preset、单层覆盖、排序和批量切换。
- 组合 appearance 的资源组接入 M18，避免每次表情切换重新加载全部层。
- Video Actor/Background：播放、暂停、停止、循环、音量和确定的完成/取消语义。
- `IRichActorBackend` 一类的可选 adapter 边界，支持项目接入 Spine、Live2D 或其他系统。
- State 只保存可重建配置：appearance/layer、visible、transform 和视频 logical position policy；
  解码器、渲染对象和第三方运行时实例不进入 Snapshot。
- 默认插件不捆绑第三方 SDK、模型格式或许可证；adapter 由独立程序集/插件提供。
- 基础 Texture Actor 的 API 与行为保持兼容，项目不采用富媒体能力时无额外启动成本。

### 7.3 严格验收

- 分层 appearance 切换、单层覆盖、并发冲突、State round-trip 和资源释放结果确定。
- 视频自然结束、Skip、Cancel、Complete、失焦和 Load 后的行为由 policy 明确定义。
- 缺失 codec/backend/asset 在 Preflight 失败，不破坏当前 Actor 或 Stage。
- 使用 Fake rich backend 验证 adapter 生命周期；可选真实集成不进入基础门禁硬依赖。
- clean-consumer 在没有 Spine、Live2D 或视频扩展时通过完整 build/headless/export gate。

## 8. M21：Editor Authoring & Validation

### 8.1 目标

把运行期才能发现的配置、Command、资源和本地化错误前移到 Godot Editor 与 CI，同时
保证 Editor 只是现有契约的展示和调用层。

### 8.2 交付

- Project Configuration 检查器：缺失引用、重复 ID、非法范围、无效 backend 和依赖版本。
- Command catalog 浏览器：名称、参数、默认值、示例和来源 module。
- Yarn 静态检查：未知 Runtime Command、未知命名参数、明显类型错误和重复 stable Line ID。
- 资源/本地化审计：缺失 appearance、Voice、font、locale key、preload group 和可选 backend。
- Project Health 面板：兼容版本、插件发行清洁度、State schema、export readiness 与诊断入口。
- 可在 CI 运行的无 UI validation command，输出稳定 error code 和机器可读报告。
- Editor 工具调用 Runtime 共用 validator/descriptor，不复制 Binder、配置规则或资源解析器。

### 8.3 严格验收

- 同一错误在 Editor、CI 和 Runtime Preflight 中使用相同稳定 code 和核心说明。
- Yarn 检查器对动态宿主 Command 可通过显式 catalog 注册消除误报。
- 扫描不修改 Yarn、Resource 或导入产物；取消和重扫不残留后台任务。
- Editor 程序集与资源不进入 Release Runtime 依赖图，不增加 clean-consumer 启动要求。
- 故意损坏 fixture 覆盖每类错误，健康项目连续扫描结果稳定且无新增警告。

## 9. M22：1.0 Readiness Gate

### 9.1 目标

停止增加功能，对 M0-M21 的公共契约、升级路径、性能和发行流程做一次完整收口，形成
可发布的 `1.0.0-rc` 基线；是否正式标记 `1.0.0` 由候选版验证结果决定。

### 9.2 交付

- 公共 API 审计：命名、nullability、取消、事件顺序、线程/主线程要求和替换点文档化。
- 从当前 M15 发行包到候选版的 compile compatibility fixture 与迁移说明。
- State/Settings/Global 格式兼容矩阵；已有有效存档要么加载，要么在破坏当前状态前给出
  明确 migration/unsupported 结果。
- 性能基线：启动、line present、locale switch、预取、存读档和长期播放的时间/分配报告；
  预算按 CI 基准机记录并防止显著回退，不声称跨硬件固定绝对帧率。
- 长时 soak：Auto/Skip、locale 切换、章节预取、Stage FX、Save/Load 与 UI modal 组合运行。
- 支持矩阵、安装/升级/扩展指南、API 示例、故障排查、许可证和 changelog。
- 发行包内容清单、版本号、checksum、Release build 与宿主目标平台 export smoke 自动化。

### 9.3 严格验收

- M0-M21 的 unit、Godot headless、visible smoke、negative probe 和 clean-consumer gate 全绿。
- 旧 consumer fixture 无源码修改即可编译，或每个有意 breaking change 都有迁移文档和版本理由。
- 旧 State 文件不会被静默覆盖；支持、迁移、拒绝和损坏四种结果均可诊断。
- soak 后无未观察 Task、Operation、Input Scope、resource lease、Audio Player 或临时 Node 泄漏。
- Release 发行物不包含测试缓存、Demo 私有资源、可选 SDK 或仓库绝对路径。
- 候选版没有已知的 blocker/critical 缺陷，且 README 安装步骤与自动化 fixture 完全一致。

## 10. 第四阶段统一测试门禁

每个 milestone 继续执行第三阶段统一门禁，并新增：

1. locale、resource variant、Stage DTO 和 adapter 公共契约保持 Yarn/Godot 隔离。
2. Fake provider/backend 测试取消、失败、竞态和生命周期，不使用固定长时间 `Task.Delay`。
3. 每个新异步边界产生稳定 event ID、correlation 和可查询 diagnostic state。
4. Reset/Shutdown 同时检查 locale transaction、resource lease、Stage operation 和 rich backend。
5. 可见 UI smoke 至少覆盖英文、简体中文、大字号和 Reduce Motion。
6. 资源测试使用隔离 fixture，失败不得接触或删除真实项目与用户资源。
7. 可选第三方 backend 不得成为基础 build、headless 或 export 的必要条件。
8. 每个 milestone 更新 Runtime Demo 的同一长生命周期场景，不创建一次性 milestone demo。

## 11. 第四阶段完成定义

只有同时满足以下条件才算完成：

- M17-M21 功能均通过严格验收，M22 候选版门禁全绿。
- Runtime 在同一 Session 中切换 locale 不重复推进、污染 History 或错误重置 Played 状态。
- 大型资源组具备显式预取、进度、预算、释放和诊断，不依赖隐式全局缓存运气。
- Camera、Transition、Screen FX 和 Rich Actor 服从唯一 Operation/Skip/State 语义。
- 未安装可选富媒体 backend 的项目仍可完整使用基础 Runtime。
- Editor 与 CI 能在运行游戏前发现主要配置、Command、资源和本地化错误。
- 现有 consumer 与 State 数据具备可验证的升级结果，发行物可独立安装和导出。
- Runtime 源码仍不包含项目专属推理玩法，也不包含 Rollback 产品实现。

## 12. 明确不进入第四阶段

- Rollback、通用 time travel 和快照栈。
- 多 Yarn Script Track、后台脚本循环和 `sync` 轨道接管。
- 云存档、跨设备冲突合并、账号体系、加密、防作弊和 DRM。
- 通用远程 Debug Console 或可以任意修改 VM/State 的调试器。
- 内建 Spine/Live2D SDK、模型编辑器或第三方资产制作工具。
- 自动翻译、翻译管理 SaaS 或替代 Yarn 官方本地化资产管线。
- 3D Camera、通用 Timeline、粒子编辑器或完整后处理框架。

这些能力只有在出现明确产品需求、维护预算和独立验收标准时才进入后续 roadmap；不会
因为与 Naninovel 功能相似而自动排期。

## 13. 已确认默认与实施前决策

已确认默认：

1. 第四阶段从 M17 开始，不复用已取消的 M16 编号。
2. locale 属于 Settings；Load Game 不覆盖当前 locale。
3. Played Register 默认跨 locale 共享 stable Line ID；History 保留展示时文本。
4. 资源预取只接受显式 manifest/metadata/API，不静态猜测所有未来分支。
5. Stage 与 Rich Actor 复用 Operation、Resource、State 和 Diagnostics，不建立平行内核。
6. 第三方富媒体能力使用可选 adapter，基础发行物不捆绑 SDK。
7. M22 是 feature freeze，候选版验证通过后才决定正式 `1.0.0`。

进入对应 milestone 前仍需以实现 spike 确定：

1. M17 对当前 YarnSpinner-Godot 本地化 API 的最小稳定 adapter 面；不得让公共契约暴露
   Yarn 类型。
2. M18 在 Godot 4.7 下可观测的资源大小与释放信号；无法精确测量时使用稳定估算并明确
   标注。
3. M20 首个真实第三方 rich backend 由实际游戏素材选择；若没有产品素材，只冻结 adapter
   并以 Fake backend 验收，不为展示而引入 SDK。
