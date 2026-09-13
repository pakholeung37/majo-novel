# Background actor 实施进度

更新：2026-09-13。P0–P6 全部完成。后续已统一实时视觉输入，以下冻结快照描述记录初版交付，最新行为以 [实时输入实施说明](live-visual-transition-implementation.md) 为准。背景创建、显示、转场、预加载和恢复均已贯通 Actor 路径。

## 已实施改动

- 提取 ActorDefinitionResolver；ActorManager 的创建、替换、预加载与 CanCreate 共用定义解析。保留 Character/Object 约定解析。
- 新增 RequireExplicitDefinition 与无实例 ResolveExplicitAppearance，校验环境排除定义、默认外观和 Factory 身份；支持 Factory 纯外观解析。
- 修正 CanCreate 对显式自定义 Factory 的 fallback 误判。
- BackgroundActorFactory 返回完整外观对应的 Texture2D 预加载条目及 OverridePath；集成测试确认不会创建 Actor。
- 新增 16 项解析单元测试；已有 534 项基线单元测试通过。

### P1 身份、命令和状态

- `BackdropState` 改为 DefinitionId、FactoryId、完整 Appearance，加上 Fit/Tint/Placement/Distance；删除 Resource 字段，允许 null patch 清空。
- `backdrop` 主参数改为定义 ID，新增 appearance；命令提交 `BackdropIntent`，规划器按 beat 工作状态顺序解析外观、distance 和 placement。默认外观不依赖当前显示状态。
- Direction schema 升级为 6，旧 3/4/5 明确拒绝；更新状态往返测试、生成命令补全和脚本文档。
- `DirectionActorDefinitionEnvironment` 使用共享目录创建隐藏的 Direction 工厂环境，不创建 Actor，不注册为全局 Actor 状态来源；普通角色绑定也获得有效 BackgroundRoot。
- 背景 Factory 注册 Runtime/Direction 两域。预加载向 Direction 实际 Factory 查询，支持多纹理清单，诊断并跳过非 Texture2D 条目。
- 预加载保留 backdrop 引用顺序，包括重复的外观基线命令；缺少前态的增量外观不猜测。Factory 可用 `IsIncrementalAppearance` 声明这种语义。
- demo-01、manosaba、Live2D 和 headless fixture 已配置显式背景定义；保留原物理资源路径，demo 映射使用 `{locale}` 并保留 flat fallback。原脚本 ID 可以直接作为定义 ID，因此无需改变这些脚本文本。
- `BackdropId` 及命令模块的背景路径依赖已删除。

## P0 基线与迁移清单

真实 GPU 入口：

```sh
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . res://tests/headless/HeadlessTestRunner.tscn -- --majo-background-baseline
```

已记录 Cover/Contain/Stretch、显式 placement、distance=5 和同 Stage 双 View PNG，保存至 artifacts/background-actor/baseline；environment.txt 记录 Godot、渲染后端与目标资源统计。参考尺寸为 640×360，实际输出跟随窗口。Contain 留边与双 View 独立投影已人工检查。

已在移除旧渲染路径前补齐 dissolve/wipe 的 0/0.5/1、背景 FX 和 resize，共 13 张基线。最终 current 图逐文件字节相同，见 `artifacts/background-actor/final-gpu-comparison.json`。资源租约统计见下方最终验收。

| 脚本 | 行 | 已建立显式定义的 ID |
| --- | --- | --- |
| `tests/headless/YarnCommands.yarn` | 17 | `background_dark` |
| `tests/headless/YarnCommands.yarn` | 19 | `background_light` |
| `tests/headless/YarnCommands.yarn` | 47 | `background_dark` |
| `tests/headless/YarnCommands.yarn` | 49 | `background_light` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv01.yarn` | 20 | `Still_140_001` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv01.yarn` | 79 | `Still_140_002` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn` | 22 | `Still_490_001` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn` | 89 | `Background_001_001` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn` | 178 | `Background_025_001` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn` | 214 | `Background_001_001` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn` | 242 | `Background_001_002` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn` | 265 | `Background_001_001` |
| `demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Trial00.yarn` | 49 | `Court_Trial00` |
| `demos/demo-01/assets/scripts/main.yarn` | 11 | `backdrop/courtroom` |
| `demos/demo-01/assets/scripts/main.yarn` | 61 | `backdrop/spotlight` |
| `demos/live2d/assets/scripts/main.yarn` | 6 | `witch-workshop` |
| `demos/live2d/assets/scripts/main.yarn` | 30 | `witch-rooftop` |

身份和序列化调用者已迁移；GPU 探针与纯领域测试使用显式 fixture 身份。

## 最终实现

