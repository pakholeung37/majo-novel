# Majo Runtime 插件快速起步指南

## 1. 环境要求与基线版本

- **Godot**: 4.7 C# (Godot .NET)
- **.NET SDK**: .NET 8
- **YarnSpinner-Godot**: 0.3.22
- **Yarn Spinner**: 3.2.0
- **导出模板**: 与 Godot 4.7.2 Mono 完全匹配的宿主平台 export templates（运行完整门禁时必需）

> [!NOTE]
> Majo Runtime 不捆绑外部 YarnSpinner-Godot 源码，宿主项目需单独安装它作为前置依赖。

---

## 2. 干净安装三步法

### 步骤 1：复制插件目录
将 `addons/majo_runtime` 复制到宿主项目的 `res://addons/majo_runtime`。
将兼容版本的 `YarnSpinner-Godot` 安装到 `res://addons/YarnSpinner-Godot`。

### 步骤 2：配置项目 `.csproj` 引入 MSBuild Props
在宿主项目的 `.csproj` 中（例如 `MyGame.csproj`），添加以下两行 `<Import>`：

```xml
<Project Sdk="Godot.NET.Sdk/4.7.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- 导入 YarnSpinner 和 Majo Runtime 的 MSBuild 配置 -->
  <Import Project="addons\YarnSpinner-Godot\YarnSpinner-Godot.props" />
  <Import Project="addons\majo_runtime\MajoRuntime.props" />
</Project>
```

> [!IMPORTANT]
> `MajoRuntime.props` 会自动配置内建的 Yarn VM State Bridge 并排除桥接源码直接编译入主程序集，无需任何外层相对路径引用。

### 步骤 3：在 Godot 中启用插件
1. 打开 Godot 编辑器，在 **Project Settings > Plugins** 中勾选启用 **Yarn Spinner** 和 **Majo Runtime**。
2. 运行一次 `dotnet build` 并在 Godot 中完成初次资源扫描。

---

## 3. 最小配置与场景挂载

插件不强制使用全局 Autoload，每个需要视觉小说能力的场景显式创建一个 `MajoRuntimeBootstrap`：

```csharp
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Configuration;
using Majo.Runtime.Dialogue;

public partial class MyVisualNovelScene : Node
{
    public override async void _Ready()
    {
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
        };

        AddChild(runtime);
        await runtime.InitializationCompletion;

        // 启动 Yarn 对话
        var dialogue = runtime.Host!.Services.Get<IDialoguePlayer>();
        await dialogue.StartAsync(new DialogueStartRequest("main", "Start"));
    }
}
```

对于内置 `char` 和 `back` command，Actor 配置是可选覆盖而不是创建前提。若省略上例中的
`ActorDefinitionConfiguration`，Runtime 会分别按
`characters/<actor-id>/<appearance>` 和 `backgrounds/<actor-id>/<appearance>` 推导资源；
自定义 Actor factory、特殊资源前缀或默认位置仍通过显式配置提供。

Yarn command 的内容错误默认记录后跳过，未知 command 也会继续执行下一条指令。需要严格
会话时可设置 `configuration.Dialogue!.AbortOnCommandError = true`。资源缺失默认使用
`PlaceholderTexture2D` 并记录 warning；`FailOnMissingAssets` 可恢复严格资源加载。

---

## 4. 资源存放约定

默认资源根目录为 `res://assets`：

```text
res://assets/characters/<actor-id>/<appearance>.svg (或 .png, .webp)
res://assets/characters/<locale>/<actor-id>/<appearance>.svg (可选本地化版本)
res://assets/backgrounds/<actor-id>/<appearance>.svg (或 .png, .webp)
res://assets/backgrounds/<locale>/<actor-id>/<appearance>.svg (可选本地化版本)
res://assets/scripts/<project-id>.yarnproject
res://assets/scripts/<project-id>.yarn
res://assets/audio/bgm/<clip-id>.ogg (或 .mp3, .wav)
res://assets/audio/bgm/<locale>/<clip-id>.ogg (可选本地化版本)
res://assets/audio/sfx/<clip-id>.ogg (或 .mp3, .wav)
res://assets/audio/sfx/<locale>/<clip-id>.ogg (可选本地化版本)
res://assets/audio/voice/<locale>/<line-id>.ogg (或 .mp3, .wav)
res://assets/audio/voice/<line-id>.ogg (无 locale 的 fallback)
```

资源按“精确 locale -> 父级 locale -> 对应通道默认 locale -> 无 locale 路径”解析。Dialogue/Text
与 Voice locale 独立；美术、BGM、SFX 使用 Dialogue locale，Voice 使用 Voice locale。只使用
无 locale 路径的简单项目无需额外配置。Runtime 自带 UI 是固定英文参考实现，UI 本地化由项目负责。

以上目录只是默认 `GodotProjectResourcePathResolver` 的约定。项目也可以实现
`IResourcePathResolver`，根据逻辑 key、资源类型和 override path 返回自己的候选物理路径，
并在 Runtime 启动前传给 Bootstrap：

