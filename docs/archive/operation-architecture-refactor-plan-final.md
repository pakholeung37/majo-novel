# Operation 架构重构最终实施文档

> 状态：可实施方案  
> 适用仓库：/Users/pakholeung/web/majo-rebuild

本次重构直接采用 breaking change。所有生产代码、测试、Headless 场景和脚本资产在同一重构分支内一次性迁移到最终 API。

## 1. 决策结论

当前 OperationScheduler 同时负责执行、全局 id/group 寻址、跨领域等待/取消/完成、Skip、Runtime 清理和诊断，已经成为中心化上帝对象。生产代码基本不再需要按 id 或 group 跨模块控制 Operation，但这些概念仍渗透在接口、测试、Played 元数据、State 和 Diagnostics 中。与此同时，Direction 已经拥有真实的视觉事务模型，Audio 也拥有独立的 transition 和抢占语义。继续让这些领域依赖全局 Scheduler，会持续放大竞态和清理问题。


最终方案采用以下方向：

- 保留 Operation 执行内核，删除其业务编排和全局寻址职责。
- Direction 拥有全部视觉事务和 Skip 终态，包括 transient cue。
- Audio 拥有音频 transition、抢占、Voice 停止和播放器清理。
- Dialogue 拥有 command session、cancellation binding 和 quiescence。
- RuntimeHost 负责生命周期顺序和跨领域静默协调。
- State 和 Diagnostics 只观察 Runtime activity，不控制底层 Operation。
- 不引入复杂 Scope 树；使用领域 owner、内部 activity 和一个只读 Runtime activity observer。

## 2. 当前实现基线

### 2.1 Operation

当前接口和实现：

- addons/majo_runtime/Runtime/Operations/IOperationScheduler.cs
- addons/majo_runtime/Runtime/Operations/OperationScheduler.cs
- addons/majo_runtime/Runtime/Operations/OperationDefinition.cs
- addons/majo_runtime/Runtime/Operations/OperationContext.cs
- addons/majo_runtime/Runtime/Operations/IOperationHandle.cs
- addons/majo_runtime/Runtime/Operations/OperationTarget.cs

IOperationScheduler 当前同时提供 Start、ActiveOperations、WaitAsync(OperationTarget)、CancelAsync(OperationTarget)、CompleteAsync(OperationTarget) 和 CompleteSkippableAsync(groupId)。OperationDefinition 仍有 Id、GroupId、IsSkippable；OperationContext 仍携带 Id、GroupId；Scheduler 还维护 active、completed、id 和 group 多组字典。

生产代码已基本不调用 OperationTarget 的全局等待、取消和完成 API，但测试 mock、Headless Operation 测试和旧字段仍在使用。重构必须同时更新这些调用点，不能只凭生产搜索结果判断完成。

### 2.2 Direction

Direction 已具备 DirectionPlan、CommittedDirectionTransaction、Settlement、Finish、Rollback、Cleanup 和 IDirectionTransactionCoordinator.FinishSkippableAsync()。

主要文件：

- addons/majo_runtime/Runtime/Direction/Execution/DirectionTransactionCoordinator.cs
- addons/majo_runtime/Runtime/Direction/Execution/DirectionBeatRuntime.cs
- addons/majo_runtime/Runtime/Direction/Execution/TransientDirectionCueExecutor.cs

当前缺口：DirectionBeatRuntime 先提交持久视觉 transaction，再单独执行 transient cue；cue handle 只在 executor 局部集合内存在。因此现有 FinishSkippableAsync() 不能保证 Camera FX、Flash、Curtain 等一起完成。

### 2.3 Audio、Dialogue、State、Diagnostics

AudioManager 仍依赖 IOperationScheduler，并在 Reset/Shutdown 中调用 CompleteTransitionsAsync。这与 OperationScheduler Reset 的取消语义不同，必须保留为显式的 Audio 领域策略。

RuntimeCommandContext.Track() 同时承担：

1. 向 Dispatcher 注册句柄；
2. 将 Dialogue cancellation token 注册为 operation.Cancel()。

StateManager 遍历 operations.ActiveOperations 取消活动；StateEligibility 用 ActiveOperations.Count 判断资格；RuntimeDiagnosticsService 读取 active operation、GroupId 和 DiagnosticId。迁移必须同时改造三者。

