# Direction Command API 与场景生命周期重构实施方案

状态：已实施并通过最终验收；实际接口见 `direction-command-api.md`，测试记录见 `direction-command-api-refactor-progress.md`。

日期：2026-09-08。

范围：Camera、Character、Object、Backdrop FX、Stage/View 生命周期、Yarn 场景入口及相关执行契约。

本文以当前代码为实施基线。第 1 节是讨论中已确认的产品要求；标为“本版实现选择”的内容用于消除落地歧义，不代表现有实现已经支持。本文取代本轮范围内旧命令设计，`docs/archive/` 仅作为历史资料，不应重新引入已删除的接口。

## 1. 已确认要求与范围

1. 裸命令声明目标状态，`move` 专门声明连续空间运动，`fx` 声明临时效果。
2. `camera pose` 改为 `camera`；移除裸状态命令的 `via:move` 和根据 `duration` 推断 Move 的行为。
3. `via` 保留，dissolve 等非运动过渡继续可用；不是把裸命令全部变为瞬时赋值。
4. 保留 `character <id>` 的创建、显示、外观更新和静态摆放能力，不增加必需的 show/appearance/pose 子命令。
5. 增加 `character move`、`character fx`、`character hide`；隐藏角色收到新的 `character` 时显示并更新指定字段。
6. 增加面向普通场景物件的 `object` 命令族。脚本不要求使用底层 `actor` 概念。
7. Camera 与 Actor FX 共享执行和生命周期机制，保留不同的 modifier 类型及实现。Character、Object、Backdrop 按能力共享 Actor FX。
8. Actor 使用空间、外观、可见性三个动画槽，FX 独立叠加。不同槽允许并发，同槽只运行一个动作；位置、缩放、旋转作为一套空间姿态一起接替。场景切换和 skip 将有效运动推进到终态。
9. 用 Yarn node 元数据显式声明场景入口；普通 node、option 分支和汇合延续当前场景。放弃按每个 node 的 scene 归属变化自动清场。
10. Curtain 全局单实例，跨 Stage，场景边界不重置。BGM 默认归场景，边界使用 `stopBgm` 的底层停止路径清理。
11. 不自动添加切场淡入、淡出、遮幕或画面保持；过渡方式由创作者安排。
12. `stage clear` 保留其局部清场语义；场景结束恢复默认空 main，而非要求 main 从有效状态中消失。
13. 不保留旧命令兼容入口。删除 `leave`，内容改用 `character hide`，同时接受从“移除状态”到“隐藏”的行为变化。
14. 不在本轮重新设计 rollback 系统；只完成新增状态与现有恢复路径的必要接入，不破坏已有行为。

非目标：逐属性并行动画与部分接替、Actor 整状态 dissolve、任意过渡中断时无缝接管混合画面、任意屏幕 overlay 系统、场景栈/嵌套场景、跨场持久 Actor/BGM 标志、路径编辑器、逐帧动画回退、无限动态 Actor 的单实例销毁 API、资源管理器整体重写。

### 1.1 本版实现选择

| 未完全指定的细节 | 本文采用的实现选择 |
| --- | --- |
| 场景入口写法 | 唯一正式写法为 node header `scene: <id>`；表示入口动作，不表示归属；不同时提供 tag 别名 |
| 公共 release | 从 catalog 移除 `stage release`、`view release`，内部删除能力保留；本轮不增加 object remove |
| 动画槽接替 | 空间从当前基础姿态接替整个动作；显隐从当前透明度接替；外观先立即结束旧混合再开始新混合，允许轻微跳变；同一 beat 重复写同槽报错 |
| Actor 过渡边界 | 空间设置只用 cut，外观用 cut/dissolve，显隐用 cut/fade；不做整状态 dissolve |
| beat | 只收集动作，end 统一提交启动；等待放在 end/join，不在子命令中等待 |
| 呈现结构 | Actor 空间根、FX 根保持稳定，外观更新只替换内部视觉内容；Prepare 准备变更资源，Commit 统一发布 |
| hide 与并发 | 先允许退场、已有运动、FX 并发；退场完成时使剩余基础运动到终态、清除 FX、释放呈现需求 |
| Actor 通用空间字段 | `at`、等比 `scale`、`rotation`；`z` 仅为离散层级，不属于 move |
| Object 定义 | `asset` 引用可复用物件定义，定义选择 factory/资源/锚点；与脚本实例 ID 分离 |
| 首批 Actor FX | 交付 `shake`，Character/Object/Backdrop 共用实现；其他效果不作为本轮完成条件 |
| Actor FX 参数形式 | 使用 `character fx <id> effect:<name>` / `object fx <id> effect:<name>`；现有 binder 仅允许一个 Primary，不为此扩大通用绑定器范围 |

这些选择直接用于实施和验收；如调整，应先更新本文契约，不在多个 backend 中各自选择不同语义。

## 2. 当前实现与改动定位

以下路径相对仓库根目录；本节记录源码事实，不把历史方案当成已实现能力。

