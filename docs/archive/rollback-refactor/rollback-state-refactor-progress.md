# Rollback 状态重构实施记录

状态：已完成并通过最终门禁（2026-09-09）。

## 已接入的执行链

所有 Runtime presenter 和注册的 Yarn 原生 presenter 经过同一适配器：

```text
进入 LineBoundaryGate
→ 资源准备（会话取消令牌）
→ 建立本句身份并冻结 substitutions
→ await State 捕获 → 同步通知 Travel 登记
→ LinePresented → 启动全部 presenter
→ 激活合并的 HurryUp / NextLine
```

- 会话 generation 和 occurrence sequence 分开；重复 lineId 分别登记。
- State 验证当前 backend 的边界对象身份，捕获后再次验证并检查取消。
- 边界捕获不调用 Save eligibility；保存读取已固定的边界，并等待正在准备的边界。
- Travel 删除 PhaseChanged / PauseChanged 捕获、fire-and-forget 捕获及捕获重试字段。热重载登记替换当前项并裁掉未来分支。
- CheckpointRestore origin 跳过新的捕获，load 成功事件用恢复后的边界作为新时间线起点。
- 捕获失败会通知 Dialogue Faulted，并阻止 Yarn 吞掉 presenter 异常后继续播放。
- 原生 presenter 的退出动画仍然必须等待完成，不能跟随可脱离的 Runtime presenter 一起被取消等待。

Yarn 的同步 LineHandler 可能仍位于 Continue 调用内。新增 `CaptureAtLineEntry` 只接受正在交付匹配 RunLine 的状态，或原有 WaitingForContinue 状态；将位置归一到交付完成后的程序计数，不通过等待若干帧实现。

## Game 状态写入审计

| 领域 | 已确认的来源 | 最终处理 |
| --- | --- | --- |
| Script | Yarn VM、backend 当前行与 substitutions | 在 presenter 前冻结；State 使用上下文中的 checkpoint |
| DialogueVariables | Yarn set、命令、公开 variables API | 所有底层 Set/Clear/ReplaceAll 通过 mutation gate；从冻结值表捕获 |
| Direction | DirectionTransactionCoordinator committed State | 异步逻辑提交通过 gate；视觉 settlement 在 gate 外；从 committed State 捕获 |
| Actors | 全局 ActorManager、ActorBase setters / animation targets | appearance、visible、position、scale、rotation、tint、opacity、z、slot/preset 和 destroy 均通过 gate |
| AudioBgm | AudioManager / BgmChannel | 保存曲目、循环、BaseVolume；淡出已逻辑停止的 track 不进入快照；取消淡入淡出保留已提交目标 |
| manosaba.ui | ManosabaUiService.SetFrameMode | 逻辑写入通过 gate；动画只用于展示；保存可生成同 occurrence revision |

Direction 的 GodotDirectionActorTreeBackend 为每个 binding 创建独立 ActorManager。Bootstrap 的全局 ActorStateContributor 捕获的是另一个全局 ActorManager，因此当前这两条内置路径不存在同一对象重复序列化；后续插件仍需验证其所有权。

BGM 沿用已有曲目起点恢复语义，旧 payload 没有播放位置字段，本次没有声称实现精确播放位置续播。取消淡入淡出的行为已改为保留逻辑目标，并更新单元及 Godot 回归断言。

`IDialogueMarkupHandler` 现在必须声明 `Visual` 或 `RunOnce` replay policy。Checkpoint/热重载 replay 只执行 Visual handler，持久或外部副作用不会重复执行。

SessionHistory 仍为 Session scope；PlayedRegister 和 Settings 保持原有 scope。

## 格式变化

- ScriptCheckpoint 格式升级到 2，boundary 使用 `LineEntry`。
- ScriptStateContributor schema 升级到 2。旧存档在 preflight 被拒绝，不删除文件、不自动将旧枚举改名迁移。
- StateSnapshot envelope 和 Direction payload 的结构未变，版本保持原值。
- 使用 `RegisterNativePresenter` 注册原生 presenter；直接写 `Runner.dialoguePresenters` 会绕过 Runtime 的边界协议，不属于受支持的注册入口。

## 验证

初始基线：433 项单元测试通过。

