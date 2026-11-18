# Trial00 独立渲染基线

这个 Godot 项目直接读取本地 Trial00 的法庭墙面、讲台和 11 名角色的原始图集及图层清单。资源准备脚本保留现有纹理导入设置。运行时只使用 Godot 和 GDScript。项目自身没有 autoload、.NET 工程、Yarn 或 Majo Runtime；两个视口共享同一组场景节点。

从仓库根目录运行，测试时让游戏窗口保持前台：

```sh
# 每个模式预热 3 秒并采样 12 秒。
python3 tools/benchmark_trial00_render_baseline.py --mode all

# 保持窗口打开，手动查看固定画面。关闭窗口返回。
python3 tools/benchmark_trial00_render_baseline.py --mode split --static --seconds 0

# macOS 上显式指定 Metal，每种模式采样 30 秒。
python3 tools/benchmark_trial00_render_baseline.py --mode all --seconds 30 --driver metal

# 另测关闭 VSync 时的吞吐余量。
python3 tools/benchmark_trial00_render_baseline.py --mode all --no-vsync
```

`--godot` 指定 Godot 4.7.2 的可执行文件，标准版或 .NET 版均可。默认使用仓库本地的 `demos/manosaba/assets`，`--assets` 可指定另一份相同结构的目录。首次运行导入大型图集，会比之后慢。默认窗口分辨率为 2560×1440，`--resolution` 可覆盖，结果记录实际窗口尺寸、VSync 和屏幕报告的刷新率。

- `direct` 在主窗口里直接绘制一个镜头。
- `single` 用一个全分辨率 SubViewport 绘制相同镜头，再合成到主窗口。
- `split` 用两个全分辨率 SubViewport 渲染同一法庭的两个镜头，再合成一个斜切分屏。
- 三种模式使用相同素材和角色默认图层。`--static` 固定镜头，便于做像素对照。

生成的独立项目在 `build/trial00-render-baseline/project`。每次运行按目录保存逐帧记录、最后一帧截图、Godot 日志和 `summary.json`。采样结束后才读回截图。`gap_ms` 表示进程帧间隔，120Hz 的名义预算为 8.33ms。`over_12_5_percent` 用来观察明显偏离 120Hz 节奏的长帧。

这是法庭图层与视口的渲染负载对照。角色使用 Godot 原生图层和简单混合，画面不做原项目的遮罩、背景视差、表情转场、对白、选项和音频。三种模式之间可以对比视口和合成成本，与完整 Trial00 的差值还包含画面、剧情和工作量差异。进程帧时间与显示器实际呈现时间是两个指标。
