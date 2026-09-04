# Operation 架构重构实施方案

## 1. 文档目的

本文档定义 Operation 模块的后续重构方向、边界和实施顺序。

这次重构的目的不是减少类或接口数量，而是让每个模块只负责自己拥有的生命周期语义：

- Direction 负责视觉演出的事务、汇合、终态和 Skip。
- Audio 负责音频轨道、播放实例、fade 和抢占。
- Dialogue 负责脚本会话和会话取消边界。
- Runtime 负责 reset、shutdown 和跨领域清理顺序。
- Operation 只负责单个异步效果的执行、完成、取消和底层资源收尾。

期望结果是降低跨领域耦合，缩小公共 API，并保证取消、强制完成、回滚和 Runtime 清理的语义不被重构破坏。

## 2. 当前状态

作者层的 `await`、`endAwait`、`cancel` command 已移除，Audio command 也不再接受 `group` 和 `operation` 参数。但底层 Operation 仍被以下模块使用：

| 模块 | 用途 |
| --- | --- |
| Direction | Camera、Actor、Screen Effect、Transient Cue 的动画和 participant settlement |
| Actor | 移动、透明度、换装、复合淡入淡出 |
| Stage | Curtain、Overlay、Letterbox、Transition 等视觉状态变化 |
| Audio | BGM/SFX/Voice fade 的执行和收尾 |
| Dialogue | 追踪 command 创建的 operation，并在 Dialogue stop 时 drain |
| Runtime | reset/shutdown 时取消并等待 active operations |
| Auto/Skip | 通过 `CompleteSkippableAsync` 直接完成可跳过效果 |
| Diagnostics | 读取 active operation 状态和耗时 |

当前 `OperationScheduler` 同时提供：

- operation 启动和执行；
- `Completion`、`Cancel`、`Complete`；
- `Id`/`GroupId` 注册和寻址；
- 按目标 `WaitAsync`、`CancelAsync`、`CompleteAsync`；
- 可跳过 operation 的分组完成；
- Runtime lifetime cancellation；
- active operation diagnostics。

其中第一组是底层执行能力，后几组属于领域编排或 Runtime 协调。它们集中在同一个服务中，是当前耦合的主要来源。

## 3. 需要保持不变的行为契约

重构期间必须保持以下语义：

### 3.1 Settlement、Cancel、Finish 是三种不同操作

- **Settlement**：效果自然执行到目标状态。
- **Cancel**：中止效果，执行取消回调，例如恢复旧 BGM、销毁临时播放器或释放 tween。
- **Finish**：跳过动画，直接应用目标终态。

不能用一个通用的 `CancellationToken` 替代这三种语义。尤其是 Skip 的 Finish 和 Dialogue stop 的 Cancel，其结果可能不同。

### 3.2 领域必须拥有自己的终态

Audio 负责音频状态的恢复和提交；Direction 负责视觉状态的提交和回滚；Operation 只负责调用领域提供的 execute/finish/cancel 回调。

### 3.3 Runtime 清理必须有确定顺序

Dialogue stop、Runtime reset 和 shutdown 必须满足：

1. 阻止新的工作进入；
2. 取消当前会话或领域活动；
3. 等待活动结束或完成收尾；
4. 释放领域资源；
5. 清空诊断和活动集合。

任何阶段都不能留下 active tween、Audio player、Direction transaction 或 command activity。

## 4. 目标架构

目标调用关系：

```text
Yarn command
    ├─ Direction command → Direction Beat/Transaction
    └─ Audio command     → AudioManager transition

Direction / Audio / Actor backend
    → Operation primitive

Runtime lifecycle
    → 各领域 Reset/Shutdown
```

Operation 不再作为所有领域共享的业务编排中心。它只表示一个可观察、可取消、可强制完成的底层效果。

建议的最终内部形态：

```csharp
public interface IOperationHandle
{
    Task Completion { get; }
    OperationState State { get; }

    void Cancel();
    void Finish();
}

public interface IOperationRunner
{
    IOperationHandle Start(OperationDefinition definition);
}
```

最终是否更名为 `IOperationRunner` 可以延后。首先要改变所有权和调用边界，避免通过改名掩盖职责仍然集中在 scheduler 的问题。

## 5. 明确不采用的直接方案

### 5.1 不直接删除整个 Operation 模块

Direction、Actor 和 Audio 仍然需要统一的异步执行、取消收尾和完成通知。直接删除会把同一套逻辑复制到多个领域，导致资源泄漏和竞态风险上升。

### 5.2 不把 Audio 并入 Direction

BGM track、SFX instance、Voice 单通道和视觉 Direction transaction 的状态模型不同。两者可以共享底层执行原语，但不应共享领域状态或事务 API。

### 5.3 不保留全局 ID/group 作为长期控制 API