### 2.4 已验证门禁

当前已验证：

- 284 个纯 C# 单元测试通过；
- bash tools/test.sh --full 通过；
- Godot headless import/C# compilation 通过；
- Runtime Demo headless smoke 通过；
- Headless 集成测试连续运行通过；
- 失败探针正确返回非零退出码；
- Release build 和目标平台 export smoke 通过。

dotnet build "Majo Novel.sln" --no-restore 当前不是有效门禁：solution 包含 Editor 代码，但仓库缺少相应 Editor API 类型。实施期间继续使用 tools/test.sh 的项目级构建；修复 Editor 依赖后，再把 solution build 加为附加门禁。

## 3. 不可破坏的行为契约

### 3.1 Settlement、Cancel、Finish

| 语义 | 典型触发 | 必须发生 | 禁止行为 |
|---|---|---|---|
| Settlement | Tween/Task 自然结束 | 平滑到声明的目标态并释放资源 | 被误报为 Cancel 或 Finish |
| Cancel | Dialogue Stop、会话切换、抢占、关闭 | 停止中间效果、释放临时资源，必要时回滚 | 将未完成目标态当作已提交 |
| Finish | Skip、Auto 快进、立即完成 | 跳过过程并立即应用目标终态 | 只取消而不应用终态 |

CancellationToken 只表达“停止继续等待/执行”的请求，不能替代三种终态语义。

### 3.2 终态所有权

- Direction：演员、镜头、背景、转场、Curtain、Overlay、Letterbox、Flash、Camera FX 的终态、回滚、Finish 和 Cleanup。
- Audio：BGM/SFX/Voice 播放器、音轨切换、音量终态、Fade 抢占和 Voice 停止。
- Dialogue：命令 session、command quiescence、Yarn runner stop 边界。
- RuntimeHost：阻止新工作、广播 token、调用领域清理、等待静默、释放底层资源。
- State：状态边界和恢复事务，不直接操作 Operation。
- Diagnostics：只读观察和报告，不提供 Cancel、Finish 或按 id/group 寻址。
- Operation：只运行领域委托、报告 Settlement/Cancel/Finish/Fault，不理解业务终态。

### 3.3 清理偏序

所有 Stop、Reset、Shutdown 遵循：

1. 阻止新的领域工作进入；
2. 取消对应 session/lifetime token；
3. 各领域停止、Finish 或恢复自己的活动；
4. 等待领域和 Runtime activity registry 达到 Quiescence；
5. 释放 Tween、AudioPlayer、Render host 和临时节点；
6. 若仍有活动，输出带 owner/type/source 的诊断并使操作失败。

触发取消不等于已经静默。

清理必须区分两类 token：session/lifetime token 负责阻止业务工作继续；cleanup token 只负责给清理过程设置外部取消和超时。领域在收到 session cancellation 后，仍必须用 cleanup token 完成自己的 Cancel、Finish、释放和 quiescence。不能因为业务 token 已取消，就跳过资源回收。

资源释放关键段应使用有界的 cleanup token；超时后必须输出 activity snapshot、保留 Runtime fault 状态并返回失败，不能静默放弃清理。

## 4. 最终目标架构

调用关系：

    Yarn/Script -> Dialogue session + command quiescence
    Yarn/Script -> Direction transaction coordinator
    Yarn/Script -> Audio manager
    PlaybackModeManager -> Direction / Audio
    RuntimeHost -> Dialogue / Direction / Audio / activity observer
    Direction -> Actor / Stage / transient cue backend
    Audio -> Godot audio players and fades
    Visual/Audio backend -> Operation runner primitive
    State/Diagnostics -> activity observer

Operation runner 位于后端执行层。业务领域不得通过 runner 的 id、group 或全局查找能力互相控制。

### 4.1 Runtime activity observer

新增一个只观察、不控制的 Runtime 级接口，建议形态：

    public enum RuntimeActivityOwner
    {
        Dialogue,
        Direction,
        Audio,
        Other
    }

    public sealed record RuntimeActivityInfo(
        string DiagnosticId,
        RuntimeActivityOwner Owner,
        string Type,
        bool IsCancelable,
        bool IsFinishable,
        TimeSpan Elapsed,
        string? Source);

    public interface IRuntimeActivityObserver
    {
        IReadOnlyList<RuntimeActivityInfo> Snapshot();
        bool HasActiveActivities(RuntimeActivityOwner owner,
            bool includeIgnorable = false);
        Task WaitForQuiescenceAsync(
            CancellationToken cancellationToken = default);
    }

