# Script Writing：脚本编写指南

本文面向剧情与演出脚本作者，以仓库当前实现为准（2026-09-08）。脚本采用 Yarn：Yarn 负责台词、变量、选项与流程，Majo 命令负责画面、音频和存档请求。

现有完整示例见 [demo-01](../demos/demo-01/assets/scripts/main.yarn)；正式内容组织可参考 [manosaba scripts](../demos/manosaba/assets/scripts/)。演出接口的实现说明见 [Direction 命令与场景入口](direction-command-api.md)。

## 1. 文件与节点

一个 `.yarn` 文件可以包含多个节点。文件夹用于组织内容，跳转目标始终是 `title`，不是文件路径。一个 Yarn Project 内的节点标题应唯一。

```yarn
title: Chapter01_Opening
scene: chapter01_room
---
// 这是注释，不会显示。
房间里安静得只能听见钟声。 #line:ch01_opening_001
艾玛: 有人在吗？ #line:ch01_opening_002
<<jump Chapter01_Choice>>
===

title: Chapter01_Choice
---
-> 敲门 #line:ch01_choice_knock
    艾玛: 打扰了。 #line:ch01_knock_001
-> 原地等待 #line:ch01_choice_wait
    又过了一分钟。 #line:ch01_wait_001
无论选择哪一项，都会继续到这里。 #line:ch01_merge_001
===
```

| 写法 | 含义 |
| --- | --- |
| `title: NodeName` | 节点名；建议使用稳定的英文、数字与下划线 |
| `scene: scene_id` | 可选，只放在真正的新场景入口，详见第 8 节 |
| `tags: ...` | 可选的节点标签，不会自动执行演出 |
| `---` / `===` | 正文开始 / 节点结束 |
| `// ...` | 注释 |
| `<<命令 参数>>` | 执行指令，每条单独一行 |
| `-> 选项` | 选项，所属分支必须缩进；建议统一四个空格 |

manosaba 的 `main.yarnproject` 通过 `sourceFiles: ["**/*.yarn"]` 收集同目录及子目录脚本，基础语言为 `zh-Hans`。新文件应放入所使用的 Yarn Project 收集范围；放进仓库的其他目录不会自动加入该项目。入口节点由宿主启动配置选择，`Start` 是现有 demo 使用的名称。

## 2. 台词、换行、样式与语音

```yarn
这是一句没有姓名栏的旁白。 #line:ch01_narration_001
艾玛: 第一句。[br /]同一条台词里的第二行。 #line:ch01_ema_001
艾玛: 这是[b]重点[/b]，这是[i]斜体[/i]。 #line:ch01_ema_002
艾玛: 这句使用指定语音。 #line:ch01_ema_003 #voice:ema/greeting
艾玛: 这句明确不播放语音。 #line:ch01_ema_004 #voice:none
```

- `姓名: 正文` 使用半角冒号。姓名是对话显示文本，不会自动创建或显示同名 Character；立绘必须单独用 `character` 控制。
- 一条正文是一条对话。需要同一条内换行时使用 `[br /]`，不要拆成两个物理行。
- 默认呈现支持 `[b]`、`[i]`、`[u]`、`[s]` 的成对样式。其他 markup 要由宿主另行支持，不能把任意 Godot BBCode 都当作现有脚本功能。
- 正式内容为台词和选项显式分配唯一且稳定的 `#line:` ID。修改文字或移动台词时保留 ID；新增内容使用新 ID。它关联文本定位、语音、已读及恢复等功能。
- 默认语音 ID 是去掉 `line:` 前缀后的行 ID；`#voice:资源ID` 覆盖这个选择，`#voice:none` 禁用。每行最多一个 voice 标记。资源会按 Voice locale 与回退链查找，缺失时可以只显示文本。
- 下一条台词开始时会停止上一条语音。因此，随台词播放优先使用 `#voice:`，不要在台词前另写一个异步 `voice` 命令。

## 3. 变量、判断和流程

