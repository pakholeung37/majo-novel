# Yarn Spinner VM State Bridge 验证与产品化边界

状态：M9.5/M9.6 产品化基础已完成；已通过 Yarn-free Script/State 契约接入 Runtime

锁定版本：Yarn Spinner 3.2.0 / YarnSpinner-Godot 0.3.22

## 1. 结论

在不修改 Yarn Spinner 源码的前提下，独立 Bridge 程序集可以读写 Yarn VM 的
内部执行状态，并让 Godot `DialogueRunner` 在新会话中重放指定 stable Line ID
对应的行，然后继续后续指令。

Bridge 已作为 M9.5 的版本锁定实现接入 Runtime，并由 M9.6 的 Script Contributor
投影为 Yarn-free `ScriptCheckpoint`。它仍不等同于通用存档、Rollback 或 time travel，
`YarnVmLineSnapshot` 也不是长期持久化格式。

## 2. 实现机制

- Yarn Spinner 声明了 `InternalsVisibleTo("YarnSpinner.Tests")`。Bridge 将程序集名设为
  `YarnSpinner.Tests`，以访问 internal VM 类型和状态。
- 少数 private 字段（当前包括 `Dialogue.vm`、VM `callStack` 和 `isContinuing`）
  通过 .NET `UnsafeAccessor` 访问。
- 捕获时检查 VM 处于 `WaitingForContinue`，且 program counter 指向已投递
  `RunLine` 的下一条指令。
- 恢复时不使用旧 program counter 定位。Bridge 遍历新 Program，用 stable Line ID
  查找唯一 `RunLine` 指令。
- Godot 集成在 `onDialogueStart` 阶段注入状态，使 Presenter 重新收到当前行。
- Runtime 启动时通过反射校验所有 `UnsafeAccessor` 依赖字段的名称和类型；布局不兼容
  时在服务创建前 fail fast。

## 3. 已验证的行为

- 在行展示等待边界捕获 node、Line ID、program counter、VM 执行状态以及
  operand/call/options 结构深度。
- 拒绝 `Continue` 正在执行、当前指令不是目标 `RunLine` 或 VM 不在等待状态
  的捕获请求。
- 停止旧会话、启动新 `DialogueRunner` Session、在安全回调注入恢复状态。
- Presenter 重新收到恢复行，下一次 Advance 进入后续行并正常结束。
- 脚本中在目标行前插入其他指令后，仍通过 Line ID 找到目标；恢复不依赖
  旧指令索引。
- 相同 Line ID 映射到多个 Yarn 指令时拒绝恢复。
- 捕获 resolved substitutions，并在重放 `RunLine` 前按 Yarn VM 需要的顺序重建操作数栈。
- Script Adapter 对 checkpoint format、ProgramFingerprint、node、Line ID 和安全边界执行
  完整 Preflight。
- StateManager 已将 Variables、Actors、Background、命名 BGM 和 Script Playback 组成
  一个有序内存恢复事务。
- 手工 Program、真实 Yarn compiler 输出、纯 C# 单元测试和 Godot headless
  `DialogueRunner` 集成测试均已通过。

## 4. 当前快照精确含义

`YarnVmLineSnapshot` 当前记录：

```text
NodeName
LineId
CapturedProgramCounter   仅诊断，恢复定位不使用
ExecutionState           当前是字符串诊断值
OperandStackDepth        安全性断言，未序列化内容
CallStackDepth           安全性断言，未序列化内容
PendingOptionCount       安全性断言，未序列化内容
ResolvedSubstitutions    重放当前 RunLine 所需的已解析值
```

因此它是“对当前 VM 布局的恢复描述”，而不是完整 VM Snapshot。

## 5. 已实现范围与保留限制

### 5.1 Dialogue Variables

State Contributor 已捕获 `InMemoryVariableStorage` 中的 string/bool/number，并在恢复前
清空当前 store 后写入完整快照；快照之后新增的变量不会泄漏。Smart Variable 继续作为
派生值处理，不单独复制。

### 5.2 Line Substitutions

Bridge 会校验 `RunLine.SubstitutionCount` 与快照值数量一致，恢复已解析值并重放完全
相同的当前行；不会从 node 开头快进，因此不会重复目标行之前的 Command。

