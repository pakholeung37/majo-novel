# SGRE 相遇 demo

约一分钟的《STEINS;GATE RE:BOOT》片段，台词截取自 `SGRE-converted/scenarios/resg01_04.ks.json` 的 `*dummy2`，第 34–51 条。背景、BGM、日语语音和红莉栖模型与源剧情对应。点击或按空格推进。

红莉栖是实时 E-mote 角色：`emote/emotePlayer.js` 基于 `SGRE-animation` 的求值器，每帧计算时间线、变量、形变网格和遮罩；Godot 绘制原始贴图及网格，不使用预渲染人物帧。`<<emote motion 红莉栖 motion:疑問>>` 可播放源模型动作，`appearance:` 切换源表情。眨眼、物理和说话口型实时求值；口型由语音播放状态驱动，是程序模拟，并非逐字音素同步。

当前 JavaScript 求值器使用系统 JavaScriptCore，**此 demo 目前仅支持 macOS**；模型求值和绘制均在 Godot 进程内完成，运行时不需要 Node、浏览器或本地服务器。与原作引擎相比，部分复杂混合模式和运动细节仍是近似实现。

运行所需的游戏素材不随仓库提供；请先按下方说明准备到本地 `assets/`。游戏素材和剧情属于第三方内容，不适用项目的 MIT 许可证。使用 Godot .NET 导入资源并构建后运行：

```sh
godot --path . res://demos/sgre/SGREDemo.tscn
```

自动检查完整剧情，以及实时网格变化、遮罩、motion 和语音触发：

```sh
godot --headless --path . res://demos/sgre/SGREDemo.tscn -- --sgre-auto
```

可额外指定 `--sgre-capture` 进行有窗口的渲染截图检查。日志中的 FPS 为该次运行的实际求值帧率，CPU 时间包含求值及网格提交，不含 GPU 绘制。

需要录制完整演出时，可启用自动推进并让 Godot 按固定帧率写出视频。录制模式按语音长度和阅读时间停留，并在剧情结束后自动退出：

```sh
godot --path . --fixed-fps 30 --write-movie /absolute/path/demo.avi \
  res://demos/sgre/SGREDemo.tscn -- --sgre-record
```

Godot 的 Movie Writer 采用实际窗口缓冲区尺寸；如需严格的 1920×1080 成片，可将输出缩放至该分辨率并转码为 MP4。

准备素材时，先在 `SGRE-animation` 运行 `npm install`，然后从项目根目录运行 `node demos/sgre/build_assets.mjs`。默认读取 `~/web/SGRE-animation` 和 `~/Downloads/SGRE-converted`；可通过 `SGRE_ANIMATION` 和 `SGRE_CONVERTED` 覆盖。脚本只复制模型、贴图和音频并裁切原版 UI，不烘焙动画。

对话界面参考 [RE:BOOT 实机截图](https://www.rpgsite.net/news/20557-steins-gate-reboot-new-story-trailer-gamma-worldline-route)，使用转换包 `motions/main` 中的原始 UI。站位依据源剧本和[对应原版画面](https://www.gamersky.com/news/202608/2184038.shtml)近似重建，原作镜头投影与 demo 的舞台坐标并非逐项等价。