注册和注销是领域内部职责。Observer 不暴露 Cancel、Finish、Wait(id) 或 Wait(group)。DiagnosticId 自动生成，禁止业务传入 operation id。注册表不持有 Godot Node 强引用；完成、取消、故障和异常路径都必须注销。

实现时增加 internal 的注册入口（例如 BeginActivity(...) -> IDisposable），由 Direction、Audio、Dialogue 的 owner 使用；这个入口不放进 public observer，租约释放必须在 finally 中执行。

### 4.2 Operation runner

最终保留执行内核，提取或重命名为 IOperationRunner：

    public interface IOperationHandle
    {
        Task Completion { get; }
        OperationState State { get; }
        void Finish();
        void Cancel();
    }

    public sealed class OperationDefinition
    {
        public OperationDefinition(
            Func<OperationContext, Task> executeAsync,
            Func<CancellationToken, Task>? onFinish = null,
            Func<CancellationToken, Task>? onCancel = null);
    }

    public interface IOperationRunner
    {
        IOperationHandle Start(OperationDefinition definition);
    }

OperationDefinition 删除 Id、GroupId、IsSkippable；OperationContext 删除 Id、GroupId；Handle 不对外暴露业务诊断字段；runner 不维护 completed cache，不支持 target/group 查找。内部 active set 可以保留，只用于泄漏检测和最后一道 shutdown 防御。

直接删除 Complete()；最终 API 只保留 Finish()。自然完成仍称 Settlement。

## 5. 前置专项：Transient cue 必须纳入 Direction transaction

这是实施前置条件，优先级高于删除 Skip metadata。

### 5.1 目标

每个 Direction transaction 同时拥有持久视觉 transition、Camera FX、Flash、Curtain、Overlay/Letterbox 以及所有 cue 的 Settlement、Cancel、Finish、Cleanup。

DirectionBeatRuntime 不得在 transaction 返回后启动无法由 transaction 管理的 cue 生命周期。

### 5.2 建议接口

    public interface ITransientCueActivity : IAsyncDisposable
    {
        Task Settlement { get; }
        bool IsSkippable { get; }
        Task FinishAsync(
            CancellationToken cancellationToken = default);
        Task CancelAsync(
            CancellationToken cancellationToken = default);
    }

    public interface ITransientDirectionCueExecutor
    {
        void Validate(IEnumerable<ITransientDirectionCue> cues);
        Task<ITransientCueActivity?> StartCommittedAsync(
            IEnumerable<ITransientDirectionCue> cues,
            DirectionCommitToken token,
            DirectionCompletionBarrier completion,
            CancellationToken cancellationToken);
    }

内部可以使用 Operation runner，但 handle 只能存在于 activity 内部。

### 5.3 Transaction 归属和失败处理

1. Prepare 持久视觉 transaction。
2. Commit 并产生 DirectionCommitToken。
3. 启动并注册 cue activity。
4. 按 completion barrier 等待 Settlement。
5. 将包含 cue activity 的完整 transaction 返回。

CommittedDirectionTransaction 增加 cue activity 归属。Settlement 等待持久 transition 和 cue；FinishAsync 同时 Finish 二者；CleanupAsync 保证二者都已释放。cue 启动失败时先取消已启动 cue，再 Rollback prepared visual transaction。Finish、Rollback、Cleanup 使用同一 lifecycle gate 串行化。

### 5.4 cue 终态

每个 handler 必须有明确契约和测试：

- Camera FX：Finish 后移除 modifier，回到声明的稳定镜头状态。
- Flash：Finish 后立即清除闪白并回到基线。
- Curtain：Finish 后跳到 cue 声明的目标透明度。
- Overlay/Letterbox：Finish 后跳到声明的目标值。
- Cancel：恢复 cue 启动前的值，或按资源协议安全停止。
- Settlement：按原时长自然到达目标值。

不能把所有 cue 无差别映射为 operation.Complete()。

## 6. 各领域实施目标

