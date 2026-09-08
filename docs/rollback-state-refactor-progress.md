# Rollback 状态重构实施记录

状态：实施中。本记录不表示原计划已经全部交付，尤其不代表无黑帧验收通过。

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

| 领域 | 已确认的来源 | 当前处理 / 剩余问题 |
| --- | --- | --- |
| Script | Yarn VM、backend 当前行与 substitutions | 在 presenter 前冻结；State 使用上下文中的 checkpoint |
| DialogueVariables | Yarn set、命令、公开 variables API | 命令串行边界已覆盖；外部 UI / 插件写入尚缺统一提交门 |
| Direction | DirectionTransactionCoordinator committed State | 从逻辑模型捕获；跨 contributor 的领域锁和插件写入契约尚待完成 |
| Actors | 全局 ActorManager、ActorBase setters / animation targets | 保存 TargetAppearance / TargetPosition / TargetVisible / TargetOpacity；Scale、Tint、Z 的外部写入隔离尚待完成 |
| AudioBgm | AudioManager / BgmChannel | 保存曲目、循环、BaseVolume；淡出已逻辑停止的 track 不进入快照；取消淡入淡出保留已提交目标 |
| manosaba.ui | ManosabaUiService.SetFrameMode | 立即更新 CurrentFrameMode，动画只用于展示；尚未接入统一提交门 |

Direction 的 GodotDirectionActorTreeBackend 为每个 binding 创建独立 ActorManager。Bootstrap 的全局 ActorStateContributor 捕获的是另一个全局 ActorManager，因此当前这两条内置路径不存在同一对象重复序列化；后续插件仍需验证其所有权。

BGM 沿用已有曲目起点恢复语义，旧 payload 没有播放位置字段，本次没有声称实现精确播放位置续播。取消淡入淡出的行为已改为保留逻辑目标，并更新单元及 Godot 回归断言。

内置 MajoBbCodeMarkupHandler 处理文字展示；Runtime 当前没有内置会写持久模型的 IDialogueMarkupHandler。插件接口仍可能写入外部状态，尚需声明 replay 策略并执行限制，不能因此把所有插件当作展示效果。

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

## 尚未完成，不能据此发布为完整重构

1. P2：全部持久写入的提交门、UI revision、插件冻结契约，以及 Save eligibility/UI 与固定边界保存的完整迁移。未配置边界源的独立 StateManager 仍保留 live capture 入口，迁移末期须明确去留并移除过渡路径。
2. P3：`IPreparedStateRestore`、依赖目标上下文、同步短提交区、prepare 取消后的旧边界恢复。RuntimeHost 与 StateManager 的恢复 Reset 路径仍存在。
3. P4：冻结 Direction 输出、真实 presentation-ready 信号、generation 校验、超时、故障锁，以及 load/session/history 的成功提交时机。尚无逐帧无黑帧证据。
4. P5：旧恢复路径及 Travel 构造参数清理、完整插件迁移说明、性能 p50/p95、200 次恢复资源增长检查。

下一步应先完成统一播放/恢复互斥门与 contributor 准备替换协议，再改 renderer；不要仅删除 Reset 或用固定帧延迟掩盖恢复空窗。

## 本批最终验证结果

- `bash tools/test.sh --full`：通过；446 项单元测试通过，Godot 集成、干净插件打包及导出门禁通过，总耗时约 30 秒。测试耗时包含缓存，不作为边界 p50/p95 指标。
- `bash tools/quality.sh --check`：通过。
- `git diff --check`：通过。
- 未执行真实渲染逐帧无黑帧验收；P3/P4 尚未实现，因此不能用上述通过结果替代该验收。
