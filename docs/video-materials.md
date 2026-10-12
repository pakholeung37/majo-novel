# 视频素材清单

本轮目标：展示 Majo Novel 当前已有的演出与脚本能力，了解是否有人对项目感兴趣。沿用现有 demo，不为视频重写剧情，也不将外部素材 demo 整理为可分发的完整运行包。

## 已确定的范围

| Demo | 素材数量 | 脚本 |
| --- | --- | --- |
| Manosaba | 2 段 | `Act01_Chapter01_Adv02`、`Act01_Chapter01_Trial00` |
| Ace Attorney | 1 段 | [`main.yarn`](../demos/ace-attorney/assets/scripts/main.yarn) |
| Live2D | 1 段 | [`main.yarn`](../demos/live2d/assets/scripts/main.yarn) |

Manosaba 已选脚本：

- [`Act01_Chapter01_Adv02.yarn`](../demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Adv02.yarn)
- [`Act01_Chapter01_Trial00.yarn`](../demos/manosaba/assets/scripts/Act01_Chapter01/Act01_Chapter01_Trial00.yarn)

## 录制工具

专用入口：[`ShowcaseRecording.tscn`](../demos/recording/ShowcaseRecording.tscn)。默认布局为 2560×1440、30 fps，全屏展示原 demo 游戏画面。默认 `technical` 模式沿用同步源码浮层与语法高亮，浮层在左上角，宽 1440、高 610，背景完全透明；源码字号 36，文字描边增强可读性。去掉行号、注释与内部 ID，跳过空行，展示当前位置附近共 10 个显示行（长源码行换行后也计入），中间约 6 行保持清晰。当前执行行通常位于第 5 行，以亮青绿色文字和同色指示条强调；前面保留 4 行，后面保留 5 行，文件开头和结尾补齐可用的上下文。上下两端逐渐透明，随连续滚动淡入淡出。

执行行高亮与平滑滚动由 VM 实际执行的指令位置驱动。`game` 模式只录制游戏画面，适合作为视频开场或剪辑转场。两种模式沿用同一套实际执行位置映射。

字体使用 2 px 实黑内描边、最宽 12 px 的渐淡黑色外晕与偏移投影，将文字与复杂的游戏背景分开。每行先绘制所有阴影，再绘制语法着色文字，避免相邻文本片段的外晕遮住字面。清晰区域的上下文文字接近完全不透明，外晕与投影随上下边缘一起渐隐。

录制前重新编译与导入工程。源码映射使用相同的节点入口变换，并验证指令与已导入的程序一致；不一致时终止录制，避免显示错误行号。记录实际执行的分支和子流程，不按源码顺序猜测进度。

工具使用独立的录制设置命名空间，启用正常逐字播放、自动推进与语音等待。Manosaba 直接从所选节点启动，并在结尾 Replay 菜单处结束，避免回放循环。

保留原脚本的准备命令和开场，剪辑时可参考 `timeline.json` 跳到首句台词或 `curtain open`。Live2D 现有脚本没有 BGM、语音或音效，导出音轨为静音。

```sh
python3 -m venv artifacts/recording-tools
artifacts/recording-tools/bin/python -m pip install -r tools/recording-requirements.txt

# 先检查 25 秒样片。
artifacts/recording-tools/bin/python tools/record-showcase.py live2d --seconds 25

# 独立录制纯游戏画面。
artifacts/recording-tools/bin/python tools/record-showcase.py live2d --style game --seconds 25

# 按当前素材范围依次录制四段完整演示。
artifacts/recording-tools/bin/python tools/record-showcase.py all
```

通过 `--godot` 指定 Godot .NET 路径；可使用 `--fps 60`、`--output <目录>`。默认使用 Godot Movie Maker 按固定帧率离线录制，保留游戏音频。需要真实渲染器，不要使用 `--headless`。逐段录制，保持录制窗口可见；macOS 上并行运行多个录制窗口可能暂停被遮挡窗口的绘制。工具会核对实际电影帧数与脚本时钟，掉帧时终止转码并保留原始文件。

每段输出到 `artifacts/recordings/<时间>/<素材名>/`：

- `script.mp4`：`technical` 模式的游戏与脚本同步展示视频，H.264 / AAC。
- `game.mp4`：`game` 模式的纯游戏视频；需单独录制，不能从已叠加脚本的视频中裁出。
- `preview.png`：视频检查帧；`game.png` 是录制入口保存的游戏视口截图。
- `timeline.json`：逐事件的帧号、时间、文件、节点、行号和执行文本，以及完成状态。
- `record.log`、`encode.log`：录制与转码日志。

`--keep-master` 保留 Godot 原始 MJPEG / PCM AVI；默认在 MP4 转码成功后移除。没有 FFmpeg 时保留 AVI，不生成 MP4。首次运行默认构建与导入工程；只在已完成这些步骤后使用 `--skip-build`。

## 当前预览节奏

采用连续同步源码展示，保留当前执行行的上下文。当前短预览不添加旁白；Live2D 保持原脚本的静音。

## 尚未确定

- 各段最终剪辑的起止点，以及是否进一步调整当前录制布局。
- 视频结构、旁白与字幕；先准备素材，再确定成片形式。