### 6.1 Direction

保留 IDirectionTransactionCoordinator 的 ExecuteAsync、FinishAsync、RollbackAsync、RestoreAsync 和 FinishSkippableAsync。

FinishSkippableAsync 必须遍历完整 transaction，包括 cue activity。Reset 先关闭提交入口，再清理所有 transaction；不再依赖 CorrelationGroupId 或 Yarn 行注解。

### 6.2 Actor 与 Stage

先移除 public API 的 operationId、groupId 参数，再移除对外暴露的基础设施句柄。

最终建议：

    public interface IActor
    {
        Task SetPositionAsync(Vector2 position,
            AnimationSpec animation,
            CancellationToken cancellationToken = default);
        Task SetOpacityAsync(float opacity,
            AnimationSpec animation,
            CancellationToken cancellationToken = default);
    }

Direction 需要 Finish/Cancel 时，使用 internal 的 IVisualTransitionParticipant 或 IPreparedVisualTransition；不要把 handle 重新泄露给 IActor。StageOperationSpec 只保留时长、曲线、冲突策略和领域需要的可跳过属性，不保留 OperationId 或 GroupId。

### 6.3 Audio

AudioManager 自己拥有 BGM crossfade、BGM 抢占、SFX、Voice、Fade 的 Settlement/Cancel/Finish 以及 Reset/Shutdown 播放器释放。

AudioPlaybackResult 改为业务结果：

    public sealed record AudioPlaybackResult(
        IAudioPlayback Playback,
        Task Settlement,
        bool IsSkipped = false);

命令层只等待 Settlement。Skip 使用 Audio 领域 API 停止 Voice 或 Finish transition。Reset/Shutdown 的“Finish transition 后释放”或“Cancel 后释放”必须作为显式 Audio 策略写入接口注释和测试，不能依赖 Operation runner 的默认行为。

StopVoiceAsync 最终也应返回领域结果或 Task，不再向调用方返回 IOperationHandle。

### 6.4 Dialogue

Dialogue 拥有 session cancellation、command activity、command quiescence 和 Yarn runner stop 边界。

将 IYarnCommandAdapter 的 DrainDialogueOperationsAsync 改为 WaitForQuiescenceAsync；语义从“逐个取消 Operation”改为“等待命令执行体退出”。

删除 Track。命令执行体在创建时直接绑定 Dialogue session cancellation token，并由 command activity 自己负责取消、故障观察和 quiescence。

### 6.5 RuntimeHost

RuntimeHost 负责顺序，不执行领域内部 transition。

Dialogue Stop：阻止新 command，取消 Dialogue session，等待 command quiescence；调用方决定 Direction 是 Cancel 还是 Finish。

调用规则固定为：用户停止、会话切换和 destructive Restore 使用 Cancel/Restore；Skip 使用 Finish；自然到达目标态使用 Settlement。任何调用点都必须在代码注释或接口契约中标明所选语义。

Skip：PlaybackModeManager 并行调用 direction.FinishSkippableAsync() 和 audio.StopVoiceAsync()。

Reset：

1. 状态进入 Resetting，关闭新工作入口；
2. 取消旧 session token；
3. 停止 Dialogue 并等待 quiescence；
4. Direction 恢复默认持久状态，再清理 transaction；
5. Audio 停止 Voice，按领域策略处理 transition，释放播放器；
6. 其余 service 按依赖图 reset；
7. 等待 Runtime activity registry 静默；
8. 创建新 session。

Shutdown：

1. 取消 session 和 runtime token；
2. 等待正在运行的 Skip task；
3. Dialogue、Direction、Audio 各自 shutdown；
4. 按逆初始化顺序释放服务；
5. registry 和底层资源全部释放。

Reset 的“恢复默认状态”和 Shutdown 的“释放资源”不能共用一个含义模糊的 Operation 方法。

## 7. Skip 链路迁移

最终链路：

    PlaybackModeManager
      ├─ audio.StopVoiceAsync(...)
      └─ direction.FinishSkippableAsync(...)

必须删除：

- PlaybackModeManager 对 IOperationScheduler 的注入；
- StopVoiceAndCompleteOperationsAsync(groupId)；
- PlayedPresentation.CorrelationGroupId；
- PlayedRegister.GroupMetadataPrefix；
- Yarn 脚本中的 #majo:operation-group=；
- Scheduler 的 CompleteSkippableAsync(groupId)。

