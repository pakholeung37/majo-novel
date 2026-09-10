# 2D 推拉镜头与背景视差实施方案

后续易用性修订：场景镜头现在在 Yarn 的 `camera preset define` 中定义，支持 `extends`，完整镜头同时应用机位和视差。法庭专用 C# 注册已移除，不引入场景预设文件。本文项目配置目录的设计保留为底层兼容能力；当前创作语义以 `script-writing.md` 的镜头章节为准。

状态：实施中（2026-09-10）。A–D 的运行时、命令、迁移、双层 View、跨距离背景转场、覆盖诊断和 idle 时钟已落地；法庭脚本首轮适配及渲染资源下限已记录。目标 GPU 帧耗时与最终视觉截图标定仍待完成。

## 1. 目标与范围

以 `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Trial00.yarn` 为首个验证场景：人物与讲台同属主体平面，不产生相互视差；法庭墙壁和窗户作为一个远景平面，视觉上距人物约 10–20 米。

保留现有 `zoom` 的整体缩放行为，新增 `dolly` 表达沿纵深推进和后退。双人同框镜头同时缩小画面和后拉机位。上层使用命名预设，底层保留连续、可验证的投影模型。

第一版不实现真实米制场景、角色间深度、座位分组、多层背景、鼠标跟随、景深模糊和逐元素高级参数。背景现有 `z`/绘制顺序不作为空间深度。

## 2. 现有实现与接入点

| 位置 | 现状与所需扩展 |
| --- | --- |
| `Runtime/Direction/Core/DirectionState.cs` | `ViewPose` 只有 Center/Zoom/RotationDegrees；扩展 Dolly，ViewState 增加视差和 idle 状态，BackdropState 增加距离 |
| `Runtime/Direction/Core/DirectionDirectives.cs` | 扩展 ViewPosePatch，增加持久的环境效果 patch |
| `Runtime/Dialogue/Commands/Direction/CameraCommands.cs` | camera/move/reset 解析与提交新字段 |
| `Runtime/Dialogue/Commands/Direction/CameraPresetCommand.cs` | 现有 preset 已补齐构图默认值；继续保持完整预设语义 |
| `Runtime/Direction/Execution/DirectionPlanner.cs` | 合并、校验新状态，声明写入冲突 |
| `Runtime/Direction/Godot/GodotDirectionViewBackend.cs` | 插值 Dolly 与环境效果配置 |
| `Runtime/Direction/Godot/GodotDirectionRenderHost.cs` | 当前 ApplyViewTransform 对整个 World2D 应用统一 CanvasTransform；需分开背景和主体投影 |
| `Runtime/Direction/Godot/GodotDirectionViewSurface.cs` | 增加每 View 背景输出及 idle 采样状态 |
| `Runtime/State/DirectionStateContributor.cs` | 当前 schema=3 且严格检查版本；增加 schema=4 和明确的 v3 迁移 |

上表 Runtime 路径均相对于 `addons/majo_runtime/`。修改还需覆盖对应接口、prepared replacement、诊断输出及命令注册，不只修改渲染函数。

## 3. 创作者 API

### 3.1 背景距离

```yarn
<<backdrop Court_Trial00 at:-140.625,115 origin:0,0 scale:1.7578125 distance:far via:cut>>
```

`distance` 接受 `flat / near / far` 或项目命名距离预设。默认 `flat`，其含义是与主体同平面。`far` 表达明显远离主体，不承诺等于某个米数。人物和讲台无需新增字段。

同一背景资源的局部更新省略 distance 时保留；更换资源或首次创建时省略则使用 flat。场景清理后不继承上一场景距离。背景过渡的源和目标各自携带距离。

### 3.2 视差与静止漂移

```yarn
<<camera parallax subtle view:main>>
<<camera parallax subtle view:trial_b>>
<<camera parallax off view:main duration:0.4>>
<<camera parallax reset view:main>>
<<camera idle gentle view:main>>
<<camera idle off view:main duration:0.5>>
```

- parallax 接受 `off / subtle / normal / strong` 或项目效果预设。默认 off。
- idle 接受 `off / gentle` 或项目漂移预设。默认 off。
- 目标规则复用现有 camera：stage/view 互斥；省略时为 main；stage 指向其默认 View。
- 两种命令支持 duration/ease/wait：默认 duration=0、wait=false；wait:true 只等待配置过渡收敛，不等待漂移结束。
- `camera parallax reset` 等价于恢复内置 off 配置，支持相同目标和时序参数。
- 开启视差不自动开启漂移。关闭视差不停止漂移，不修改主体取景。
- 一个 View 只有一份 idle 状态。重复设置相同预设不重启相位；修改振幅/周期平滑生效；off 先收敛振幅再停止。

