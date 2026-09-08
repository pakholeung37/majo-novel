# Direction API 实施与验收记录

实现指南：`direction-command-api.md`。实施基线：`direction-command-api-refactor-plan.md`。未创建旧命令兼容入口。

## 已完成实现

- P0：共享 Yarn node 前导 transform 接入 native editor/runtime/hot reload；恢复 authored diagnostics、string table 和 debug 源位置；scene header 编译与 companion schema 2。真实 Runner 对首条 set/jump/line/option 等待；入口异常阻止正文。
- P1：Actor 类型、定义引用、Visible、rotation 与 scene context；StateContributor schema 3。Character/LayeredCharacter/Object 使用独立空间、外观、显隐能力。Object JSON 定义、ObjectsPath、锚点、画外坐标、资源/实例分离与 typed preload。
- P2：稳定 per-instance 根，Prepare 外观资源与离屏输入，Commit 同步发布；空间/外观/显隐槽各自接替。完整基础空间姿态在 Commit 采样并写回权威状态；hide→show 防迟到销毁。移除旧镜头 cut+move 同 beat 顺序特例。Camera 与 Actor 使用不同 delta、共享 modifier 执行器；Backdrop source/target 共享 FX 根。
- P3：SceneBoundaryCoordinator 区分 Normal/Restore/InternalRebuild；边界逐项清理 scene-owned 内容、停止 BGM 并使旧异步加载失效，恢复空 main/single；混合事务中的全局 Curtain 保持进行。提供宿主 EndPresentationAsync。清理异常仍尝试释放其余资源，正文不继续。
- P4：新 camera/character/object/backdrop 命令族与 defaults/policy/schema；旧 pose/leave/public release 均不注册。演示与活动 Yarn fixtures 迁移并加入 scene 入口；命令补全从 runtime catalog 生成，普通测试检查漂移；companion 通过 editor 重生成。
- P5：新增生命周期、契约、源映射、Object、FX、镜头接替、真实入口失败及 snapshot 回归；更新旧整树替换断言。外观租约在没有待准备消费者后按实际视觉内容裁剪，包括 LayeredCharacter atlas；hide/scene 清理释放剩余呈现需求。

## 实现细节

- 删除能力以内部 ActorRemovePatch、StageReleasePatch、ViewReleasePatch 保留；无公开 remove/release 脚本入口。
- ConfirmCommit 是现有原子提交的完成通知，用于延后不可恢复的呈现释放；没有引入新的 rollback 产品能力。
- 场景完成会从混合事务移除已释放的场景组件，避免等待全局 Curtain 时仍强引用旧节点。
- Object 内建 factory 为 object；定义可选择已注册 factory，Direction 在准备阶段校验其呈现能力。
- Scene epoch、目标 generation 和槽 writer token 分别防止旧场景、旧目标身份和旧动作回调影响新状态。

## 验收

2026-09-08 最终验收（包含背景同目标 cut 接替回归）：

- `./tools/quality.sh --check`：通过；最终导出后再次检查成功，`git diff --check` 无错误。
- `./tools/test.sh --fast`：通过；422 项运行时单元测试、演示 smoke、Godot headless 及失败退出码探针。
- `python3 tools/test-direction-import.py`：通过；初次/重复导入、源更新/删节点、失败失效与恢复、scene 校验/源映射、删 manifest 全部成功。
- `dotnet test tests/Majo.CommandGenerator.Tests/Majo.CommandGenerator.Tests.csproj`：通过，10 项测试。
- `./tools/test.sh --full`：通过；三轮 Godot headless、Release 构建、macOS PCK 导出 smoke。

另外，quality 统一了少量现有脚本的空白格式，无语义变更。

当前无 git 提交。日志位于 `/tmp/majo-direction-*.log`。
