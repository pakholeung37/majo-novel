# 发布与排错指南

本指南从已经能播放第一幕的示例工程出发，使用 Godot 桌面预设导出独立播放器，再从解压目录复验。示例工程包含 `Example.csproj`、`Main.tscn` 和第一幕的剧情、背景、立绘与 BGM。 [第一幕教程](first-scene.zh-CN.md)

## 1. 构建、导入并新建导出预设

从示例工程根目录运行 `dotnet build Example.csproj`。用 Godot .NET 打开 `project.godot`，在 **Project Settings → Plugins** 中启用 **Yarn Spinner** 和 **Majo Runtime**，等首次导入完成。检查 `assets/scripts/main.yarnproject.direction.tres` 已生成，然后按 F5 播放第一幕。示例将 **Project Settings → Application → Config → Name** 设为 `FirstScene`。

1. 在 **Editor → Manage Export Templates** 安装与当前 Godot .NET 编辑器匹配的 .NET 导出模板。本机已有对应模板时可直接使用。
2. 打开 **Project → Export**，点击 **Add…**，选择要发布的平台，例如 macOS、Windows Desktop 或 Linux。将预设命名为 `Desktop Release`，后面的命令用这个名字查找预设。
3. 在 **Options** 中选择目标架构，并填好平台要求的字段。本文实测的 macOS 预设使用 **Universal** 架构和包标识 `org.majo.firstscene`，本机验证使用 **Xcode codesign**、**Identity** 为 `-` 的 ad-hoc 签名。向其他用户分发 macOS 程序时，按目标渠道配置签名与公证。
4. 在 **Resources** 页设置下一节的资源选择。Godot 将预设保存到工程根目录的 `export_presets.cfg`。 [Godot 导出项目](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html) · [macOS 导出、签名和公证](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_macos.html)

## 2. 把动态加载的素材加入包