```yarn
title: Chapter01_Investigation
---
<<declare $has_key = false>>
<<declare $trust = 0>>
<<declare $visitor = "调查员">>

艾玛: 你好，{$visitor}。 #line:ch01_investigation_001
-> 拾起钥匙 #line:ch01_pick_key
    <<set $has_key = true>>
    <<set $trust = $trust + 1>>
-> 暂时离开 #line:ch01_leave_key
    <<set $trust = $trust - 1>>

<<if $has_key>>
    门锁打开了。 #line:ch01_door_open
<<elseif $trust > 0>>
    或许可以请人帮忙。 #line:ch01_door_help
<<else>>
    门仍然锁着。 #line:ch01_door_locked
<<endif>>
===
```

`declare` 声明变量及初始值；需要在流程中重新赋值时使用 `set`。变量属于 Yarn 会话，不局限于声明它的节点；不要把 `declare` 当作每次进入节点都会执行的重置。保持同一变量的类型一致，字符串用双引号，布尔值用 `true` / `false`。

| 指令 | 行为 |
| --- | --- |
| `<<jump NodeName>>` | 转到目标节点，不返回当前节点 |
| `<<detour Subroutine>>` | 调用子节点，结束后返回调用点继续 |
| `<<return>>` | 从 detour 提前返回 |
| `<<stop>>` | 停止当前 Yarn 对话流程 |
| `<<wait 0.5>>` | 固定等待 0.5 秒；这是 Yarn 默认指令 |

节点末尾不会按文件顺序自动运行下一节点。需要继续剧情时显式 `jump`；普通节点结束、`stop` 和 detour 返回都不等于 Majo 场景清场。

## 4. Majo 命令的参数规则

```yarn
<<character hero appearance:calm at:left duration:0.3 wait:true>>
```

这里 `character` 是命令，`hero` 是主参数，其余为 `名称:值`。子命令写成 `character move hero`，不要写成 `character hero move`。

- 参数名与标点按示例使用半角字符；不要写 `wait：true` 或 `duration=0.3`。
- 一个 `名称:值` 保持为一个参数，不在冒号后加空格；坐标写 `25,50`。
- 数值用小数点，时长单位为秒；布尔参数明确写 `wait:true`，不要省略值。
- 只使用该命令声明的参数。`via`、`ease`、`duration`、`wait` 并非所有命令都支持。
- 资源 ID 按当前 demo 的资源配置填写，大小写与目录保持一致。本文中 `hero`、`calm`、`backdrop/courtroom`、`demo-theme` 等来自 demo-01；其他示意 ID 需要替换为本项目真实资源。
- 结构声明（节点、scene、Stage/View、mark）优先使用字面量，便于编译器建立符号和资源信息。

## 5. 背景、角色与物件

### 背景

```yarn
<<backdrop backdrop/courtroom>>
<<backdrop backdrop/spotlight via:dissolve duration:0.5 wait:true>>
```

`backdrop <资源ID>` 默认操作 `stage:main`。内建默认转场是 `cut`；需要交叉淡化时显式使用 `via:dissolve`。背景资源 ID 相对于配置的背景资源路径解析。

```yarn
<<backdrop Court_Trial00 at:0,100 origin:0,0 scale:1 distance:far>>
```

指定 `at`、`origin`、`scale` 中任一个，就使用纹理原始像素尺寸进行空间摆放；省略全部三个参数仍使用原有 Cover 行为。默认 `at:50,50 origin:0.5,0.5 scale:1`。`at` 是舞台坐标（X 向右、Y 向上），支持 mark；`origin` 是纹理内锚点（0..1，X 向右、Y 向下）；`scale` 是正数，作用于纹理像素尺寸。舞台 `0..100` 对应固定参考尺寸，不是背景外接框，也不是位置限制。`distance` 是背景相对主体平面的命名距离，内建 `flat/near/far` 分别为 `1/2/6`；默认 `flat`。它只影响 Dolly 下的视差，不会在中性机位直接缩小贴图。

