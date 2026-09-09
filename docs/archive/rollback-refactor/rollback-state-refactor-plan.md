# 台词边界、状态快照与无黑帧恢复重构计划

日期：2026-09-08  
状态：已完成（2026-09-09）。实现与验收结果见 [实施记录](rollback-state-refactor-progress.md)。
范围：允许修改内部接口及 checkpoint 格式，统一整理 Script、State、Direction、Travel 的流程。

## 1. 目标与决策

用一条明确的执行链代替事件驱动的机会式捕获：

```text
执行本句之前的命令
→ 完成逻辑提交
→ 固定本句边界状态并登记 checkpoint
→ 呈现本句
→ 接受推进
```

动画可以在逻辑提交后继续运行。checkpoint 保存逻辑目标状态，不保存 tween 进度、临时纹理或瞬时特效执行进度。恢复以 cut 呈现目标状态，不重播本句之前的命令。

统一恢复流程：

```text
验证目标 → 暂停并停止旧执行（保留旧画面）
→ 准备目标资源与领域状态 → 提交替换
→ 确认目标画面已呈现 → 激活目标台词 → 解除暂停
```

核心验收约束：

- 每次正常进入并呈现的台词都有一个对应 checkpoint；同一 lineId 在循环中出现多次，分别记录。
- 快进和动画时长不影响 checkpoint 覆盖率。
- checkpoint 内脚本位置、变量和领域状态来自同一个边界。
- rollback/读档不先清空可见画面；不暴露空白渲染中间态。
- Reset 只用于真正清场，不参与正常的状态替换。

不在本轮实现：动画中途精确续播、任意 VM 指令回溯、持久化完整 Travel 时间线、增量快照压缩、整个运行时改写成事件溯源系统。

## 2. 现状与确定的故障路径

| 位置 | 当前行为 | 后果 |
| --- | --- | --- |
| `Runtime/Travel/DialogueTravelManager.cs` | 监听 AwaitingAdvance，fire-and-forget 执行 CaptureBoundaryAsync | 捕获不是推进的必经步骤 |
| 同上 | eligibility 拒绝后只记录 capture_skipped；前后检查 lineGeneration | 动画结束不会自动补捕获；快速进入下一句会丢弃旧请求 |
| `Runtime/State/StateManager.cs` | CaptureGameAsync 使用 Save eligibility | checkpoint 被普通存档的稳定性规则限制 |
| `Runtime/State/StateEligibility.cs` | 拒绝非 ignorable activity | 已提交但仍在播放的 Direction 动画也阻止捕获 |
| `Runtime/Direction/Execution/DirectionTransactionCoordinator.cs` | commit 后更新逻辑 State，activity 持续到 settlement/dispose | 逻辑状态已经可用，视觉 activity 却仍挡住 capture |
| `Runtime/Core/RuntimeHost.cs` | CancelForRestoreAsync 调用 direction.ResetAsync | 恢复一开始就清掉旧画面 |
| `Runtime/State/StateManager.cs` | ExecuteAsync 再逆序 Reset contributors | Direction 被重复清理 |
| `Runtime/Direction/Godot/GodotDirectionRenderHost.cs` | Reset 清纹理、删除额外视图并 Deactivate | 异步恢复资源期间存在可见空窗 |

上表中的 Runtime 路径均相对 `addons/majo_runtime/`。黑帧的清屏路径已由代码确认；Viewport 首帧时序仍需真实渲染测试，不能根据逻辑 settlement 推定。

## 3. 模块边界

| 模块 | 改造后职责 | 移除的职责 |
| --- | --- | --- |
| Script/Dialogue | 创建台词边界，串行化推进，激活目标台词 | 通过展示完成事件间接决定能否 capture |
| State | 固定一致性快照；验证、准备、提交恢复 | 依据全部视觉 activity 推断快照安全；恢复前统一 Reset |
| Direction | 提交逻辑目标；管理渲染替换与资源生命周期 | 将动画结束作为持久状态成立的条件 |
| Travel | 登记 checkpoint、维护游标/分支/容量、选择目标 | 监听 PhaseChanged 排队 capture、捕获重试 |
| Runtime 生命周期 | 停止执行、真正清场、关闭服务 | restore 时提前销毁仍需显示的渲染资源 |