小工程可以选 **Export all resources in the project**，让 Godot 打包全部已导入资源。若要控制包体，选择 **Export selected resources (and dependencies)**，然后勾选下表的文件。选择范围会写入 `export_presets.cfg` 的 `export_files`。 [Godot 资源选项](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html#resource-options)

| 要加入的文件 | 运行时用途 |
| --- | --- |
| `res://Main.tscn` | 游戏主场景 |
| `res://assets/scripts/main.yarnproject` | `StartAsync()` 启动的 `main` 剧情工程 |
| `res://assets/backgrounds/station.png` | `<<backdrop station>>` |
| `res://assets/characters/visitor/neutral.png` | `<<character visitor appearance:neutral>>` |
| `res://assets/audio/bgm/evening.wav` | `<<bgm evening track:music>>` |

素材 ID 是 Yarn 运行时查找的键，Godot 从 `Main.tscn` 收集静态依赖时无法推导这些路径。Majo 导出插件会加入默认 UI、相关脚本和着色器，并为已选中的 `.yarnproject` 加入 Direction 伴随资源。其他分支使用的背景、表情、音效、各语言语音、动态加载的字体或配置资源，也要加入选中资源，或由选中的场景直接引用。 [素材路径和资源 ID 对照](assets-and-host.zh-CN.md)

按同样方式确认本地化配置引用的 `.translation` 已入包。项目若用 `FileAccess` 直接读取 JSON、CSV 等原始文件，将这些路径加入 **Filters to export non-resource files/folders**。排除过滤器建议保留 `addons/YarnSpinner-Godot/SourceGenerator/*`，并检查其他排除规则是否覆盖到游戏素材。 [Godot 资源过滤器](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html#resource-options)

## 3. 运行受检导出

安装 Python 3.10 或更新版本，从示例工程根目录执行下面的命令。将 `/path/to/Godot` 替换为 Godot .NET 编辑器的可执行文件路径。在 macOS 上，该文件位于编辑器 `.app` 的 `Contents/MacOS/` 中。输出路径应指向新文件，示例将游戏导出到工程旁边的目录。

```sh
python3 addons/majo_runtime/Editor/export.py --godot "/path/to/Godot" --project . --preset "Desktop Release" --output ../release/FirstScene.zip
```

macOS 示例输出 `.zip`。Windows 预设可输出 `FirstScene.exe`，Linux x64 预设可输出 `FirstScene.x86_64`。命令先检查 Yarn 与 Direction 编译，输出 `MAJO_EXPORT_VALIDATED` 后才进行暂存导出。确认命令退出码为 0，并在输出目录看到产物。`--pack` 配合 `.pck` 输出路径可单独生成资源包，下面的复验使用完整桌面播放器。 [Godot 命令行导出](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html)

## 4. 在工程外复验播放器

把导出产物解压或整体复制到一个空目录。保留导出生成的可执行文件、`.pck`、程序集目录和其他同级文件的相对位置。macOS 可以直接打开解压后的 `.app`，也可以从终端运行 `Contents/MacOS/` 内的程序查看日志。例如从工程根目录执行下列命令，应用名以实际导出包为准。

```sh
mkdir ../release/verify
ditto -x -k ../release/FirstScene.zip ../release/verify
cd ../release/verify
./FirstScene.app/Contents/MacOS/FirstScene
```

在目标平台启动播放器，确认开场显示背景和居中的旅人，同时播放 BGM。分别选「走到站台」和「留在候车室」，确认两条分支都能结束，立绘只在第一条分支移到右侧，两条分支结束时音乐都淡出。若游戏启用了文本翻译、配音或存读档，复验对应语言和读档后的画面。记录首次失败的节点、行 ID 或资源路径，便于对照预设。

本指南用 macOS 独立播放器在工程外跑通了第一幕的两个分支。另一次只从预设中取消勾选 `evening.wav`，受检导出仍返回成功，导出的播放器运行到 `bgm` 时报告 `audio/bgm/evening` 缺失。

## 5. 按失败阶段排查

### 构建

| 现象 | 检查 |
| --- | --- |
| `dotnet build` 找不到 SDK，或还原 `Godot.NET.Sdk` 失败 | 在工程根目录运行 `dotnet --list-sdks`，核对 `global.json` 与 `Example.csproj`。首次还原需要可用的 NuGet 源。确认使用 Godot .NET 编辑器。 |
| 编译提示缺少 Majo 或 Yarn 类型 | 检查工程引用和两个 `addons` 目录。受管理工程可运行 `python3 addons/majo_runtime/Tools/majo.py doctor --project .` 核对依赖、引用、Autoload 和插件状态。 |

### 导入

| 现象 | 检查 |
| --- | --- |
| `.yarnproject` 加载失败或没有 `.direction.tres` | 使用已构建的工程打开 Godot，确认两个插件启用，等首次导入完成。检查 `.yarnproject` 的 `sourceFiles` 能匹配修改的 `.yarn` 文件。 |
| `Direction compilation failed` | 从日志中的 `.yarnproject` 路径和首条编译诊断定位错误。检查 Yarn 节点结构、重复的 `scene:` 或错误参数，修正后重新导入。 |

### 播放

| 现象 | 检查 |
| --- | --- |
| 启动后看不到台词，或仍在播示例 | 确认主场景是 `Main.tscn`、启动节点是 `Start`，并检查当前宿主的 `DefaultProjectId`、`DefaultStartNode` 与实际项目及节点名。 |
| 台词正常，立绘或语音缺失 | 把资源 ID 还原成项目路径，核对文件名大小写、导入状态、`appearance:`、行 ID 和语音语言。若日志提示 `Stage coordinates require a positive reference resolution`，在宿主配置并传入 `Actors.ReferenceResolution`。 [素材与宿主配置指南](assets-and-host.zh-CN.md) |

### 导出

| 现象 | 检查 |
| --- | --- |
| 受检导出提示校验未运行 | 检查传给 `--godot` 的可执行文件、`--project` 指向的工程和插件启用状态。确认构建与导入已结束，再重试。 |
| `Output already exists`，或提示找不到预设／模板 | 换一个新的输出路径或目录。核对 `--preset` 与 `export_presets.cfg` 中的名称完全一致，并安装匹配的 .NET 模板。 |
| 包打不开，或启动时报程序集缺失 | 检查目标平台、架构与签名。对含多个文件的桌面导出，用完整输出目录启动。 |
| 播放器提示 `invalid UID` 并回退到文本路径 | 检查日志中回退到的 `res://` 路径是否能加载。示例包中出现过默认 UI 的 UID 回退警告，两条分支均正常完成。若后续出现加载失败，按下面的方法查对应路径。 |

### 编辑器里正常，打包后缺素材

1. **定位实际请求。** 从播放器终端日志找第一个失败的命令及其资源 ID。例如 `audio/bgm/evening` 对应 `res://assets/audio/bgm/evening.wav`。如果默认回退只留下空白或静音，可以在验证构建中临时打开严格检查，然后重新构建并导出。

   ```csharp
   configuration.Resources!.FailOnMissingAssets = true;
   configuration.Audio!.FailOnMissingClips = true;
   configuration.Dialogue!.AbortOnCommandError = true;
   ```

2. **检查预设。** 先核对主场景和 `.yarnproject`，再核对该素材本身、所在分支、外观或语言版本是否被加入 Resources，或由选中的场景直接引用。检查排除过滤器和路径大小写。对于通过 `FileAccess` 读取的原始文件，核对包含过滤器。
3. **重导后复验。** 将缺失项加入预设，用受检命令导出到全新的输出目录，再解压启动并走到原来失败的台词。可用 **Export all resources in the project** 做一次对照。全量包正常时，逐项核对选择性预设与实际读取路径。