| 位置 | 当前情况 | 重构动作 |
| --- | --- | --- |
| `addons/majo_runtime/Runtime/Dialogue/Commands/Direction/CameraCommands.cs` | 已有 camera move；camera pose/reset 仍使用混合过渡策略；文件还包含 View 命令 | 新裸 camera；收紧 reset；按职责拆分文件 |
| `addons/majo_runtime/Runtime/Dialogue/Commands/Policies/DirectionCommandPolicy.cs` | 显式 duration、未传 via 时可自动选择 Move；defaults 含 pose/leave 名称 | 删除推断；迁移配置名；区分状态过渡与运动策略 |
| `addons/majo_runtime/Runtime/Dialogue/Commands/Direction/ActorCommands.cs` | character 带 AtLeastOne，不能真正只写 ID；leave 提交 ActorLeavePatch | 允许裸 ID；增加 hide/move/fx；拆出 object |
| `addons/majo_runtime/Runtime/Dialogue/Commands/Core/BoundCommand.cs` | RawCommandArguments 和绑定过程只有一个 Primary；契约校验拒绝第二个 | Actor ID 为 Primary，效果名为必需的命名参数 effect；不绕过 schema 手动拆参数 |
| `addons/majo_runtime/Runtime/Direction/Core/DirectionState.cs` | ActorState 没有 Visible、类型/定义引用、rotation；main Stage/View 必须存在 | 扩展逻辑状态，保持 main 不变量 |
| `addons/majo_runtime/Runtime/Direction/Execution/DirectionPlanner.cs` | ActorPatch 可创建；leave 删除状态；stage clear 清背景/Actors | 显示、隐藏与移除分离；按动画槽生成执行意图 |
| `addons/majo_runtime/Runtime/Direction/Godot/GodotDirectionActorTreeBackend.cs` | Prepare 重建 Actor tree；固定 Character factory/ICharacterActor；每 Actor 仅一种 transition spec；Move 主要改变位置 | 泛化呈现能力；保留准备原子性；改为稳定根和增量视觉发布，移除常规外观更新的整树重建 |
| `addons/majo_runtime/Runtime/Direction/Godot/GodotDirectionStageBackend.cs` | Direction backdrop 有独立的纹理准备和过渡路径 | 为 backdrop slot 接入 Actor 视觉能力，不再建立第二份背景状态 |
| `addons/majo_runtime/Runtime/Direction/Camera/` | 已有 CameraPoseDelta、registry、modifier manager 和内建效果 | 保留类型；提取可共享执行机制，增加 Actor modifier |
| `addons/majo_runtime/Runtime/Dialogue/Yarn/NodeDirectionCompiler.cs` | 识别 title 和 Direction 命令，生成 node-local symbols、imports、marks | 解析入口元数据；新增命令与资源引用分析 |
| `addons/majo_runtime/Runtime/Dialogue/Yarn/YarnDialogueBackend.cs` | HandleNodeStarted 同步应用模块和发事件 | 接入可等待的 node 正文前屏障 |
| `addons/YarnSpinner-Godot/Runtime/DialogueRunner.cs` | NodeStartHandler 同步；命令执行可异步等待 | 不能用 async void NodeStarted 假装暂停 VM；采用第 5 节编译前导方案 |
| `addons/majo_runtime/Runtime/Direction/Godot/GodotDirectionRenderHost.cs` | ResetAsync 会 PublishEffects(Default)，重置 Curtain | 新场景清理路径保留全局 Curtain，不能直接调用该 reset |
| `addons/majo_runtime/Runtime/Audio/AudioManager.cs` | StopBgmAsync 加载/播放路径外有独立异步协调 | 场景 owner、待加载结果失效与停止接口接入 |

## 3. 新脚本 API

### 3.1 命令矩阵

| 命令 | 字段/定位 | 语义 |
| --- | --- | --- |
| `camera` | `view` 或 `stage`；center/zoom/rotation/as；via/duration/ease/wait | 设置构图；默认 main View |
| `camera move` | 同样的目标定位；center/zoom/rotation；duration/ease/wait | 仅运动；无 via |
| `camera reset` | view 或 stage；via/duration/ease/wait | 回默认构图的状态过渡；不隐式运动 |
| `camera fx <effect>` | view 或 stage；效果参数及 duration/wait | 当前 View 的临时 modifier |
| `character <id>` | stage；appearance/at/scale/rotation/z；via/duration/ease/wait | 确保存在、可见，更新指定字段 |
| `character move <id>` | stage；at/scale/rotation；duration/ease/wait | 操作已有且可见的角色，不创建、不换外观 |
| `character fx <id> effect:<name>` | stage；效果参数及 duration/wait | 操作已有且可见的角色 |
| `character hide <id>` | stage；via/duration/ease/wait | 改为隐藏，保留逻辑状态 |
| `object <id>` | stage；asset/appearance/at/scale/rotation/z；via/duration/ease/wait | 确保物件存在且可见 |
| `object move/fx/hide` | 与 character 对应子命令相同 | 校验 Object 类型和所需能力 |
| `backdrop <resource>` | 保留现有定位和过渡参数 | 设置 Stage 的背景 slot |
| `backdrop fx <effect>` | stage；效果参数及 duration/wait | 只影响该背景，不影响整个镜头 |
| `stage define/show/clear` | 保留现有定位 | 定义舞台、全屏展示默认 View、清空舞台内容 |
| `view define`、`layout` | 保留现有参数 | 定义 View，组合画面 |
| `shot use` | 保留定位、别名和 via/timing | 应用构图状态，不接受 Move |

不增加 character show/appearance/pose、公共 actor 命令、backdrop move。Mark/import 保持现有名字作用域，不因为 scene 的增加自动全局化。

### 3.2 绑定与默认行为