不新增平行的“rollback 专用 StateManager”。读档与 rollback 共用恢复引擎，区别只在会话和时间线策略。

## 4. 台词边界与推进协议

### 4.1 接入点与顺序

改造 `YarnDialoguePresenterAdapter.RunLineAsync`。现有 `PrepareLineAsync` 接到预加载器 `BeforeLineAsync`，保留该用途，新增显式的边界回调，不把 capture 隐藏进预加载器。

顺序固定为：

1. 转换 LocalizedLine，完成本句必要的资源准备。
2. 在 YarnDialogueBackend 建立本句位置及 substitutions，创建不可变的边界描述。
3. await 边界提交：固定各领域状态，并向 Travel 登记快照。
4. 发送 LinePresented，再调用各 presenter。
5. 仅此后允许本次台词的 Advance 生效。

不能依赖现有 HandleLinePresented 才写入 currentLineId，否则提前 capture 会读到上一句。需要拆出“建立脚本边界”和“通知展示”两个动作。

建议新增内部类型，名称可按项目命名规范调整：

```csharp
record LineBoundaryContext(
    long SessionGeneration,
    long OccurrenceSequence,
    ScriptCheckpoint Script,
    DialogueLine Line);

Task CommitLineBoundaryAsync(LineBoundaryContext boundary, CancellationToken token);
```

`Script` 已包含冻结的 substitutions；不能在 contributor 执行途中重新读取 backend 当前行。

### 4.2 输入、取消与锁

- 增加 PreparingLineBoundary 阶段，或等价的明确内部状态；UI phase 不再是 capture 授权依据。
- 手动、自动、skip 的 Advance 请求统一经过 playback 入口。准备期间至多合并为一个 pending advance，待该句激活后执行；不得用 Advance 取消 capture。
- 区分 session/restore cancellation 与 HurryUp/NextLine cancellation。现有 NextLineToken 同时参与准备等待的模式必须拆开。
- Stop、读档、rollback 可以取消尚未提交的边界；取消边界不得发布 LinePresented 或残留半条 Travel entry。
- 固定锁顺序：播放边界/恢复互斥门 → State 事务门 → 领域提交门。Travel 仅用短锁修改列表，绝不持锁 await State/Session。
- 若旧边界持播放门，恢复先发取消信号，再等待边界退出，最后取得门；不能持门等待被同一门阻塞的 Stop。

### 4.3 脚本恢复语义

新增 `LineEntry` checkpoint boundary，并提升 ScriptCheckpoint 格式版本。其含义是：本句之前的命令已经完成，substitutions 已解析，本句可以重新呈现。

恢复仍可利用现有 Yarn state bridge 的按行恢复能力，但必须验证：重呈现目标句、从目标句后继续、不会重复执行 prelude，也不会再次产生之前的变量/音频副作用。

恢复目标句使用 CheckpointRestore origin，不新增 Travel entry。默认重走目标句的正常文字呈现；不重播前置 Direction 演出。markup handler 必须检查：视觉展示效果可重建，持久状态或外部副作用不得因重呈现重复执行。

## 5. 一致性快照协议

### 5.1 第一版实现

第一版在边界互斥区内等待全部 contributor capture 完成并组装 StateSnapshot，然后登记 Travel。先实现完整快照，不引入后台序列化或 copy-on-write 优化。

State capture 接收边界上下文，不再使用 `EnsureEligible(Save)` 判断该内部操作。边界凭据必须由播放流程产生并校验 session generation，外部代码不能用过期上下文随意 capture。

所有持久状态写入必须遵守边界协议：

