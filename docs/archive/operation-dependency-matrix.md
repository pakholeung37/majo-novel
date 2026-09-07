# Operation 架构重构依赖矩阵 (Phase 0 基线)

本文档记录重构前后各系统核心模块与 OperationScheduler、Activity Observer、生命周期和领域的依赖关系。

## 1. 当前基线依赖矩阵 (Before)

| 领域模块 | 依赖的上游 / 协作方 | 对 OperationScheduler 的直接依赖 | 终态 / Skip 控制方式 | 存在的问题 |
|---|---|---|---|---|
| **Direction** | IDirectionPlanner, IDirectionPreparationBackend, ITransientDirectionCueExecutor | 局部使用（TransientCueHandler / Godot Backend） | Coordinator.FinishSkippableAsync()（仅涵盖持久 visual transition，遗漏 transient cue） | Transient cue handle 存留在局部集合中，无法被 transaction 统一 Finish/Rollback/Cancel/Cleanup |
| **Audio** | IAudioBackend, IAudioClipLoader | 构造注入 `IOperationScheduler operations`，用于 `StartFade`；依赖 handle 的 `Complete()` | Reset/Shutdown 依赖 `CompleteTransitionsAsync` 轮询 handle | Audio transition 生命周期委托给全局 Scheduler，而不是领域自治 |
| **Dialogue** | CommandBinder, IActorManager, IOperationScheduler | 构造注入 `IOperationScheduler? operations`；`RuntimeCommandContext.Track()` 收集句柄；`DrainDialogueOperationsAsync` 遍历句柄调用 `Cancel()` | 依赖 dispatcher 的 `dialogueOperations` 集合逐个取消 | 命令执行体没有真正拥有自身 session cancellation binding，混淆了命令与底层 tween/operation |
| **PlaybackModeManager (Skip)** | IScriptPlayback, IDialoguePlayer, IAudioManager, IPlayedRegister, IOperationScheduler | 构造注入 `IOperationScheduler operations`；调用 `operations.CompleteSkippableAsync(groupId)`；读取 Yarn `#majo:operation-group=` | 通过全局 Scheduler 按 groupId 查找 handle 并 Complete | Yarn 脚本元数据倒挂控制底层 Scheduler，耦合严重 |
| **State** | IScriptPlayback, IInputManager, IOperationScheduler, IYarnCommandAdapter | 构造注入 `IOperationScheduler operations`；`StateEligibility` 判断 `ActiveOperations.Count > 0`；`StateManager` 遍历 `operations.ActiveOperations` 强制取消 | 遍历全局 `ActiveOperations` 强制取消并等待 | 把低级 Operation 计数当成业务 Save/Restore 状态边界判定 |
| **Diagnostics** | IServiceResolver -> IOperationScheduler | 抓取 `operations.ActiveOperations`，构造 `OperationDiagnostics` (Id, GroupId) | 只读展示，但诊断依赖全局 Scheduler 的内部 active 列表 | 暴露了已废弃的 Id / GroupId 概念 |

---

## 2. 目标架构依赖矩阵 (Target / After)

| 领域模块 | 依赖的上游 / 协作方 | 对 Operation Runner 的依赖 | 终态 / Skip 控制方式 | 收益与契约 |
|---|---|---|---|---|
| **Direction** | IDirectionPlanner, IDirectionPreparationBackend, ITransientDirectionCueExecutor | 仅后端内部作为执行原语（无 Id/GroupId/Target） | Coordinator.FinishSkippableAsync()（同时遍历持久 visual transaction 和 cue activity） | 视觉事务完全拥有 transient cue 与 persistent transition 的完整生命周期与终态 |
| **Audio** | IAudioBackend, IAudioClipLoader | 无依赖（领域内实现轻量 AudioTransition，或使用无状态 runner） | 领域暴露 `StopVoiceAsync` 与 `FinishTransitionsAsync` | Audio 领域自治管理播放器与渐变，无跨领域调度泄露 |
| **Dialogue** | CommandBinder, IActorManager, IRuntimeActivityObserver | 无依赖 | Command activity 自行绑定 Session CancellationToken；`WaitForQuiescenceAsync` 等待命令任务静默 | 彻底移除 `Track` 与句柄集合，Yarn runner stop 边界清晰 |
| **PlaybackModeManager (Skip)** | IScriptPlayback, IDialoguePlayer, IAudioManager, IDirectionTransactionCoordinator | 无依赖 | 并行调用 `audio.StopVoiceAsync()` 与 `direction.FinishSkippableAsync()` | 完全解耦 Yarn group metadata 与 Scheduler，直接调用领域 API |
| **State** | IScriptPlayback, IInputManager, IRuntimeActivityObserver, IRuntimeRestoreBoundary | 无依赖 | 通过 `IRuntimeActivityObserver` 判断活动；通过 `IRuntimeRestoreBoundary` 请求领域 Cancel & Quiescence | 领域自主响应取消，状态资格由 Observer 的活动语义判定 |
| **Diagnostics** | IRuntimeActivityObserver, 各领域 snapshot | 无依赖 | 读取 Observer 的 `RuntimeActivityInfo` 快照与领域快照 | 只读、无控制句柄、按 Owner/Type 统一呈现 |

---

## 3. 跨领域生命周期顺序 (RuntimeHost Quiescence Order)

1. **Stop / Pause Work**: 阻止新的工作提交，取消对应 session token。
2. **Domain Action**:
   - Dialogue 停止并等待命令 Quiescence。
   - Direction Finish 或 Rollback/Cancel（依调用意图决定）。
   - Audio StopVoice 并按领域策略处理渐变。
3. **Quiescence Wait**: 等待 RuntimeActivityRegistry 静默。
4. **Resource Disposal**: 释放底层渲染/音频节点与临时资源。
