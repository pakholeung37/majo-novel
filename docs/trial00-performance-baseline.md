# Trial00 性能基线（2026-09-10）

## 复现

Apple M4，Godot 4.7.2 Mono，Metal / Forward+，项目默认窗口配置。两轮真实渲染运行，直接进入 Trial00，停留在首句，不自动推进。

```sh
dotnet build --no-restore
/Applications/Godot_mono.app/Contents/MacOS/Godot --path . -- --trial00-profile
```

参数仅启用校准：跳过菜单，打印前 20 秒的帧间隔、RSS、托管堆、Godot 静态内存及渲染内存。之后停止采样，游戏继续运行。正常启动入口不变。计时从控制器创建探针起算，不包含进程启动、引擎初始化及此前的场景加载。每 500ms 或帧间隔 >=50ms 采样，因此峰值是采样最大值；首个 gap 是到首帧的等待，不能作为普通长帧统计。NodeAdded 也会在节点重挂时触发，不能把日志条数当作唯一视口数量。

日志：`build/trial00-performance/baseline.log`、`repeat.log`（本地忽略文件）。

## 观测与原因

- 两轮 RSS 采样最大值分别为 1988.0 / 1824.2 MiB；渲染内存报告最大值均为 3303.5 MiB。两者口径不同，Apple 统一内存下不能相加。托管堆最大仅 30.2 / 35.0 MiB，当前证据不支持把峰值主要归因于 C# 对象堆。
- 第一轮约 6.4 秒执行揭幕；Runtime 服务初始化日志区间仅约 333ms。主要等待落在角色资源准备阶段。
- 11 张角色图集像素数 × 4 合计 2432 MiB，导入配置为 `compress/mode=0`、`vram_texture=false`，无 mipmaps。磁盘压缩不等于显存压缩。AnAn 单张 512 MiB，第一轮与前一张完成日志间隔约 1.575 秒。Ema/Hanna/Sherry/Alisa/Margo 各 256 MiB，其余各 128 MiB。
- `ResourceLookAheadWindow.FillAsync` 逐项 await 加载，启动 `PrepareStartAsync` 等待 ahead。因此异步加载虽能让帧继续执行，加载总等待仍累积。重复出现的 resource.load_completed 不足以证明重复解码，底层使用 Godot CacheMode.Reuse。
- 法庭背景准备会同步读回图片、解压、Lanczos 缩放、拼接及上传 15360×1024 RGBA 纹理，仅最终像素为 60 MiB，另外有中间副本。它是开场开销之一，无法单独解释约 2GiB RSS。
- 第一轮开场后约 7.1–7.3 秒出现 78.5 / 80.0 / 76.5ms 帧间隔。第二轮相近阶段出现 61.8 / 80.8 / 68.5 / 91.3ms，之前还出现 158ms 间隔。
- 脚本揭幕后 0.6 秒修改 Alisa、AnAn 表情，再显示首句。该阶段建立 AppearanceSource/Target 两个离屏输入；Alisa 每个为 900×2303，AnAn 每个为 1450×2105。代码按全部图层包围盒创建 SubViewport，首次渲染后禁用更新，过渡完成再释放。同期渲染内存从 3206.0 升至 3303.5 MiB，首句语音与后续语音预加载也发生在这一区间。

离屏分配/首次绘制与卡顿的时间关联已重复确认，但尚未以关闭过渡的 A/B 或 GPU profiler 分离视口分配、shader/pipeline 首次使用、语音及驱动释放的各自耗时，不能把所有长帧都断言为 shader 编译。

## 本轮修复：优先解决开场卡顿

约束：不改原始素材、导入参数或演出脚本；继续完整加载角色，不引入镜头驱动的加载。允许加载期增加准备时间。

- `GodotActorCapturePool` 在场景内复用两对离屏画布。角色异步创建时实际绘制两个过渡输入，使分配和已有材质的首次使用落在加载期。正在使用的画布不会被其他过渡复用或调整尺寸；并发超过两对时临时扩容，归还时回收多余画布。
- 画布容量随已创建角色的最大尺寸增长；逻辑尺寸通过 viewport 的 2D override 保持，overlay 对应缩放，保持原有角色尺寸及自定义过渡 shader 的归一化 UV。源素材完全不变。
- 过渡结束仅归还画布，移除本次图层副本；不再每次销毁/重建 render target，也不在提交时迁移输入视口。
- `DirectionStateContributor` 仍在原有快照冻结事务内、主线程读取并复制状态；只将已复制的纯数据 JSON 序列化放到后台，等待完成后才提交边界。未关闭保存、回溯或首句快照。

细分计时：角色副本构造约 1ms，遮罩准备约 0.2–4ms，首句文字 prepare 不到 1ms；未修复的首句快照捕获约 25ms，与其他首次执行开销累积到单个约 62ms 帧。只取消表情过渡的临时对照减少了连续长帧，仍保留约 66ms 的首句长帧；该对照代码已移除。只去掉输入视口迁移或禁用 3D 未产生足够改善，最终采用复用和预热。