- 命令返回“逻辑已提交”后，后续动画不能继续修改持久模型。
- 尚未完成逻辑提交的异步命令必须在进入边界前完成，或显式声明不支持该边界；不允许静默丢 checkpoint。
- 插件、UI 和后台任务的持久写入也必须被提交门约束，或向下一边界排队。仅阻止 Yarn 推进不足以保证一致性。
- capture 不访问动画节点上的实时位置。集合、payload 和 substitutions 必须冻结或深拷贝，不能共享可变容器。

捕获失败时停止本次推进并报告明确错误；不能继续展示一条没有 checkpoint 的正常台词。主动取消和显式不支持 rollback 的领域应有独立诊断，不混为成功。

### 5.2 各 contributor 的改造

| Contributor | 快照来源与恢复要求 |
| --- | --- |
| Script | 使用边界携带的 ScriptCheckpoint；脚本激活放到恢复流程最后 |
| DialogueVariables | 固定边界变量表；恢复时完整替换，删除目标不存在的变量 |
| Direction | 读取解析后的 committed State；禁止从当前插值位置反推逻辑状态 |
| Actors | 审计与 Direction 的角色所有权重叠；同一对象只能由一个 contributor 持久化。Direction-owned 角色归 Direction，其余角色也必须有独立逻辑模型 |
| AudioBgm | 保存已提交的曲目/音量等语义状态；不得等待淡入淡出结束。延续已有播放位置语义并增加测试 |
| 插件 contributor | 迁移为冻结快照/准备替换契约；不满足要求时在注册或显式边界检查中失败 |

手动存档定义为保存当前已登记的台词边界。若正在准备新边界，则等待边界结束；不再临时遍历 live state。界面存档标题、台词和 snapshot 必须引用同一 entry。与行边界不同步的持久 UI 修改必须先形成新 revision，不能显示新状态却保存旧状态。

## 6. Direction：逻辑提交与视觉生命周期分离

保留现有 DirectionPlan、ResolveCommitState、PreparationBackend 的基础，集中调整生命周期：

- 明确 logical commit completion 与 visual settlement 两个信号。前者参与边界；后者参与动画等待、跳过和资源释放。
- 视觉 activity 继续用于诊断/取消/finish，不再隐含“不能 capture”。禁止简单把全部 Direction activity 标为 ignorable。
- 暴露停止旧执行且保留当前输出的操作，与 ResetAsync 分开。取消 tween、cue、modifier，递增运行时 generation，拦截旧回调，不销毁旧输出所需资源。
- 将 State revision 与运行时失效 generation 分开。恢复旧逻辑状态时仍生成新的运行时 generation，不能恢复旧 token。
- Restore 使用完整目标替换语义：既设置目标内容，也删除目标没有的角色、Stage、View 和效果。
- 动画中途停止可能导致节点位置与 committed State 不同。恢复准备不能仅按 State diff 忽略“逻辑相同”的对象；必须重建/重设目标视觉状态，或跟踪视觉失效并强制 reconcile。

## 7. 恢复协议与无黑帧呈现

### 7.1 Contributor 契约

将原 `PreflightAsync + ResetAsync + RestoreAsync` 改为明确的准备和提交协议；ResetAsync 留在清场生命周期。

建议的职责形态：

```csharp
ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
    StateRecord record, RestoreContext context, CancellationToken token);

interface IPreparedStateRestore : IAsyncDisposable
{
    void Commit();
}
```

Prepare 可异步，负责验证、加载、构造候选对象，不修改已发布状态。Commit 不进行异步加载，只执行准备好的替换。完成后 Dispose 释放候选句柄或移交后的临时资源，不能误释放已发布对象。

保留 contributor Dependencies 拓扑排序；准备阶段也须支持读取 RestoreContext 中目标依赖，不能读取旧 live 变量来构造新对象。缺失 record 必须由 schema 明确拒绝或准备默认状态，不允许无意继承旧会话数据。