拼接、程序绘图等资源生成由开发者通过[场景资源准备接口](scene-resources.md)完成，不增加 backdrop 特有的 repeat 参数或额外配置 schema。


### 显示与更换外观

```yarn
<<character hero appearance:calm at:left via:fade duration:0.3 wait:true>>
<<character hero appearance:determined via:dissolve duration:0.2 wait:true>>
<<character hide hero duration:0.3 wait:true>>
<<character hero>>
```

`character <id>` 确保角色存在并显示。只有 ID 也合法：已显示时保持现状，隐藏后再次显示时保留原有逻辑字段并重建呈现。`hide` 保留角色状态，不等于删除定义。

| 当前操作 | 内建默认 | 可用 `via` |
| --- | --- | --- |
| 新建 / 重新显示 Character 或 Object | `fade` | `fade`、`cut` |
| 已显示实例更换 `appearance` | `dissolve` | `dissolve`、`cut` |
| 已显示实例仅设置空间字段或 `z` | `cut` | `cut` |
| `character hide` / `object hide` | `fade` | `fade`、`cut` |

项目可配置部分默认值，明确的演出需求应写出 `via` 和 `duration`。更换外观只混合图像；同一条中同时写 `at` / `scale` / `rotation` 会立即设置空间状态，不会顺便产生移动。

### 移动

```yarn
<<character move hero at:right scale:1.1 rotation:5 duration:0.6 ease:cubic-out wait:true>>
```

`character move` / `object move` 至少指定 `at`、`scale`、`rotation` 中一个。只允许操作已存在且可见的实例；不接受 `appearance`、`asset`、`z` 或 `via`。

位置、缩放、旋转共用一个运动槽。后来的运动会接替前一个，未指定的空间字段会冻结在接替时的基础姿态；不要假设两条 move 会自动合并。单独改 `z` 不会打断运动。

### 坐标与站位

`at:x,y` 使用 Stage 百分比坐标：X 向右、Y 向上，允许有限的画外坐标。`scale` 为正数（命令下限 0.001），`rotation` 为角度；`z` 为 Godot 支持范围内的整数。

内建站位为 `left = 25,50`、`center = 50,50`、`right = 75,50`，项目可以覆盖默认站位。角色保留自己的落脚点偏移，Object 不继承角色偏移，因此相同坐标不一定对应相同的图像边缘。

```yarn
<<mark doorway at:85,50>>
<<character move hero at:doorway duration:0.4 wait:true>>
```

自定义 mark 属于当前节点；建议集中写在节点开头，并使用明确的坐标值。

### 物件

```yarn
<<object knife1 asset:evidence/knife at:60,30 rotation:15 z:100 wait:true>>
<<object knife2 asset:evidence/knife at:80,30 wait:true>>
<<object move knife1 at:65,35 duration:0.3 wait:true>>
<<object hide knife1 wait:true>>
```

首次创建必须提供 `asset`。`knife1` 是实例 ID，`evidence/knife` 是定义资源 ID，因此同一定义可创建多个实例。已有实例省略 `asset` 时保留原资源，不能通过同一 ID 切换 asset 或 Character/Object 类型。`move`、`fx`、`hide` 是保留实例词。

