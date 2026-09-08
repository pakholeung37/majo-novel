# Majo Rebuild (Majo Runtime)

基于 Godot 4.7+ (C# / .NET 8) 与 Yarn Spinner 构建的高性能、模块化视觉小说（Visual Novel）运行时框架。

## 质量门禁

- **代码格式化**：`./tools/quality.sh --format`（统一工程代码规范，CI 校验可使用 `--check`）
- **单元测试**：`./tools/test.sh --unit`（恢复依赖、增量构建并运行 C# 单元测试，不启动 Godot；适合纯逻辑修改）
- **快速本地回归**：`./tools/test.sh --fast`（增量构建、单元测试、Godot 资源导入、单轮无头集成及失败退出码检查；适合引擎集成修改）
- **完整验收门禁**：`./tools/test.sh --full`（非增量构建、单元测试、Godot 资源导入、单轮无头集成、失败退出码检查及宿主平台导出冒烟；用于 PR 及发布，也是省略参数时的默认模式）