Script contributor 只准备恢复位置；启动 VM/呈现台词是单独的末尾激活动作，避免 Commit 中异步执行新脚本。

### 7.2 StateManager 的执行步骤

1. Preflight 完成格式、兼容性、依赖及外部 barrier 检查。
2. 取得播放/恢复互斥权并暂停输入，保留旧边界与可见输出；停止旧执行，不调用 Direction Reset。
3. 按依赖准备所有领域；失败则释放候选资源，使用保留的旧边界恢复播放能力。
4. 全部准备成功后进入不可取消的短提交区，同步提交领域替换；不在 contributor 之间 await。
5. 等待 renderer 的 presentation-ready 信号，再激活目标台词。只有此时才能更新 CurrentSnapshot、Travel 游标并发布完成事件。
6. 释放旧资源和暂停；rollback 保留 session，读档的 session/history 变更在成功路径中完成。

Commit 应通过预验证尽量保证不抛异常，但不宣称多个任意插件天然具备原子性。若提交或提交后激活发生异常，保留画面与输入锁，进入明确 Faulted 状态并报告失败，不发布完成事件。第一版不实现通用跨领域撤销日志。

取消策略：提交前可取消并恢复旧边界；提交开始后延迟取消，完成替换或进入 Faulted。所有清理只执行一次。presentation-ready 需要超时及故障路径，不允许无限等待。

### 7.3 第一版保留画面的方案

优先使用现有渲染基础，增加 renderer 内部的输出保留机制：

1. 清理旧动画/节点之前固定最后一个完整的 Direction 输出；该输出不包含对话 UI。
2. 在独立显示层显示冻结输出。不能引用仍会被后续渲染覆盖的同一个动态 ViewportTexture。
3. 准备并提交目标 Stage/View/Screen；目标渲染可以在覆盖层下更新。
4. 渲染后信号及目标 generation 检查确认整个输出链已产生有效目标帧，再移除覆盖层。
5. 最后释放旧纹理/资源句柄。保留层自身不能被 Direction Reset 或场景资源释放误删。

`Settlement` 仅代表逻辑/动画工作完成，不作为 GPU 帧就绪保证。不要硬编码延迟一帧；用真实渲染测试确定 Stage → View → Screen 链的就绪条件。若覆盖方案在现有 Viewport 结构下无法保证，改为双输出 surface，同一准备/发布契约保持不变。

## 8. Travel、读档与热重载

- Travel 提供同步登记已完成 checkpoint 的入口；删除 QueueBoundaryCapture、CaptureBoundaryAsync 以及 capture 专用 PhaseChanged/PauseChanged 订阅和 generation 重试字段。
- 保留 sequence、游标、容量、回退后推进截断未来分支的规则。仅在新边界成功登记后截断未来；失败不能破坏原时间线。
- 序号按台词 occurrence 分配，不以 lineId 去重。容量测试须明确：历史淘汰不计为漏捕获。
- rollback 重呈现不登记；load 成功后清理旧时间线并登记加载的目标作为新起点。
- 热重载通过同一边界/恢复协议进入，不再依赖“解除暂停时补 capture”。成功 replay 替换当前 checkpoint 并清理与新程序不兼容的未来条目；失败保留旧时间线。
- 审计 HotReloadService 的 AwaitingAdvance/CanCaptureCheckpoint 依赖，避免恢复内部等待边界 capture 而 capture 又等待 restore 完成。
- 选项展示暂不建立独立 checkpoint；选择产生的状态进入下一条台词的边界。测试回退到选项前再选择另一分支。

## 9. 破坏性变化与兼容策略

本轮选择直接迁移内部 API，不长期保留两套 capture/restore 流程。