最终第一轮：揭幕后统计区间（探针 t >= 6750ms，至 20 秒）1511 帧，最大 42.168ms，P99 11.236ms，超过 50ms 为 0。原始版本同阶段有连续约 77–80ms 长帧。日志为 `final-1.log`，全帧数据为 `frames-20260910-084826.csv`。第二轮在相同固定区间（t >= 6750ms，包含揭幕及两次首次表情过渡，到 20 秒）1490 帧，最大 39.776ms，P99 12.830ms，超过 50ms 同样为 0；日志 `final-2.log`、全帧数据 `frames-20260910-085211.csv`。全帧 CSV 另外记录窗口焦点，避免把后续人工操作或失焦导致的慢帧直接归因于渲染逻辑。

代价：两对画布常驻；本机总渲染内存从原始稳定约 3206MiB 增至约 3364MiB（约 +158MiB）。本轮选择用有限的额外渲染内存和加载期准备换取过渡稳定性，没有宣称内存优化。池预热/调整后 Godot 的纹理细分监控出现疑似下溢的无效巨大值，不用该细分值评价内存；这里引用的是总渲染内存。RSS 与总渲染内存不能相加。

验证：

- `dotnet build --no-restore`：0 warning / 0 error。
- `./tools/test.sh --fast`：520 个单元测试、无头集成、失败退出码检查通过。
- 真实 Metal 渲染像素回归通过，包括 Miria/Coco 的默认/愤怒/恐惧过渡端点及遮罩；新增画布并发隔离、扩容不影响正在使用的画布、复用清屏、超额租用和自定义 wipe shader 的 UV/尺寸检查。
- 全仓格式检查存在原有 `CameraPresetDefinition.cs` 与 `GodotDirectionRendering.cs` 的空白格式问题；本轮修改文件已单独格式化，不扩展修改这些无关文件。

基线启动已有 TextLineProvider 的 YarnProject 未设置报错，首轮自动退出曾出现清理异常；它们未在本次修改，最终探针到 20 秒只停止采样，不退出游戏。当前结果只证明 Trial00 开场窗口的改善；新材质首次出现、超额并发或更大角色引起的池扩容仍可能产生准备开销。

## 墙面重复绘制修复

`ManosabaCourtPreparation` 现在注册 `HorizontalRepeatTexture2D`，逻辑尺寸仍为 15360×1024、15 个 1024×1024 面板，实际 canvas 绘制逐块引用原始 2048×2048 的 Court_Wall。未修改原始文件、导入参数或墙面的脚本坐标；不再读取图片、解压、CPU 缩放、BlitRect 拼接或上传全景图片。资源面板会沿代理找到真实源纹理并按 RID 去重。

移除了额外的 60MiB 全景纹理。与此前稳定总渲染内存约 3364MiB 相比，本轮约 3320MiB，净减少约 44MiB；差额是现在保留使用的 16MiB 原墙面纹理。本轮校准实例失焦，且同时有用户的编辑器游戏实例运行，所以不使用本轮帧率作前后性能结论；验证完成后只结束额外校准进程。

- 构建通过，0 warning / 0 error。
- `./tools/test.sh --fast` 通过（520 单元测试及无头集成）。
- 真实 Metal 像素回归 `--majo-repeated-wall` 通过：缩放、非等比例 cover 裁剪、平铺、三种旋转角度、翻转、面板接缝不漏底、共享源 RID、内存估算去重。
- 像素参考在 `build/wall-repetition-evidence/`；实际 Trial00 截图为 `build/trial00-performance/screen-20260910-090416.png`，校准日志为 `wall-final.log`。

原图现在由 GPU 直接过滤到显示尺寸，取消旧的 CPU Lanczos 中间缩放。合成坐标与布局一致，纹理细节的采样方式与旧实现有所不同。

## 同 beat 切镜头首帧旧画面

Trial00 第 119/120 行同时提交 trial_b 的 camera cut 与 screen show。动态 View 与 Screen 原先是渲染树中的同级 Viewport，Screen 可能先合成并采样到 View 上一帧的纹理。现在为主 View 和动态 View 显式设置 Screen 为渲染父 Viewport，确保 View 先渲染、Screen 后采样；场景节点所有权和脚本保持原样，没有添加等待。

新增真实渲染回归入口 `--majo-camera-cut`：用红蓝空间分区模拟旧、新机位，在隐藏 View 改机位并同帧切换输出后，逐个 FramePostDraw 检查首帧及随后两帧。修改前首帧读到红色旧画面而失败；修改后首帧即为蓝色目标画面。另覆盖主 View 与动态 View 双向反复切换。