先扫描全部历史脚本和 fixture，确认没有运行时依赖；随后直接删除 metadata、parser、字段和相关测试，不增加“忽略并警告”的运行时路径。

Skip 验收必须覆盖多角色移动、立绘切换、Camera transition、Curtain/Overlay/Flash、Camera FX、多个 committed transaction、Skip 与新 Beat 竞态、Voice 立即停止和最终 activity 静默。

## 8. State 与 Diagnostics 迁移

### 8.1 State

StateEligibility 不再使用 ActiveOperations.Count 作为业务资格判断，改为检查：

- Script 是否处于 durable boundary；
- Dialogue 是否有 active command；
- activity observer 是否有不可忽略活动；
- Input 是否有 modal handle；
- State transaction 是否进行中；
- 外部 barrier 是否存在。

Destructive Restore/Rollback 通过 RuntimeHost 的恢复边界请求各领域取消，再等待 quiescence；StateManager 不再遍历 Operation handle。

建议新增：

    public interface IRuntimeRestoreBoundary
    {
        Task CancelForRestoreAsync(
            CancellationToken cancellationToken);
        Task WaitForQuiescenceAsync(
            CancellationToken cancellationToken);
    }

### 8.2 Diagnostics

RuntimeDiagnosticsService 最终读取 Dialogue command snapshot、Direction transaction snapshot、Audio transition/playback snapshot 和 Runtime activity snapshot。

诊断按 owner/type 展示，DiagnosticId 自动生成，类型直接改为 RuntimeActivityDiagnostics。任何诊断 API 都不能返回可调用 Cancel 或 Finish 的对象。

## 9. 分阶段实施计划

每个 Phase 独立提交、独立验证、可单独回滚。阶段之间允许接口尚未全部收口，但不允许新增旧式调用。

### Phase 0：基线和依赖矩阵

目标：先固定行为。

工作项：

- 记录 Scheduler、Direction、Audio、Dialogue、State、Diagnostics 的依赖矩阵；
- 增加复合 Direction transaction 的 Settlement、Cancel、Finish、Rollback、Skip 测试；
- 为每种 transient cue 增加 Settlement、Cancel、Finish、Cleanup 测试；
- 增加 Audio crossfade 的自然完成、抢占、Reset、Shutdown 测试；
- 增加 Dialogue Stop、command fault 和 quiescence 测试；
- 增加 RuntimeHost Reset/Shutdown 顺序与并发测试；
- 增加 activity observer fake 和泄漏测试。

验收：现有 284 个单测和新增测试全绿；Headless 基线连续三轮通过；记录已知警告和耗时。

### Phase 1：活动观察边界

目标：为 State、Diagnostics、Runtime cleanup 提供迁移目标。

工作项：

- 新增 IRuntimeActivityObserver 和内部 registry；
- Direction、Audio、Dialogue 注册各自活动；
- StateEligibility 改读 observer；
- Diagnostics 改读 observer 和领域 snapshot；
- RuntimeHost 增加统一 quiescence 等待；
- CI 禁止新增 OperationTarget、CompleteSkippableAsync 和 ActiveOperations 业务调用。

验收：State/Diagnostics 行为与基线一致；完成、取消、故障、reset 后 registry 为空；所有迁移后的调用均使用最终 activity、领域和 runner API。

### Phase 2：Transient cue 纳入 Direction transaction

目标：先修复 v2 的关键遗漏。

工作项：

- 引入 ITransientCueActivity；
- 将 cue activity 挂入 CommittedDirectionTransaction；
- 修改 TransientDirectionCueExecutor、cue handlers 和 fake；
- 删除 DirectionBeatRuntime transaction 外的 cue 执行；
- 实现 cue 的 Finish、Cancel、Settlement、Cleanup；
- 覆盖 cue 启动失败、commit 失败、Finish 失败和 Cleanup 失败的 aggregate error。

验收：transaction Settlement 包含 cue；FinishSkippable 完整覆盖可跳过 cue；Reset/Shutdown 后无 modifier、Flash、Curtain、Tween 或 activity 泄漏。

### Phase 3：Skip 直连领域

