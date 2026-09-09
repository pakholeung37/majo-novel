# Game 存档与 checkpoint 格式

状态：当前格式说明（2026-09-09）。

外层 `StateEnvelope` format version 保持 1，`StateSnapshot` format version 保持 1。Envelope 保存 Runtime/game build/storage namespace、scope、record schema metadata、script 摘要、显示 metadata 和完整 snapshot。JSON 由 `CanonicalJsonStateSerializer` 确定性编码。

本轮不改变 Direction payload。Script contributor schema 升级到 2，内部 `ScriptCheckpoint.formatVersion` 为 2，且 `spot.boundary` 只能是 `LineEntry`。Checkpoint 保存 project、node、line ID、program fingerprint 和冻结的 resolved substitutions。

旧 Script checkpoint/schema 1 默认在 preflight 拒绝；Runtime 不删除旧存档，也不把旧展示期快照自动改名为 LineEntry。游戏若需要兼容，应注册明确的 envelope/contributor migration，并对旧捕获语义作项目级判断。

Game snapshot 只能来自已提交的 `CommittedLineBoundary`；不再支持无 playback boundary 的 live contributor 遍历。存档 UI metadata 与 snapshot 使用同一 boundary 返回值。台词 occurrence 内的持久 UI revision 会生成替换后的 snapshot，但保持相同 session generation、occurrence sequence 和 line identity。

BGM record 保存 committed track identity、instance ID、loop 和基础音量，不保存 fade 进度或精确播放头。Direction/Actor 保存逻辑目标，不保存 tween、modifier、临时纹理或 GPU surface。SessionHistory、Global、Settings、Played 等各自 scope 不混入 Game rollback。
