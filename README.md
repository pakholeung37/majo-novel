# Majo Rebuild (Majo Runtime)

基于 Godot 4.7+ (C# / .NET 8) 与 Yarn Spinner 构建的高性能、模块化视觉小说（Visual Novel）运行时框架。

## 文档

- [Script Writing：脚本编写指南](docs/script-writing.md)：Yarn 语法、台词与分支、演出命令、等待与 Beat、场景切换、音频及完整示例。
- [Direction 命令与场景入口](docs/direction-command-api.md)：演出接口约束与实现说明。

## 质量门禁

- **代码格式化**：`./tools/quality.sh --format`（统一工程代码规范，CI 校验可使用 `--check`）
- **单元测试**：`./tools/test.sh --unit`（恢复依赖、增量构建并运行 C# 单元测试，不启动 Godot；适合纯逻辑修改）
- **快速本地回归**：`./tools/test.sh --fast`（增量构建、单元测试、Godot 资源导入、单轮无头集成及失败退出码检查；适合引擎集成修改）
- **完整验收门禁**：`./tools/test.sh --full`（非增量构建、单元测试、Godot 资源导入、单轮无头集成、失败退出码检查及宿主平台导出冒烟；用于 PR 及发布，也是省略参数时的默认模式）

叠层角色遮罩的像素回归需要真实渲染器。构建后运行 `Godot --path . res://tests/headless/HeadlessTestRunner.tscn -- --majo-layered-masks`（不要加 `--headless`；将 `Godot` 替换为本机 Mono 版可执行文件路径）。测试覆盖遮罩阈值、编号覆盖、变换、表情切换、Multiply 和 Miria/Coco 的渐变端点；截图输出到 `build/layered-mask-evidence/`。遮罩裁剪已支持导入的 `Mask/Masked` 材质标记，Softlight/Overlay 的混合计算仍沿用既有实现。