Object 需要真实的定义 JSON；资源布局和 `Default` 外观要求见 [Object 定义](direction-command-api.md#object-定义)。

## 6. 等待与并行演出

### 单条命令

多数演出命令默认 `wait:false`：等提交完成就继续，画面可能仍在动画中。`wait:true` 等这条动作结束，包括被后续动作接替的情况，并不等待整个场景里所有动画。

```yarn
<<character move hero at:right duration:0.6 wait:true>>
艾玛: 我到了。 #line:timing_arrived
```

如果要边走边说，改成 `wait:false`。如果要动作完成后再说，使用 `wait:true`；只有刻意留白时才使用 `<<wait 秒数>>`，避免用固定时间猜动画是否完成。

### Beat：一组同时提交的演出

```yarn
// hero 必须在进入这个 beat 前已经存在并可见。
<<beat begin duration:0.5 ease:cubic-out>>
<<character hero appearance:determined>>
<<character move hero at:right>>
<<camera move center:60,50 zoom:1.1>>
<<beat end wait:true>>
艾玛: 这三项演出已经完成。 #line:timing_beat_done
```

`beat begin` 开始收集演出，`beat end` 一次提交，组内动作并行执行。`beat end` 默认 `wait:true`，会等待整组收束。

- Beat 不能嵌套，也不能开放着跨节点。每个分支必须正确 `end` 或 `abort`。
- Beat 内保持连续的演出命令，不夹台词、选项、跳转或固定等待。
- 子命令不要写 `wait:true`；统一在 `beat end` 或后续 `beat join` 等待。
- 同一实例可以组合外观、move、FX、hide，但同一动画槽不能重复写入。
- 创建实例与它的 move 不能放进同一个 Beat；先创建并提交，再开始下一组移动。创建与 FX 可以同组。
- 音频命令独立执行，不会因为放进 Beat 就被收集为同一组，也不继承其时长。将音频写在 Beat 前后。

需要先继续剧情、稍后再等这一组时，为 Beat 命名：

```yarn
<<beat begin id:hero_exit duration:0.8>>
<<character hide hero>>
<<beat end wait:false>>
脚步声渐渐远去。 #line:timing_exit
<<beat join hero_exit>>
```

| 命令 | 用途 |
| --- | --- |
| `beat begin id:名称 duration:秒 ease:曲线` | 开始收集；三个参数均可省略 |
| `beat end wait:true` | 提交并等待，默认等待 |
| `beat end wait:false` | 提交后继续 |
| `beat join 名称` | 等待已提交的指定 Beat |
| `beat finish 名称` | 强制完成已提交的 Beat，应用终态，不是回滚 |
| `beat abort` | 丢弃尚未提交的开放 Beat，不是取消已提交动画 |

通常 `duration` 优先级为：命令显式值 → Beat 默认 → 项目 / 内建默认。`ease` 同样优先使用命令，其次 Beat，然后项目默认，例如 `linear`、`cubic-out`、`back-out`。`cut` 的有效时长始终为零，不要显式给它正时长。

Curtain 是例外：当前实现省略 `duration` 时固定用 0.35 秒、线性变化，不继承 Beat 的 duration/ease；它也不接受 `via` 或 `ease` 参数。Actor/Backdrop FX 接受时长但不接受 `ease` 参数。

## 7. 镜头与临时效果

```yarn
// 直接设置镜头。
<<camera center:50,50 zoom:1 rotation:0 dolly:neutral>>
// 镜头连续移动。
<<camera move center:65,50 zoom:1.5 dolly:near duration:0.8 wait:true>>
// 混合到另一镜头构图。
<<camera center:30,50 zoom:1.2 via:dissolve duration:0.4 wait:true>>
<<camera reset>>
```

`camera` 至少指定 `preset`、`center`、`zoom`、`rotation`、`dolly`、`as` 之一；内建默认 `cut`，可用 `dissolve`。`camera move` 至少指定 preset/center/zoom/rotation/dolly 之一，不接受 `via`。Dolly 是绝对机位：内建 `neutral/near/back` 分别为 `0/0.15/-0.25`，重复 `back` 不会累积。`camera reset` 回到中心 `50,50`、zoom `1`、rotation `0`、dolly `neutral`，不修改视差或 idle。

`stage:名称` 控制该 Stage 默认 View 的镜头；`view:名称` 控制指定 View。两者互斥，都省略时目标是 `main` View。

```yarn
<<mark witness at:250,50>>
<<camera preset define close_base zoom:0.7826087 dolly:0.7826087 parallax:strong parallaxLimit:2,50>>
<<camera preset define closeup extends:close_base center:witness parallaxAnchor:250,46>>
// 定义不改变画面；同一预设可以用于不同 View。
<<camera preset:closeup>>
<<camera view:reaction preset:closeup via:dissolve duration:0.3 wait:true>>
<<camera move preset:closeup zoom:1.6 duration:0.8 wait:true>>
```

`camera preset define` 一次定义完整构图，包括背景视差；没有基类时省略字段默认 center `50,50`、zoom `1`、rotation `0`、dolly `neutral`、parallax `off`。`extends:名称` 继承当前节点的基础预设，显式字段覆盖基类，支持多级继承和前向引用，循环和不存在的基类会在进入节点时拒绝。定义只保存数据，不改变画面。所有定义均写在 Yarn 中，不需要额外预设文件或 C# 注册。

`parallax` 可选内置 `off/subtle/normal/strong`；少量特殊镜头可在定义中覆盖 `parallaxStrength`（0–1）、`parallaxAnchor`（坐标或 mark）和 `parallaxLimit`（非负百分比坐标）。先应用继承，再应用 parallax 档位，最后应用显式高级字段。`dolly` 支持内置名称及 `[-2,0.85]` 内的数值机位。典型用法是把高级数值集中在基础预设，各人物只补充位置。数值求解仍属于高级创作，本版不自动求解取景目标。

应用 preset 时，机位和视差以同一事务、同一时长和缓动更新，不必再调用 `camera parallax`。使用处的显式 center/zoom/rotation/dolly 覆盖预设字段；View、转场、时长与缓动仍在使用处指定。`center` 可引用 mark，也可使用超出 `0..100` 的有限坐标。坐标始终相对于固定舞台参考尺寸，背景变宽不会重新归一化角色或镜头坐标。普通 camera/move 未指定 preset 时继续保留未写的字段；idle 是独立持续效果，不由镜头预设启停。

兼容性：旧镜头预设没有视差声明时现在明确应用 off，不再继承 View 上一次的视差；需要保留效果的场景应在预设内声明 parallax。直接调用 `camera parallax` 仍然可用，但后续完整 preset 会覆盖它。

预设属于当前节点，声明参数必须是静态字面量或 mark 名称。编译元数据保存声明，直接读档时不必重新执行节点开头。旧的 `camera ... as:` 与 `shot use` 保留兼容；`as` 会同时应用镜头，而新预设声明不会。

背景视差和静止漂移是每个 View 独立保存的环境状态：

```yarn
<<camera parallax subtle view:main>>
<<camera parallax off view:main duration:0.4>>
<<camera parallax reset view:main>>
<<camera idle gentle view:main>>
<<camera idle off view:main duration:0.5>>
```

视差内建 `off/subtle/normal/strong`，默认 off；idle 内建 `off/gentle`，默认 off。两者支持 `stage/view`、`duration/ease/wait`。视差省略 duration 时立即提交；idle 省略时使用 0.5 秒启停包络，显式 `duration:0` 才立即切换。构图、视差和 idle 可以并行使用不同持续时间，更新其中一项不会打断其他项。开启视差不会自动开启 idle；关闭视差也不会停止 idle。持续 idle 不阻塞 Beat，`wait:true` 只等待启停包络收敛。项目可在 `CameraEnvironment` 配置中注册同类型命名预设；连续参数中距离范围为 `[1,100]`、Dolly 为 `[-2,0.85]`、视差强度为 `[0,1]`，anchor/limit/amplitude 均使用固定参考画面的百分比。

```yarn
<<character fx hero effect:shake strength:8 axis:x frequency:20 duration:0.3 wait:true>>
<<object fx knife2 effect:shake duration:0.3 wait:true>>
<<backdrop fx shake strength:6 duration:0.3 wait:true>>
<<camera fx shake strength:8 duration:0.3 wait:true>>
```

Character/Object 的效果通过 `effect:shake` 指定；Backdrop/Camera 的效果名是主参数。FX 是临时偏移，不修改基础站位。隐藏实例不能新发 move/fx。

Actor/Backdrop shake 默认 duration `0.4`、strength `8`、frequency `20`、axis `all`；axis 支持 `all/x/y/diagonal`，frequency 为正整数。Actor/Backdrop strength 是 Stage 参考像素，会受镜头 zoom 放大；Camera 位移 strength 是 View viewport 像素，不能按相同数值比较视觉幅度。Reduce Motion 模式可能保留时长而不施加运动。

## 8. 场景边界与黑幕转场

`scene:` 放在节点头部，表示进入正文前先开始一个新场景。普通 `jump`、选项分支或子程序无需重复声明 scene。

进入 scene 会结束旧场景动作、停止全部 BGM、清理内容和额外 Stage/View，恢复空的 main 与 single/main 布局。Curtain 的状态及正在进行的操作保留。即使正常重入同一入口，也会重新清场；同一 Yarn Project 的 scene ID 只能声明一次，不能为空。

系统不会自动为 scene 加黑幕。要让清场不可见，在旧场景先完全关幕，再跳到新场景，在新正文布置后开幕：

```yarn
title: Room_Exit
---
<<curtain closed duration:0.4 wait:true>>
<<jump Hall_Entry>>
===

title: Hall_Entry
scene: hall
---
<<backdrop backdrop/courtroom via:cut>>
<<character hero appearance:calm at:center via:cut>>
<<bgm demo-theme>>
<<curtain open duration:0.4 wait:true>>
艾玛: 到了。 #line:hall_entry_001
===
```

scene 清场发生在目标节点正文之前，因此只在目标正文第一行关幕，不能遮住已经发生的清理。若目标也可从标题画面直接进入，要由入口流程或宿主保证初始幕状态。

`curtain closed` 为不透明，`curtain open` 为透明；默认黑色，可用 `r:1 g:1 b:1` 指定白色，颜色分量取 0～1。

`stage clear main` 只清该 Stage 的背景、Actor（含隐藏实例）及相应 FX，保留 tint、Camera、布局、BGM、Curtain。需要真正换场时使用 scene 入口；只清部分画面时使用 stage clear。

## 9. 音频与存档

```yarn
<<bgm demo-theme track:music loop:true volume:0.8 duration:0.5 wait:true>>
<<sfx demo-chime instance:bell waitPlayback:true>>
<<stopBgm track:music duration:0.5 wait:true>>
<<stopSfx instance:bell>>
<<voice narrator-welcome wait:true>>
<<stopVoice>>
```

| 命令 | 主要参数与等待语义 |
| --- | --- |
| `bgm <clip>` | `track`、`loop`（默认 true）、`volume`（默认 1）、`duration`、`wait`；wait 等淡入/切换收束，不等整曲 |
| `stopBgm` | `track`、`duration`、`wait`；省略 track 停止全部 BGM，wait 等停止收束 |
| `sfx <clip>` | `instance`、`loop`（默认 false）、`volume`（默认 1）、`duration`、`wait`、`waitPlayback`；wait 等淡入，waitPlayback 等非循环音效播放结束 |
| `stopSfx` | `instance`、`duration`、`wait`；省略 instance 停止全部 SFX |
| `voice <clip>` | `volume`（默认 1）、`duration`、`wait`；wait 等语音播放结束 |
| `stopVoice` | 无参数，停止当前语音 |
| `save` | 无参数，请求一个 Quick Save 候选 |
| `autoSave` | 无参数，请求一个 Auto Save 候选 |

音频中的 `duration` 是淡入淡出时长，不是截取或播放时长。上述可选 `wait` 默认 false。循环 SFX 不会因为 `waitPlayback:true` 而等到循环结束；需要用 `stopSfx` 明确停止。

`save` / `autoSave` 需要宿主启用存档服务。它们登记候选请求，由存档系统在符合条件的边界处理，不保证命令返回时文件已落盘。不要给它们添加 slot 或 wait 参数。

## 10. 多舞台与多视图（进阶）

Stage 保存背景和实例，View 是观察 Stage 的镜头，screen 决定哪些 View 出现在屏幕上。默认只有 main；同一 Stage 可被多个 View 观察，同名 Actor 可分别存在于不同 Stage。

```yarn
<<stage define broadcast>>
<<backdrop backdrop/spotlight stage:broadcast>>
<<character hero stage:broadcast appearance:determined at:center wait:true>>
<<camera stage:broadcast center:50,50 zoom:1>>
<<screen show broadcast via:cut wait:true>>

<<view define reaction stage:main>>
<<camera view:reaction center:65,50 zoom:1.8>>
<<screen split left:broadcast right:reaction via:cut wait:true>>
<<screen show main via:cut wait:true>>
```

`stage define <名称>` 同时创建其默认 View；`view define <名称> stage:<名称>` 创建额外 View。`screen show` 切换到 single 布局，并不修改以后命令的默认目标：不写 `stage` 的 Character/Backdrop 仍然作用于 main。`screen show <view>` 全屏显示指定 View，退出分屏或画中画，但保留镜头取景和正在进行的运镜。`screen split` 的 left/right 和 `screen pip` 的 background/inset 参数绑定的是 View；本节点 Stage 名也可用于引用该 Stage 默认 View。

所有 `screen` 命令立即切换屏幕组合，不定义可复用布局；省略 `wait` 默认 false。`camera` 只调整指定镜头，不切换屏幕；省略目标仍操作 main，不跟随当前显示的 View。屏幕切换的 `via/duration/wait` 与 `camera move` 的运动时序相互独立，可通过 beat 同时提交。

```yarn
<<screen show main>>
<<screen pip background:main inset:reaction via:dissolve duration:0.2>>
```

分屏支持独立的裁切边界与画面摆放：

```yarn
<<screen split left:broadcast right:reaction divider:35 angle:10 leftAt:20,50 rightAt:55,50 via:dissolve duration:0.2 wait:true>>
```

- `divider`：分割线与屏幕水平中线的交点 X，范围 0..100，默认 50。
- `angle`：从竖直线顺时针旋转的角度，范围严格大于 -90、小于 90，默认 0。正角度时上端偏右、下端偏左；角度按屏幕像素计算，不随宽高比变形。
- `leftAt / rightAt`：把对应 camera 的取景中心放在整个屏幕的指定位置。使用屏幕百分比坐标，X 向右、Y 向上；支持有限的屏幕外位置，不支持舞台 mark。省略时分别位于左右区域中心，即 `(divider / 2, 50)` 和 `((100 + divider) / 2, 50)`。

left 显示分割线左侧，right 显示右侧，两侧使用互补裁切。布局只平移镜头画面并裁切，不旋转画面，也不因为分屏宽度改变 zoom；画面旋转仍由 camera rotation 控制。摆放太远导致源镜头画面不能覆盖可见区域时，区域显示黑色，需要调整摆放或取景。

这些参数只适用于 `screen split`，每条 screen 命令使用完整声明及默认值，不继承上一条的省略字段。参数进入存档与回滚状态；旧存档缺失这些字段时仍为原有五五分屏。`via:dissolve` 混合前后布局。使用 `screen show` 或 `screen pip` 会清除分屏配置。

main 是公共名称，其他 Stage/View 符号属于定义节点。跳到其他普通节点并不会删除画面，但新节点的同名局部符号不自动指向旧节点实例。自定义 mark、shot 别名也不应当作全局名称使用。

**当前跨节点 import 限制：** 编译器识别 `<<import stage news from Broadcast:broadcast>>`，运行时命令声明却接受 `<<import stage alias:news source:Broadcast:broadcast>>`，目前两端写法不一致。本文不将 import 作为可复制的跨节点方案；需要共享演出时优先使用 main，或把额外 Stage/View 的控制放在同一节点，待接口统一并验证后再使用跨节点 import。

## 11. 一段可直接试跑的演出

以下示例使用 demo-01 已有资源，可追加到它的 `assets/scripts/main.yarn`，再让入口跳到 `WritingGuide_Opening`。demo-01 当前只收集 `main.yarn`；若另建文件，须同步将文件加入其 `.yarnproject` 的 `sourceFiles`。在其他 demo 中使用前替换资源 ID；重复复制时需更换 title、scene 和 line ID。

```yarn
title: WritingGuide_Opening
scene: writing_guide_demo
---
<<curtain closed duration:0>>
<<backdrop backdrop/courtroom via:cut>>
<<character hero appearance:calm at:left via:cut>>
<<bgm demo-theme track:music duration:0.3>>
<<curtain open duration:0.4 wait:true>>

Maya: 我们开始吧。 #line:writing_guide_001 #voice:none

<<beat begin duration:0.5 ease:cubic-out>>
<<character hero appearance:determined>>
<<character move hero at:center>>
<<camera move center:55,50 zoom:1.1>>
<<beat end>>

-> 继续询问 #line:writing_guide_option_ask
    <<character fx hero effect:shake strength:4 duration:0.2 wait:true>>
    Maya: 还有一个细节。 #line:writing_guide_ask #voice:none
-> 听一听提示音 #line:writing_guide_option_listen
    <<sfx demo-chime waitPlayback:true>>
    Maya: 听到了。 #line:writing_guide_listen #voice:none

<<camera reset via:dissolve duration:0.3 wait:true>>
<<character hide hero duration:0.3 wait:true>>
<<stopBgm track:music duration:0.3 wait:true>>
<<curtain closed duration:0.4 wait:true>>
===
```

## 12. 排错与交付

| 现象 | 检查方向 |
| --- | --- |
| 台词先出现，动画还没结束 | 单条补 wait:true；并行动画在 beat end 等待 |
| 写了 duration 但直接跳到位置 | 状态设置不负责移动，改用 character/object/camera move |
| 提示 cut 与时长冲突 | 显式设置兼容的 fade/dissolve，或删除正 duration |
| 更换外观时报 fade 不支持 | 已显示角色换外观用 dissolve/cut；入场用 fade/cut |
| 同 Beat 创建后 move 失败 | 把创建先提交，再另起一组移动 |
| 切节点后立绘/BGM 还在 | 普通节点保留场景；新场景入口加唯一 scene |
| 新场景出现空白闪帧 | 在旧节点关幕并等完成后再跳转 |
| screen show 后角色出现在其他画面 | 命令默认目标仍是 main，显式写 stage |
| 有台词但没有立绘 | 姓名栏不控制角色显示，检查 character 命令与资源 |
| 有台词但没有语音 | 检查 line ID、voice 标记、资源 ID 和 Voice locale |
| 脚本继续了但某条命令没效果 | 检查 command 诊断；可恢复内容错误在非严格模式下可能被跳过 |

编写后检查：节点/scene/line ID 无重复，所有分支能汇合或跳转，Beat 闭合，资源存在，动画前后等待符合节奏。使用 Godot 导入脚本并检查诊断，再从实际入口播放、逐个选择分支；涉及恢复的演出另外确认存档加载后的画面与台词。

维护本文时应对照以下实现，优先采用实际注册契约及运行时校验，而不是 archive 里的旧设计：

- [Direction 命令](../addons/majo_runtime/Runtime/Dialogue/Commands/Direction/DirectionCommandModule.cs)、[转场规则](../addons/majo_runtime/Runtime/Dialogue/Commands/Policies/DirectionCommandPolicy.cs)
- [Audio 命令](../addons/majo_runtime/Runtime/Dialogue/Commands/Audio/)、[语音 ID 解析](../addons/majo_runtime/Runtime/Audio/DialogueVoiceResolver.cs)
- [节点元数据编译](../addons/majo_runtime/Runtime/Dialogue/Yarn/NodeDirectionCompiler.cs)、[符号作用域](../addons/majo_runtime/Runtime/Dialogue/Commands/DirectionScopeResolver.cs)
- [存档命令](../addons/majo_runtime/Runtime/Dialogue/Commands/BuiltIn/SaveCommands.cs)、[manosaba 命令](../demos/manosaba/ManosabaCommands.cs)