目标：切断 Yarn metadata 到 Scheduler 的倒挂链路。

工作项：

- PlaybackModeManager 改注入 Direction coordinator，不再注入 Scheduler；
- Skip 并行执行 Direction Finish 和 Audio Voice stop；
- 扫描全部脚本和 fixture，删除 metadata、CorrelationGroupId、parser 和 CompleteSkippableAsync；
- 更新所有测试剧本和生产调用，使 Skip 只经过 Direction 和 Audio 领域接口。

验收：Auto、History、Skip headless 全部通过；Skip 后视觉和 Voice 状态正确；同帧切换 Skip/Manual 不重复 Finish；生产代码不通过 group id 控制 Skip。

### Phase 4：Operation 内核和 Actor/Stage API 收口

目标：删除业务命名从视觉后端泄漏到基础设施。

工作项：

- OperationDefinition 删除 Id、GroupId、IsSkippable；
- OperationContext 删除 Id、GroupId；
- Scheduler 提取或改名为 IOperationRunner；
- 删除 completed cache、id/group dictionaries 和 target API；
- IActor、ICharacterActor、Stage public API 删除 operationId/groupId；
- 引入 internal visual participant；
- 删除 Complete()，所有调用改为 Finish()。

验收：Operation 测试只覆盖 Settlement、Cancel、Finish、Fault、runtime cancellation 和幂等；public API 无 id/group；runner 仅以 active set 做泄漏检测。

### Phase 5：Audio transition 自治

目标：Audio 不再依赖 Scheduler 协调生命周期。

工作项：

- 引入内部 AudioTransition；
- AudioManager 维护 transition、Voice、player 的 owner activity；
- AudioPlaybackResult.FadeOperation 改为 Settlement；
- Bgm/Sfx/Voice commands 删除 context.Track(handle)；
- 明确抢占、Skip、Reset 的 Finish/Cancel 策略；
- 删除 AudioManager 对 Scheduler 的依赖。

验收：自然 crossfade、抢占、Skip、Reset、Shutdown 全部通过；无 player、fade task、fault 或 activity 泄漏。

### Phase 6：Dialogue session 解耦

目标：Dispatcher 只管理 command session。

工作项：

- 引入 command activity 和 WaitForQuiescenceAsync；
- 删除 Track，改为 command activity 自己持有 cancellation binding；
- DrainDialogueOperationsAsync 改名为 command quiescence；
- YarnDialogueBackend.StopAsync 使用 session stop 协议；
- 删除 Dispatcher 的 dialogueOperations 集合和 handle Cancel 循环。

验收：Dialogue Stop/Reset/Shutdown 无延迟台词；command fault 全被观察；quiescence 与 activity registry 一致。

### Phase 7：最终 API 收口

目标：一次性删除所有旧概念，只保留最终模型。

工作项：

- 删除 OperationTarget、OperationTargetKind、相关异常和 target API；
- 删除 completed cache、全部 id/group 字段、group metadata 和 Complete()；
- OperationDiagnostics 改为 Runtime activity diagnostics；
- 删除 Track、dialogueOperations、旧 Scheduler 名称和所有旧测试 mock；
- 更新所有 fake、unit、headless 测试和文档；
- 如命名没有额外收益，可保留实现类名，但 public contract 必须是 IOperationRunner。

验收：生产代码、测试和内容资产均使用最终 API；无 id/group 控制语义；完整门禁通过；完成定义全部满足。

## 10. 主要文件清单

Operation：

- addons/majo_runtime/Runtime/Operations/IOperationScheduler.cs
- addons/majo_runtime/Runtime/Operations/OperationScheduler.cs
- addons/majo_runtime/Runtime/Operations/OperationDefinition.cs
- addons/majo_runtime/Runtime/Operations/OperationContext.cs
- addons/majo_runtime/Runtime/Operations/IOperationHandle.cs
- addons/majo_runtime/Runtime/Operations/OperationTarget.cs
- addons/majo_runtime/Runtime/Operations/OperationExceptions.cs

Direction：

- addons/majo_runtime/Runtime/Direction/Execution/DirectionTransactionCoordinator.cs
- addons/majo_runtime/Runtime/Direction/Execution/DirectionBeatRuntime.cs
- addons/majo_runtime/Runtime/Direction/Execution/TransientDirectionCueExecutor.cs
- addons/majo_runtime/Runtime/Direction/Execution/TransientCueHandlers.cs
- addons/majo_runtime/Runtime/Direction/Camera/CameraModifierManager.cs
- addons/majo_runtime/Runtime/Direction/Godot/

