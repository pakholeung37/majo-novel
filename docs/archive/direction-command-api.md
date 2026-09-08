# Direction 命令与场景入口

本指南对应 `direction-command-api-refactor-plan.md` 的新接口。旧 `camera pose`、`leave`、`stage release`、`view release` 不再注册；状态命令不接受 `via:move`。

## 状态、运动和临时效果

```yarn
title: Room_Start
scene: room
---
<<backdrop room>>
<<camera center:50,50 zoom:1>>
<<character Ema appearance:normal at:left wait:true>>
<<object knife1 asset:evidence/knife at:60,30 rotation:15 z:100>>
<<object knife2 asset:evidence/knife at:120,30>>
<<beat begin duration:0.5>>
<<character Ema appearance:happy>>
<<character move Ema at:right scale:1.1 rotation:5>>
<<character fx Ema effect:shake strength:8 duration:0.3>>
<<beat end wait:true>>
<<camera move center:65,50 zoom:1.5 duration:0.8 wait:true>>
<<backdrop fx shake strength:6 duration:0.3>>
<<character hide Ema duration:0.2 wait:true>>
<<character Ema>>
<<jump Room_Choice>>
===

title: Room_Choice
---
-> 查看证物
    <<object fx knife1 effect:shake>>
-> 继续询问
    <<character Ema appearance:normal>>
===
```

- `camera` 至少指定 center/zoom/rotation/as 之一，默认 cut，可用 dissolve；`camera reset` 和 `shot use` 同样不能产生运动。默认 cut 下写正 duration 会报冲突。
- `character <id>` / `object <id>` 确保实例存在并显示。只写 ID 合法，已显示的实例不受影响。首次 object 必须有 asset，后续省略时保留，不能切换 asset 或 Character/Object 类型。
- 新建/重新显示默认 fade；已显示实例指定 appearance 默认 dissolve，只混合视觉内容；纯空间字段/z 默认 cut。appearance 与空间同写时，空间立即设置，duration 仅影响外观。
- move 至少指定 at/scale/rotation 之一，只操作 beat 开始前已有且可见的实例，不接受 appearance、asset、z、via。位置、缩放、旋转属于一个槽；新指令从提交时完整基础姿态接替，省略字段冻结并写回状态。z 不打断运动。
- hide 默认 fade，保留逻辑字段。完成后释放呈现与 FX；重新显示可重建呈现。退场途中 show 会接替透明度，不会被旧回调删除。已隐藏实例不能新发 move/fx。重复 hide 不重启退场。
- 同一 beat 可组合外观、move、FX、hide；同一动画槽不能写两次。创建与 move 不能同 beat，创建与 FX 可以。外观和镜头 dissolve 接替时先结束旧混合；不保证保留被打断的混合画面。

## 等待、时长与坐标

命令默认 wait:false，只等提交；wait:true 等该命令动作结束（包括被接替）。beat 子命令不能 wait:true；把等待放在 `beat end`（默认 true）或 `beat join`。duration 优先级为显式值 → beat → 对应项目/内建默认；cut 始终有效时长 0。ease 使用显式值 → beat → 项目默认。

Actor at 是 Stage 百分比，X 向右、Y 向上，允许有限画外值。Character 保留原落脚点偏移；Object 不继承该偏移。scale 为正数，rotation 为角度，z 遵守 Godot 范围。

Actor/Backdrop shake 的默认值为 duration=0.4、strength=8、frequency=20、axis=all。axis 可为 all/x/y/diagonal，frequency 必须是正整数。strength 是 Stage 参考像素，位于 Actor 基础缩放外侧；Camera zoom 会放大它。Camera FX 的位移 strength 是 View viewport 像素，两者数值不代表相同视觉幅度。Reduce Motion 保留时长但不施加运动。

## Object 定义

`ResourceConfiguration.ObjectsPath` 默认 `objects`。asset `evidence/knife` 查找 `<RootPath>/<ObjectsPath>/evidence/knife.json`：

```json
{
  "factory": "object",
  "anchor": [0.5, 1.0],
  "appearances": {
    "Default": "evidence/knife.png",
    "highlight": "evidence/knife-highlight.png"
  }
}
```

相对纹理路径相对于 ObjectsPath，亦可使用 res:// 路径。Default 必须存在；anchor 默认 [0.5,0.5]，表示纹理内归一化锚点。factory 默认 object，也可选择已注册并支持 Direction 呈现能力的 factory。实例 ID 与定义资源 ID 分离；move/fx/hide 是保留实例词。定义发现与预加载不会创建呈现节点。

## 场景边界与恢复

只有 node header 的 `scene: <id>` 表示新场景入口。普通 node、option、汇合与 Yarn 执行暂时结束均保留当前场景；同一入口正常重入也产生新的 epoch。一个 project 内每个 scene ID 只能声明一次。空/重复 scene 和源脚本调用保留前导命令均在编译时诊断。

编译器为每个 authored node 插入可等待的正文前导。边界先验证没有开放 beat，再结束场景动作、停止全部 BGM（含防止旧请求迟到播放）、释放内容和额外 Stage/View、恢复空 main 与 single/main。Curtain 节点、状态和进行中操作保持；没有自动切场转场。清理失败会阻止正文。

`stage clear` 只清目标 Stage 的 backdrop、Actor（包括 hidden）及相应 FX，保留 tint、Camera、布局、BGM 和 Curtain。宿主可调用 `ISceneBoundaryCoordinator.EndPresentationAsync()` 明确结束演出。Restore/InternalRebuild 使用专门进入原因，不重跑正常 scene-entry 清场；快照保存 Actor 新字段和 scene context，恢复使用新 epoch。Direction StateContributor schema 为 3，Yarn companion schema 为 2。

## 扩展与生成

`IActorTransformTarget`、`IActorAppearanceTarget`、`IActorVisibilityTarget` 提供独立呈现能力。Character/Object/Backdrop 使用 `ActorVisualDelta` 与 Actor modifier registry；Camera 保留 `CameraPoseDelta`。两者共享 `ModifierOperationRunner` 的 clock、贡献 token、finish/取消与清理协议。`MajoRuntimeBootstrap.ConfigureActorModifiers` 可在 registry seal 前注册 Actor 效果。

`Majo.Direction.ysls.json` 从实际 runtime catalog 生成；参数帮助、默认文本与根/子命令路径不另写一套解析器。更新方式：

```sh
MAJO_UPDATE_COMPLETIONS=1 dotnet test tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj --filter DirectionCompletionTests
```

普通测试会比较生成结果以防补全漂移。`.direction.tres` 由 Godot editor 导入重新生成，不应手工编辑。
