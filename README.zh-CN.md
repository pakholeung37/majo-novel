# Majo Novel

基于 **Godot .NET、C# 和 Yarn Spinner** 的脚本驱动视觉小说运行时。

[English](README.md) · [脚本编写指南](docs/script-writing.md) · [MIT 许可证](LICENSE)

Majo Novel 将对话、角色演出、镜头、转场、音频与存档状态整合在一个模块化运行时中。剧情和演出使用 Yarn 编写，宿主配置、自定义命令和角色类型通过 C# 扩展。


## 已有能力

- **剧情脚本**：使用 Yarn Spinner 编写台词、选项、变量、分支和子流程，通过 Majo 命令控制演出与音频。
- **画面演出**：背景、角色、物件、外观切换、转场、镜头移动、缩放、旋转与特效；通过 Stage 和 View 组织多舞台、多视图画面。
- **演出协调**：使用 `beat` 将多条演出命令组合提交，并选择是否等待演出完成后继续脚本。
- **多种角色表现**：现有 demo 通过 Actor 插件接入分层立绘、Sprite 动画、Live2D 和自定义动态背景。
- **玩家功能**：逐字显示、历史记录、自动播放、仅跳过已读内容、存读档、设置，以及启用后的剧情回溯。
- **开发支持**：文本与语音语言独立配置、资源预加载、开发期间的脚本热重载和运行时诊断。

## 用脚本组织演出

下面的片段沿用[内部验收 demo](demos/demo-01/assets/scripts/main.yarn) 的角色与资源配置：

```yarn
title: Start
scene: runtime_demo
---
<<bgm demo-theme track:music duration:0.35>>

<<beat begin>>
<<backdrop backdrop/courtroom via:dissolve duration:0.45>>
<<character hero appearance:calm at:left via:fade duration:0.45>>
<<beat end wait:true>>
Maya: 场景准备好了。接下来调查什么？

<<beat begin>>
<<character hero appearance:determined via:dissolve duration:0.35>>
<<character move hero at:center duration:0.35>>
<<beat end wait:true>>

<<camera move center:48,50 zoom:1.08 duration:0.25 wait:true>>
-> 追问证词
    Maya: 请再说一次刚才的证词。
-> 出示证物
    Maya: 这里有一个矛盾。
===
```

Yarn 负责剧情流程，Majo 演出命令负责画面呈现。`beat` 组织需要协调的变化，`wait:true` 让脚本在演出结束后继续。完整语法与示例见[脚本编写指南](docs/script-writing.md)。

## Demo

| Demo | 展示内容 | 入口场景 |
| --- | --- | --- |
| 内部验收 | 使用随仓库提供的测试素材，验证对话、演出、音频、分支、存读档、历史、设置与诊断 | [`RuntimeDemo.tscn`](demos/demo-01/RuntimeDemo.tscn) |
| Manosaba | 分层角色表情、剧情演出与多舞台、多视图的法庭场景 | [`ManosabaDemo.tscn`](demos/manosaba/ManosabaDemo.tscn) |
| Ace Attorney | 精灵动画角色、法庭调度、音效与异议效果 | [`AceAttorneyDemo.tscn`](demos/ace-attorney/AceAttorneyDemo.tscn) |
| Live2D | 将 Cubism 表情与动作接入角色演出和镜头命令 | [`Live2DDemo.tscn`](demos/live2d/Live2DDemo.tscn) |
| 背景扩展示例 | 通过自定义 Actor 插件实现持续动画背景与实时转场 | [`BackgroundExtensionDemo.tscn`](demos/background-extension/BackgroundExtensionDemo.tscn) |

内部验收 demo 是仓库提供的可运行参考。Manosaba、Ace Attorney 和 Live2D 用于能力展示，仓库不提供让它们完整运行的外部素材包；可以阅读对应脚本和集成代码。这些演示与原作游戏或模型作者没有官方关联，也不代表其认可。

## 运行内部验收 demo

当前工程使用 **Godot 4.7.2 .NET** 和 **.NET 8**。请使用 Godot 的 .NET 版本，构建后明确指定验收场景；工程默认场景是 Manosaba 展示。

```sh
dotnet build "Majo Novel.csproj"
godot --path . res://demos/demo-01/RuntimeDemo.tscn
```

将 `godot` 替换为本机 Godot .NET 可执行文件路径。也可以在编辑器中打开 `project.godot`，构建 C# 工程后打开验收场景，按 F6 运行当前场景。

## 代码与文档

可复用运行时位于 [`addons/majo_runtime`](addons/majo_runtime)，各 demo 的呈现与扩展代码位于 [`demos`](demos)。

- [脚本编写指南](docs/script-writing.md)：Yarn 语法、演出命令、舞台、视图、镜头、音频与示例。
- [开发者场景资源准备](docs/advanced/scene-resources-injection.md)：在场景入口与状态恢复时准备或生成资源。
- [背景插件示例](demos/background-extension/README.md)：使用公开 Factory 与资源接口实现 Actor 插件。
- [Ace Attorney 集成说明](demos/ace-attorney/README.md)与 [Live2D 集成说明](demos/live2d/README.md)。
- [运行时服务与插件](addons/majo_runtime/Runtime)、[测试套件](tests)。

详细文档目前主要使用中文。[`docs/archive`](docs/archive) 保留历史设计与实施记录，其中的 API 描述可能已过时。

## 开发检查

```sh
./tools/quality.sh --check  # 检查 C# 格式
./tools/test.sh --unit     # 构建并运行 C# 单元测试
./tools/test.sh --fast     # 单元测试与 Godot 无头集成检查
./tools/test.sh --full     # 完整检查、独立宿主与导出冒烟测试
```

测试脚本默认执行 `--full`。如果 Godot .NET 不在 `/Applications/Godot_mono.app/Contents/MacOS/Godot`，通过 `GODOT_BIN` 指定可执行文件路径。完整检查还需要匹配版本的导出模板。真实渲染器相关的回归说明见 [`tests/rendering`](tests/rendering/README.md)。

## 许可证与第三方内容

Majo Novel 的原创代码与文档使用 [MIT 许可证](LICENSE)，版权 © 2026 Pakho Leung。

第三方组件保留各自许可证。游戏剧情文本、美术、音乐、语音、字体和 Live2D 模型不会因为本项目公开而获得 MIT 授权，包括 demo 脚本中改编的 Manosaba 剧情文本。具体边界及随仓库提供的许可声明见[第三方声明](THIRD_PARTY_NOTICES.md)。