- ScriptCheckpoint 新格式明确使用 LineEntry；ScriptStateContributor schema 同步升级。
- 默认拒绝旧 checkpoint 格式，错误在 preflight 暴露；不自动删除旧存档文件。原因是旧快照采集时刻可能包含展示期副作用，不能仅改枚举就声称等价。
- Direction payload 若未改变，不为接口重构无条件升级 schema；若移除重复 Actor 记录，必须明确受影响 record 的版本和兼容性。
- 更新 SaveSlotManager、GameSessionCoordinator、HotReloadService、bootstrap 注册、插件契约及 fake contributors。
- State envelope 是否升级按实际结构变化决定，不能用外层版本掩盖 contributor 不兼容。
- SessionHistory、全局设置等非 Game 状态保持各自 scope，不能因新行边界混入 rollback。

## 10. 分阶段实施与完成条件

按下列顺序实现，每阶段独立编译并通过对应测试；过渡代码在最后阶段删除。

### P0：建立回归基线并审计状态写入

- [x] 添加动画跨多句、延迟 contributor、快进取消三个漏捕获回归场景。
- [x] 记录双 Reset 调用链，并建立彩色背景/角色/分屏的逐帧恢复场景。
- [x] 列出所有 Game contributor、持久写入来源、Direction/Actor 所有权及 markup 副作用。
- [x] 确定第一版 BGM、markup、手动存档语义并写入测试。

完成条件：测试能够稳定暴露现有问题；新增机制覆盖范围有明确清单。

### P1：引入脚本行入口边界

- [x] 调整 ScriptModels、IScriptPlayback、YarnDialogueBackend 的行身份建立与 checkpoint 格式。
- [x] 在 YarnDialoguePresenterAdapter 增加可等待边界入口，保留预加载顺序。
- [x] 统一 Advance 入口并拆分推进与 session 取消令牌。
- [x] 验证 state bridge 在 LineEntry 恢复后不重复前置指令。

完成条件：无论 presenter 是否立即完成，都有一次可等待、可取消但不可被 Advance 跳过的行入口。

### P2：固定快照并简化 Travel

- [x] State 增加接受边界上下文的 capture 入口，迁移各 contributor 为逻辑快照。
- [x] 约束全部持久写入，清除 Actor/Direction 双重保存。
- [x] 在边界成功路径登记 Travel，移除机会式 capture。
- [x] SaveSlotManager 使用同一已固定边界，确保显示信息与记录一致。

完成条件：长动画期间快速经过 N 次台词出现，登记 N 次 checkpoint，变量/位置/revision 不串行；失败不推进。

### P3：恢复替换协议

- [x] 修改 contributor 的 prepare/commit 契约与依赖处理。
- [x] RuntimeHost 拆分 StopExecutionForRestore 与 Reset/Clear。
- [x] StateManager 移除恢复时的全 contributor Reset。
- [x] Direction 保留旧输出并停止旧执行，正确处理相同逻辑状态下的中途动画。
- [x] 完成 prepare 失败、取消、commit 故障与资源释放路径。

完成条件：恢复不经过默认空状态；失败无成功事件；目标不存在的旧对象被完整移除。

### P4：呈现就绪与外围接入

- [x] 增加冻结输出及 presentation-ready 协议和超时。
- [x] 接入 rollback、load、hot reload，统一末尾激活台词。
- [x] 调整 session/history 提交时机及 Travel 游标更新。
- [x] 在真实渲染下验证黑帧、分屏、资源冷加载和连续恢复。

完成条件：从开始恢复到目标帧就绪，每帧是有效旧输出或有效目标输出，没有意外空白输出。

### P5：清理与发布检查

- [x] 删除旧 capture、恢复 Reset 特例及仅服务旧流程的 API/字段。
- [x] 更新架构、插件迁移及存档格式说明。
- [x] 通过完整测试与格式检查，记录性能基线和逐帧证据。

完成条件：正常播放、读档、rollback、热重载各只有一条明确的边界/恢复路径。

## 11. 测试矩阵与执行方法