- Camera `stage` 与 `view` 互斥；Stage 只是其默认 View 的快捷定位，不改变 View 的 Stage 绑定。
- 裸 camera 至少修改一个构图字段或使用现有 `as` 保存入口；空 camera 报错。`as` 为别名能力，本轮不重新设计 shot 系统；必须消除它对旧 pose 命令名称的依赖。
- `character Ema` 合法：不存在时按角色定义创建；隐藏时显示；已显示时保持现状，不接替正在进行的动画。首次创建使用定义默认值；未写空间字段时保持空间槽不变，写入空间字段时省略的空间分量按第 6 节保留当前基础呈现值。其他省略字段保留旧值。
- `(StageId, ActorId)` 为 Stage 内共享实例命名空间。Character/Object 同 ID 不能隐式切换类型，必须报错。
- Object 首次出现必须给 `asset`；例如 knife 是实例，evidence/knife 是物件定义。同一 asset 可创建 knife1、knife2。已有实例省略 asset 时保留；传入不同定义报错，本轮不支持运行中更换 factory。
- Object 定义默认包含一个 `Default` appearance；可用 appearance 切换同一定义下的视觉内容。新增 `ObjectsPath`、Object factory/定义解析和预加载分析。
- hide 未知 ID 报错；已完成隐藏的对象再次 hide 是无副作用的立即完成。退场中的重复 hide 不重新启动，wait:true 等现有退场完成。独立命令对逻辑上隐藏（含退场中）的目标新发 move/fx 报错；同一 beat 的起点校验见第 6 节。
- beat 外，本节命令矩阵中的动画命令默认 `wait:false`，等提交后返回；`wait:true` 等当前命令的动作结束（完成或被接替），不等待目标上其他动作。beat 内命令只收集声明，省略 wait 或 wait:false 均不提交、不等待；显式 wait:true 报错并提示改用 `beat end wait:true`。`beat end` 默认 wait:true，wait:false 在统一提交后返回；`beat join` 等指定 beat 的动作结束。
- `duration:0` 允许，立即落到目标并执行相同清理；负数、非有限值报错。move 至少指定一个空间字段。
- Actor move 不接受 appearance、asset、z、via。禁止用 move 完成创建/移除；入场先显式创建，退场使用 hide。
- 带子命令的裸根命令使用 catalog 最长路径匹配。`move/fx/hide` 等子命令词不能作为该命令族的裸实例 ID；在定义/编译时诊断。新增案例覆盖 backdrop 的 resource 与 fx 子命令分派。
- Camera/Backdrop FX 的唯一 Primary 是效果名；Character/Object FX 的唯一 Primary 是实例 ID，`effect` 为必需命名参数。这是对讨论中双位置参数示例的落地调整，不需要保留那个尚未实现的写法。

### 3.3 via 与 defaults

`via` 表达非运动的视觉切换方式；状态命令和项目 defaults 不再接受 Move。只有 move 命令能产生连续空间运动。

| 操作 | 内建默认 | 可用方式 |
| --- | --- | --- |
| camera / camera reset / shot use | cut | cut、dissolve；整个 View 构图属于一个槽，接替规则见第 6 节 |
| 新建或重新显示 character/object | fade | cut、fade；外观与空间先设置，再显示 |
| 已显示 Actor 指定 appearance | dissolve | cut、dissolve；只混合外观内容 |
| 已显示 Actor 只指定空间字段或 z | cut | cut；空间字段立即设置，z 立即改变层级 |
| character/object hide | fade | cut、fade |
| backdrop | 保留既有默认 | 外观 cut/dissolve；新换图立即结束旧混合后开始新混合 |
| stage show、stage clear、layout | 保留既有非运动 defaults | 按对应 domain 校验，不因此扩大 Actor 动画槽支持的过渡集合 |

Actor 命令按提交前状态与指定字段选择过渡：新建/重新显示优先，其次是显式 appearance，最后是纯空间/z。选择不依赖 appearance 是否恰好等于旧值；纯 ID 无操作时不启动过渡。新建/重新显示的 appearance 直接安装，via 只控制显隐；已显示时 appearance 与空间同时出现，via 只控制外观，空间仍立即设置。不支持的 via 在提交前报错，插件注册不能绕过这张表。

时长统一使用显式 duration → beat duration → 对应操作的项目/内建默认值；此顺序适用于 move、非 cut 过渡与 FX，应修正现有 Character 项目时长优先于 beat 的差异。cut 的有效时长为 0，不继承 beat/project 时长；选定 cut（含默认 cut）时，显式正 duration 报冲突。一个命令混合空间 cut 与外观 dissolve 时，duration/ease 只作用于 dissolve。ease 使用显式值 → beat → 项目默认，不改变命令类别。

```yarn
<<camera center:50,50 zoom:1>>
<<camera center:70,50 zoom:2 via:dissolve duration:0.4 wait:true>>
<<camera move center:50,50 zoom:1 rotation:0 duration:0.8 wait:true>>
<<character Ema appearance:normal at:left wait:true>>
// 同一 beat 开始换表情、走动、抖动；等待统一放在 end。
<<beat begin>>
<<character Ema appearance:happy>>
<<character move Ema at:right rotation:5 duration:0.5>>
<<character fx Ema effect:shake strength:8 duration:0.3>>
<<beat end wait:true>>
<<character hide Ema via:fade duration:0.2 wait:true>>
<<character Ema>>
<<object knife asset:evidence/knife at:60,30 z:100>>
<<object fx knife effect:shake duration:0.2>>
<<backdrop fx shake strength:6 duration:0.3>>
```

对已显示角色，`character Ema appearance:happy at:right via:dissolve` 表示位置立即到 right，旧、新表情在 right 溶解切换；不会在左右两处之间溶解。要边走边换表情，分别声明 appearance 与 move。对纯空间设置写 via:dissolve 报错。

## 4. 场景入口与所有权

### 4.1 唯一边界语义

```yarn
title: Interrogation_Start
scene: act01_interrogation
---
<<backdrop interrogation_room>>
<<character Ema appearance:normal at:left>>
<<jump Interrogation_Choice>>
===

title: Interrogation_Choice
---
-> 询问证物
    <<jump Interrogation_Evidence>>
-> 询问证人
    <<jump Interrogation_Witness>>
===

title: Interrogation_Evidence
---
<<character Ema appearance:happy>>
证物在这里。 #line:scene_example_evidence
===

title: Interrogation_Witness
---
<<character Ema appearance:normal>>
先询问证人。 #line:scene_example_witness
===
```