### 5.3 Call Stack 和 Options

当前只允许 operand stack、call stack 和 pending options 全部为空的顶层行边界。
M9.5/M9.6 继续使用这个可解释的安全子集；不恢复选项显示中间态、非空
call stack 或任意 VM 指令边界。

### 5.4 Script 版本兼容

`ScriptCheckpoint` v1 使用 compiled Program protobuf 的 SHA-256 指纹并要求完全一致；
指纹和锚点在破坏当前 Runtime 状态前完成 Preflight。脚本变更后的迁移仍延后。

### 5.5 多服务恢复

VM 位置作为一个 Game State record，已和 Variables、Actors、Background、命名 BGM
统一捕获和恢复；扩展 Service 可通过 `ConfigureStateContributors` 显式注册。

## 6. 与 Naninovel Script / State 模块的对应

Bridge 应归属 Script 集成层，而不是 StateManager 本身：

```text
Majo.Runtime.Script
    定义 ScriptId / ScriptPlaybackSpot / ScriptCheckpoint / safe boundary
            |
            v
Majo.Runtime.Dialogue.Yarn + Yarn VM Bridge
    读写 Yarn 3.2.0 内部状态，投影为 Yarn-free ScriptCheckpoint
            |
            v
Majo.Runtime.State
    将 ScriptCheckpoint 与 Variables / Actors / Audio record 组合为 StateSnapshot
```

这一边界避免 State 公共 API 知道 `Yarn.Dialogue`、`Yarn.Program`、program counter 或
YarnSpinner-Godot Node。

## 7. 已实现的恢复协议

```text
1. 对所有 StateRecord 做 schema/fingerprint/anchor Preflight
2. 阻断 Advance/Options 等模态输入
3. 停止当前 Dialogue Session，取消并汇合 Dialogue-owned Operations
4. 清理当前 Game 状态
5. 恢复 Variables
6. 恢复 Actors / Background / Audio / 扩展 Service
7. 启动目标 Project/Node，在 onDialogueStart 安全点注入 ScriptCheckpoint
8. Presenter 重放当前行且不重复执行前置 Command
9. 释放输入；下一次 Advance 从快照后续指令继续
```

若 1 失败，当前会话不受影响。若 3-8 中任一步骤失败，Runtime 必须进入明确
Faulted 状态并保持输入阻断，不带着部分恢复状态继续脚本。

## 8. 产品化门禁结果

M9.5 已通过：

- 普通行与带 string/number/bool substitution 的行恢复。
- stable Line ID 唯一性、ProgramFingerprint 和 ScriptCheckpoint 格式版本。
- 非安全边界、选项、非空 stack、缺失锚点和版本不兼容的类型化失败。
- Yarn Spinner 3.2.0 版本和 Bridge 内部布局 fail-fast 检查。
- Bridge DTO 到 Yarn-free ScriptCheckpoint 的隔离投影。

M9.6 已通过：

- Dialogue Variables 完整快照和“先清理后恢复”语义。
- Script、Variables、Actors、Background 和 BGM 的统一内存 round-trip。
- Capture/Preflight/Reset/Restore 顺序和任一 Contributor 失败时的 Runtime 终态。
- 真实 Yarn/Godot headless 中的重放一次、继续一次、无重复 Command 和零泄漏验证。

## 9. 风险决策

`InternalsVisibleTo("YarnSpinner.Tests")` 和 `UnsafeAccessor` 依赖 Yarn 的非公开布局，是明确的
版本锁定风险，不是 Yarn 承诺的稳定 API。当前决策是：

- 短期保留独立 Bridge，仅支持精确 Yarn 3.2.0，以完成 Script/State 基础验证。
- 任何 Yarn 升级都是独立兼容 milestone，必须重跑内部布局、快照矩阵和真实
  Godot headless 恢复测试。
- 中期优先评估 Yarn 官方 VM snapshot/resume API 或可上游的最小公开扩展；
  在没有替代方案前，不让 Bridge 类型泄漏到 Majo 稳定公共 API。

综合结论：M10 所需的 Script Runtime Foundation 和 State Snapshot Foundation 已完成；
交付范围是播放/内存快照内核，不包含存档槽位、Rollback 或 Naninovel 多轨脚本系统。