### 3.3 构图与机位

```yarn
<<camera move view:main zoom:1 duration:8>>
<<camera move view:main dolly:back duration:8>>
<<camera preset define pair_wide center:0,56 zoom:1 rotation:0 dolly:back>>
<<camera move view:main preset:pair_wide duration:8 ease:sine-in-out>>
```

`dolly` 接受 `neutral / near / back` 或项目机位预设；全部是绝对目标，不是增量动作。连续值供插值和高级配置使用，不在普通 Yarn 命令暴露无单位数字。

| 操作 | 规则 |
| --- | --- |
| 普通 camera/move 省略字段 | 保留当前值 |
| 定义 preset 省略字段 | Center=(50,50)，Zoom=1，Rotation=0，Dolly=0 |
| 应用 preset | 应用完整四项构图；显式参数再覆盖 |
| camera move | 四项构图同一时间轴、同一 easing；Dolly 插值连续值，不插值预设名称 |
| camera via:cut | 原子提交目标构图 |
| camera reset | 重置四项构图，不更改 parallax/idle |
| 创建 View | 默认构图、parallax off、idle off；不继承别的 View |
| 场景完整 reset / 释放 View | 清理配置、时间源注册及渲染资源 |

现有 as/shot 捕获路径必须包含 Dolly。上层不增加局部构图 preset 类型。

## 4. 底层投影模型

### 4.1 坐标与参数

运算使用固定参考画面像素坐标，沿用 DirectionPoint 到像素的转换（Y 轴方向也沿用现有规则），最后再做 View 输出缩放和分屏裁切。不得用左右分屏区域的实际宽度替换参考宽度。

- `p`：已有布局计算得到的元素像素位置。
- `c`：镜头中心加 idle 位移后的像素坐标。
- `o`：固定参考画面中心。
- `z`：原有 Zoom。
- `d`：归一化机位位移；主体平面在中性机位距离为 1，正值前进，负值后退。
- `D >= 1`：背景相对距离，flat=1。
- `a in [0,1]`：视差效果强度；0 表示所有层统一投影。
- `R`：现有镜头旋转矩阵。

所有数值必须有限。法庭特写校准后限定 `d in [-2, 0.85]`，主体投影分母至少为 0.15；D 范围 [1,100]。边界非法配置应报错，不能依靠运行时 clamp 掩盖。详见 `court-camera-calibration.md`。

### 4.2 缩放与推拉

```text
subjectScale = 1 / (1 - d)
physicalBackgroundScale = D / (D - d)
backgroundScale = lerp(subjectScale, physicalBackgroundScale, a)

subjectScreen(p) = o + R * [z * subjectScale * (p - c)]
```

背景在中性机位保持原始贴图尺寸，D 只影响机位变化的响应。不是把既有背景因距离变远立即缩小。

当 a=0 或 D=1，背景与主体完全一致。Zoom 是投影后的统一乘数，不能单独产生深度差。d=0 时所有层的投影倍率都是 1。

不额外套用“背景缩放 ±10%”限制；较早讨论中的该限制废弃，避免与连续推拉模型冲突。由 d、D、a 的有效范围及预设标定约束效果。

### 4.3 背景平移与稳定参考点

为了保留已绘制全景图的基本位置关系，采用以主体构图为基准的受限背景补偿，而非无限位移的物理投影：

```text
k = a * (1 - 1 / D)
rawOffset = z * backgroundScale * k * (c - anchor)
boundedOffset[i] = limitPixels[i] * tanh(rawOffset[i] / limitPixels[i])
backgroundScreen(p) = o + R * [z * backgroundScale * (p - c) + boundedOffset]
```

limit=0 的轴直接返回 0，避免除零。limitPixels 由参考画面百分比换算，限制的是旋转前、缩放后屏幕坐标中的额外平移。额外缩放引起的边界变化不在此限制内。

anchor 默认是参考画面中心 (50,50)，可在项目效果预设覆盖。它是持久配置，绝不在每次 cut、move、隐藏/显示或读档时隐式重置。相同完整状态必须产生相同画面。

设计取舍：极长横移进入饱和区后，背景与主体的速度差会减弱。这是保持全景结构稳定的有意选择，第一版不承诺长距离全程保持物理速度差。法庭标定必须检查这一点；若效果不足，调整项目 preset 的 anchor 和 limit，不能引入依赖镜头历史的自动重新取点。

旋转对两层使用同一 R。没有逐层旋转系数。用户无需配置 anchor、limit 或上述比例。

### 4.4 Modifier 与 idle 顺序

顺序固定为：基础 ViewPose 插值 → idle 修改观察中心 → 分层投影 → 现有屏幕空间 camera FX → screen 合成。