```csharp
public sealed class ProjectResourcePathResolver : IResourcePathResolver
{
    public IReadOnlyList<string> GetCandidatePaths(ResourceRequest request) =>
        [$"res://game-content/{request.ResourceType.Name}/{request.Key.Path}.tres"];
}

runtime.ResourcePathResolver = new ProjectResourcePathResolver();
```

因此 Runtime 内的 loader、Actor、Audio 和 Yarn 仍只使用稳定的逻辑 key；项目可自由调整实际
资源目录。Resolver 返回多个路径时会按顺序尝试，首个存在且类型匹配的资源会被加载。

---

## 5. Yarn Command 与动画 / 状态支持

```yarn
title: Start
tags: intro
---
<<declare $score = 10>>

<<await>>
<<back backdrop appearance:courtroom duration:0.5 operation:bg-fade>>
<<char hero appearance:calm position:left duration:0.5 operation:hero-move>>
<<endAwait>>

Narrator: 两个动画已并发汇合并到达确定终态。 #line:intro-line-1

<<char hero appearance:determined position:center duration:0.3 wait:true operation:hero-focus>>
Narrator: 等待型动画执行完毕。 #line:intro-line-2
===
```

---

## 6. 自定义扩展

### Durable State（M12 基础层）

M12 提供 Snapshot、规范 Envelope、迁移、原子 `user://` Storage、统一保存资格和底层恢复事务。

```csharp
using Majo.Runtime.Core;
using Majo.Runtime.State;

var state = runtime.Host!.Services.Get<IStateManager>();
var eligibility = runtime.Host.Services.Get<IStateEligibility>();
var serializer = runtime.Host.Services.Get<IStateSerializer>();
var storage = runtime.Host.Services.Get<IStateStorage>();

if (eligibility.Check(StateOperationKind.Save).IsAllowed)
{
    var snapshot = await state.CaptureGameAsync();
    var envelope = state.CreateEnvelope(
        snapshot,
        StateScope.Game,
        MajoRuntimeMetadata.PackageVersion,
        configuration.State!.GameBuildId,
        configuration.State.StorageNamespace);
    await storage.WriteAsync("manual/example", serializer.Serialize(envelope));
}
```

自定义 Contributor 必须显式声明 `StateContributorCapability`；默认能力是瞬态且不进入磁盘。
使用 `runtime.ConfigureStateMigrations` 注册 schema/key migration 或明确移除的旧 key。宿主玩法在
不可保存的外部阶段使用 `IStateEligibility.AcquireBarrier`，不要在保存请求中强制完成动画或停止
Dialogue。默认 Serializer 与 Storage 都可通过 `ConfigureServices.ReplaceSingleton` 替换。

### Dialogue Travel（开发调试）

Dialogue Travel 使用内存中的 Game Snapshot 时间线，在每条台词完整显示并进入
`AwaitingAdvance` 后自动捕获稳定 checkpoint。它不会创建存档文件，也不与 History 或
Played Register 共用数据：

```csharp
using Majo.Runtime.Travel;

configuration.State!.DialogueTravel.Enabled = true;
configuration.State.DialogueTravel.Capacity = 128;
configuration.State.DialogueTravel.ShowDefaultUi = true;

var travel = runtime.Host!.Services.Get<IDialogueTravelManager>();
await travel.StepBackAsync();
await travel.StepForwardAsync();
await travel.TravelToAsync(travel.Entries[0].Sequence);
```

默认只在 Godot 编辑器运行环境创建该服务；如需在导出的 Debug build 使用，显式设置
`EnableInExportedDebugBuild = true`。`Backspace` / `←` 回退一步，`→` 前进一步，`F4` 打开
默认时间线 UI；手柄对应 D-pad 左/右。回退后尚未推进时可以恢复保留的 future；从过去重新
推进会截断旧 future 并形成新分支。只能准确跳转到
已经捕获的 checkpoint，不能凭 Line ID 跳到尚未执行过的未来剧情。New Game 和 Return To
Title 会清空内存时间线；Load Game 会清除旧分支，并以读档恢复出的当前行作为新时间线起点。

### Save / Load Slot（M13）

Slot 默认关闭，启用后才会创建玩家文件以及 Quick Save、Save/Load UI 的输入 Scope：

```csharp
using Majo.Runtime.Save;

var configuration = MajoProjectConfiguration.CreateDefault();
configuration.Save!.Enabled = true;
configuration.Save.ManualSlotCapacity = 20;
configuration.Save.QuickSlotCapacity = 3;
configuration.Save.AutoSlotCapacity = 5;
configuration.Save.ShowDefaultUi = true;
configuration.Save.CaptureThumbnails = true;

var slots = runtime.Host!.Services.Get<ISaveSlotManager>();
var slot = new SaveSlotId(SaveSlotKind.Manual, 1);
if (slots.CanSave(slot).IsAllowed)
{
    await slots.SaveAsync(slot, new SaveSlotSaveRequest("第一章"));
}

if ((await slots.CanLoadAsync(slot)).IsAllowed)
{
    await slots.LoadAsync(slot);
}
```

