# Yarn Spinner 源码修改与侵入式耦合问题记录

> 2026-09-07：本文所列两处耦合已按方案 A 修复。第 1–3 节保留为历史问题记录；当前实现见第 5 节。

## 1. 背景与现状

在近期对演出（Direction）编译和热重载功能的迭代中，系统对第三方插件 **Yarn Spinner for Godot**（`addons/YarnSpinner-Godot/`）进行了源码修改与侵入式类扩展。

主要涉及以下两处修改：
1. **修改 1**：在 `addons/YarnSpinner-Godot/Editor/YarnProjectEditorUtility.cs` 中直接插入演出脚本编译与错误上报逻辑，并引入 Majo 运行时命名空间。
2. **修改 4**：在 `addons/majo_runtime/Runtime/Dialogue/Yarn/YarnProjectDirectionMetadata.cs` 中，利用 `partial class` 在 `namespace YarnSpinnerGodot` 下为 `YarnProject` 资源类注入 `CompiledDirectionModulesJson` 导出字段。

本文档详细记录这两处修改的具体内容、存在的架构与工程隐患，以及后续的解耦重构方案。

---

## 2. 修改详情与问题分析

### 修改 1: 直接修改 `YarnProjectEditorUtility.cs`

#### 具体变动
* **文件路径**: `addons/YarnSpinner-Godot/Editor/YarnProjectEditorUtility.cs`
* **变更内容**:
  * 引入上层运行时命名空间：
    ```csharp
    using Majo.Runtime.Dialogue.Yarn;
    using Majo.Runtime.Dialogue.Yarn.HotReload;
    ```
  * 在 `UpdateYarnProject()` 编译 `.yarnproject` 的主流程中插入了如下逻辑：
    ```csharp
    var directionFiles = scriptAbsolutePaths.Select(path =>
    {
        var content = File.ReadAllText(path);
        return new YarnSourceFileSnapshot(
            ProjectSettings.LocalizePath(path),
            path,
            content,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant());
    }).ToArray();
    var directionSnapshot = new YarnSourceSnapshot(
        Path.GetFileNameWithoutExtension(project.JSONProjectPath),
        project.JSONProjectPath,
        project.JSONProject.FileVersion,
        project.JSONProject.BaseLanguage,
        directionFiles,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", directionFiles.Select(file => file.ContentHash)))))
            .ToLowerInvariant());
    var directionModules = new NodeDirectionCompiler().Compile(directionSnapshot);
    project.CompiledDirectionModulesJson = YarnProjectDirectionMetadata.Serialize(directionModules);
    foreach (var diagnostic in directionModules.SelectMany(module => module.Diagnostics)
        .Where(diagnostic => diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
    {
        GD.PushError($"Direction authoring error: {diagnostic.File}:{diagnostic.StartLine}: {diagnostic.Message}");
    }
    ```

#### 存在的问题与风险
1. **反向依赖（架构反模式）**：
   * `YarnSpinner-Godot` 本应作为通用、底层的第三方对话系统插件。
   * 修改后，底层通用插件反向依赖了上层业务框架 `Majo.Runtime`（`NodeDirectionCompiler`、`YarnSourceSnapshot`、`YarnProjectDirectionMetadata` 等）。
   * 脱离 Majo 运行时环境后，`YarnSpinner-Godot` 将无法独立编译运行。
2. **上游升级与维护困难**：
   * 任何更新、升级或重新安装 Yarn Spinner 插件的操作都会直接覆盖该文件，导致演出编译逻辑静默丢失。
3. **职责混淆**：
   * Yarn Project 编译器的职责仅为编译 Yarn 语法树与对话程序集，不应强行绑定项目特有的机位、立绘和舞台演出编译器逻辑。

---

### 修改 4: 侵入式 `partial class YarnProject` 扩展

#### 具体变动
* **文件路径**: `addons/majo_runtime/Runtime/Dialogue/Yarn/YarnProjectDirectionMetadata.cs`
* **变更内容**:
  ```csharp
  namespace YarnSpinnerGodot
  {
      // This partial keeps the serialized field on YarnProject itself, so imported
      // projects retain Direction metadata even when hot reload is disabled. The
      // metadata is intentionally JSON-only and does not make YarnSpinner depend on
      // Majo runtime types at the resource layer.
      public partial class YarnProject
      {
          [Export] public string CompiledDirectionModulesJson = string.Empty;
      }
  }
  ```

#### 存在的问题与风险
1. **程序集边界脆弱性**：
   * C# 的 `partial class` 要求所有分部类定义必须位于**同一个编译程序集（Assembly）**中。
   * 当前项目因为所有 C# 代码同属单一 Godot C# 工程因而能够编译；但如果未来将 `addons/YarnSpinner-Godot` 拆分为独立程序集（例如独立 `.csproj` 或 precompiled DLL），跨项目的 `partial class` 将立即触发编译错误（`CS0260`/`CS0263`）。
2. **第三方数据模型污染与隐式依赖**：
   * 在业务层通过劫持 `namespace YarnSpinnerGodot` 向外部类注入字段，模糊了代码边界。
   * 对外部使用者和维护者来说，`YarnProject.CompiledDirectionModulesJson` 看起来像是 Yarn Spinner 官方提供的字段，容易造成维护困惑。