现有 shake/punch 的 CenterOffset 继续是统一屏幕偏移，不自动变成空间运镜；ZoomMultiplier 和 RotationDegreesOffset 保持已有整体效果语义。必须显式组合矩阵，避免重复应用 Zoom 或 modifier。

## 5. 预设注册与初始数值

新增项目级 CameraEnvironmentPresetCatalog，通过运行时配置/依赖注入注册四个有类型的集合：Distance、Dolly、Parallax、Idle。第一版不增加任意 JSON 文件发现机制。

内置名称保留，项目名称使用如 `court_far`、`court_back`；同类别重名报错，跨类别允许同名。未知名称在脚本验证或执行提交前报含源位置的错误。项目预设不支持相互继承，先避免循环和隐式覆盖。

| 类别 | 内置映射初值 |
| --- | --- |
| Distance | flat=1，near=2，far=6 |
| Dolly | neutral=0，near=0.15，back=-0.25 |
| Parallax | off: a=0；subtle: a=0.25；normal: a=0.5；strong: a=1 |
| Parallax 限幅 | subtle=(1.5,1)%；normal=(3,2)%；strong=(6,4)%；anchor=(50,50) |
| Idle gentle | amplitude=(0.15,0.08)%；period=12 秒；进入/退出包络默认 0.5 秒 |

上述是可实施初值，不是已完成视觉标定的结论。内置配置冻结前用验收镜头校准；之后修改按视觉兼容性变更处理。far=6 是相对距离，不声称人物到墙壁恰好 10–20 米。

高级创作者通过项目命名预设配置 D、d、a、anchor、limit 或 idle 振幅/周期；普通 Yarn 只引用名称。文档必须解释各连续参数的方向、单位和范围。

## 6. 状态、时间与事务

建议新增不可变记录（命名可随项目规范微调）：

```csharp
// 扩展现有记录时优先用带默认值的 init 属性，减少构造函数破坏。
ViewPose.Dolly                 // float，默认 0
BackdropState.Distance         // 解析后的 D，默认 1
ViewState.Parallax              // a、anchor、limit，默认 off
ViewState.Idle                  // 已解析配置、phase，默认 off
ViewPosePatch.Dolly             // DirectionValue<float>
ViewParallaxPatch               // IDurableDirectionPatch
ViewIdlePatch                   // IDurableDirectionPatch
```

运行时状态保存解析后的数值；预设名称可作为诊断元数据保留，但读档不重新解析名称。项目后来改预设不能改变旧存档画面。

Planner 需将构图、parallax、idle 作为分别可追踪的写入目标；同一 beat 内按现有冲突规则处理同字段重复写入。配置过渡占据各自动画槽，不互相取消 camera move。替换运动从当前显示值接续，不从上一个未完成目标值接续。

idle 使用可暂停的演出时钟，不用系统墙钟。推荐两个正弦分量和固定相位差；启停通过振幅包络避免首帧跳变。重复设置和周期变化保持相位连续。暂停演出时停止相位；隐藏 View 仍按演出时间推进，相位不因 screen show 重启。

无限循环不是未完成的 beat：beat join 和保存活动判定只看到有限配置过渡。idle 稳态允许存档；现有不允许捕获活动 camera move 的策略保留。若有有限配置/启停包络尚未完成，也按活动运动处理，第一版不序列化半程插值。

捕获时在状态锁/演出时间快照下保存 idle 当前相位，而不是每帧递增 durable revision。prepared restore 先准备资源、再原子恢复所有状态和相位，首帧不得出现默认背景或默认机位。释放或恢复旧 View 时注销时钟订阅。

Schema 升至 4：明确接受 v3 并补 Dolly=0、Distance=1、Parallax=off、Idle=off。保留对其他不支持版本的明确错误；不要把所有缺失字段都当迁移。v4 必填状态损坏、非法浮点或不合法范围在 preflight 阶段拒绝，不能部分提交。

## 7. Godot 渲染实施路径

当前多个 View 共享 Stage.World2D，不能通过移动共享背景节点实现不同视差。

第一版采用共享场景内容、每 View 分层绘制：

1. 将背景树和角色/物件树分配到独立 CanvasItem visibility layer；维护所有子 CanvasItem 的 layer 约定，不修改 z。
2. 每 View 使用两个透明 SubViewport，共享同一个 Stage.World2D，但使用互斥 CanvasCullMask：背景 viewport 只绘制背景，主体 viewport 只绘制人物/物件。
3. 两个 viewport 使用各自 CanvasTransform；View 输出按背景→主体合成，再进入现有屏幕转场、分屏和保留帧路径。
4. a=0 仍走同一路径，先保证正确性和兼容性；性能测量后才考虑合并快路径。

