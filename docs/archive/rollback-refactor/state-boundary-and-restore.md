# 台词边界与状态恢复架构

状态：已实施（2026-09-09）。

## 正常播放

每条正常台词只有一条入口链：完成台词前的 Yarn 命令和逻辑提交，建立不可变的 `LineBoundaryContext`，在 `StateMutationGate` 冻结持久写入，捕获完整 Game snapshot，向 Travel 登记 occurrence，随后才发布并呈现台词。`SessionGeneration` 与 `OccurrenceSequence` 共同标识一次出现；恢复重开播放时 sequence 会重置，不能跨 generation 比较 sequence。循环中的相同 line ID 不会合并。Travel 在回退成功后将当前历史条目映射到重呈现的身份，后续正常台词仍新增条目。

`ScriptCheckpoint` v2 的唯一 boundary 是 `LineEntry`。它表示目标句之前的命令已完成，保存已解析 substitutions；恢复时重呈现目标句，并从该句之后继续，不重跑前置命令。

动画 settlement 不参与 snapshot 成立条件。Direction、Actor 和 BGM contributor 从已提交的逻辑目标捕获，不读取 tween 中间值。同步持久写入通过 `StateMutationGate.Write`，异步逻辑提交通过 `WriteAsync`；边界 capture 等待已有异步 writer，并在整个 contributor 遍历期间阻止新 writer。视觉动画必须在逻辑提交返回后继续，不能再写持久模型。

手动存档只读取 `CaptureSaveBoundaryAsync` 返回的 `CommittedLineBoundary`，标题、正文、locale 和 snapshot 因而来自同一 occurrence。台词展示期间发生的持久 UI 修改会增加 mutation revision；下一次保存会在同一 occurrence 上重新固定 snapshot，并让 Travel 替换当前条目而非伪造历史。`ILineBoundarySource.CanReviseBoundary` 仅在该 occurrence 仍在呈现且未收到推进/停止取消时授权刷新；获取 mutation freeze 后再次校验。句间命令执行期间保存上一条已冻结 snapshot，命令修改进入下一台词边界，不能与上一句脚本位置拼接。

## 恢复

读档、rollback 和热重载共用 `StateManager` 的恢复事务：

```text
Preflight
→ 取得播放恢复门和输入 muter
→ 复制并保留旧 Direction 输出
→ 停止旧执行（不 Reset）
→ 按依赖 Prepare 全部 contributor
→ 无 await 的 Commit 区
→ 等待领域 presentation 与 renderer 提交
→ 最后激活 Script checkpoint
→ 释放候选、旧资源和保留层
→ 发布成功
```

Prepare 失败或 commit 前取消会精确释放候选并恢复旧边界。Commit、presentation 或 activation 开始后的故障进入 `Faulted`，继续持有输入锁及冻结输出，直到显式 Reset/Shutdown；不会发布成功事件。Reset 仅用于真正清场。

热重载使用 `HotReloadReplay` origin 经过同一事务。视觉 markup 可以重建；`RunOnce` markup 在 checkpoint 与热重载 replay 中跳过。热重载 replay 使用 Yarn 内部 stop/start，不改变公开 dialogue session ID，也不发布公开 Stopped/Completed。

## 所有权与诊断

Direction-owned Stage actor 仅由 Direction contributor 保存；全局 ActorManager contributor 只保存其自己的 actor tree。BGM snapshot 原子复制已提交的曲目、实例、循环和基础音量；不保存淡入淡出进度、SFX、Voice 或精确播放位置。

结构化事件包括 `state.boundary_committed`、`travel.checkpoint_registered`、`state.restore_prepared`、`state.restore_committed`、`state.restore_presented` 和 `state.restore_failed`。边界事件包含 session generation、occurrence、line ID 和 capture 耗时。