- `scene:` 仅允许在 node header 中出现一次，必须非空；重复、空值在编译期报错。`scene: inherit` 不是特殊语法，普通 node 不写 scene 即延续。
- 场景定义身份为 `(ProjectId, SceneId)`，每次正常进入入口都生成新的运行时代次，即使 SceneId 相同也重启。
- 本版要求一个 project 内 SceneId 对应一个声明入口；重复声明报错。多个跳转路径进入同一入口即可，不引入多个初始化入口语义。
- 普通 node 不声明归属；漏写 scene 不触发推断清场。不能用文件名、node title 前缀或 option 分支分析自动制造边界。
- 没有任何标记也能运行：宿主建立匿名初始演出上下文。调试直接启动内部 node 使用空 main，不自动重放入口正文。
- 跳出某个 node、`jump`、公共子流程返回、一次 Yarn 执行暂时结束均不自动清场。结束完整演出会话由宿主明确调用同一清理服务。
- 开放 beat 不能穿越 node/scene 边界。边界诊断应在旧内容发生破坏性清理前完成。
- 恢复与内部重建使用明确的进入原因 `Normal/Restore/InternalRebuild`，只在 Normal 执行 scene 标记。恢复仅接入既有流程，不扩展其产品能力。

### 4.2 状态所有权表

| 对象 | Owner | 场景边界 |
| --- | --- | --- |
| Stage 内容、Stage tint | 当前演出实例 | 删除旧内容/状态 |
| Character/Object，包括隐藏状态 | Stage | 随 Stage 内容清除 |
| Backdrop slot、slot FX | Stage | 清除 |
| View、Camera pose、Camera FX | 当前场景中的 Stage/View 关系 | 删除额外 View，main 恢复默认构图 |
| Screen layout/bindings | 当前演出实例 | 恢复 single/main |
| 正在运行的 move、Actor/View 非全局过渡与 FX | 所属目标与演出代次 | 完成有效基础运动/过渡，FX 回到 identity，释放 |
| Curtain 状态、节点及其进行中的全局操作 | 全局演出服务 | 不重置、不取消、不重复实例化 |
| BGM 播放、待完成加载 | 当前演出实例 | 等待停止完成，禁止迟到播放 |
| Mark、import、shot 名称绑定 | 现有 node/module 作用域 | 随既有 Exit/Enter 处理；不把模块定义永久删除 |
| 剧情变量、对话位置、非本场拥有的服务 | 原有 owner | 不受场景清理影响 |
| 历史快照、共享资源缓存 | 各自管理器 | 不清历史；只释放本场 lease，缓存按原策略处理 |

本轮不自动把 SFX/voice/UI 新增为 scene-owned；保留其既有生命周期。全局 FX/Curtain 与 scene-owned 操作即使来自同一 beat，也必须能够按 owner 分别清理，不能简单 Finish 所有事务而推进全局 Curtain。

### 4.3 clear、边界清理和内部删除

- `stage clear` 仅清该 Stage 的 backdrop、全部 Actor 逻辑记录（包含 hidden）及这些对象的 FX。保留 Stage tint、View、Camera、布局和其他 Stage；不停止本场 BGM。
- `stage show` 仍是把该 Stage 默认 View 放入 single/main 布局的快捷命令，不表示 Stage 的创建或生命周期开始。
- 场景边界清理所有旧场景实例，建立默认 main 状态；保留 Curtain。不能放宽 State.Validate 让 main 消失。
- main 的空 Viewport/容器可以复用；复用必须改变代次、清理内容/绑定，不等于复用上一场逻辑身份。
- 删除 stage/view 的内部 patch/backend 能力保留，供边界清理、准备失败和 reset 使用。公共 release 不再注册，也不引入同义别名。
- 未受任何 View 显示的 Stage 仍保留逻辑状态直到边界；不因镜头切换推断销毁。

## 5. 可等待的场景进入与清理协议

### 5.1 编译与 VM 屏障

新增 `SceneEntryMetadata` 到 `NodeDirectionModule` 和 companion 序列化，保留入口源位置。编辑器编译、运行时编译/热重载、导出必须使用同一语义和一致的 program fingerprint。

**实施路径：编译阶段在每个 authored node 正文最前插入一个内部可等待前导命令。** 命令调用 node-entry coordinator；无 scene 标记时只完成既有模块进入，有标记时先完成场景边界。该命令不是公共 catalog API，源脚本使用其保留名称应报错。

选此路径的原因：当前 Yarn 的 NodeStartHandler 是同步回调；单独注册 async 事件不能阻止正文中的 `set`、`jump`、台词或 option 继续执行。前导命令必须先于任何正文指令，利用 Runner 已有的 await command 机制阻止 Continue。

实施要求：

1. 新增共享的 Yarn compilation transform；保留原始文件作为诊断源，映射插入产生的行号差异。优先通过 Yarn AST/编译扩展接入；若采用源转换，必须按 node body 边界解析，不能全局替换 `---`。
2. 编辑器当前调用原生 `UpdateYarnProjectImmediate`，不能只给 runtime compiler 注入前导。必须统一生产字节码的输入链路，或提供明确的共同 transform hook。
3. 原始源码 fingerprint 与转换后 program fingerprint 分开记录，避免旧程序配新 metadata。升级 companion schema 并重新导入/导出；不修改源文件来伪造入口命令。
4. HandleNodeStarted 只记录待进入节点/调用身份。Majo 的 ApplyModule、NodeEntered 通知及依赖模块的预加载时序移到屏障完成后；不得由同步事件先执行场景清理。
5. 对 Yarn 生成节点、嵌套调用、显式循环重新进入去重：去重键是本次 node activation，不是 `(node, sceneId)`。一次正常重新进入必须再次执行边界。
6. 读档/内部重建如果跳过正文前导，仍由现有恢复入口恢复并应用模块；如果经过前导，则携带恢复原因，不能误执行正常场景重启。
7. 第一阶段必须验证“入口第一条为 set/jump/台词/option”均被屏障阻挡。若具体 Yarn compiler 接入点不满足此要求，先补同步 VM 的正式暂停/继续扩展，再推进后续阶段；禁止以 async void 代替。

### 5.2 清理步骤

建议新增 `ISceneBoundaryCoordinator`（名称可按项目风格调整），由它协调 Direction、BGM 与 node-entry；不要让每个 Command 自行 reset 服务。

