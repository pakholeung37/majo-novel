# Manosaba Trial00 性能基准

Trial00 是运行时性能优化的固定场景。保留 11 名角色、原图集、背景、镜头、转场、语音、保存和回溯功能；不通过修改素材、缩短演出或开启 skip 制造收益。

## 运行

需要本地 Manosaba 素材和 Godot 4.7.2 .NET。默认使用项目配置的 Forward+、VSync 和 2560×1440 窗口。关闭其他游戏实例，在测试期间保持游戏窗口前台，不手工推进。

```sh
# 构建并连续运行两次快速推进；约两分钟
python3 tools/benchmark_trial00.py --mode rapid --runs 2

# 正常阅读节奏：后续每句停留两秒
python3 tools/benchmark_trial00.py --mode normal --runs 2

# 单独验证 Screen 转场 GPU 快照：默认关闭；检查复制 span，排除 CPU 读回／上传。
python3 tools/benchmark_trial00.py --gpu-screen-snapshots --runs 2

# 指定引擎和输出目录
python3 tools/benchmark_trial00.py --godot /path/to/Godot --output build/trial00-benchmark/my-run

# 汇总已有轨迹，兼容旧版 --trial00-progress 产物
python3 tools/analyze_trial00_progress.py path/to/run
```

两种模式均让第一句停留 28 秒，覆盖 25 秒开场镜头；随后通过注入物理 Space 的按下和释放事件，经 InputMap、GUI、`_UnhandledInput`、输入 scope 和正常对白桥接推进。快速模式每 100ms 尝试操作，正常模式每 500ms；等待上一请求完成后才发下一次。脚本自身的 `wait`、beat join、文字显示和转场结算保持原行为。完整播放 24 句后停在选项菜单，不选择 Replay。

手动运行等价入口：

```sh
godot --path . res://demos/manosaba/ManosabaDemo.tscn -- \
  --trial00-progress --trial00-progress-input --trial00-progress-rapid
```

不传 `--trial00-progress-input` 时，旧探针继续直接调用对白 API。两条路径的调度不同，不能混作输入延迟对照。诊断及逐帧采集只由这些参数启用。

## 指标与证据

每次运行保存 `frames.csv`、`inputs.json`、`spans.json`、`lines.json`、`events.json`、`configuration.json`、`final.png`、`run.log` 和 `summary.json`。批次的 `manifest.json` 记录 Git revision、工作区状态、源码指纹、命令、引擎错误和运行是否有效。

| 指标 | 口径 |
| --- | --- |
| FPS、P50/P95/P99、最大帧间隔 | Stopwatch 单调墙钟测量 `_Process` 间隔；分别统计启动、开场、推进及前台子集 |
| 120 / 60 FPS 预算超限 | 超过 8.333 / 16.667ms 的帧数、比例；另记 >25 / >50ms 和每秒长帧数 |
| 分发完成延迟 | 注入事件至 `IInputManager.Dispatched`，包括 scope 回调执行 |
| 对白响应延迟 | 注入至文字揭示完成（hurry），或对白开始退出（advance） |
| 内容提交延迟 | 在 FramePreDraw 确认文字可见、alpha 非零，并已完整揭示／换成下一句后，记录该帧 FramePostDraw |
| 下一句准备延迟 | 注入 advance 至下一次 LinePresented，包含脚本、资源准备及主动等待 |
| 首句时间 | 探针创建至第一次 LinePresented；包含配置、运行时初始化、资源准备 |
| 分配与 GC | 进程累计托管分配差值、MiB/s、各代 GC 次数 |
| 内存 | 每秒采样 RSS，逐帧记录托管堆和 Godot 总视频内存；各口径分别报告峰值和末值 |
| 渲染规模 | Draw calls、节点数、pipeline 计数及可用的原生渲染计时 |

输入统计只汇总前台样本，保留未分发、未响应和未完成内容提交的计数。最后一次 advance 进入选项菜单，没有下一句文字，单独标记 `TerminalOptions`。进入下一句的时间可能包含数秒的作者指定等待，不能据此认定输入卡顿。

