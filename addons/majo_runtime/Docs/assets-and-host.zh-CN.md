# 素材与宿主配置指南

以下示例沿用第一幕工程。先确认 `main.yarn` 能在默认对话框里播放。素材示例使用默认目录，配置片段可以按需要加在 `Main.cs` 的 `_Ready()` 中。 [第一幕教程](first-scene.zh-CN.md)

## 素材路径、资源 ID 和 Yarn 写法

路径以包含 `project.godot` 的目录为起点。Majo 的默认素材根目录是 `res://assets`。把文件放进项目后，等 Godot 完成导入再启动游戏。脚本里使用表中的 ID，保持大小写一致，图片和音频的扩展名由资源加载器查找。

| 用途 | 文件路径 | 资源 ID 或脚本标识 | Yarn 写法 |
| --- | --- | --- | --- |
| 剧情工程与入口 | `assets/scripts/main.yarnproject`、`assets/scripts/main.yarn` | 工程 `main`、节点 `Start` | `title: Start`，文件须匹配 `.yarnproject` 的 `sourceFiles` |
| 背景 | `assets/backgrounds/station.png` | 背景 `station` | `<<backdrop station>>` |
| 背景的另一种外观 | `assets/backgrounds/station/night.png` | 背景 `station`、外观 `night` | `<<backdrop station appearance:night>>` |
| 立绘 | `assets/characters/visitor/neutral.png` | 角色 `visitor`、外观 `neutral` | `<<character visitor appearance:neutral at:50,50>>` |
| 物件 | `assets/objects/evidence/ticket.png` | 素材 `evidence/ticket`、实例 `ticket1` | `<<object ticket1 asset:evidence/ticket at:65,40>>` |
| 物件的另一种外观 | `assets/objects/evidence/ticket/marked.png` | 素材 `evidence/ticket`、外观 `marked` | `<<object ticket1 appearance:marked>>`，在创建实例后使用 |
| 背景音乐 | `assets/audio/bgm/evening.wav` | 音乐 `evening`、轨道 `music` | `<<bgm evening track:music>>` |
| 音效 | `assets/audio/sfx/train-bell.wav` | 音效 `train-bell` | `<<sfx train-bell>>` |
| 按行 ID 配音 | `assets/audio/voice/en/first-scene-arrival.wav` | 语音 `first-scene-arrival` | `Traveler: It is getting dark. #line:first-scene-arrival` |
| 指定语音 ID | `assets/audio/voice/en/visitor/greeting.wav` | 语音 `visitor/greeting` | `Traveler: Hello. #line:first-scene-greeting #voice:visitor/greeting` |

- 普通背景和物件可直接按路径约定加载。宿主配置了同 ID 的 Actor 定义时，优先使用定义提供的 Factory 和外观映射。首次创建物件时填写 `asset:`，后续对 `ticket1` 的指令可以沿用该实例的素材。`track:` 同样用于区分播放轨道，例如用 `<<stopBgm track:music>>` 停止 `music`。
- 默认目录可以在宿主配置中修改。`Resources.RootPath` 是 `res://` 下的根路径，`Resources.ScriptsPath`、`BackgroundsPath`、`CharactersPath`、`ObjectsPath` 和 `Audio.BgmPath`、`SfxPath`、`VoicePath` 均相对素材根目录。例如设 `Resources.RootPath = "res://game_assets"` 后，`<<backdrop station>>` 会在 `res://game_assets/backgrounds/` 下查找。
- 用 `#voice:none` 跳过单句配音。需要把一段语音作为独立动作播放时，可以使用 `<<voice first-scene-arrival wait:true>>`，等声音播完再继续。随台词播放使用表中的行 ID 或 `#voice:`。

## 宿主配置放在哪里

下面是第一幕工程的宿主结构。修改配置的代码放在 `CreateDefault()` 之后、创建 `MajoRuntimeBootstrap` 之前。后面的片段共用同一个 `configuration`。修改 `Main.cs` 后运行 `dotnet build Example.csproj`，再启动游戏。

```csharp
using Godot;
using Majo.Runtime;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;

public partial class Main : Node
{
    public override async void _Ready()
    {
        var configuration = MajoProjectConfiguration.CreateDefault();
        configuration.Actors!.ReferenceResolution = new Vector2I(1280, 720);
        // 把下面需要的配置加在这里。

        var runtime = new MajoRuntimeBootstrap { Configuration = configuration };
        AddChild(runtime);
        await runtime.InitializationCompletion;
        await runtime.Host!.Services.Get<IDialoguePlayer>().StartAsync();
    }
}
```

`ReferenceResolution` 是立绘和物件使用百分比坐标时的参考尺寸。`StartAsync()` 默认启动 `main` 工程的 `Start` 节点，可通过 `configuration.Dialogue!.DefaultProjectId` 和 `DefaultStartNode` 更改。

## 默认 UI 与输入

`CreateDefault()` 已启用对话框、选项、历史及设置面板，也启用了输入和默认绑定。下面显式设置这些开关，并把默认打字速度设为每秒 45 字符。设置面板依赖 `State.Enabled`，默认值为 `true`。

```csharp
configuration.Presentation!.Enabled = true;
configuration.Presentation.LettersPerSecond = 45;
configuration.History!.ShowDefaultUi = true;
configuration.Settings!.Enabled = true;
configuration.Settings.ShowDefaultUi = true;
configuration.Input!.Enabled = true;
configuration.Input.InstallDefaultBindings = true;
```

