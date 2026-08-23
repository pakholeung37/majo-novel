# Yarn Spinner VM State Bridge 验证结论

## 结论

在不修改 Yarn Spinner 源码的前提下，可以通过独立 Bridge 程序集访问其 VM 内部状态，并让 `DialogueRunner` 恢复到指定 Line ID 后继续执行。

方案利用 Yarn Spinner 已声明的 `InternalsVisibleTo("YarnSpinner.Tests")`：Bridge 将程序集名设为 `YarnSpinner.Tests`，直接访问 internal 类型；少数 private 字段则通过 .NET `UnsafeAccessor` 访问。

## 已验证

- 在行展示边界捕获 node、Line ID、program counter 和 VM 执行状态。
- 停止并重新启动 `DialogueRunner` 后，在 `onDialogueStart` 阶段注入恢复状态。
- 通过 Line ID 重新查找指令，因此脚本中插入其他指令后仍能定位目标行。
- Presenter 会重新收到恢复行，随后可继续下一行并正常结束。
- 真实 Yarn 编译结果、C# 单元测试和 Godot headless 集成测试均已通过。

## 当前边界

这次验证只证明了“从稳定 Line ID 恢复到安全行边界”可行，尚未覆盖：

- Yarn 变量和 substitution 恢复；
- call stack、选项和嵌套节点恢复；
- 脚本版本兼容性校验；
- 完整的存档、rollback 和 time travel 快照协议。

因此 Bridge 可以作为后续 State/Playback 架构的技术基础，但在实现持久化前，还需要继续验证变量快照以及带 substitution 的行恢复。
