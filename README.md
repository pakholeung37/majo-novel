# Majo Rebuild (Majo Runtime)

基于 Godot 4.7+ (C# / .NET 8) 与 Yarn Spinner 构建的高性能、模块化视觉小说（Visual Novel）运行时框架。

## 质量门禁

- **代码格式化**：`./tools/quality.sh --format`（统一工程代码规范，CI 校验可使用 `--check`）
- **快速本地回归**：`./tools/test.sh --fast`（包含编译、单元测试与单轮无头集成验证，用于日常开发）
- **完整验收门禁**：`./tools/test.sh --full`（包含纯净构建、3 轮无头测试与导出打包冒烟，用于 PR 及发布）
