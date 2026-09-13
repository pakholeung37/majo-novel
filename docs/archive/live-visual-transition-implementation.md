# 实时视觉输入实施说明

更新：2026-09-13。

背景、角色外观、镜头和 Screen 共用 `GodotVisualInput` / `GodotVisualTransition`。输入描述纹理、内容坐标边界、预乘 alpha、更新策略与释放回调；Driver 只处理混合，不创建 Actor、停止动画或决定是否截图。此修订取代背景扩展方案初版的默认冻结策略。

## 职责与输入寿命

- `GodotVisualInput.Live` 引用持续渲染的 SubViewport。生产者保持更新，不因转场开始而关闭；纹理依赖交给 Godot 排序。
- `Static` 描述不需要持续渲染的静态纹理；`Frozen` / `Freeze()` 明确表示调用方需要固定帧。冻结不会隐式发生在通用转场服务里。
- `ReadyAsync` 等待完整的 GPU 提交边界（FramePreDraw → FramePostDraw），支持取消；角色离屏输入等待上游依赖。headless 无实际渲染，可显式保留帧调度以验证节点清理。
- 视觉输入可以携带资源/内容租约的释放回调；没有回调时，其生产者由外层事务或呈现作用域持有。`GodotVisualTransition.Prepare` 接管输入句柄，即使 Driver 准备失败也释放它们。结束时先释放 Driver 消费者，再释放两路输入；清理某一路失败不阻止另一路。
- 事务确认、回滚权利与视觉结束仍属于 Direction/Actor 操作。共享渲染句柄不替代领域事务，也不让 Driver 管理业务状态。

## 已接入路径

| 路径 | 当前行为 |
| --- | --- |
| 背景外观/定义切换 | 保留旧、新内容的实时原生表面，不再调用 Freeze；每个 View 分别投影后交给公共混合服务 |
| 角色外观 | `PrepareForCommitAsync` 保持两路临时内容持续渲染，不再在首帧后关闭 Viewport 更新；`PrepareSnapshotAsync` 保留显式冻结语义 |
| 同 Stage 镜头 | 在 Prepare 中建立实时旧镜头并等待首帧；Commit 更新旧构图，使用公共输入与混合服务。适用于非 Cut/Move 视觉转场，不仅是 dissolve |
| 跨 Stage 镜头 / 恢复镜头 | 继续明确使用旧画面冻结输入，目标为实时输入；未承诺源 Stage 持续模拟 |
| Screen | 旧布局的显式冻结输入与实时目标共用同一 Driver 桥接；Move 仍是布局插值 |
| 恢复遮罩 | 保留恢复前固定画面的语义，复用公共 GPU 就绪屏障 |

不同内容不强制使用同一个节点树：角色保留 CapturePool，背景保留原生内容表面，镜头保留共享 World2D。统一的是输入、就绪、混合与持有方式。

## 动态背景与镜头并发

背景内容的动画必须跟随自己的内容树或内容寿命推进。提交新外观不能停止仍被租约持有的旧内容。基类只控制仍在自身容器中的内容可见性；已交给宿主的内容由宿主管理。

同一份内容被多个 View 观察时仅模拟一次。背景转场期间创建的 View（包括临时旧镜头）会加入现有背景混合，并使用当前进度；移除或切换 Stage 时退出。避免两个镜头在同一时刻观察到不同的背景转场结果。

结束时保留已参与混合的实时目标，没有“截图接回实时内容”的时间跳变。连续替换仍先完成旧转场，不承诺混合链无缝接续。

## Live2D 边界

既有的 Live2D 持续动画镜头 dissolve 已纳入公共路径。这里没有把两个镜头误当作两种表情，也没有给 Live2D 新增双模型外观 dissolve。不同表情的两路实时内容、模型参数/物理/事件同步仍需 Actor 适配层提供；通用服务现在可以保持这些输入更新，不会自行冻结它们。

## 验证

- `--majo-live-visual`：公共句柄释放顺序、重复释放、Driver 准备失败、单路释放失败与取消；GPU 检查角色双输入在固定 dissolve 中点持续更新，背景实时 dissolve/wipe 与透明 alpha、显式 Freeze 不随源变化、背景外观确认后的旧动画继续运行、背景与实时镜头重叠和临时 View 清理。
- 默认 headless 套件继续覆盖确认前回滚、加载取消、100 次背景切换及关闭后的租约归零。
- 最终结果与 GPU 图像写入 `artifacts/live-visual/`；内置背景基线比较仍使用 `--majo-background-baseline --majo-background-current`，不得覆盖旧 baseline。

运行入口：

```sh
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . res://tests/headless/HeadlessTestRunner.tscn -- --majo-live-visual
bash tools/test.sh --full
bash tools/quality.sh --check
```

最终验收：`--fast`、`--full`（574 项单测、完整 headless、独立 core consumer、macOS 导出）、质量检查通过。真实 Metal GPU 的 `live-visual`、`background-baseline`、`camera-cut`、`repeated-wall`、`layered-masks`、`render-restore` 全部通过；13 张原背景基线逐字节一致。Live2D 自动流程完成 14 行并正常关闭。背景转场测量为两份实时内容、零快照、四个纹理租约；关闭后全部归零。证据见 `artifacts/live-visual/`。
