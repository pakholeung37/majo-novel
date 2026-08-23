# Majo Runtime 插件快速指南

## 环境要求

- Godot 4.7 C# / .NET 8 SDK。
- YarnSpinner-Godot 0.3.22，对应 Yarn Spinner 3.2.0。
- Majo Runtime 不内置 Yarn Spinner；宿主项目需要单独安装它。

## 安装

1. 将 `game/addons/majo_runtime` 复制到宿主项目的 `addons/majo_runtime`。
2. 将兼容版本的 YarnSpinner-Godot 安装到 `addons/YarnSpinner-Godot`。
3. 在 Godot 的 **Project Settings > Plugins** 中启用 Yarn Spinner 和 Majo Runtime。
4. 确认项目是 C# 项目，并完成一次 Godot 资源导入和 C# 编译。

插件不要求 Autoload。每个需要视觉小说 Runtime 的场景显式创建一个
`MajoRuntimeBootstrap`，这样生命周期和场景树所有权保持清晰。

## 最小配置

可以在 Inspector 创建并赋值 `MajoProjectConfiguration` Resource，也可以用
C# 构造。下面是最小程序化配置：

```csharp
var configuration = MajoProjectConfiguration.CreateDefault();
configuration.Actors!.Actors.Add(new ActorDefinitionConfiguration
{
    Id = "hero",
    FactoryId = ActorFactoryIds.Character,
    DefaultAppearance = "calm",
});

var runtime = new MajoRuntimeBootstrap
{
    Configuration = configuration,
    YarnDialogueRunner = dialogueRunner,
};
AddChild(runtime);
await runtime.InitializationCompletion;
```

默认资源根目录为 `res://assets`，文件名不需要写进配置：

```text
res://assets/characters/hero/calm.svg
res://assets/characters/hero/determined.png
res://assets/backgrounds/backdrop/courtroom.webp
res://assets/scripts/main.yarnproject
```

Actor 纹理由 `characters/<actor-id>/<appearance>` 或
`backgrounds/<actor-id>/<appearance>` 解析；Yarn 项目可通过
`resources.CreateLoader<YarnProject>("scripts").LoadAsync("main")` 加载。
`ResourceConfiguration` 可替换根目录和分类前缀。只有不符合约定的个别资源才添加
`ActorAppearanceConfiguration`；其中 `TexturePath` 是严格 override，路径失效时不会
静默回退到约定资源。

`DialogueRunner` 必须在 Runtime 初始化前赋给 `YarnDialogueRunner`。Runtime 初始化
成功后再启动 Yarn 对话；关闭场景前可以显式等待 `RequestShutdownAsync()`。

## Command 与动画闭环

```yarn
<<await>>
<<back backdrop appearance:courtroom duration:0.5 operation:backdrop-fade>>
<<char hero appearance:calm position:left duration:0.5 operation:hero-move>>
<<endAwait>>
Narrator: 两个动画已经汇合。
<<char hero appearance:determined position:center duration:0.3 wait:true operation:hero-focus>>
Narrator: 等待型动画已经完成。
```

Actor ID 是唯一的位置参数，其余参数使用不区分大小写的 `name:value` 形式，文档统一
采用 camelCase Command 名。动画 Command 默认不等待；`await` / `endAwait` 块会等待
块内所有经 Context 追踪的 Operation。也可使用 `<<await group:setup>>` 或
`<<await operation:hero-move complete:true>>` 精确汇合。`wait:true` 只等待当前
Command 创建的 Operation。Yarn Stop 会取消当前对话通过 Context 追踪的 Operation。

## 扩展

持续验证 Demo 的组合代码位于 `game/demo/RuntimeDemoComposition.cs`，演示三条公共扩展路径：

```csharp
runtime.ConfigureServices += services => services.AddSingleton<MyService>(
    resolver => new MyService(resolver.Get<IRuntimeLifetime>()),
    typeof(IRuntimeLifetime));

runtime.ConfigureActorFactories += actors =>
    actors.RegisterFactory(myActorFactory);

[RuntimeCommand("myCommand", Category = "Game")]
public sealed class MyCommandArguments
{
    [CommandParameter("value", 0, Primary = true, Required = true)]
    public string Value { get; set; } = string.Empty;
}

runtime.ConfigureCommands += commands =>
    commands.Register<MyCommandArguments>(async (arguments, context) =>
    {
        var services = context.Require(context.Services, "ServiceResolver");
        services.Get<MyService>().Apply(arguments.Value);
        await Task.CompletedTask;
    });
```

参数模型通过 `RuntimeCommand` 和 `CommandParameter` 自描述；统一 Binder 负责
`name:value`、类型转换、默认值、未知/重复参数和 Usage 诊断。新增 Command 不需要
修改 Yarn Adapter 或中央 Parser。自定义值类型可通过
`ConfigureCommandConverters` 注册转换器；同一功能域的多条 Command 可封装为
`IRuntimeCommandModule`，通过 `RegisterModule` 原子注册。
推荐每条 Command 将参数类型与 Handler 放在同一文件，模块文件只负责注册；内置
Command 的 `Runtime/Dialogue/Commands/BuiltIn` 目录可作为结构参考。

所有注册必须在 `MajoRuntimeBootstrap` 进入场景树前完成。自定义 Actor Factory
应验证自己的 `ActorDefinition.FactoryData`，并保证 `Destroy()` 能释放创建的 Node。
自定义资源来源通过 `ConfigureResourceProviders` 注册并指定优先级；同一逻辑 ID
的并发异步请求会共享一次 provider 加载。

## 运行演示

在 Godot 中运行项目，或直接打开 `res://demo/RuntimeDemo.tscn`。演示包括：

- 背景和角色并行动画及 Group 汇合。
- `wait=true` 单动画。
- 台词、两个可点击选项与 Yarn 分支。
- 自定义 Service、Actor Factory 和 Command。
- 对话结束后的 Runtime 清理状态。

无交互 smoke test：

```sh
/path/to/godot --headless --path game -- --demo-auto
```

## 测试

在仓库根目录执行：

```sh
./tools/test.sh
```

门禁依次执行限定范围的格式/静态检查、Godot C# 编译、纯 C# 单元测试、
Godot 资源导入、Runtime Demo 主场景 smoke test、三轮完整 Godot/Yarn headless suite，
以及确认失败断言返回非零退出码的反向探针。

如果 Godot Mono 不在默认 macOS 路径：

```sh
GODOT_BIN=/path/to/godot ./tools/test.sh
```