1. **校验**：入口 metadata、模块诊断、未结束 beat、调用原因；失败时不破坏当前演出。
2. **封闭旧上下文**：获取边界互斥门，停止接受旧场景的新提交和新 BGM 请求；标记 closing。使待加载结果不能提交，但仍允许已有运动通过受控 finish 写终态。
3. **完成操作**：完成该场景仍拥有的基础运动和视觉过渡；瞬态 FX 完成后移除自身贡献。只处理 scene-owned 操作，不干扰 Curtain。
4. **停止 BGM**：复用 `IAudioManager.StopBgmAsync` 的服务路径，对所有本场 track 立即停止并等待完成。边界不读取默认淡出时长、不增加隐式音频过渡；作者需要淡出时先写 stopBgm。已在淡出的 track 也要确保最终停止。
5. **释放呈现**：等待操作退出，释放旧 Actor tree、额外 View/Stage、过渡资源与 lease。旧 BGM 加载完成只释放结果，不得再次播放。
6. **发布默认状态**：新 scene instance/epoch、空 main Stage/View、默认 Camera、single/main 布局，保留当前全局 Curtain 与其执行上下文。
7. **开放新正文**：应用目标 node 模块，建立新 BGM owner，发 Majo NodeEntered，再允许正文继续。

关闭、finish、释放必须是幂等协议；执行失败不能让下一场正文继续。清理使用独立于已取消命令 token 的受控清理上下文，收集所有错误后报告，不因第一项失败遗留后续资源。发生不可恢复清理失败时停止对话并允许宿主 reset，不宣称已成功进入新场景。

清场后到作者设置新内容之间出现空 main 是允许的，不自动保留旧帧。作者可提前关闭 Curtain 并等待、在入口布置新场景后再打开。不得为“无缝衔接”偷偷增加截图、淡变或正文执行延迟策略。

需要审计锁序：边界服务先关闭提交入口，再调用 Direction/audio 各自的异步方法；不能持有 commitGate 时再次调用会获取同一 gate 的公共 reset/finish，也不能等待包含当前内部前导命令自身的 dispatcher quiescence。

## 6. 动画槽、并发与打断

### 6.1 三个动画槽与独立 FX

Actor 只保留三个固定动画槽，不建立逐属性调度框架：

| 槽 | 包含内容 | 接替规则 |
| --- | --- | --- |
| 空间 | at、scale、rotation 的完整基础姿态 | 新动作从当前基础呈现姿态接替整个旧动作；未指定分量停在当前呈现值 |
| 外观 | appearance 的 source/target 混合 | 新内容准备好后，在 Commit 立即将旧混合结束到旧目标，再开始新混合；不等待旧时长走完，允许轻微跳变 |
| 可见性 | opacity 与最终 Visible | 从当前透明度继续 fade，或 cut 到目标；与空间和外观独立 |

z 是即时层级字段，不是动画槽，不接替空间动作。FX 是独立贡献集合，在基础呈现之上合成，不占上述槽，也不写回基础姿态。

槽键为 `(SceneEpoch, TypedTarget, Slot)`；Actor target 包含 StageId/ActorId，Backdrop 使用类型化 Stage slot。每个槽仅需当前操作 token、目标、基础呈现值和 handle；没有部分通道撤销和残余通道聚合。

逻辑目标在 Commit 发布；逐帧值留在呈现层。空间接替时，Commit 读取不含 FX 的当前完整姿态，用显式字段覆盖后，将完整新目标写回权威状态。Prepare 不提前冻结起点，避免资源准备期间旧动画继续运动造成跳变。例：旧动作同时走路、放大，新动作只写 rotation，则位置和 scale 停在接替当刻，不再去旧终点；新动作只旋转。

Camera 使用一个完整构图槽（center、zoom、rotation）和独立 FX。move/cut 按空间规则接替；View dissolve 是整幅镜头过渡，不拆成属性动画。若新构图命令打断 dissolve，立即结束旧 dissolve 到旧目标，再执行新命令；若 dissolve 接替 move，先停在当前基础构图并以此为 source。无需捕获半混合画面继续运动。Backdrop 只使用外观槽与 FX，换图采用同样的外观接替规则。

### 6.2 beat 与冲突

beat 是动作声明的集合。end 统一校验、Prepare 和 Commit；同一时刻启动各槽动作，不按文本顺序隐式排队。

- 同目标不同槽可以并发；同槽两次声明报错，即使空间字段不同或目标值相同。需要同时移动、缩放、旋转时，写在同一条 move 中。
- 裸 Actor 命令只有指定空间字段才占空间槽，只有指定 appearance 才占外观槽；仅在提交前不存在或逻辑上隐藏时隐含写入显示槽。已显示角色改表情不重新触发显示，可以与 hide 同 beat。
- 同 beat 重复写 z 报错；多个可合成 FX 允许并发，不以效果名互相覆盖。
- move 必须有 beat 开始前已存在且逻辑可见的起点；创建/重新显示与 move 同 beat 拒绝。走入场先在画外用 cut 创建，再提交 move。
- FX 可以作用于 beat 开始前可见的目标，也可以作用于本 beat 合法创建/显示的目标。已有可见角色的 move、FX、hide 同 beat 合法，不因最终 Visible=false 拒绝 FX；hide duration:0 时不留下运行中的 FX。
- 创建/显示和 hide 同 beat 是可见性槽冲突，不按文本最后一条获胜。所有冲突在活动呈现改变前报告。

beat 外后提交命令接替同槽旧动作；不同槽互不等待。被接替是正常结束，不是作者错误。旧 token 失效后，其迟到回调和 finish 不得恢复旧目标。内部区分完成、被接替、显式取消和异常；命令/beat 等待仅汇总各自声明的动作是否结束，不继承后续新动作。

### 6.3 hide 和重新显示

Commit hide 时 Visible=false 成为逻辑目标，退场呈现仍可存在。已有空间、外观和 FX 在 fade 窗口继续；退场完成后将仍有效的空间/外观动作立即推进到终态，移除 FX，释放视觉资源，仅保留逻辑数据。duration:0 立即执行同样清理。