Manual、Quick、Auto 是类型化 Slot，不依赖字符串前缀。Quick/Auto 各自按配置容量确定性轮换；
`F5` 触发 Quick Save，`M` 打开默认 Save/Load UI。`RenameAsync` 只修改玩家可见名称，不改变
Slot ID、类型、保存时间或 Snapshot 身份。

Auto Save 可配置 `AutoSaveNodeIds`、`AutoSaveLineIds`、`AutoSaveMetadata`，宿主也可调用
`ISaveProductManager.RequestAutoSaveCandidate`。候选条件本身不会立即保存；只有关联台词进入
`AwaitingAdvance` 且统一 Eligibility 仍允许时才提交，同一安全边界最多一次。缩略图捕获或编码
失败只记录降级原因，不影响主存档写入和加载。

### Settings 与 Global State（M14）

`Audio`、`Presentation`、`Auto` 和 `Settings` Resource 提供项目默认值；玩家值由
`ISettingsManager` 持有，启动时从独立 Settings Scope 恢复。更新会立即投影到音量、打字速度
和 Auto deadline，并在短 debounce 后原子落盘：

```csharp
using Majo.Runtime.Settings;

var settings = runtime.Host!.Services.Get<ISettingsManager>();
settings.Update(settings.Current with
{
    VoiceVolume = 0.8f,
    LettersPerSecond = 48,
    AutoWaitForVoice = false,
});
await settings.FlushAsync();
```

默认 `S` 打开 Settings UI；UI 获取 Modal Input Scope，并可通过
`SettingsConfiguration.DefaultUiScene` 整体替换。损坏的 Settings 文件会被隔离后回退当前项目
默认值，不阻止启动。`IGlobalStateManager` 为 Global Contributor 提供 `MarkDirty()`、debounce
和 `FlushAsync()`；默认同样隔离损坏文件，也可设置 `GlobalState.StrictRecovery = true` 改为
严格启动失败。Global 与 Settings 都不会进入 Game Slot，也不会被 New Game / Load Game 回退。

### Played Register 与 Skip（M15）

`IPlayedRegister` 只在 `LineDisplayCompleted` 后，以 Project ID 与稳定 Line ID 标记已读，并作为
Global Contributor 通过 debounce 写盘。`IPlaybackModeManager` 统一拥有 Manual、Auto、
`SkipReadOnly` 和 `SkipAll`；默认 `K` 切换 Settings 指定的 Skip 模式。Read-only Skip 使用展示
开始时冻结的 `WasPlayedBeforePresentation`，精确停在第一条此前未读台词；选项会结束 Skip，
History、Save/Load、Settings、Debug modal、restore 与外部 pause handle 会暂停 Skip。

需要让当前台词关联动画直接到终态时，为台词添加 `majo:operation-group=<id>` metadata，Operation
使用相同 group 并显式传入 `isSkippable: true`。Skip 只完成该组中已标记的 Operation，不影响宿主
玩法或其他组。可选源内容指纹使用 `majo:fingerprint=<fingerprint>`，不会基于 substitutions 或最终
渲染文本计算。

### 自定义 Yarn Command
```csharp
[RuntimeCommand("setScore", Category = "Game")]
public sealed class SetScoreArguments
{
    [CommandParameter("value", 0, Primary = true, Required = true)]
    public int Value { get; set; }
}

runtime.ConfigureCommands += commands =>
{
    commands.Register<SetScoreArguments>((arguments, context) =>
    {
        var services = context.Require(context.Services, "ServiceResolver");
        services.Get<ScoreService>().SetScore(arguments.Value);
        return Task.CompletedTask;
    });
};
```

### 自定义 Service
```csharp
runtime.ConfigureServices += services =>
{
    services.AddSingleton<ScoreService>(
        resolver => new ScoreService(resolver.Get<IRuntimeLifetime>()),
        typeof(IRuntimeLifetime));
};
```

---

## 7. 自动化测试与 CI 门禁

在仓库根目录执行：
```sh
./tools/test.sh
```
开发中需要快速反馈时执行：
```sh
./tools/test.sh --fast
```
快速模式保留主项目格式检查、Debug 构建、纯 C# 单元测试、Godot 导入、可见 Demo、
一轮 Headless 集成测试和失败码探针。提交或发布前仍应执行默认的完整门禁。

门禁会自动验证：
1. 静态格式与类型诊断；
2. 插件 staging 包清洁度（排除 Demo、测试、构建缓存和仓库专用路径）；
3. 纯 C# 单元测试；
4. 独立 Clean-Consumer 夹具从零安装、编译与闭环运行；
5. Headless 集成测试，以及缺失/不兼容 Yarn 依赖的类型化启动失败探针；
6. Release 构建与宿主目标平台 Godot export smoke 测试。