`Input.ParseInputEvent` 是应用内合成输入，排除设备和操作系统送达时间；FramePostDraw 表示 Viewport 更新阶段结束，不能当作显示器出光或 GPU 硬件完成时间。定义参见 Godot 官方 [Input 文档](https://docs.godotengine.org/en/stable/classes/class_input.html#class-input-method-parse-input-event)与 [RenderingServer 文档](https://docs.godotengine.org/en/stable/classes/class_renderingserver.html#class-renderingserver-signal-frame-post-draw)。

工具以退出 0、24 句完成、推进阶段至少 99% 前台、全部输入样本前台且无丢失响应、无意外引擎错误和真实 renderer 标识为有效条件。后台运行仍保留证据，但不用于性能收益判断。该有效标记只检查测量完整性，不代表达到某个 FPS SLA。

GPU 查询全零记为不可用，不解释成 GPU 耗时为零。异常回绕的纹理内存计数丢弃。RSS、托管堆和视频内存可能重叠，不相加；帧预算超限也不等于显示器实际丢帧。比较时需保持硬件、渲染器、分辨率、VSync、轨迹、构建和探针一致，使用多次运行范围，不能与历史机器状态下的数据直接相减。

## 2026-11-17 首轮修改

- 两张角色转场输入在同一次完整渲染提交中等待就绪，取消源就绪后再等目标的串行等待。提交、取消和释放契约保持不变。
- 为表情预热增加独立准备接口。内置实现实际渲染两对保留捕获表面，停止更新后保留 GPU 纹理，不做 CPU 图像冻结／重新上传。显式 snapshot 仍冻结；自定义服务默认回退到原 snapshot 行为。
- 旧背景表面只在背景转场持有需求时更新，同时受 View 的可达性约束。需求按注册实例计数，覆盖重叠转场、隐藏 View、临时实时镜头和 reset 后的旧租约释放。未依赖 Godot `WhenVisible` 的上一帧消费状态。
- 在扫描 InputMap 前过滤不能触发按下绑定的释放、echo 和指针移动事件。摇杆轴保留按 action deadzone 匹配，动态绑定和 GUI 消费规则不变。

渲染回归同时检查：预热输入已就绪且仍是 GPU 纹理、显式 snapshot 仍为冻结图像、取消释放输入、背景停绘保留纹理、重启第一帧刷新、重叠需求和 reset 隔离。其余真实 GPU 套件覆盖背景混合、实时角色源、遮罩、同帧切镜头及保存恢复。

## 本机复测结果

2026-11-17，Apple M4、Godot 4.7.2 Mono、Metal Forward+、2560×1440、120Hz、VSync Enabled、MaxFps=0。对照与优化版本使用相同探针；均为快速模式，完整 24 句、48 次合成输入，推进段和输入样本全部前台，退出 0，无意外引擎错误。

对照临时恢复串行输入等待、snapshot 预热、全 View 表面更新和原输入扫描，运行后恢复最终代码。两轮对照为 `build/trial00-benchmark/control/run-{1,2}`，随后两轮优化为 `build/trial00-benchmark/optimized/run-{1,2}`；各批次 manifest 包含源码指纹。对照切换细节保存在 `build/trial00-benchmark/control-source/control.patch`。

下表给出两次运行的范围，不将逐帧样本当作相互独立的重复实验。

| 指标 | 对照，两轮 | 优化，两轮 |
| --- | ---: | ---: |
| 推进 FPS | 69.34–74.25 | 77.94–81.52 |
| 推进 P95 ms | 22.32–23.41 | 20.79–22.18 |
| 推进 P99 ms | 28.29–28.97 | 26.63–28.95 |
| >16.67ms 帧比例 | 18.41–24.04% | 13.51–15.69% |
| 推进最大帧间隔 ms | 62.27–115.92 | 88.82–108.22 |
| advance 至下一句可见内容提交 P50 ms | 82.73–90.01 | 64.31–70.51 |
| advance 至对白退出响应 P50 ms | 15.48–15.82 | 12.67–13.23 |
| hurry 至完整文字提交 P50 ms | 26.48–30.30 | 27.95–28.00 |
| 首句时间 s | 10.26–12.55 | 9.20–9.52 |
| 预热图像冻结次数 | 44 | 0 |
| 图像冻结同步耗时合计 ms | 436.40–463.59 | 0 |
| 角色输入准备 span 中位 ms，含启动 | 39.78–40.55 | 5.96–7.99 |
| 推进托管分配 MiB/s | 3.00–3.02 | 2.95–2.98 |

明确消除的是 44 次 GPU 图像冻结和空闲旧背景表面的持续更新，并减少转场准备的等待。两轮整段指标改善，但这是依次执行的两轮对照与两轮优化，仍受系统和热状态影响；不能把全部 FPS 差值归因于某一处修改。hurry 延迟没有显示稳定改善，P99 仍约 27–29ms，长帧上限仍约 100ms，尚未达到稳定 120 FPS。

advance 的内容提交时间包含剧本行为。第 22 句之后约 5.6 秒的等待仍保留在原始样本中，因此其 P99 不能解释为纯输入处理延迟。快速模式每类只有 24／23 个输入样本，P95/P99 接近极值，后续需更多重复运行。

此前校准中的 `build/trial00-benchmark/final/run-1` 推进阶段为 0% 前台，工具已标记无效，未进入上表。最早直接调用对白 API 的轨迹、未包含文字 alpha 检查的校准轮，以及历史 2026-09 数据也未混入比较。

正常节奏另跑一轮 `build/trial00-benchmark/normal/run-1`：24 句、29 次输入、全部前台、无丢失或未响应输入，退出 0；推进 FPS 86.77，P99 21.39ms。该轮用于覆盖正常模式，没有相同正常模式的修改前对照，不用于计算性能收益。

验证：零构建警告／错误；最终 `--fast` 通过 706 项 C# 单元测试、Godot headless 集成及失败退出码检查，新增三项分析器测试已纳入该 gate；13 项真实 Metal 渲染套件通过。证据分别为 `build/trial00-benchmark/tests-final/results.json`、`build/trial00-benchmark/rendering-final-acceptance/results.json`，修改文件的 C# 空白格式与 `git diff --check` 通过。

两轮优化的最大推进尖峰都出现在第 8 句 `AwaitingAdvance`，未伴随 GC 计数变化，也没有已有 span 覆盖。这是下一轮需要补充主线程／Metal 时间线的位置；现有计时不足以确定其根因，不将它归因于角色准备或资源加载。