退场未完成时收到 character/object，新的显示动作接替可见性槽，从当前透明度继续；无须捕获过渡画面。旧 hide 回调因 token 失效不得销毁重新显示的实例。未指定的槽不受影响；新指定的外观按重新显示规则直接安装，新指定的空间字段按空间接替规则处理。

已完成隐藏后再次显示，按保存的逻辑状态重建视觉内容。stage clear 和场景结束清除逻辑状态，不等同于 hide。

### 6.4 稳定呈现根与增量发布

Actor 空间根、FX 根和 opacity 容器在活动呈现生命周期内保持稳定；外观 source/target 位于其内部，共同受空间姿态、透明度与 FX 影响。活动外观最多混合两份内容；打断时旧 source 退出呈现，Commit 成功后释放。Prepare 暂存和失败恢复所需引用不参与活动混合，不累积混合树或截图。

Prepare 只准备本次变更所需的资源、脱离活动树的视觉内容和发布描述，不修改活动节点、不接替旧操作。全部准备成功后，Commit 统一发布逻辑状态及各槽变更，安装新视觉内容。发布过程不得 await；提交失败应沿用事务恢复路径恢复旧状态和引用，成功前不释放恢复所需的旧内容。

常规 appearance、move、FX、显隐更新不得重建整棵 Actor tree。A 改外观不替换 A 的稳定根，更不替换 B 的节点。不再为整树替换实现运行中动画迁移系统。隐藏完成、移除、场景边界、restore 可以释放或重建根；先结束有效动作并使旧 token 失效，再释放资源，restore 从稳定逻辑状态重建。

保留 Prepare 不影响活动呈现、Commit 原子发布、失败释放暂存资源的保证；不保留“必须 Prepare 整树再 root swap”的实现限制。Actor/Factory 不得再自行排队动画，由固定槽执行层统一调度。

## 7. Actor 能力、Object 和 Backdrop

### 7.1 类型和状态

扩展 ActorState：类型化 Character/Object 种类、稳定定义引用/必要 factory 信息、Visible、Position、Appearance、Scale、RotationDegrees、Z。只放可重建逻辑数据，不保存 Node、Task、modifier 实例或资源 lease。

Backdrop 保留 `StageState.Backdrop` 的单 slot 状态，不为了共享 FX 把它同时加入 Actors 字典。它通过相同视觉能力 adapter 接入 FX，避免一张背景由两个 manager 同时创建和拥有。

### 7.2 能力接口

本轮不把 IActor 一次性重写成全新的通用对象框架。提取 backend 真正需要的独立接口，旧内部类在迁移期间实现这些接口；最终 Direction backend 不得 downcast ICharacterActor 或具体 ActorBase 才能执行通用行为。

| 建议接口/协议 | 职责 |
| --- | --- |
| Actor identity/lifetime | 逻辑 ID、factory 身份、受控释放；不包含所有可选视觉能力 |
| `IActorTransformTarget` | 原子应用基础位置、scale、rotation、z；无内置排队策略 |
| `IActorAppearanceTarget` | Prepare 外观、发布内容、提供 source/target 过渡输入 |
| `IActorVisibilityTarget` | 可见性、opacity 和退场呈现输入 |
| `IActorModifierTarget` | 应用/清除合成后的局部视觉 delta，与基础姿态分离 |
| presentation binding | 逻辑 target → 稳定视觉根；创建/释放与代次校验，不承担逐帧动画跨树迁移 |

IActorManager/Factory 管构建和释放，Direction 固定槽执行层管理动画调度。能力接口按需要实现，不建立万能属性图或第二套 Actor 动画队列。

### 7.3 坐标和渲染层

- Character/Object 的 at 沿用舞台百分比坐标，X 向右、Y 向上；Object 默认锚点在定义内声明，Character 保留既有落脚点约定。
- 提取通用 Stage 坐标转换；Character 的 ScenePositionOffset 仍是角色配置，不自动套到 Object。
- 本版允许 at 使用有限的画外百分比（小于 0 或大于 100），以便显式走入/走出；同步移除当前 CharacterPositionMap 的 0..100 backend 限制，前后端校验一致。Camera center 保持既有坐标契约，不强行和 Actor 转换合并。
- scale 为正的等比标量，rotation 使用角度，z 使用现有 Godot 支持范围并校验；前景先用 z 表达，不新增一套 layer/z 双重排序规则。
- 参考分辨率转换后的位置、基础变换和 modifier 变换必须分层。覆盖 resize、不同锚点与 LayeredCharacter 的测试。

## 8. FX 执行架构

### 8.1 共享范围

Camera registry 与 Actor registry 分开；效果查找概念键是 `(domain, effectName)`。共享 clock、OperationRunner、owner/epoch 校验、等待/finish/取消、Reduce Motion 策略和必要的波形工具，不共享万能 delta 或强制相同参数。

- Camera 输出 `CameraPoseDelta`，继续支持现有 shake/punch/zoom_pulse 等效果。
- Actor 输出 `ActorVisualDelta`，至少包含局部位移、旋转偏移、缩放乘数、颜色/opacity 乘数，为后续通用效果留有明确能力边界。
- Character/Object/Backdrop 使用 Actor registry。通用 shake 不依赖角色 preset；需要分层立绘骨骼等能力的未来效果单独声明 requirement。
- 新增 ActorFxCue/handler，沿用现有 transient cue registry。BackdropFx 可复用 ActorFxCue 的类型化 slot target，不用字符串伪 ActorId。

### 8.2 首批 shake 契约

`character/object fx <id> effect:shake` 与 `backdrop fx shake` 支持 duration、wait、strength、axis、frequency。duration 非负，strength 非负，frequency 为正整数，axis 使用明确枚举；错误值报错，不静默使用 fallback。