| 场景 | 必须验证的结果 | 层级 |
| --- | --- | --- |
| 长动画期间快速进入 100 句，容量 ≥ 100 | 每次 occurrence 恰好一条记录，动画不挡 capture | unit + Godot |
| 循环重复 lineId、变量/substitutions 每次不同 | sequence 不同且恢复各次实际值 | unit + Yarn bridge |
| contributor 人为延迟、连续 Advance | 不进入下一句写状态，不取消当前 capture | unit |
| capture 异常、Stop、restore 竞争 | 不发布半条记录，无死锁，无幽灵 LinePresented | unit + Godot |
| 动画途中恢复到相同逻辑 State | 视觉落到目标位置，不残留插值/modifier | Godot |
| 背景/角色/镜头/布局跨场景恢复 | 删除旧对象，目标完整，无旧回调污染 | Godot |
| 冷加载、split、多 View 输出 | 逐帧仅出现有效旧帧/目标帧 | 真实渲染 |
| prepare 失败及 commit 前取消 | 旧画面保留，旧边界可继续，候选资源释放 | unit + Godot |
| commit/激活故障或呈现超时 | 保持输入锁并 Faulted，不发布成功 | unit + Godot |
| 回退后正常推进、选项后换分支 | 只在新边界成功后截断未来 | unit + Yarn |
| load/rollback/hot reload | origin、session、history、Travel 条目符合第 8 节 | unit + Godot |
| 旧格式及不兼容插件 | preflight/注册明确拒绝，不提前销毁旧画面 | unit |
| 连续 200 次恢复，冷/热资源 | 资源句柄、节点与任务数量不持续增长 | Godot |

优先扩展现有 `DialogueTravelTests`、`StateManagerTests`、`StateEligibilityTests`、`GameSessionCoordinatorTests`、Direction transaction 测试、`YarnHotReloadTests`，以及 `tests/headless` 下 ScriptAndState、YarnVmStateBridge、Direction 和 DefaultPresentation 场景。

执行仓库已有入口：

```bash
bash tools/test.sh --unit
bash tools/test.sh --fast
bash tools/quality.sh --check
# 最终集成后执行完整门禁
bash tools/test.sh --full
```

headless 逻辑通过不代表没有黑帧。另建有真实渲染输出的验证场景，固定分辨率和素材，保存恢复前后逐帧图像及目标 generation；使用非黑测试背景和标记区判断异常空帧，避免把剧情预期的黑幕当作失败。

增加结构化事件：boundary_committed、checkpoint_registered、restore_prepared、restore_committed、restore_presented、restore_failed。包含 session generation、occurrence sequence、lineId、Direction revision、耗时及失败阶段。正常行入口不允许出现无明确原因的 capture_skipped。

记录热缓存/冷缓存下边界耗时 p50/p95、100 句快进耗时、时间线内存及恢复到首个目标帧的延迟。本轮不预设未经基准验证的毫秒承诺；若完整序列化成为瓶颈，再从已冻结状态拆出后台编码，不改变边界契约。

## 12. 风险控制与交付要求

- 最大风险是“视觉变化仍写回持久状态”。P0/P2 必须覆盖插件、Actor 和 markup，不能仅调整 eligibility。
- 第二风险是取消/锁重入。所有新 await 点都测试 Stop 与 restore 竞争，禁止 Travel 持锁跨层调用。
- 第三风险是资源生命周期。冻结输出、候选渲染资源、已提交对象必须分别定义所有者与释放时刻。
- 第四风险是仅靠 diff 恢复。必须验证中途动画、相同目标与缺失对象，避免无黑帧但画面错误。
- 工作区在编写本文时已有 presenter、demo 和 headless 测试的未提交改动；实施前检查差异，围绕现有改动推进，不覆盖或回退它们。

最终交付：新流程实现、旧路径删除、格式与插件迁移说明、测试结果、逐帧无空白证据及性能基线。不得以增加重试、全部 activity 改为 ignorable，或固定等待若干帧代替上述验收。
