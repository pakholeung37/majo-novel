# LayeredCharacter Dissolve 对比实验

状态：用户已确认视觉效果可接受，运行时已采用整图 Dissolve。本场景左侧调用当前生产实现，已不再代表历史差量层算法；如需历史对比，请运行迁移前版本。生产回归见 `tests/headless/HeadlessTestRunner.TransitionRegressions.cs`。

打开 `LayeredDissolveExperiment.tscn`，在 Godot 编辑器按 F6 运行当前场景。需要本地 Ema 角色资产（与现有分层角色测试相同）。也可在仓库根目录运行：

```bash
dotnet build "Majo Rebuild.csproj"
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . --resolution 1200x850 res://tests/experiments/LayeredDissolveExperiment.tscn
```

- 左侧：调用实际 LayeredCharacter.ChangeAppearance，将它创建的 Tween 暂停并以固定步长推进，保存 41 个进度样本。没有重新实现参考算法。
- 右侧：捕获同一角色的完整起点/终点，用一个 shader 混合两个预乘 alpha 输入，再反预乘用于普通 UI 合成。
- 两侧使用相同构图、线性进度和显示转换。拖动进度条或点击 Play；默认停在 50%。
- 下拉选择目标外观，源每次重置为角色初始化后的规范化默认图层集合（包含 manifest 默认启用的身体层）。可以检查 Surprised1、ArmR10、Pale1-2，以及其他组合。
- 支持灰、白、黑背景；重点比较眼睛、嘴、手臂重叠、Multiply 阴影与透明边缘。
- 每次切换外观需等待重新采样；左侧播放是 40 段离散样本，不用于比较实时性能。右侧是静态端点混合，不用于验证动态目标和事务生命周期。

请反馈目标外观、进度、背景及差异位置，以及差异是否影响使用。进度 0 和 1 应首先一致；若端点已经不一致，先排查实验捕获/显示，不据此判断算法。

构建与启动检查可添加 `-- --experiment-smoke`，成功打印 `DISSOLVE_EXPERIMENT_READY` 并退出，截图写入 `/tmp/majo-dissolve-experiment.png`。必须启用真实渲染，不能用 dummy headless 判定画面。

原型使用完整 bootstrap，可能输出现有默认 YarnProject 未配置提示；本实验不运行对话。用户已接受候选效果；不表示新旧算法数学等价，也不表示生产生命周期和性能已通过验证。