Godot 具体可见性属性及子树行为须在实施时用当前引擎版本做最小验证；若现有节点不能完整隔离，先调整 Stage 的渲染树分组，不改成共享节点按 View 轮流变换。

背景转场需要源/目标各按自己的 Distance 投影后再混合，不能将不同深度的两张图先混成一张纹理再套单一矩阵。扩展 PrepareBackdropInput 和 stage/view transition 合同，确保其输入绑定 View；可复用纹理资源，不复用错误的已投影图像。普通背景 tint/FX 同时覆盖两个过渡输入。

必须同步检查 Stage 截图、screen dissolve、split、retained output、prepared replacement、viewport resize 和资源释放。render target 数量和显存按同时显示 View 数统计，记录新增成本，不预先声称无性能影响。

背景覆盖检测对投影后的背景多边形与 View 可见区域做包含检查，需考虑旋转和分屏取景；不足时给诊断提示，可选调试叠层。不得自动放大、拉伸、平铺或修改镜头以掩盖边缘。提示不进入游戏对白 UI。

## 8. 法庭脚本适配

在保持人物、讲台位置/比例不变的前提下：

1. Court_Trial00 增加 distance:far。
2. main/trial_b 分别开启 subtle；镜头隐藏/显示不改变配置。
3. 所有既有 preset 自动补 Dolly=neutral；明确需要空间推拉的镜头再指定 near/back。
4. pair_wide 使用 back，同时保留 zoomout；重新标定其 Zoom，不能机械沿用 1，因为后退已经扩大取景。
5. 开场横移检查补偿饱和和背景边界；必要时注册 court 专用效果 preset。
6. idle 先关闭完成运动标定，再选择一处稳定对白验证 gentle。不要全场自动开启。

保存基准截图与新截图，覆盖相同对白边界及运动 0/25/50/75/100% 时间点；核对分屏中两个机位互不干扰。

## 9. 分阶段工作与验收

### A. 状态和命令

实现记录、目录、解析、preset/reset、patch/planner、序列化与迁移。更新生成的命令元数据和脚本验证支持。此阶段不改示例脚本使其依赖未实现渲染。

验收：旧 preset 默认值不变；显式覆盖正确；重复 back 不累积；非法预设和范围报错；v3 读档迁移、v4 round-trip 和失败回滚。

### B. 纯投影与主体推拉

新增独立于 Godot 节点的 CameraProjectionCalculator，输入完整解析状态、参考分辨率和采样时间，输出主体/背景矩阵；将 Dolly 接入插值。

验收：d=0/a=0 与原矩阵一致；单独 Zoom 不产生深度差；a=0 背景/主体一致；后拉主体缩小更多；投影连续、有限、无奇点；相同状态不依赖历史；不同分辨率百分比一致。

### C. 每 View 背景和转场

状态：运行时完成。源/目标背景使用独立 visibility layer 和每 View 投影后合成；覆盖诊断按投影后四边形检查且去重；资源下限见 `docs/camera-dolly-parallax-performance.md`。目标 GPU 帧耗时仍归入发布标定。

实现分层 viewport、两层投影、转场输入适配和边界诊断。

验收：独立双 View/倾斜分屏/后台预备镜头；背景换图跨距离转场；旧场景 off 模式截图兼容；恢复、resize、release 无残留。测量双 View 显存及帧耗时。

### D. Idle 与状态恢复

状态：运行时完成。idle 由演出帧时钟推进，phase 随 schema 4 状态捕获/恢复，有限配置过渡与持续漂移分离。

接入演出时钟、启停包络、相位捕获与配置槽。

验收：不阻塞 beat join；稳定漂移可保存；暂停不漂移；读档首帧一致；重复设置不重启；off 平滑收敛；与 move/shake 叠加无重复变换。

### E. 法庭标定与发布文档

状态：进行中。Trial00 已加入 far/subtle/pair_wide back，并只在一处稳定段落启用 gentle；创作说明已更新。尚需非 headless 的同边界截图组和目标 GPU 帧耗时记录后冻结最终参数。

适配脚本和项目预设，完成三个核心镜头视觉验收，更新 `docs/script-writing.md` 的创作说明。记录最终参数和可接受的素材边界。

最终完成条件：A–E 全部通过；没有依赖隐式历史的构图；旧脚本/旧存档保持兼容；常用脚本只需距离、效果档位和机位预设即可完成所需演出。

## 10. 实施边界

本文已定行为无需再次请求创作侧逐参数确认。实施者可调整内部类型命名和资源组织，但不得改变 zoom 含义、引入累计 dolly、让视差档位改变主体大小、隐式重置 anchor，或将持续 idle 计为永不完成的动作。

确需超出本版范围时，先记录具体失败镜头和原因再扩展模型；不能通过不断新增 Yarn 必填参数补偿底层不稳定。