新增覆盖：连续 100 个同名 line occurrence、准备期间重复推进、捕获等待、Stop 取消、失败清理、会话失效、过期凭据、同步 VM LineHandler 捕获、恢复不重跑前置变量修改 / 命令、BGM 淡入淡出目标语义。

`tools/test.sh --fast` 及 `tools/test.sh --full` 已在行入口改造后通过。完整门禁包含 Godot headless、打包和导出检查；这不构成真实渲染无黑帧证据。最终重跑结果见本文件末尾。

格式检查还暴露了此前存在于 GodotConsoleSink、ManosabaDemoController 等文件的空白格式问题，已用仓库质量工具统一修正，未改变这些文件的行为。

## 准备替换与呈现协议（2026-09-09）

已接入以下恢复链：

```text
Preflight → 取消并等待旧行边界退出 → State 事务门与输入锁
→ 冻结 Direction 输出 → 停止旧执行
→ 按依赖 Prepare 全部候选 → 无 await 的 Commit 循环
→ 领域 settlement → 渲染提交前/后信号及 generation 校验
→ 激活目标句 → 释放候选/旧资源和覆盖层 → 发布恢复成功
```

- 新增 `IPreparedStateContributor`、`IPreparedStateRestore`、目标只读 `RestoreContext`；Game contributor 初始化时拒绝缺少准备替换契约的插件。
- Variables、Actors、AudioBgm、Direction、Script 和 manosaba.ui 均实现准备替换。Actor 在隐藏候选上加载；Audio 创建未播放的候选；Direction 强制准备所有领域，避免相同逻辑状态下遗漏视觉重置。
- Script 的候选 Commit 只安装 checkpoint，VM 启动位于最后的 Activation。恢复返回时目标句可以仍在正常打字，不要求全文已经显示。
- RuntimeHost 不再在恢复停止阶段 Reset Direction/Audio；StateManager 不再逆序 Reset 全部 contributor。真正清场仍使用 Reset。
- Prepare 失败/取消释放候选并尝试恢复旧边界；Commit 开始后延迟外部取消。提交、呈现超时、激活或清理失败进入 Faulted，保留输入门、播放门及冻结画面，直到显式 Reset/Shutdown。
- Direction 冻结层复制 LiveOutput 与转场纹理为独立 ImageTexture，并复制 Curtain；位于独立 CanvasLayer，排除对话 UI，不受 Direction Reset/场景释放影响。
- 渲染等待观察 Commit 后的一次完整 FramePreDraw/FramePostDraw 提交并校验运行时 generation 和目标纹理有效性。headless 明确跳过 GPU 检验，不作为视觉证据。
- Load 的 session/history 变更移到成功路径；CheckpointRestore 不重复记录 History。失败恢复原播放模式，Faulted 保持 Manual 与输入锁。
- Travel 不再持 SemaphoreSlim 跨 await State/Session；短锁保留游标与列表一致性，并拒绝并发 travel。时间线重置后，旧 travel 不能将游标指向复用 sequence 的新条目。已清理未使用的 Script/Dialogue/Playback 构造依赖。

## 持久写入门

新增每个 StateManager 独立的 `StateMutationGate` 与 `IStateMutationParticipant`。Game capture 在整个异步 contributor 遍历期间持有冻结租约。同步持久写入必须通过 Write 回调；冻结期间在修改前明确失败，避免同步 API 阻塞 Godot 主线程。嵌套写入允许，写入回调内重入 capture 被拒绝，取消/失败后冻结租约释放。

已接入 DialogueVariables/Yarn store、manosaba.ui、ActorManager/全部 Actor 持久 setter、Audio BGM 逻辑提交和 Direction logical commit。Yarn VM 直接访问底层 store 也受约束。异步 writer 在 capture freeze 后排队，freeze 等待已进入的 writer；逻辑提交完成即增加 revision，视觉 settlement 不占 writer。

StateManager 初始化强制 Game 插件实现 `IStateMutationParticipant` 或内部 Script boundary-owned marker；缺少契约时明确拒绝。mutation revision 变化后，保存会重新固定当前 occurrence 并让 Travel 原位替换，不增加虚假历史。