内建默认：duration=0.4s、strength=8、frequency=20、axis=all；duration 的覆盖顺序见第 3.3 节，beat 不提供 strength/frequency/axis 默认。Actor/Backdrop strength 使用 Stage 参考像素，坐标轴为 Stage X/Y；应用在基础缩放外侧，因此角色 scale 不放大偏移，Camera zoom 会放大场景中的抖动。Camera strength 单位保持现有 View 实现并写入 schema 帮助文本，不能承诺与 Actor 同数值同视觉幅度。

每个效果贡献都有独立 token。位移/角度分量相加、缩放/颜色/opacity 乘数相乘，最后按固定的 transform 分解顺序构建结果；不按不可控节点插入顺序任意连乘矩阵。支持非交换效果的未来插件必须声明顺序，不在本轮偷偷混入。

正常结束、取消、skip、失败、隐藏完成、Stage 内容移除均清除自身贡献；不能直接把整个 target delta 归零而删除别人的效果。duration=0 不残留贡献。无能力或无有效目标在提交前报错。

### 8.3 Backdrop 的特殊接入

当前 Direction backdrop 并非由 Character Actor tree 管理。给 Stage backdrop 的视觉根提供 modifier adapter，并让纹理切换/溶解的 source 和 target 都处于该根之下。换背景时沿用 slot 的有效 FX 时间进度；slot 被清除时终止 FX。

背景偏移可能显露边缘，这是 backdrop shake 的预期空间行为；不自动扩大 Camera zoom 来掩盖，也不偷偷抖动整个 Stage。已有 cover/bleed 行为保持。

### 8.4 参数扩展边界

首批命令保留可描述的强类型公共参数；注册效果时绑定该效果参数校验和能力需求。现有 CameraFX 的固定参数表不意味着插件能任意扩展 binder。新增 schema/bag 能力若确有必要，必须接入现有 command binding 体系并生成补全；本轮不通过绕过校验的任意 dictionary 输入宣称无限扩展。

## 9. 生命周期与恢复的最低集成要求

本节只列必要接入，不扩展用户已要求省略的 rollback 专项设计。

- 新 Actor 字段、Object 定义引用和场景上下文进入现有 StateContributor；更新 schema 并同步 fixtures。旧命令不兼容不等于可以让新快照漏字段。
- 场景切换不清 DialogueTravelManager 历史、不触发 GameSession 的新游戏/reset；释放节点后仍可通过已有 RestoreAsync 重建。
- Restore/InternalRebuild 不执行正常 scene-entry 清场。使用新的活动代次阻止旧回调写入；不能把旧 token 原样复活。
- 时间进度和瞬态 FX 不进入新的逐帧快照协议，保持既有稳定状态恢复语义。
- 加载结果提交前检查 scene epoch、target generation 和 writer token；过期结果释放。内部 finish 先完成其拥有的终态，再封死 target generation。
- completed operation/transaction 不长期保留 Node、视觉快照、lease 的强引用；历史只保留现有范围内的必要数据。

## 10. 实施阶段与完成门槛

各阶段可拆成独立提交，但未完成迁移前不发布混合契约。只改本方案范围内文件；实施前检查工作区，保留并协调其他并行工作的修改，不覆盖它们。

### P0：契约与 Yarn 屏障原型

- 固化命令表、三个动画槽及 Camera/Backdrop 的对应规则、scene header、负例和 owner 表；建立小型双场景/option fixture。
- 验证原生编译/编辑器/热重载共用前导 transform 的接入点和源映射；先完成真实 Runner 的正文前等待原型。
- 验证 camera/character/object/backdrop 的裸根与子命令匹配，不错误吞掉旧语法。
- 完成门槛：入口第一条 set/jump/台词/option 全部等待；普通 node 不清场；源位置和 program/metadata 配对正确。此门槛不通过，不能仅靠事件回调开始后续生命周期接入。

### P1：状态与 Actor 能力

- 扩展 ActorState/patch/plan；新增显隐状态及通用定义引用、rotation。
- 提取 presentation capability/binding 和通用坐标转换；Character、LayeredCharacter、Object factory 接入。
- 添加 Object 定义/资源目录配置与 typed preload；为 backdrop slot 提供 modifier adapter。
- 最低限度更新 StateContributor 和 metadata schema，保持现有恢复接口可用。
- 完成门槛：显隐后字段不丢；Character/Object 类型不会串用；Object 可从同一定义创建两个实例；Stage clear 清除 hidden 记录。

### P2：固定槽执行、稳定根与 FX

- 将 Actor 动画限定为空间、外观、可见性三个槽；实现整槽接替和 beat 同槽冲突诊断，不实现逐属性 owner。
- 改造为稳定呈现根与增量视觉发布；Prepare 失败不影响活动呈现，外观切换不重建其他 Actor。
- 统一 beat 等待与时长优先级；Camera 构图和 Backdrop 外观采用第 6 节的有限接替规则。
- 引入 Actor modifier registry/manager/cue；接入 shake 和 backdrop FX；复用 Operation/clock/accessibility。
- 完成门槛：外观+move+FX/退场并发；空间整槽接替且省略分量停在当前值；外观打断立即结束旧混合；根节点稳定；hide→show 不被旧回调删除；skip 无残留。

### P3：场景边界与 BGM

- 正式加入 SceneBoundaryCoordinator、scene owner/epoch、Normal/Restore/InternalRebuild 路由。
- 拆出保留 Curtain 的演出清理，不调用重置 Curtain 的全量 host reset。
- 为 BGM 请求/播放绑定场景，清场 StopBgmAsync 等待且阻止晚到结果。
- 移除公共 stage/view release，保留内部删除；提供宿主结束演出入口。
- 完成门槛：main 回默认；额外 Stage/View 消失；Curtain 状态及有效操作保持；BGM 停止；分支保留；恢复不重复清场。

### P4：命令切换与全仓迁移