Audio / Dialogue：

- addons/majo_runtime/Runtime/Audio/AudioManager.cs
- addons/majo_runtime/Runtime/Audio/IAudioManager.cs
- addons/majo_runtime/Runtime/Audio/AudioModels.cs
- addons/majo_runtime/Runtime/Dialogue/Commands/Audio/BgmCommands.cs
- addons/majo_runtime/Runtime/Dialogue/Commands/Audio/SfxCommands.cs
- addons/majo_runtime/Runtime/Dialogue/Commands/Audio/VoiceCommands.cs
- addons/majo_runtime/Runtime/Dialogue/Commands/RuntimeCommandContext.cs
- addons/majo_runtime/Runtime/Dialogue/Commands/RuntimeCommandDispatcher.cs
- addons/majo_runtime/Runtime/Dialogue/Yarn/YarnCommandAdapter.cs
- addons/majo_runtime/Runtime/Dialogue/Yarn/YarnDialogueBackend.cs

Runtime / State / Diagnostics / Played：

- addons/majo_runtime/Runtime/Core/RuntimeHost.cs
- 新增 addons/majo_runtime/Runtime/Core/IRuntimeActivityObserver.cs
- 新增 addons/majo_runtime/Runtime/Core/RuntimeActivityRegistry.cs
- addons/majo_runtime/Runtime/State/StateManager.cs
- addons/majo_runtime/Runtime/State/StateEligibility.cs
- addons/majo_runtime/Runtime/Diagnostics/RuntimeDiagnosticsService.cs
- addons/majo_runtime/Runtime/Diagnostics/RuntimeDiagnosticsSnapshot.cs
- addons/majo_runtime/Runtime/Played/IPlayedRegister.cs
- addons/majo_runtime/Runtime/Played/PlayedRegister.cs
- tests/headless/M10AutoHistory.yarn

## 11. 测试矩阵

Operation runner：

- 自然 Settlement；
- Cancel 后 Completion 完成且状态正确；
- Finish 立即应用终态；
- Execute fault 被观察；
- runtime/session cancellation；
- 重复 Cancel/Finish 幂等；
- Finish 与 Cancel 竞态；
- reset/shutdown 不接受新 Start；
- active set 最终为空；
- 不存在 target/group 查找测试。

Direction：

- 多 actor 并行 transition；
- appearance、position、opacity、camera、stage；
- Settlement、Rollback、Cancel、Finish；
- 多 transaction 并发提交；
- FinishSkippable 只处理可跳过 transaction；
- 每种 transient cue 的四种生命周期；
- cue 启动失败和 transaction 回滚；
- Skip 与新 Beat 竞态；
- Reset/Shutdown 资源清理。

Audio：

- BGM 自然 crossfade；
- 新 BGM 抢占旧 BGM；
- Voice 停止；
- Skip 时 Voice 立即停止；
- Fade Settlement/Cancel/Finish；
- Reset 和 Shutdown；
- player、fade task、activity registry 无泄漏。

Dialogue：

- command 正常完成、取消、fault；
- Stop 后无后台 command；
- 多 command 同时 quiesce；
- 新 session 不受旧 session command 影响。

Runtime / State / Diagnostics：

- Reset 和 Shutdown 顺序；
- Reset 与 Skip 竞态；
- durable boundary 的 Save eligibility；
- active command/activity 时 Save 被拒绝；
- destructive Restore 取消并等待静默；
- diagnostics snapshot 一致；
- diagnostics 不暴露可控 handle；
- 初始化失败和部分 shutdown 失败。

## 12. 验收命令和搜索门槛

日常验证：

    bash tools/test.sh --fast

完整验证：

    bash tools/test.sh --full

完整脚本当前覆盖项目级 .NET build、纯 C# unit tests、Godot headless import/C# compilation、Runtime Demo smoke、Headless 集成测试三轮、失败探针、Release build 和目标平台 export pack smoke。