| 游戏中的操作 | Godot Input Map 动作 | 默认键鼠 |
| --- | --- | --- |
| 推进台词 | `majo_advance` | 空格、鼠标左键 |
| 上一项 / 下一项 | `majo_option_up` / `majo_option_down` | 上 / 下方向键、滚轮上 / 下 |
| 确认选项 | `majo_confirm` | 回车、鼠标中键。也可以左键点击选项 |
| 取消 / 关闭面板 | `majo_cancel` | Esc、鼠标右键 |
| 自动播放 / 跳过 | `majo_auto` / `majo_skip` | A / K |
| 历史 / 设置 | `majo_history` / `majo_settings` | H / S |
| 存读档 / 快速存档 | `majo_menu` / `majo_quickSave` | M / F5，启用存档后可用 |
| 后退 / 前进 | `majo_stepBack` / `majo_stepForward` | Backspace 或左方向键 / 右方向键，启用回溯后可用 |
| 回溯面板 | `majo_travel` | F4，启用回溯面板后可用 |

例如要用 J 推进台词，在 Godot 的项目设置 → 输入映射中新增 `majo_advance` 并绑定 J。保留 `InstallDefaultBindings = true`，运行时会补齐缺失的动作，并沿用项目里已有的同名动作及其绑定。

## 存读档

启用存档服务以提供手动、快速和自动存档槽。以下配置也为游戏指定独立的持久化目录。

```csharp
configuration.State!.Enabled = true;
configuration.State.StorageNamespace = "mygame/state";
configuration.Save!.Enabled = true;
configuration.Save.ShowDefaultUi = true;
```

等台词显示完成、演出稳定后，在游戏中按 M 打开存读档面板，选择手动槽的 **Save**。推进到下一句，再打开面板，选同一槽的 **Load**，验证台词和演出回到保存点。F5 请求快速存档。Yarn 的 `<<save>>` 和 `<<autoSave>>` 分别请求快速和自动存档，在可保存的边界处理。默认存档、设置等持久化数据都位于 Godot 的 `user://` 下，以上配置使用 `user://mygame/state/` 作为存储根目录。

## 剧情回溯

回溯用运行时记录的检查点在当前会话内前后移动。保留启用的 `State`，再加上以下配置。回溯可独立于存读档面板启用。

```csharp
configuration.State!.Enabled = true;
configuration.State.DialogueTravel.Enabled = true;
configuration.State.DialogueTravel.ShowDefaultUi = true;
```

从编辑器运行，推进几句等到有多个检查点。按 Backspace 或左方向键回到上一处，按右方向键沿已有记录前进。按 F4 查看回溯面板。默认保留最多 128 个检查点，可用 `configuration.State.DialogueTravel.Capacity` 调整。

此功能在编辑器运行时可用。导出的 Debug 构建还需要设置 `configuration.State.DialogueTravel.EnableInExportedDebugBuild = true`。Release 构建会省略回溯服务。

## 分别设置文本和语音语言

下面让新用户先看到简体中文台词、听到英语配音。`Localization.DefaultLocale` 是文本回退链的默认语言，`Settings.DialogueLocale` 和 `Settings.VoiceLocale` 是玩家首次启动的语言设置。已有持久化设置的用户会恢复自己上次的选择，可按 S 在设置面板分别切换两种语言。

```csharp
configuration.Localization!.DefaultLocale = "en";
configuration.Localization.SupportedLocales = ["en", "zh-CN"];
configuration.Settings!.DialogueLocale = "zh-CN";
configuration.Settings.VoiceLocale = "en";
```

### 台词翻译

保持 `assets/scripts/main.yarnproject` 的 `baseLanguage` 为 `en`。将第一幕的开场台词替换成下面这一行。切换回英语时，Yarn 会显示这条原文。

```yarn
Traveler: It is getting dark. Where should we wait? #line:first-scene-arrival
```

新建 UTF-8 文件 `localization/story.csv`，用完整的 `line:` ID 作为翻译键。带姓名的台词保留半角冒号，选项文字也可以用各自的 `line:` ID 添加翻译。

```csv
keys,zh_CN
line:first-scene-arrival,旅人: 天快黑了。我们去哪里等车？
```

Godot 导入 CSV 后生成 `res://localization/story.zh_CN.translation`。在项目设置的本地化页面将它加入 **Translations** 列表，或在 `project.godot` 的 `[internationalization]` 段中加入该路径。若已有翻译列表，把新路径追加到现有列表。

```ini
[internationalization]
locale/translations=PackedStringArray("res://localization/story.zh_CN.translation")
```

这里的 `zh_CN` 是 Godot 翻译资源使用的写法，Majo 配置和语音目录使用 `zh-CN`。只有开场台词加入了译文，其余台词和选项会显示各自的 Yarn 原文。

### 台词配音

将英语录音放到 `assets/audio/voice/en/first-scene-arrival.wav`。有简体中文录音时，放到 `assets/audio/voice/zh-CN/first-scene-arrival.wav`。两份录音沿用同一个语音 ID，文件名取台词的 `#line:` ID 去掉 `line:` 前缀的部分。

打开游戏后确认开场显示中文、播放英语。再按 S 将语音语言切到简体中文，重新播放开场，检查配音切换。随台词的语音按所选语言、父语言、默认语音语言、`assets/audio/voice/` 下的平铺文件依次查找。没有匹配录音时，台词仍可显示。
