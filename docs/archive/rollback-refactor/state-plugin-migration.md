# State 与 markup 插件迁移

状态：适用于台词边界恢复架构（2026-09-09）。旧 Game contributor 接口不兼容。

## Game contributor

持久 Game contributor 必须同时实现：

- `IPreparedStateContributor`：`PrepareRestoreAsync` 只验证、加载并构造未发布候选；`Commit()` 同步发布，不能加载资源或 await；`DisposeAsync()` 对未提交候选和已移交临时所有权都必须恰好清理一次。
- `IStateMutationParticipant`：在初始化时保存 Runtime 绑定的 `StateMutationGate`，所有公开和底层持久写入入口都通过 `Write`/`WriteAsync`。若状态完全由 Script 行入口建立，可实现内部 marker `ILineBoundaryOwnedStateContributor`。
- `CaptureAsync`：返回深拷贝的 committed logical state，不引用可变集合、节点插值值或临时资源。
- `PreflightAsync`：在停止当前游戏之前拒绝 schema、payload 和外部资源不兼容。

注册时不满足 prepared replacement 或 mutation contract 会抛出 `StateCompatibilityException`。不再提供从旧 `RestoreAsync` 到 prepared restore 的 Game adapter。

`RestoreContext` 只暴露目标 records。若 contributor 依赖另一个 contributor，声明 `Dependencies`，Prepare 通过 `context.Require(key)` 读取目标 record；不要读取旧 live dependency。

可选 record 必须用 `StateRestoreRecordPolicy.UseDefault` 明确声明，并在 `record == null` 时准备完整默认替换。Required record 缺失会在 preflight 阶段拒绝。

## 非 Game contributor

Global、Settings 等仍使用独立即时持久化流程。需要即时恢复的 contributor 实现 `IImmediateStateContributor`；基础 `IStateContributor` 已不再包含 `RestoreAsync`。SessionHistory 仍是 Session scope，不进入 Game snapshot。

## markup

每个 `IDialogueMarkupHandler` 必须声明 `ReplayPolicy`：

- `Visual`：恢复或热重载重呈现目标句时再次执行，用于可重建的文字视觉效果。
- `RunOnce`：只在正常进入台词时执行，用于持久状态或外部副作用。

Markup 不应绕过领域 mutation gate。未知或无效 replay policy 在注册时拒绝。

## 迁移检查

插件测试至少覆盖：capture 期间写入不会穿透；Prepare 失败/取消释放候选；Commit 不 await；目标缺失对象被删除；同逻辑目标在动画中断后仍重建视觉；checkpoint replay 不重复 `RunOnce` 副作用。