`operation:<id>` 和 `group:<id>` 已从 Yarn 移除。继续让领域公共 API暴露这些字段，会保留“任意模块可控制任意 operation”的耦合，并迫使业务层理解底层调度器的寻址模型。

## 6. 分阶段实施计划

### Phase 0：行为基线和依赖清单

目标是先固定现有行为，再进行 API 迁移。

工作内容：

1. 按领域列出所有 `new OperationDefinition`、`IOperationScheduler` 和 `IOperationHandle` 使用点。
2. 标注每个调用点的 execute、settlement、cancel、finish、reset 责任。
3. 补齐以下测试：
   - Direction Beat 并行 participant 的 settle；
   - Direction transaction 的 Finish、Rollback、Cleanup；
   - Audio BGM crossfade 的自然完成和取消；
   - Voice 抢占；
   - Dialogue stop 后 command activity 和领域 activity 清空；
   - Runtime reset/shutdown 后无 active player、tween 和 operation。
4. 将 diagnostics 作为观察手段，不把其字段作为控制契约。

验收条件：当前测试全部通过，并能为每一个 active operation 指明 owner 和清理责任。

### Phase 1：引入领域 scope，保留兼容 scheduler

在删除旧 API 前，引入内部的领域 scope 或 owner registry：

```text
Runtime operation root
    ├─ Direction scope
    ├─ Audio scope
    ├─ Rendering/Actor scope
    └─ Legacy scope（过渡期）
```

要求：

- 新代码只能通过所属领域 scope 启动 operation。
- 新 operation 不再创建业务 ID 或 group。
- scope 能列出自身活动，并能执行 `CancelAllAsync`。
- 根 Runtime scope 只负责生命周期兜底，不参与领域编排。
- 旧的 `OperationTarget` API 进入过渡状态，不允许新调用方增加依赖。

这一步的作用是先建立所有权，再删除全局寻址。不能反过来做。

### Phase 2：将 Skip 完全移交给 Direction

当前 Skip 仍通过 Played metadata 中的 `CorrelationGroupId` 间接调用 Operation group。该链路应删除：

```text
Played metadata
    → operation group
    → OperationScheduler.CompleteSkippableAsync
```

改为：

```text
PlaybackModeManager
    → DirectionTransactionCoordinator.FinishSkippableAsync()
```

具体步骤：

1. 从 `PlayedPresentation` 删除 `CorrelationGroupId`。
2. 删除 `PlayedRegister` 对 `majo:operation-group=` metadata 的解析。
3. Direction transaction 自己保存 participant 和 skippable 状态。
4. `PlaybackModeManager` 只调用 Direction 的 Finish 能力，并单独处理 Voice 停止。
5. 删除 `IOperationScheduler.CompleteSkippableAsync` 和 `IOperationHandle.IsSkippable`。

验收重点：Skip 后视觉状态立即进入目标终态，取消和 reset 仍然执行正确的资源清理。

### Phase 3：收紧 Direction、Actor、Stage 公共 API

Direction 已经具备 transaction 和 settlement，因此 Actor/Stage 不应把 operation ID/group 暴露给作者层或通用调用方。

迁移步骤：

1. 先保留内部 handle，使 Direction backend 能收集 participant。
2. 从 `IActor`、`ICharacterActor` 和 Stage public spec 移除 `operationId`/`groupId`。
3. 从 `StageOperationSpec` 移除 `OperationId`、`GroupId`。
4. 将取消和 Finish 的终态逻辑保留在 Actor/Stage backend，由 Direction transaction 调用。
5. 删除 Stage manager 内部按属性维护的全局 ID/group 依赖。
6. Direction 之外的调用方只能看到 Direction transaction，而不能看到 participant operation handle。

这一步完成后，视觉演出 API 的同步语义由 Direction 定义，不再由 OperationScheduler 的全局寻址定义。

### Phase 4：Audio 自己管理 transition

Audio 不并入 Direction，但应拥有自己的活动集合和 transition 生命周期。

目标结构：

```text
AudioManager
    ├─ BGM track transitions
    ├─ SFX instance transitions
    ├─ Voice transition
    └─ Audio reset/shutdown cleanup
```

迁移步骤：

1. `AudioManager` 内部持有 active transitions。
2. Audio transition 负责自然完成、取消和最终播放器状态。
3. Audio command 只等待 Audio 返回的 settlement，不再读取 `IOperationHandle`。
4. `RuntimeCommandContext` 只传 Dialogue cancellation token，不再要求 command 调用 `Track`。
5. Audio reset/shutdown 自己取消并等待 transitions。
6. `AudioPlaybackResult.FadeOperation` 逐步改为 Audio 专用的 completion/transition 结果。

过渡期可以继续用 Operation runner 驱动 Godot Tween，但该 runner 必须是 AudioManager 的内部实现细节。

