# 2D 推拉镜头渲染资源记录

记录日期：2026-09-10。这里记录的是渲染目标的确定性下限，不把它包装成 GPU 实测显存；驱动对齐、mipmap、后备缓冲和材质资源仍会增加实际占用。

每个 Stage 使用一个共享 World2D 渲染目标。每个 View 使用五个同分辨率目标：主体、当前背景、背景转场源、View 合成和最终 View 输出。另有一个 Screen 合成目标。

以 1280×720、RGBA8 颜色缓冲估算：

| 场景 | 渲染目标数 | 颜色缓冲下限 |
| --- | ---: | ---: |
| 单 Stage、单 View | 7 | 24.61 MiB |
| 单 Stage、双 View | 12 | 42.19 MiB |
| 双 Stage、双 View | 13 | 45.70 MiB |

`GodotDirectionRenderHost.GetRenderResourceMetrics()` 在运行时按实际 Stage/View 数量与分辨率返回同口径数据，headless 集成测试会核对双 View 计数。背景转场不再临时创建整张空间输入纹理；源背景目标常驻，以换取跨 Distance 转场时每个 View 的独立投影。

帧耗时必须在目标 GPU、目标窗口尺寸和实际法庭素材上用非 headless 构建采样。headless 的 CPU 调度时间不作为渲染性能结论。发布前至少记录单 View、倾斜双 View，以及背景 dissolve 进行中的 GPU 帧时间。
