# Actor 生命周期已知问题

## 状态

本文记录截至 2026-08-28 已确认、但不在当前 Background Slot / cross-fade 工作中处理的
Actor 生命周期问题。当前优先保证 Background 合成语义正确；Actor 自动回收与 Operation
历史释放后续单独设计和实施。

## 1. Hide 不等于释放

`hide` 只把 Actor 淡出并设置为不可见。Actor 仍然保留在 `ActorManager`，其 Godot Node、
当前 Texture、Material 和可复用状态也继续驻留。

真正释放 Actor 的路径目前只有：

- `ActorManager.Remove(actorId)`；
- Runtime Reset；
- Runtime Shutdown。

因此，如果脚本持续使用新的 Actor ID，即使随后调用 `hide`，注册 Actor 数量和驻留内存仍可能
持续增长。使用稳定 ID 的 `GetOrCreate` 会复用实例，不会重复创建。

这属于缺少生命周期/回收策略造成的无界驻留，不是 Godot Node 脱离所有引用后仍无法回收的
传统泄漏，但对长时间运行的游戏具有相同的内存风险。

## 2. Operation predecessor 引用链

`ActorBase.StartSequencedOperation` 的新 Operation 会捕获前一个 `IOperationHandle`，Actor 又会在
`propertyOperations` 中保留最新 Handle。反复修改同一属性时，可能形成：

```text
Actor
  -> latest operation
    -> previous operation
      -> previous operation
        -> ...
```

Appearance Operation 的闭包还可能保留 Texture、临时 View 和 Actor 引用。

后续修复方向：

- Sequencing 只等待 predecessor 的轻量 completion，不持有完整 Handle；
- 当前属性 Operation 进入终态后，从 `propertyOperations` 条件移除；
- Actor 销毁时取消并等待其 Operation，再释放 Scene Node。

## 3. 已完成 Operation 历史

`OperationScheduler` 为支持按 ID/Group 延后等待，会保存已完成的完整 `OperationHandle`。
其中 Group 历史会不断追加 Handle，可能继续持有 Actor 动画闭包。

后续修复方向：

- 已完成历史改为不包含执行闭包的轻量 tombstone；
- 明确 completed ID/Group 的消费、容量上限或过期策略；
- 增加重复 Actor 动画和大量唯一 Operation ID/Group 的驻留回归测试。

## 4. 计划中的生命周期语义

后续应明确区分：

- `hide`：暂时不可见，保留可复用实例；
- `despawn/remove`：结束 Operation 并释放 Actor；
- `scene`：场景结束自动释放；
- `persistent`：允许跨场景保留；
- Background Slot：新 Background 稳定提交后，旧 Actor 可按策略隐藏或释放。

Diagnostics 已能输出注册 Actor 列表和总数。后续可对 Actor 总数持续增长、不可见 Actor 过多和
资源预算压力增加结构化警告。
