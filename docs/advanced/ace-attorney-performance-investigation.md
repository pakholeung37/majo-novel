# Ace Attorney 帧率初步排查

日期：2026-09-14。设备：Apple M4，Godot 4.7.2 Mono，Metal / Forward+。本次只调查，没有修改正式帧率配置或渲染策略。

## 是否主动限帧

全仓库运行代码及 project.godot 未发现设置 MaxFps、VSync、低功耗或帧延迟的代码（测试目录有主动关闭 VSync 的测试）。独立运行 demo 时实测：

| 项目 | 运行值 |
|---|---|
| Engine.max_fps | 0，无显式上限 |
| DisplayServer.window_get_vsync_mode() | 1，开启 |
| 显示器报告刷新率 | 120 Hz |
| OS.low_processor_usage_mode | false |
| application/run/frame_delay | 0 |
| physics_ticks_per_second | 60；这是物理更新频率，不是渲染上限 |
| 窗口及 Direction 表面 | 960×640 |

默认开启 VSync，因此显示节奏受刷新率约束。加 `--disable-vsync --max-fps 0` 后运行值确实变为 VSync=0，但 demo 和空场景仍约 120 FPS。现有证据只能确认此设备/显示路径的有效节奏接近 120，不能区分 Metal、系统合成器或显示调度中的具体原因，不能据此认定 demo 的算力上限只有 120。

参考：[Godot Engine.max_fps](https://docs.godotengine.org/en/4.4/classes/class_engine.html#class-engine-property-max-fps)。本机版本的 `Godot --help` 也确认 max-fps=0 为不限帧，disable-vsync 不保证覆盖驱动层强制同步。

## 帧间隔实测

独立窗口，非 headless；预热 3 秒，再采样约 7 秒，使用单调墙钟 Time.get_ticks_usec() 统计主循环间隔，避免 delta smoothing 影响。除第一行外均关闭 VSync。静止组停留在当前脚本首句真宵对白，活动组使用现有 `--ace-attorney-auto` 自动推进，不能直接等同于玩家阅读节奏。

| 模式 | 平均 FPS | 中位 ms | P95 ms | P99 ms | 最大 ms |
|---|---:|---:|---:|---:|---:|
| demo 静止，默认 VSync | 119.42 | 8.330 | 8.715 | 10.787 | 26.419 |
| demo 静止，关闭 VSync | 120.00 | 8.332 | 8.752 | 9.046 | 9.541 |
| 空场景，关闭 VSync | 120.01 | 8.332 | 8.976 | 9.142 | 9.492 |
| demo 自动推进，关闭 VSync | 119.57 | 8.321 | 11.207 | 17.750 | 24.501 |

活动组平均值接近 120，不能掩盖尾部帧耗时超过 8.33ms 的事实。需要区分稳态成本和切换尖峰。这是短时初测，不是多轮稳定性验收；未记录逐帧窗口焦点、系统调度和 GPU 频率，不能用单个离群帧精确归因。

## 已确认的渲染冗余

只显示真宵时，32 个 960×640 SubViewport 仍然处于 Always 更新模式：5 个 Direction Stage、5 套各 5 层的 View 表面、Screen，以及旧 Stage 表面。

`GodotDirectionRenderHost.ActivateViewports()` 无条件激活所有 Stage 和 View；创建 View 时也将 Content、Background、TransitionBackground、Composite、View 全部设为 Always。没有按当前 Screen 可达性调度。没有背景转场时，TransitionBackground 仍更新。

临时消融实验仅针对首句固定构图，停止真宵 View 和 Screen 以外的离屏更新：

| 指标 | 原始 | 临时裁剪 |
|---|---:|---:|
| draw calls | 72 | 20 |
| 渲染 CPU 中位耗时 | 0.578ms | 0.293ms |
| 平均 FPS | 120.00 | 118.72 |
| 最大帧间隔 | 9.457ms | 78.388ms |

渲染 CPU 为所有启用测量的 Viewport CPU 渲染时间之和加 frame setup；不是全部游戏逻辑耗时。裁剪组出现单个明显离群帧，未做交替重复，也未做像素等价验证。因此只能说确有多余绘制且 CPU 渲染工作减少，不能宣称裁剪提升 FPS，更不能把这个固定场景特例作为正式修复。

GPU 计时接口返回 0，视为不可用。Performance.TIME_PROCESS 含帧节奏相关影响，本报告不将其作为 CPU 忙碌时间。视频内存监控在原始静止组约 406MiB，开启渲染计时后约 422MiB；不是纯角色纹理占用。

## 切换尖峰的待验证风险

`SpriteSheetCharacterActor.SetAppearance()` 每次都调用 SetPose；即使 appearance 未变，也没有快速返回。`SpriteSheetPlaybackNode.SetPose()` 清空 AtlasTexture 和页引用后同步 ResourceLoader.Load 图集，随后逐渐重建帧纹理。`PrepareAppearanceAsync()` 只验证 manifest 中的 pose，没有准备这些资源。

这确认了同步加载/重建路径存在，但 ResourceLoader 自身可能命中缓存，不能把每次 Load 都说成磁盘 IO 或 GPU 上传。需要给 SetPose、资源加载、状态快照和事务提交添加分别计时，关联超过 8.33ms 的帧，再决定缓存和预载策略。当前默认 Runtime MemoryBudgetMiB=0 也会在最后资源租约释放时驱逐其缓存，需要一起检查预载租约与演员直接加载的衔接。

建议优先顺序：

1. 在用户实际运行方式（编辑器嵌入/独立窗口）、分辨率和切换位置复测，保留默认 VSync 与关闭 VSync 两组。
2. 给切换路径添加耗时分解；避免同 appearance 的无谓图集重建，完善准备阶段与缓存租约衔接。
3. 按 Screen、转场源/目标、捕获任务的实际依赖调度 View；不可直接冻结所有隐藏 Stage，因为 Live2D 子视口、转场和状态恢复存在依赖。
4. 使用 GPU 捕获/平台 profiler 检查带宽和渲染等待；不要依据 GPU=0 判断没有 GPU 成本。

## 产物与限制

本地原始日志及结构化结果：`artifacts/ace-frame-audit/results.json`、同目录日志。临时 GDScript 采样原型也保存在该目录，只用于这次固定构图调查。

临时原型在采样后直接退出 SceneTree，部分运行在异步关闭阶段以 134 退出；帧时间结果已在退出之前写出。它不是已完成正常关闭流程的性能测试入口，也不应直接用作 CI。启动的 TextLineProvider 初始化报错位于预热之前，本次没有证据将它与稳态掉帧关联。

## 后续 trial00 对照修正

同日的前台 trial00 压力测试在关闭 VSync 并停止隐藏 View 重绘后达到 136–144 FPS，排除了不可突破的系统 120 FPS 全局上限。这里的空场景和 Ace Attorney 约 120 只能视为当时测量条件下的结果。详见 [trial00 前台压力测试](trial00-stress-test.md)。
