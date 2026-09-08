# 开发者场景资源准备

脚本只消费 ResourceKey。拼接、程序绘图、外部资源转换等特殊需求在开发者代码中完成，不需要增加脚本命令或外部 schema。

## 注册准备逻辑

在 MajoRuntimeBootstrap 配置中注册与 Yarn `scene:` 标识对应的回调：

```csharp
ConfigureScenePreparation = scenes => scenes.Register(
    "act01_chapter01_trial00", ManosabaCourtPreparation.PrepareAsync),
```

回调签名为 `Task PrepareAsync(ScenePreparationContext context, CancellationToken cancellationToken)`。context 提供 ProjectId、SceneId、Reason（Enter / Restore）和 Resources。

进入显式场景边界时，runtime 清理旧舞台后等待回调完成，再继续执行场景脚本；读档恢复 Direction 状态前也等待同一回调。普通节点跳转不会重复执行场景准备。异常或取消会传播给调用方，不会忽略错误后继续使用缺失资源。

回调应可重复执行：已注册资源可直接复用。它负责资源准备，不应启动对话或修改舞台状态。Godot 纹理操作应保留在 Godot 主线程，勿直接移入 Task.Run。

## 注册生成资源

```csharp
var key = new ResourceKey("backgrounds/Court_Trial00");
if (context.Resources.Exists<Texture2D>(key)) return;

// 开发者自行准备 image 的尺寸与内容。
var texture = ImageTexture.CreateFromImage(image);
try
{
    context.Resources.Register<Texture2D>(key, texture, value => value.Dispose());
}
catch
{
    texture.Dispose();
    throw;
}
```

随后脚本使用普通背景命令：

```yarn
<<backdrop Court_Trial00 at:0,100 origin:0,0 scale:1>>
```

注册资源参与正常 Exists / Load / Lease 解析，优先于文件 provider；带显式 OverridePath 的请求仍按路径解析。注册对象可以通过兼容基类请求。重复注册同一 key 会报错，不能隐式覆盖已使用资源。

注册持有独立于加载缓存的强引用，因此最后一个 lease 释放、缓存淘汰或 runtime reset 都不会撤销注册。`Unregister(key)` 撤销注册；存在加载任务或未释放 lease 时拒绝撤销。主动使用 Load 获得裸引用的代码必须自行保证使用期间不撤销注册，建议使用 Lease。

传入 release 回调表示在 Unregister 或 runtime shutdown 时由 ResourceManager 调用一次；未传入时，资源的释放仍由开发者负责。场景退出不自动撤销注册。开发者负责生成策略、尺寸、资源预算及提前释放策略；框架提供等待时序、资源查找和明确的所有权边界。

存档保存资源 key 和背景摆放参数，不保存 GPU 纹理。冷启动读档由 Restore 准备回调重建同一 key。请保持场景标识和资源 key 稳定，并确保从保存的参数能够使用新生成的资源。

完整实例见 `demos/manosaba/ManosabaCourtPreparation.cs`。