- **P3**：每个 Stage 持有独立背景 Binding 和私有 ActorManager；相同定义/Factory 复用实例，仅布局变化复用内容。Node2D、Control 和组合二维内容统一通过原生尺寸离屏表面呈现，支持非零/负边界、Fit、placement、Tint、distance、背景 FX 和多 View。Unknown coverage 只做保守诊断。
- **P4**：候选内容、首帧和 Driver 均在 Prepare 中完成。非 Cut 在同一渲染边界冻结源/目标快照；Commit 同步发布，整个事务确认前保留回滚权利。连续替换先收束旧转场；取消、提交失败、确认前完成、Stage 删除和 shutdown 都清理消费者、内容和实例。
- **P5**：恢复先校验全部背景身份与外观，再准备各 Stage；第二 Stage 失败会清理第一个候选。locale 刷新经过同一事务路径。schema 6 仅保存逻辑身份与完整外观，拒绝旧 3/4/5。Factory 多纹理预加载与运行加载一致。
- **P6**：新增仅使用公共契约的 [组合背景插件示例](../demos/background-extension/README.md)，展示 Day/Night、不同原点与尺寸、透明 Control、多个 Sprite2D/Polygon2D 和稳定动画。提取公共 `GodotBackgroundActorBase`，删除 `DirectionTextureLoader`、P1 纹理适配器及旧纹理布局分支，更新脚本说明。
- 统一离屏输出的预乘 alpha 处理，避免透明内容与 Tint 重复变暗；转场结束恢复原输出材质。Screen mask 独立使用自己的 shader，避免与输出材质共享参数。

### 明确边界

内容必须在声明的有限 LocalBounds 内绘制，原生离屏表面每边最多 16384 像素；表面与快照内存计入诊断。禁止通过 CanvasLayer、SubViewport 或 top-level CanvasItem 绕过宿主。稳定背景动画持续更新；转场使用冻结快照，结束接回实时内容时可能产生动画时间跳变。插件内部动画相位存档、实时双输入转场和 Live2D/Spine 背景适配保持方案原定的后续范围。

## 验证记录

- 修改前：`bash tools/test.sh --unit`，534 项通过。
- 解析与背景 preload 集成后：`--fast` 与 `--full` 均通过；full 包含独立 core consumer 和 macOS 导出检查。
- `bash tools/quality.sh --check` 通过。
- 真实 GPU baseline 入口通过，已检查生成画面。
- 最终补充约定 fallback 的 RequiredFactoryId 校验及回归测试；对应单元测试结果见 `artifacts/background-actor/resolver-unit.log`。
- 原有文档删除和实时转场草案修改属于开始实施前的工作区状态，本批保留。

### P1 最终验证

- `bash tools/test.sh --fast`：通过，含 Direction-only Factory 多纹理、未知增量和重复基线集成验证。
- `bash tools/test.sh --full`：561 项单元测试、完整 headless、独立 core consumer、macOS 导出均通过。
- `bash tools/quality.sh --check`：通过。
- Live2D `--live2d-auto`：`LIVE2D_DEMO_OK: 14 lines completed.`，正常 shutdown。
- GPU 命令追加 `--majo-background-current`，将迁移后画面写入 `artifacts/background-actor/current`，保留原 baseline。5 张 PNG 与原基线逐文件字节相同，结果见 `p1-gpu-comparison.json`。
- Live2D 启动时仍有 Yarn TextLineProvider 未设置项目的日志；实施前 full 日志已有相同信息，自动脚本正常完成。本轮未修改该初始化逻辑。


### P2 内容协议与宿主

- 新增 `IBackgroundContent`、`IPreparedBackgroundAppearance` 和可复用的 `BackgroundContentLifetime` / `GodotBackgroundContent`。内容声明固定本地边界及保守覆盖能力；节点与资源由同一租约控制。
- 内置 `BackgroundActor` 返回原始尺寸的 Sprite2D 内容。Prepare 不改旧外观，Commit 同步切换，Rollback 恢复旧外观，ConfirmCommit 结束旧内容所有权；Dispose 在未确认时回滚。迟到加载、取消和回调失败均有清理路径。
- 每份内容使用独立 texture loader scope；关闭所有者后拒绝新租约，现有租约保留节点和资源。直接销毁 Actor 或移除其生命周期树时，先分离内容再释放生命周期根。
- Runtime 宿主以独立 placement 节点做 Cover 和 resize；背景层负责裁剪。背景 Actor 不再暴露 `CurrentView`、`Slot/SetSlot`，slot 由 Manager 按实例持有。诊断和 Actor 状态贡献者同步迁移，恢复直接提交准备内容。
- `ActorMountRoots` 同时供 FactoryContext 和 Direction Manager 创建委托使用。Runtime、Direction 元数据环境和普通角色 Binding 都提供有效的 CharacterRoot / BackgroundRoot。
- `DirectionLayoutCalculator.CalculateBackgroundTransform` 支持三种 Fit、负原点和 placement。P2 阶段曾保留 P1 纹理适配器；最终已由 P3/P4 通用呈现与快照转场取代。
- 新增 13 项纯 C# 测试和 headless 背景生命周期测试：失败与迟到准备、取消、回调失败回滚、未确认释放、冻结期间拒绝回滚后重试、确认后旧消费者、Actor 销毁、Runtime 树退出、slot 恢复、resize、两域实际创建及 100 次切换后的租约回归。

