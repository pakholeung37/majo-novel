# 用示例工程做出第一幕

从发布包里的小故事开始，换上自己的背景、立绘和音乐，再用一个选项让角色走向站台。整个过程沿用示例工程的宿主和默认对话界面。

## 1. 跑通原始示例

从项目发布页下载文件名以 `-example.zip` 结尾的示例工程，解压到空目录。安装与示例匹配的 Godot .NET 编辑器和 .NET SDK，要求见包内的 addon README 和工程根目录的 `global.json`。打开包含 `Example.csproj` 和 `project.godot` 的目录。首次构建需要从 NuGet 还原依赖。 [项目发布页](https://github.com/pakholeung37/majo-novel/releases) · [addon README](../README.md)

```sh
dotnet build Example.csproj
```

在 Godot 项目管理器中导入并打开这个 `project.godot`。等编辑器导入完成后按 F5。默认对话框会显示英文欢迎词。点击对话框或按空格推进，选一个选项后继续，确认能看到分支台词。

## 2. 放入自己的素材

先停止游戏。在项目的 `assets` 目录下建立所需子目录，把素材放到下面的位置。可以用任意一张横向 PNG 当背景，用带透明背景的 PNG 当立绘，再准备一段 WAV 作为音乐。

| 文件路径（从项目根目录算起） | 脚本中的写法 |
| --- | --- |
| `assets/backgrounds/station.png` | `backdrop station` |
| `assets/characters/visitor/neutral.png` | `character visitor appearance:neutral` |
| `assets/audio/bgm/evening.wav` | `bgm evening` |

按表里的文件名和大小写保存。Yarn 使用逻辑 ID，Godot 导入后由 Majo 按目录和扩展名查找。立绘目录 `visitor` 是角色 ID，文件名 `neutral` 是外观 ID。这些路径使用示例工程的默认素材目录。

## 3. 设置舞台参考分辨率

示例工程原本只显示对话。要用百分比坐标摆放立绘，打开 `Main.cs`，将 `_Ready()` 开头创建宿主的语句替换为下面几行。替换范围从 `var runtime = new MajoRuntimeBootstrap` 到对应的 `};`。

```csharp
var configuration = MajoProjectConfiguration.CreateDefault();
configuration.Actors!.ReferenceResolution = new Vector2I(1280, 720);
var runtime = new MajoRuntimeBootstrap { Configuration = configuration };
```

保存并再次运行 `dotnet build Example.csproj`。参考分辨率是 Majo 将舞台百分比坐标换算为像素时使用的尺寸。

## 4. 写入自己的剧情

用下面的完整片段替换 `assets/scripts/main.yarn`。`main.yarnproject` 已经收集这个文件，示例的默认入口是 `Start`。

```yarn
title: Start
scene: first_scene
---
<<backdrop station via:cut>>
<<bgm evening track:music duration:0.4>>
<<character visitor appearance:neutral at:50,50 via:cut>>
旅人: 天快黑了。我们去哪里等车？ #line:first-scene-arrival

-> 走到站台 #line:first-scene-platform-choice
    <<character move visitor at:75,50 duration:0.35 wait:true>>
    旅人: 我在站台这边等你。 #line:first-scene-platform
-> 留在候车室 #line:first-scene-wait-choice
    旅人: 那就在这里等广播吧。 #line:first-scene-wait

<<stopBgm track:music duration:0.4 wait:true>>
===
```

`scene:` 声明新的演出场景。`at:50,50` 将旅人放在舞台中央，`at:75,50` 将旅人移到右侧。两个 `->` 各自有一段缩进的分支内容，分支结束后都执行 `stopBgm`。`wait:true` 让台词等角色移动完再出现。给台词和选项保留唯一、稳定的 `#line:` ID，便于后续配音、本地化和恢复进度。

## 5. 导入并验证两条分支

保存后切回 Godot，等新增素材和 Yarn 文件导入完成，再按 F5。开场应看到背景和居中的旅人，同时听到音乐。推进到选项，选「走到站台」时，旅人会先移到右侧，再显示分支台词。重新运行，选「留在候车室」时，旅人留在中间。继续推进到结束，两条分支的音乐都会淡出。

| 现象 | 检查方式 |
| --- | --- |
| 仍然显示英文示例 | 确认你保存的是当前工程的 `assets/scripts/main.yarn`，等 Godot 重新导入。 |
| 游戏启动后看不到台词 | 检查 Godot 底部的「输出」和「调试器」。确认使用的是 Godot .NET 版，且 `title: Start`、`---` 和 `===` 都已保留。 |
| 有台词，背景、立绘或音乐缺失 | 对照第 2 节检查项目内路径、文件名大小写与导入状态。角色台词前的 `旅人:` 是显示名，立绘由 `character visitor` 控制。 |
| 输出提示 `Stage coordinates require a positive reference resolution` | 确认第 3 节设置的 `configuration` 已传给 `MajoRuntimeBootstrap`，然后重新构建。 |
| 选项语法报错 | `->` 从行首开始，分支内的命令和台词都缩进四个空格。 |

- 继续接入素材、存读档和文本或语音语言。 [素材与宿主配置指南](assets-and-host.zh-CN.md)
- 为剧情添加角色表情、镜头、音效和新节点。 [脚本编写指南](script-writing.md)
- 导出独立播放器，并在源码目录外复验。 [发布与排错指南](release-and-troubleshooting.zh-CN.md)