新增测试覆盖跨 await/跨线程冻结、嵌套写入与重入拒绝、重复释放、捕获取消后解冻，以及变量 Set/Reset 不得修改冻结状态。

## 固定边界的存档读取

`IStateBoundarySource.CaptureSaveBoundaryAsync` 等待行准备后，在 State 事务门内取得完整 `CommittedLineBoundary`。SaveSlotManager 使用该返回对象同时读取 snapshot、speaker、text、locale，不再在捕获返回后重新查询 CurrentBoundary 并回退到可能已经变化的 History。新增回归覆盖了“捕获返回前进入下一句”的竞态。

Save eligibility 依据是否已有边界或正在准备边界判断；不再因打字、已提交的长动画或存档菜单输入层而拒绝保存。恢复中的状态与显式外部 barrier 仍然阻止保存。未配置 boundary source 的 StateManager 不再允许 live Game capture，SaveSlotManager 也不再回退到该路径。

标题/文本/locale 与快照的边界身份相同；持久 UI 修改由 revision snapshot 覆盖。

## 真实渲染证据

运行：

```bash
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . \
  --rendering-method gl_compatibility --resolution 640x360 \
  res://tests/headless/HeadlessTestRunner.tscn -- --majo-render-restore
```

Apple M4 / OpenGL Compatibility 实际窗口验证了 200 次完整 StateManager preflight/prepare/commit/presentation/activation。每次在保留层下故意清空画面，加载并重建角色素材和第二个 Stage/View，发布带 Curtain 的 split 输出。检查 prepare、ready-covered、target 共 600 个实际绘制帧；节点数稳定为 38。200 个候选全部且仅释放一次，同时活动候选最多 1 个。前两次帧图与 JSON 在 `build/restore-render-evidence/`（可重建，build 目录不提交）。

该样本完整 StateManager 恢复到目标帧的 p50 为 7.1242 ms，p95 为 9.1733 ms。它是固定测试场景的基线，不是生产内容的性能承诺。

初次测试发现窗口启动时尚未完成首个分屏输出，验证场景现先通过相同 presentation-ready 协议建立初始画面。没有以固定帧延迟替代恢复的渲染协议。

## 收敛与最终验证

- P2：同步/异步 mutation gate、Actor/Audio/Direction/Variables/UI 接入、插件注册拒绝、BGM 原子快照、UI revision 与 markup replay policy 均已完成。
- P3：Game contributor 只保留 prepared replacement；Global/Settings 使用独立 `IImmediateStateContributor`。Prepare 失败、取消、commit/activation/timeout 故障、候选释放和同逻辑动画中断均有回归。
- P4：rollback、load、hot reload 共用 State 恢复事务；热重载保持公开 session 连续。真实窗口覆盖 split、多 Stage/View、角色素材、Curtain、冷准备空窗及连续 200 次恢复。
- P5：删除无 boundary 的 live Game capture 和 Save fallback，移除 Game contributor 的旧 `RestoreAsync`，并新增 [架构](state-boundary-and-restore.md)、[插件迁移](state-plugin-migration.md) 与 [格式](state-save-format.md) 文档。

Godot 的 100 句快速推进场景在 30 秒 Direction 动画仍未 settlement 时完成，登记 100/100 个唯一 occurrence。本次最终运行总计 1542.416 ms；边界 capture p50 0.148 ms、p95 0.261 ms；100 条 Travel snapshot payload 共 136000 个 JSON 字符；强制 GC 前后 managed memory 差为 2239544 bytes。该内存差包含 Yarn、presenter、日志与测试对象，不作为纯 Travel 分配量。

最终门禁（2026-09-09）：

- `bash tools/test.sh --full` 通过：471 项单元测试、Godot headless 集成、失败传播探针、干净插件打包与 macOS 导出 smoke，46 秒。
- `bash tools/quality.sh --check` 与 `git diff --check` 通过。
- 真实窗口命令通过；200 次完整 StateManager 恢复检查 600 个关键帧，节点稳定 38，候选创建/释放 200/200、同时最多 1 个，p50 7.1242 ms、p95 9.1733 ms。

结构化结果和首两轮 PNG 位于 `build/restore-render-evidence/`，可由上面的真实窗口命令重建。上述性能数值只描述固定验收场景，不是项目内容的 SLA。