### P2 验证结果

- `bash tools/test.sh --fast`：通过。
- 最终状态 `bash tools/test.sh --full`：574 项单元测试、完整 headless、独立 core consumer、macOS 导出全部通过。
- `bash tools/quality.sh --check` 和 `git diff --check`：通过。
- Live2D 实际渲染自动流程：`LIVE2D_DEMO_OK: 14 lines completed.`，正常 shutdown；原有 Yarn TextLineProvider 初始化日志仍存在。
- 日志位于 `artifacts/background-actor/p2-*.log`。本阶段未把 P0 的部分 GPU 基线标为完整，也未声称完成 P3/P4 的自定义内容显示与快照转场。


## P0–P6 最终验收

- `bash tools/test.sh --full`：574 项单元测试、完整 headless、独立 core consumer 和 macOS 导出通过。
- `bash tools/quality.sh --check`、`git diff --check`：通过。
- Godot 4.7.2 / macOS / forward_plus / Metal：`background-baseline`、`background-alpha`、`background-extension`、`camera-cut`、`repeated-wall`、`layered-masks`、`render-restore` GPU 探针通过。运行入口为 `Godot --path . res://tests/headless/HeadlessTestRunner.tscn -- --majo-<探针>`；基线比较需追加 `--majo-background-current`，避免覆盖旧 baseline。
- 13 张内置背景 PNG 与旧路径逐字节一致；透明像素验证覆盖 Tint、dissolve/wipe 的 0/0.5/1、冻结源及实时接回。已检查自定义 Control 与双 Stage 输出。
- 自定义插件连续 100 次切换后，保留的仅为两个 Stage 的两份实时内容、两个 Binding、四个纹理租约，快照为零；shutdown 后全部归零。故障测试覆盖资源加载后失败、首帧前取消、提交回调失败、确认前回滚、迟到结果和非法挂载。测量文件：`custom/metrics-*.json`。
- 真实恢复检查连续执行 200 次，检查 600 帧，候选全部释放，节点稳定为 62。修正该探针直接创建角色时遗漏 SubjectVisibilityLayer 的 fixture 配置。详见 `build/restore-render-evidence/results.json`。
- 独立背景示例：`BACKGROUND_EXTENSION_DEMO_OK`；Live2D 自动流程：`LIVE2D_DEMO_OK: 14 lines completed.`，两者正常 shutdown。
- 最终日志及图像位于 `artifacts/background-actor/final-*`、`current/`、`alpha/`、`custom/`。启动时原有 Yarn TextLineProvider 日志仍存在，不影响完成流程；测试故障注入日志不代表验收失败。

## Trial00 注入背景迁移补修

`Court_Trial00` 是 `ManosabaCourtPreparation` 动态生成的 `HorizontalRepeatTexture2D`，不是 PNG。初版迁移误将它加入普通图片定义，文件 override 绕过 RegisteredResourceProvider；场景钩子的旧注册键也没有新外观协议的 `/Default`。现由场景准备类同时提供独立背景定义和 `backgrounds/Court_Trial00/Default` 注册键，不设置文件覆盖路径，控制器使用该定义。

新增默认 headless 回归和 `--majo-trial-court` GPU 入口，验证场景注入、Factory 预加载、Actor 实际纹理、15 段逻辑尺寸与首/中/尾像素，以及恢复钩子的注册复用。`--fast`（574 项单测及集成）、质量检查、专项 GPU 均通过。真实 Trial00 场景已确认背景显示与资源就绪；证据在 `artifacts/trial00-background/`。定时退出真实 Demo 时，调试面板 `DefaultDebugOverlay.Dispose` 另出现已释放 OptionButton 的清理异常，此项未在背景修复中修改；不将该次 Demo 启停记录为全部通过。

## 背景资源约定恢复

背景统一 Actor 呈现不再要求作者为普通图片配置定义。`ResolveBackdropAppearance` 优先使用显式定义；无定义时按原 `BackgroundsPath/id` 逻辑键生成内置背景定义，默认外观不追加 `/Default`。带目录前缀的 ID 不重复拼接，资源提供者继续负责本地化、文件扩展名和动态注入。规划、预加载、Binding 创建与恢复预检均使用同一规则；显式定义的错误与存档 Factory 身份不一致仍直接报错。

已移除 demo-01、Live2D 和 Manosaba 中为初版迁移增加的普通背景定义。Trial00 回到 `backgrounds/Court_Trial00` 注入键，同样不需要 Actor 定义；上节的 `/Default` 专用定义修补已被这一通用规则取代。

新增资源键、目录前缀、默认/指定外观、显式定义优先与 Factory 身份回归；Yarn 集成直接加载无定义图片，Trial00 覆盖无定义注入、预加载与实际存档恢复，真实 GPU 校验 15 段内容。Live2D 无背景定义自动流程完成 14 行。验证日志位于 `artifacts/backdrop-conventions/`。完整回归中的分层角色清理断言改为跨过完整帧再检查 QueueFree，避免在帧起始提前判断节点泄漏。