- 替换注册、Arguments/schema、policy/defaults、compiler recognized commands、资源分析和错误文本。
- camera pose → camera；静态命令 via:move → 专用 move；leave → character hide。
- reset/shot use 的旧 via:move 示例改成显式 camera move 目标值，不偷偷新增旧语法解析。
- 旧 character 同时 appearance+via:move 的内容拆成外观命令和 move；需要同步开始时使用合法 beat，不丢 appearance。
- 同 Actor 的分条位置/缩放/旋转动作合并为一条 move；Actor 空间 dissolve 改为明确的 cut 或 move，显隐 dissolve 改为 fade/cut。beat 子命令的 wait:true 移到 end/join，不静默忽略。
- stage/view release 原有内容按意图改为场景边界、局部 clear 或布局调整，不机械删除后留下无界状态。
- 同步 demos、tests、插件示例和生成的命令补全配置；重新生成 `.direction.tres`，不手工修改生成内容。
- archive 文档可保留原文作为历史，不能被当成新 API 指南。新增负例断言旧接口不可执行。
- 完成门槛：活动脚本无旧接口；default 配置不再含可选 Move 状态过渡；所有新命令有可用帮助与定位错误。

### P5：验收与文档收尾

- 按第 11 节验收表完成单元和真实 Godot/Yarn headless 回归。
- 运行仓库规定的快速与最终完整门禁；只有失败、新改动或未解决风险才重复拓展测试。
- 记录实际默认参数、接口名与本设计差异；删除中间兼容分支和废弃注册，不遗留“双轨运行”。

## 11. 验收清单

| 类别 | 必须覆盖的结果 |
| --- | --- |
| 命令契约 | camera pose、leave、公共 release、状态 via:move 失败；新 root/child 正确解析；只写 character ID 成功 |
| 过渡 | Actor dissolve 只混合外观；appearance+at 在新位置混合；显隐仅 fade/cut；纯空间 dissolve 报错；cut 不继承时长、显式正 duration 冲突；duration=0 正确终结 |
| 并发 | appearance+move+FX 同时工作；move+FX+hide 同 beat 合法；同目标同槽重复写入报错，分条位置/旋转也冲突；创建+move 拒绝，创建+FX 合法 |
| 等待与默认 | beat 子命令 wait:true 报错，end/join 等待正确；动作被接替后等待结束；时长显式→beat→操作默认；过渡选择不随相同 appearance 值改变 |
| 中断 | 空间整槽从 Commit 时基础姿态接替，省略分量冻结且写回状态；z 不打断 move；外观/View dissolve 打断允许结束旧混合的跳变，最多两份内容；旧 finish 不覆盖新值；skip 运动到终态、FX 清零 |
| 显隐 | hide 保留状态；重新显示遵守省略字段和空间接替规则；退场时透明度连续、无晚到销毁；hidden 新 move/fx 报错 |
| 稳定呈现 | A 改外观不替换自身根或 B 节点，B 的 move/FX 不受影响；外观两份内容共同移动；Prepare 失败无活动树变化；发布失败可恢复，暂存资源可释放 |
| Object | 资源与实例分离；多个实例；类型误用/缺失资源/不支持能力在 Prepare 前报错；锚点和画外位置有效 |
| Backdrop FX | 只动背景；cut/dissolve 换图时 FX 连续；清空 slot 后无贡献；不改 Camera zoom |
| 场景 | 普通 node/option/汇合不清场；重新进入入口新建；空/重复 metadata 诊断；开放 beat 不静默丢弃 |
| 入口屏障 | 第一条 set/jump/line/option 均在边界之后；清理异常阻止正文；无隐藏自动过渡 |
| 全局/局部 | clear 不改 Camera/Curtain/BGM；边界清全部旧场景内容且保留 Curtain；main 常驻入口、内容不常驻 |
| BGM | 多 track 停止；先前异步加载不在新场播放；已淡出 track 不复活；边界默认无隐式 fade |
| 清理 | Prepare 失败、Finish 异常、Reset/Shutdown 重复清理均无孤儿节点/lease；连续换场计数有界 |
| 既有恢复 | 新字段完整；边界不清 Travel 历史；恢复入口不再次清场；不扩展逐帧回退 |
| 构建链 | Editor/runtime/export/hot reload metadata 一致；source map 正确；生成产物无旧 schema |

优先扩展 `tests/Majo.Runtime.UnitTests/` 中现有 planner、binding、lifecycle 测试，以及 `tests/Majo.CommandGenerator.Tests/CommandGeneratorTests.cs`、`tests/headless/HeadlessTestRunner.Direction.cs`、`HeadlessTestRunner.TransitionRegressions.cs`、`HeadlessTestRunner.Actors.cs`、`HeadlessTestRunner.YarnCommands.cs`、`HeadlessTestRunner.DirectionData.cs`、`HeadlessTestRunner.Audio.cs`、`HeadlessTestRunner.ScriptAndState.cs`。新场景测试可拆成同一 partial runner 的独立文件，避免继续膨胀单文件。

命令门禁：

```sh
./tools/quality.sh --check
./tools/test.sh --fast
python3 tools/test-direction-import.py
./tools/test.sh --full
```

`test-direction-import.py` 在新增 transform 后扩展入口元数据、正文前导和源映射案例；按脚本要求先有 Debug build。文档提交本身不运行上述运行时测试，实施时按阶段执行。

## 12. 完成定义

重构完成需同时满足：新命令契约唯一、场景入口屏障真实可等待、固定动画槽并发与接替符合契约、稳定呈现根和增量发布落地、Camera/Actor modifier 类型分离但执行规则统一、Backdrop 共用适用 FX、隐藏状态与节点寿命分离、main 场景重置不改 Curtain、BGM 按场景停止、旧接口及活动内容迁移完毕、既有状态恢复接入且验收通过。

仅新增命令壳、仅把 leave 改名、仅在 NodeEntered 启动后台 reset、仅把 Actor 节点 Visible 设为 false，均不算完成。