### Phase 5：移除 Dialogue 对底层 operation 的追踪

当前 `RuntimeCommandDispatcher` 通过 `Track(IOperationHandle)` 和 `DrainDialogueOperationsAsync` 追踪 command 创建的 activity。只有在 Audio、Direction 和其他可创建异步效果的领域都能自行清理后，才执行这一步。

目标停止流程：

```text
1. 取消 Dialogue session token
2. 停止/取消 Audio 当前会话活动
3. 取消/清理 Direction 当前 transaction
4. 等待 Audio、Direction 和 command quiescence
5. 停止 Yarn runner
```

`RuntimeCommandDispatcher` 最终只保留：

- command binding 和执行；
- active command diagnostics；
- command failure；
- command quiescence。

第三方 command 如果需要异步行为，必须通过明确的领域服务完成，而不是直接把任意 operation 注册到 Dialogue dispatcher。

### Phase 6：删除全局寻址和兼容层

当没有生产代码依赖旧寻址后，删除：

- `OperationTarget` 和 `OperationTargetKind`；
- `WaitAsync(OperationTarget)`；
- `CancelAsync(OperationTarget)`；
- `CompleteAsync(OperationTarget)`；
- `CompleteSkippableAsync(groupId)`；
- `OperationDefinition.Id`、`GroupId`；
- `OperationContext.Id`、`GroupId`；
- `IOperationHandle.Id`、`GroupId`；
- scheduler 的 ID/group dictionary 和 completed cache。

保留的 Operation 能力应只包括：

- 启动一个 operation；
- 观察完成和状态；
- 单个 operation 的 Cancel/Finish；
- owner scope 的活动清理；
- Runtime 生命周期兜底。

## 7. API 迁移原则

### 7.1 公共 API 返回领域结果，不返回基础设施句柄

外部调用方应该得到：

- `CommittedDirectionTransaction`；
- `AudioTransition` 或 Audio completion；
- `DialogueCompletion`。

不应该得到可跨领域控制的 `IOperationHandle`。

### 7.2 cancellation 由调用边界传入

命令和领域 API 可以接收 `CancellationToken`，但 token 只表示调用边界取消。它不替代领域的 Finish、Rollback 或资源清理协议。

### 7.3 diagnostics 只读且不具备控制能力

可以保留自动生成的 diagnostic ID、owner、类型和耗时，但这些字段不能用于等待、取消或完成效果。

## 8. 风险与控制措施

| 风险 | 控制措施 |
| --- | --- |
| 删除全局 scheduler 后资源泄漏 | Phase 0 固定 reset/shutdown 测试，Phase 1 先建立 scope |
| Cancel 和 Finish 语义混淆 | 为每个领域分别测试自然完成、Cancel、Finish |
| Skip 行为退化 | 先迁移 Direction，再删除 group metadata |
| 第三方 command 绕过领域清理 | 在 Phase 5 前审计扩展 command 的异步调用方式 |
| diagnostics 失去可见性 | 使用 owner/type/自动 diagnostic ID，不依赖业务寻址 |
| 迁移期间双重清理 | 每个 activity 明确唯一 owner，scope 只允许 owner 清理 |
| 一次性 breaking change 范围过大 | 每个 Phase 单独编译、单测和 headless 验收 |

## 9. 每阶段验收门槛

每一阶段完成前必须满足：

1. `dotnet build 'Majo Rebuild.sln' --no-restore` 通过。
2. Runtime 单元测试全部通过。
3. Headless Audio 测试覆盖完成、Cancel、Finish、Reset。
4. Headless Direction 测试覆盖 settle、Finish、Rollback、Reset。
5. Dialogue stop 和 Runtime shutdown 后没有 active activity。
6. Demo Yarn 不出现 `await`、`endAwait`、`cancel`、`operation:` 或 `group:` 控制语法。
7. Diagnostics 能说明 activity 的 owner 和状态，但不依赖业务 ID/group 控制它。
8. 删除兼容 API 前，仓库搜索确认没有生产调用方。

## 10. 当前建议

当前不应直接进入 Phase 6，也不应继续删除 Operation 的执行内核。推荐先实施 Phase 0 和 Phase 1，随后优先完成 Phase 2 的 Direction Skip 解耦。

原因是 Direction 已经拥有最完整的事务模型，而 Operation group 目前最明显的跨领域泄漏正是从 Played/Auto/Skip 链路进入的。先切断这条链路，可以在不影响 Audio 播放模型的前提下验证“领域拥有自己的编排语义”这一核心方向。

完成 Phase 2 后，再分别迁移 Actor/Stage 和 Audio，最后才处理 Dialogue tracking 和全局寻址 API。这样每一步都能保持可运行、可验证、可回退，重构的收益也能对应到明确的耦合减少和 API 边界改善。