3. **序列化资产强耦合**：
   * 演出编译产物直接以内嵌 JSON 字符串的形式保存在 Godot 生成的 `YarnProject` 资源文件（`.tres`）中。若演出数据 schema 变更，将很难单独对演出数据做版本迁移与清理。

---

## 3. 建议的解耦与重构方案

为保证第三方插件代码的纯洁性、可升级性与架构清晰度，建议按以下方案逐步解耦：

### 方案 A：独立演出资产与独立导入器（推荐）
1. **独立资源类型**：
   * 定义 `MajoDirectionData` 或 `MajoScenarioProject` 作为独立的 Godot `Resource`。
   * 不将 `CompiledDirectionModulesJson` 存放在 `YarnProject` 内部，而是作为单独的 `.tres` 或伴随资源存储。
2. **独立编译与监听**：
   * 由 Majo 自己的编辑器插件（`MajoEditorPlugin`）监听 `.yarn` 文件的变更或 YarnProject 的重新导入事件。
   * 在监听到变动后，由 Majo 独立触发 `NodeDirectionCompiler` 并生成对应的演出资源文件。

### 方案 B：Godot 编辑器导入后置处理（EditorResourcePostProcessor）
1. 若仍希望演出数据与 YarnProject 绑定，可利用 Godot 的 `EditorResourcePostProcessor` 或构建管道钩子在导入后进行扩展处理，而非侵入修改 `YarnProjectEditorUtility.cs`。

---

## 4. 后续任务清单

- [x] 移除 `addons/YarnSpinner-Godot/Editor/YarnProjectEditorUtility.cs` 中的 Majo 演出编译代码，还原 Yarn Spinner 官方源码。
- [x] 移除 `YarnProjectDirectionMetadata.cs` 中的 `namespace YarnSpinnerGodot` 分部类声明。
- [x] 为 Majo Direction 建立独立的编译与资源持久化管线。


## 5. 当前实现与迁移方式

- `addons/majo_runtime/Runtime/Dialogue/Yarn/MajoDirectionData.cs` 定义独立 Godot Resource；每个 `example.yarnproject` 对应 `example.yarnproject.direction.tres`，包含 schema 版本、源文件指纹、Yarn 程序指纹与模块 JSON。
- `addons/majo_runtime/Editor/MajoDirectionCompiler.cs` 负责生成资源。使用 Yarn 自己的 manifest/glob 解析规则；来源发生变化时由 Majo 调用 Yarn 的公开编译接口，随后生成演出数据。依赖方向为 Majo → Yarn，Yarn 插件不引用 Majo。
- `MajoRuntimePlugin` 在启用时以及 Godot 文件系统变化、资源重新导入后检查项目。指纹相同则跳过写入；脚本删除会重建模块列表，manifest 删除会清除伴随资源。编译失败会移除旧产物，并报告文件与错误信息。
- `YarnProjectVersionStore` 从伴随资源读取普通导入版本的演出模块，并校验 schema、manifest 和程序指纹。资源缺失或版本不符时明确失败，不静默使用空模块。
- `YarnProjectMaterializer` 继续将热重载模块直接放在 `YarnProjectVersion.DirectionModules`，不再序列化到第三方资源。
- `MajoDirectionExportPlugin` 在导出 Yarn 项目时显式加入对应资源和资源脚本，兼容按场景/资源选择的导出方式。运行时无需读取原始 `.yarn` 文件。

升级后先构建 C# 工程，再启用 Majo Runtime 编辑器插件并打开项目（CI 可使用 Godot `--headless --editor --quit`），即可从源脚本重建伴随资源。旧 `.godot/imported/` 中的内嵌字段不再被读取。生成的 `.direction.tres` 是 Majo 管理的产物，不应手工编辑；格式变更时提升 `CurrentSchemaVersion` 并重新生成。

### 回归检查

- 单元测试：模块 JSON 往返，以及损坏 JSON 的拒绝行为。
- `HeadlessTestRunner.DirectionData.cs`：普通导入模块加载、独立资源落盘往返、缺失资源、schema 不兼容和 Yarn 程序指纹不匹配。
- `python3 tools/test-direction-import.py`（先构建 Debug 工程）：实际编辑器首次导入、重复导入、源修改、源删除、编译失败、恢复与 manifest 删除。该脚本创建并清理临时测试目录。

### 本次验证结果

- Debug / Release 构建通过；363 个单元测试通过；Godot headless 集成测试通过。
- 实际编辑器导入回归全部通过。
- 将 YarnSpinner-Godot 单独复制到临时 Godot C# 工程，在不包含 Majo 的情况下构建成功（保留插件原有 SYSLIB1220 警告）。
- macOS 资源包导出成功；直接从包中加载 Yarn / Direction 资源，校验模块 JSON 与程序指纹通过。
- 完整包内 Demo 启动验证未通过：现有 `MajoRuntimeBootstrap.InitializeRuntimeAsync` 调用 `YarnDependencyValidator.ValidateProject` 时，打包模式下 `ProjectSettings.GlobalizePath("res://")` 返回空路径，在加载演出前失败。这是另一个启动路径问题，本次未修改该校验。
