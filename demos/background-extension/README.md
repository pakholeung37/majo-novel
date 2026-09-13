# 自定义背景插件

运行 `BackgroundExtensionDemo.tscn`。自动验收入口：

```sh
Godot --path . res://demos/background-extension/BackgroundExtensionDemo.tscn -- --background-auto
```

示例通过真实 Yarn `backdrop` 命令显示、切换和施加 FX。插件只依赖公开的 Actor、Factory 和资源协议，不引用 Direction 内部渲染类。`Day` 使用有负原点的 Node2D 组合内容；`Night` 使用不同尺寸、不同原点的 Control 内容，保留透明区域。两种外观都有持续更新的三角形动画。

## 接入

```csharp
runtime.AddPlugin(new CompositeBackgroundPlugin());
configuration.Actors.Actors.Add(new ActorDefinitionConfiguration
{
    Id = "landscape",
    FactoryId = CompositeBackgroundPlugin.FactoryId,
});
```

```yarn
<<backdrop landscape appearance:Day via:cut>>
<<backdrop landscape appearance:Night via:dissolve duration:1 wait:true>>
<<backdrop landscape appearance:Day at:50,50 origin:0.5,0.5 scale:2>>
```

省略 appearance 使用 Factory 默认值 Day。同一定义切换外观复用 Actor；两个 Stage 使用同一定义时，各自创建 Actor 和内容资源租约。

## 内容与所有权

可以继承 `GodotBackgroundActorBase`，在 `LoadContentAsync` 中加载资源，通过 `OwnContent` 返回本地内容；同步 `LoadContent` 仅供 Runtime 的稳定设置使用。基类实现准备、提交、确认、回滚和内容寿命，也可以自行实现 `IBackgroundActor`。

- `LocalBounds` 必须是固定、有限且宽高为正的完整本地边界，可包含负原点。更换边界应准备新内容。
- 内容动画应由各自内容树推进；旧外观结束所有权后，只要租约仍存在就必须继续可绘制和更新，不能在外观切换时停止旧内容的动画。
- 内容保留原始坐标；不要自行做 Cover、Stage placement、相机投影或 Stage Tint。宿主会移动内容根到独立表面，因此不要把内容根离开旧父节点当作销毁信号。
- 仅完全不透明的矩形内容可声明 `OpaqueRectangle`；透明或不规则内容使用 `Unknown`。
- 每份内容的资源 scope 交给 `OwnContent`；scope 在所有者关闭且所有消费者释放后回收。示例预加载清单与实际加载使用同样的两张纹理及 override 路径。
- `SceneNode` 必须挂到收到的 Factory roots。内容不得自行建立 CanvasLayer、SubViewport 或 top-level CanvasItem；动态新增普通 CanvasItem 会继承隔离渲染层。
- 当前原生内容表面每边最多 16384 像素；原生表面与快照内存计入渲染诊断。

## 转场语义

稳定内容和转场中的源、目标均持续更新。非 Cut 转场等待首帧后，通过公共视觉输入机制将两路实时表面交给 Driver；每个 View 独立投影，内容只模拟一次。结束后保留已经在显示的实时目标，不经过截图接回。插件内部动画相位仍不属于存档协议。

`Commit` 不加载资源。整个事务确认后旧内容才结束所有权；即使零时长转场先完成，也保留回滚权利。销毁 Actor 会拒绝新租约，已有消费者仍能持有内容。宿主先解除图像消费者，再释放内容和 Actor。

## 验证

- 默认 headless 套件包含自定义背景、两域环境、同实例替换、跨 Stage 恢复失败、迟到取消、非法挂载、locale 刷新及 100 次切换的资源回归。
- `--majo-background-extension` 单独运行插件生命周期测试；真实 GPU 模式额外检查首帧取消，并保存 Control 内容与双 Stage 图像。
- `--majo-background-alpha` 读取透明像素，验证 Tint、实时 dissolve/wipe，以及显式 Freeze 与实时输入的区别。
- `--majo-background-baseline --majo-background-current` 生成内置背景回归图，与迁移前基线比较。

共享机制及边界见 [实时视觉输入实施说明](../../docs/live-visual-transition-implementation.md)。