最终收口前执行以下精确搜索：

    rg -n \
      "OperationTarget|CompleteSkippableAsync|CorrelationGroupId|majo:operation-group=" \
      addons tests

以上结果必须为空。若仍有结果，说明删除不完整。Actor/Stage 公共 API 另行检查：

    rg -n "operationId|OperationId|groupId|GroupId" \
      addons/majo_runtime/Runtime/Actors \
      addons/majo_runtime/Runtime/Stage \
      addons/majo_runtime/Runtime/Direction

这里允许与业务分组无关的局部变量，但不允许 Operation 标识出现在 public API、StageOperationSpec 或 Direction 的跨领域控制路径中。

生产代码还必须清除：

    rg -n "context\.Track\(|ActiveOperations\.Count|WaitAsync\(OperationTarget|CancelAsync\(OperationTarget|CompleteAsync\(OperationTarget" addons/majo_runtime/Runtime

ActiveOperations 作为 runner 内部泄漏检测可以保留，但不得成为 State eligibility、Skip 或 Diagnostics 的业务控制面。

## 13. Breaking change 实施原则

- 直接修改所有 IActor、ICharacterActor、Stage、Audio、Dialogue 和 Operation 调用方；
- 直接删除旧 IOperationScheduler、OperationTarget、Complete、Track、CorrelationGroupId 和 group metadata；
- 直接更新全部 fake、unit、headless 测试、脚本 fixture 和文档；
- 构建和测试失败必须修复调用方，不能恢复已删除的 API 绕过编译错误；
- 所有 public API 文档只描述最终模型。

## 14. 风险、停止条件和回滚

Transient cue 风险：任一 Camera FX、Flash、Curtain 在 Finish、Cancel、Reset 后仍有活动或残留时，停止删除 group metadata，回退 Phase 2，先修复 transaction ownership。

Reset/Shutdown 风险：出现孤儿 Tween、AudioPlayer、未观察 fault 或 activity 非零时，保留 runner active set，修复阻止入口、token 和 quiescence 顺序。

State eligibility 风险：Save 错误放行或 Restore 不能静默时，回退 observer 业务判断，保留 registry 只读记录并补齐领域 activity。

Audio 语义风险：若已有行为依赖 transition 完成后才释放播放器，明确 Audio 的 Finish-then-reset 领域策略，并通过测试固定该语义；不要把该语义放回 runner。

回滚纪律：

- 每个 Phase 一个独立 commit；
- Phase 2、3、5、6 可以使用仅存在于当前分支的临时开发开关，但合并前必须删除；
- 回滚只回退当前 Phase；
- 失败路径保留 activity snapshot；
- 禁止删除测试或放宽超时来通过门禁。

## 15. 明确不做的事情

- 不重新引入 Runtime operation scope 树；
- 不让 runner 承担业务编排；
- 不保留新的全局 id/group 控制面；
- 不把 Diagnostics 变成控制 API；
- 不为每个 Tween 建立跨领域公共句柄；
- 不在本次重构中重写 Godot SceneTree 或整个异步框架；
- 不强行改变现有 Direction 事务提交/回滚模型；
- Editor API 未修复前不把 solution build 宣称为有效门禁；
- 允许一次性删除 API；通过分阶段 commit 和完整测试保证可回滚。

## 16. 完成定义

只有同时满足以下条件，才能宣布重构完成：

1. Direction transaction 完整拥有 persistent transition 和 transient cue 的 Settlement、Cancel、Finish、Cleanup。
2. Skip 不读取 Yarn group metadata，不调用 Scheduler group API。
3. Audio 自主管理 transition、Voice 和 player 生命周期。
4. Dialogue Dispatcher 不持有底层 Operation handle 集合。
5. State 和 Diagnostics 不依赖 Scheduler 全局寻址或 active count 控制业务。
6. Operation runner 不包含 id/group、completed cache 或 target API。
7. Actor/Stage public API 不暴露 operationId/groupId；基础设施句柄只存在于 internal backend。
8. Runtime Reset、Shutdown、Dialogue Stop 的顺序和语义有测试保护。
9. bash tools/test.sh --full 通过，headless 集成至少连续三轮通过。
10. 没有未解释的孤儿 Tween、AudioPlayer、Task、Command 或 Runtime activity。

仅删除 OperationTarget 或把类名改成 OperationRunner，不算完成。
