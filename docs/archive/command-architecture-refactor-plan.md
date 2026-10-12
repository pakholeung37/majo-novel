# Runtime Command 架构重构方案（修订最终版）

> 状态：可实施方案（breaking change）  
> 目标：消除 `Arguments`、handler、全局 `CommandBindingContext` 之间的拆分，同时保留系统级 timing 参数、Direction 运行时语义和 plugin command 扩展能力。  
> 重要决定：本方案不以 source generator 为目标，也不引入 Microsoft.Extensions.DependencyInjection。

## 1. 决策结论

当前 command 的 schema、Arguments、handler、默认值和运行时配置分散在多个位置：

```text
Arguments → CommandDescriptor（反射）
Handler → RuntimeCommandRegistry（delegate registration）
CommandBinder → CommandBindingContext（跨领域 defaults）
Handler → RuntimeCommandContext.Binding（间接读取 defaults）
```

这破坏了 colocation，并且把所有 command 绑定到一个了解 Animation、Audio、Direction 的全局上下文。

最终采用以下边界：

```text
Concrete RuntimeCommand
 ├── command schema
 ├── immutable Arguments
 ├── reusable parameter groups
 ├── domain policy/default dependencies
 └── ExecuteAsync
```

通用 runtime 只负责注册、索引、schema validation、invocation context、生命周期、日志、取消和错误策略。

删除以下旧模型：

- `CommandBindingContext`；
- `CommandDefaultSource`；
- `RuntimeCommandContext.Binding`；
- `IRuntimeCommandHandler<TArguments>`；
- `Register<TArguments>(...)` delegate API；
- writable property + `new()` 约束的反射 binder。

Source generator 不属于本次架构目标。schema 使用普通 C# 类型、显式 schema builder 和 parameter groups 表达；未来如需生成代码，只能作为实现优化，不能改变运行时契约。

## 2. 目标与不变量

1. command 的 schema、参数模型、依赖和行为在同一个类型中可见。
2. handler 只接收已绑定的 immutable Arguments。
3. `duration`、`ease`、`via`、`wait` 等系统参数只声明一次，用户 command 可以复用。
4. 绑定结果必须区分“未提供”“contract default”和“显式提供”。
5. 依赖 beat、当前 node、command 类型或 Direction state 的默认值必须在执行策略阶段解析。
6. Direction 参数复用采用 parameter group/value object 组合，不复制字段。
7. plugin command 可以注册自定义 schema、converter、parameter group 和 command factory。
8. 继续使用现有 `IServiceResolver` / `RuntimeServiceRegistry`，不建立第二套 DI 容器。
9. Runtime catalog、Yarn adapter 和 editor metadata 读取同一个 `CommandSchema`/manifest。

## 3. 核心 API

### 3.1 Execution context

```csharp
public sealed class CommandExecutionContext
{
    public CancellationToken CancellationToken { get; }
    public string ExecutionId { get; }
    public string? SourceLineId { get; }
    public string? DialogueSessionId { get; }
    public ICommandInvocationCapabilities Capabilities { get; }
}
```

`CommandExecutionContext` 只保存 invocation-scoped 数据，不包含 Animation/Audio/Direction defaults，也不暴露完整的 `IServiceResolver`。普通业务依赖通过 command factory 的构造函数注入。

```csharp
public interface ICommandInvocationCapabilities
{
    // 这里只放真正属于一次 invocation 的窄能力；不得提供 Get<T>()、Resolve(Type)
    // 或其它 service-locator API。
}
```

当前代码中需要 `CurrentNode`、dialogue/session 状态或其它动态能力的 command，必须通过显式窄接口注入到 command/policy；不得把 `IServiceResolver` 重新命名后放回 context。

### 3.2 Input

```csharp
public sealed class CommandInput
{
    public string CommandName { get; }
    public IReadOnlyList<string> RawArguments { get; }
}
```

`CommandInput.CommandName` 必须是 catalog 解析后的完整 command name（例如 `camera pose`），不得包含已由 Yarn adapter 消费的 root/child path token；`RawArguments` 只包含参数 token。schema bind 时必须验证 name 与自身 `Name` 匹配，防止 command instance 被错误复用。

### 3.3 Command

```csharp
public interface IRuntimeCommand
{
    ICommandSchema Schema { get; }

    ValueTask ExecuteAsync(
        CommandInput input,
        CommandExecutionContext context);
}

public abstract class RuntimeCommand<TArguments> : IRuntimeCommand
    where TArguments : class
{
    public abstract CommandSchema<TArguments> Schema { get; }

    ICommandSchema IRuntimeCommand.Schema => Schema;

    public async ValueTask ExecuteAsync(
        CommandInput input,
        CommandExecutionContext context)
    {
        var arguments = Schema.Bind(input);
        await ExecuteAsync(arguments, context);
    }

    protected abstract ValueTask ExecuteAsync(
        TArguments arguments,
        CommandExecutionContext context);
}
```

### 3.4 Schema

```csharp
public sealed class CommandSchema<TArguments> : ICommandSchema
    where TArguments : class
{
    public string Name { get; }
    public string? Category { get; }
    public string Usage { get; }
    public IReadOnlyList<CommandParameter> Parameters { get; }

    internal TArguments Bind(CommandInput input);

    public void Validate(CommandBindingEnvironment environment);
}
```

所有 command 都通过非泛型 `ICommandSchema` 暴露给 registry、catalog、dispatcher 和 editor；只有 concrete command 使用 `CommandSchema<TArguments>`。

```csharp
public interface ICommandSchema
{
    string Name { get; }
    string? Category { get; }
    string Usage { get; }
    IReadOnlyList<CommandParameter> Parameters { get; }

    void Validate(CommandBindingEnvironment environment);
}

public sealed class CommandBindingEnvironment
{
    public CommandValueConverterRegistry Converters { get; }
    public IReadOnlyDictionary<string, ICommandParameterGroup> Groups { get; }
}
```

`CommandSchema<TArguments>` 实现 `ICommandSchema`。schema 是单个 command 的契约，不是全局配置容器；它包含 command-specific 参数和显式加入的公共 parameter groups。`CommandBindingEnvironment` 只在 catalog build/bind 阶段使用，不包含 runtime defaults 或 service locator。

为避免 command 在 invocation 时重新查找全局注册表，`BuildCatalog()` 在 validation 成功后把只读的 `CommandBindingEnvironment` attach 到每个 schema；之后 `Bind(CommandInput)` 只使用该已冻结环境。schema、converter、group attach 完成后不得再修改，未 attach 的 schema 不能执行。

schema builder 必须接收显式 immutable factory；参数 selector 只用于 metadata/diagnostics，不能被误认为构造逻辑：

```csharp
CommandSchema<Arguments>
    .Create("camera shake", category: "Direction")
    .Primary<double>("strength")
    .Use("transition", CommandParameterGroups.Transition())
    .Build(static values => new Arguments(
        values.Required<double>("strength"),
        values.Group<TransitionArguments>("transition")));
```

`CommandArgumentValues` 是一次 bind 的只读 typed value bag，只能由 schema 内部创建。`Build(...)` 的 factory 必须一次性构造完整 Arguments；最终路径不允许 parameter/property setter、`Activator.CreateInstance` 或半初始化对象。

```csharp
public sealed class RawCommandArguments
{
    public string? Primary { get; }
    public IReadOnlyDictionary<string, string> Named { get; }
}

public sealed class CommandArgumentValues
{
    // Required<T> 只允许读取已绑定且存在的值；缺失值在 bind 阶段失败。
    public T Required<T>(string name);
    // Optional<T> 返回 presence-aware value，不得用 default(T) 表示缺失。
    public CommandValue<T> Optional<T>(string name);
    public TGroup Group<TGroup>(string groupId);
}
```

`Required<T>` 已经保证值存在，因此返回具体 `T`；`Optional<T>` 和 group 内部字段必须保留 `CommandValue<T>` 的 presence 信息。对 optional 参数不得用 `default(T)` 推断“未提供”。具体 command 的 Arguments 应直接持有 `CommandValue<T>` 或包含它们的 value object。

## 4. Presence-aware 参数模型

### 4.1 为什么不能在 bind 阶段填完所有 default

Direction 当前有重要语义：

- 未提供 `duration` 时可能使用 cut 或 beat 默认值；
- 显式提供 duration 但没有 via 时，可能推导为 move；
- beat 可覆盖 duration/ease；
- 不同 command 的默认 transition 不同。

因此不能把以下状态压成同一个 double：

```text
duration 未提供
duration 显式为 0
```

也不能把以下状态压成同一个 transition id：

```text
via 未提供
via 显式为 cut
```

### 4.2 `CommandValue<T>`

```csharp
public readonly record struct CommandValue<T>(
    bool WasProvided,
    T? Value,
    CommandValueOrigin Origin)
{
    public static CommandValue<T> Unspecified() =>
        new(false, default, CommandValueOrigin.Unspecified);

    public static CommandValue<T> Explicit(T value) =>
        new(true, value, CommandValueOrigin.Explicit);

    public static CommandValue<T> ContractDefault(T value) =>
        new(false, value, CommandValueOrigin.ContractDefault);
}

public enum CommandValueOrigin
{
    Unspecified,
    Explicit,
    ContractDefault,
}
```

`WasProvided` 只表示作者是否在 Yarn input 中提供了该参数；`ContractDefault` 的 `WasProvided` 为 `false` 但 `Value` 非空。runtime policy 的结果不是 bind 输入，不使用 `CommandValueOrigin` 表示；如需记录解析结果，使用独立的 `ResolvedCommandValue<T>`。

## 5. 公共参数组

`duration/ease/via/wait` 是系统设计的一部分，不应要求每个 command 重复声明。它们由可组合的 parameter groups 提供：

```csharp
public sealed record TransitionArguments(
    CommandValue<double> Duration,
    CommandValue<DirectionEase> Ease,
    CommandValue<DirectionTransitionId> Via,
    CommandValue<bool> Wait);

public static class CommandParameterGroups
{
    public static CommandParameterGroup<TransitionArguments> Transition(
        TransitionParameterOptions options = default);

    public static CommandParameterGroup<TimingArguments> Timing(
        TimingParameterOptions options = default);

    public static CommandParameterGroup<WaitArguments> Wait(
        bool contractDefault = false);

    public static CommandParameterGroup<AudioFadeArguments> AudioFade();
}
```

每个 group 必须实现统一的组合协议：

```csharp
public interface ICommandParameterGroup
{
    string Id { get; }
    IReadOnlyList<CommandParameterSpec> Parameters { get; }
    void Validate(CommandSchemaCompositionContext context);
}

public interface ICommandParameterGroup<TValue> : ICommandParameterGroup
{
    TValue Bind(RawCommandArguments raw, CommandBindingEnvironment environment);
}
```

组合规则固定为：参数在展开后全局唯一；group 的参数名不得覆盖 command 参数或其它 group；positional order 由 command 显式声明，group 只能提供 named 参数，除非 `Use(...)` 明确指定位置；parameter set 在所有 group 展开后统一校验；usage 和 editor metadata 使用同一展开结果。group 不读取 beat、当前 node 或其它运行时状态。

group 同时提供：

- 参数 schema、usage 和 editor metadata；
- named/positional binding；
- converter 和约束；
- presence tracking；
- immutable value object factory；
- 可选的 policy input。

group 不决定所有 command 的最终业务行为。最终 transition 由 `IDirectionCommandPolicy` 根据 command name、group input、beat 和当前 state 解析。

### 5.1 用户 command 复用系统参数

```csharp
public sealed class ShakeCameraCommand
    : RuntimeCommand<ShakeCameraCommand.Arguments>
{
    private readonly IDirectionCommandPolicy policy;

    public ShakeCameraCommand(IDirectionCommandPolicy policy) =>
        this.policy = policy;

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("camera shake", category: "Direction")
            .Primary<double>("strength")
            .Use("transition", CommandParameterGroups.Transition())
            .Build(static values => new Arguments(
                values.Required<double>("strength"),
                values.Group<TransitionArguments>("transition")));

    protected override ValueTask ExecuteAsync(
        Arguments arguments,
        CommandExecutionContext context)
    {
        var transition = policy.ResolveTransition(
            "camera shake", arguments.Transition, context);
        // submit camera effect with resolved transition
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments(
        double Strength,
        TransitionArguments Transition);
}
```

用户只声明 command-specific 参数，并通过 `Use(...)` 获得系统 timing 参数。

## 6. Runtime policy 与默认值

删除全局 `CommandDefaultSource`，改用领域 policy：

```csharp
public interface IDirectionCommandPolicy
{
    DirectionTransitionSpec ResolveTransition(
        string commandName,
        TransitionArguments input,
        CommandExecutionContext context);
}

public interface IAudioCommandPolicy
{
    double ResolveFadeDuration(
        AudioFadeArguments input,
        CommandExecutionContext context);
}
```

固定解析顺序：

```text
raw tokens
  → contract binding + presence tracking
  → immutable Arguments
  → command/domain policy resolution
  → actual operation
```

Bootstrap 根据配置创建不可变 defaults snapshot，注入 `AudioCommandPolicy`、`DirectionCommandPolicy` 等窄领域 policy。command 不读取完整 `AnimationConfiguration`。

## 7. Schema、Binder 与复用

不使用当前基于 writable property 的反射 binder 作为最终实现。推荐：

- schema builder 显式注册参数；
- parameter group 贡献自己的 schema 和 binder；
- immutable Arguments 通过 constructor/factory delegate 创建；
- parameter name lookup 在 schema build 时预构建；
- registry seal 后 schema、converter 和 groups 只读。

当前 Direction 的参数继承意图是合理的，但最终规则是“组合优先”：

- `TransitionArguments`、`CameraPatchArguments` 等 value object 负责复用运行时语义；
- parameter groups 负责复用 Yarn 参数声明；
- 只有真正稳定的 is-a 参数模型才允许 record 继承；
- 继承不得隐藏参数顺序、覆盖同名参数类型或改变默认语义。

派生属性不属于 raw schema：

```csharp
public sealed record LayoutArguments(
    string Layout,
    string? Main,
    string? Inset)
{
    public IReadOnlyDictionary<string, string> Bindings => ...;
}
```

`Bindings` 是纯派生属性，不应被 Binder 当作可写参数。

## 8. Registry、Dispatcher 和 plugin command

### 8.1 Registry

```csharp
public interface IRuntimeCommandRegistrar
{
    IRuntimeCommandRegistrar Add(
        string name,
        Func<IServiceResolver, IRuntimeCommand> factory,
        string providerId,
        string? version = null);
}
```

完整 command name 在整个 catalog 中全局唯一；`providerId` 只用于来源追踪、manifest 和诊断，不形成命名空间隔离。第一版只支持 singleton command；需要 invocation-scoped state 时，状态必须放在显式 session service 中，而不是 command 实例中。

`BuildCatalog()` 执行：

1. name、reserved name、重复注册检查；
2. command factory 创建；
3. schema、converter、parameter group validation；
4. catalog seal；
5. 生成只读 Yarn/runtime catalog。

catalog 只保存已创建且已验证的 command instance，并提供按完整 command name 的 ordinal/case-insensitive lookup：

```csharp
public sealed class RuntimeCommandCatalog
{
    public IReadOnlyList<IRuntimeCommand> Commands { get; }
    public IRuntimeCommand Get(string commandName);
}
```

Dispatcher 不解析 schema，也不参与参数语义判断。它只接收 catalog 已解析的 command：

```csharp
public Task ExecuteAsync(
    IRuntimeCommand command,
    CommandInput input,
    CancellationToken cancellationToken);
```

Yarn adapter 负责把 Yarn 的 root/child segments 解析成完整 command name，并把已消费的 path segments 从 raw arguments 中移除；unknown command 仍由 dispatcher 通过现有 recoverable/abort 策略报告。binding failure 由 `RuntimeCommand.ExecuteAsync` 抛出并由 dispatcher 归类为 binding failure，业务异常归类为 execution failure。

### 8.2 Bootstrap 生命周期

当前仓库的 plugin registration、service graph 和 dialogue command adapter 存在跨阶段创建。重构后必须固定为以下顺序：

```text
创建 registration context
  → built-in/plugin/user configure 注册 services、converters、groups、command factories
  → seal registration-only extension points
  → build RuntimeServiceGraph / Host
  → 使用 Host.Services 创建 singleton command instances
  → BuildCatalog + schema validation + catalog seal
  → 创建 YarnCommandAdapter 并注册到 DialogueRunner
  → initialize plugins
  → start dialogue
```

不得在 service factory 中隐式触发 command registry 的二次注册。`BuildCatalog()` 失败时，Host 和 plugin initialization 必须按现有 bootstrap rollback 路径清理。运行时开始后禁止新增 command、converter 或 parameter group。

### 8.3 Plugin command

插件通过现有 `PluginRegistrationContext` 注册：

```csharp
public void Configure(PluginRegistrationContext context)
{
    context.CommandConverters.Register<ItemId>(ParseItemId);
    context.CommandParameterGroups.Add(
        "inventory-target", InventoryTargetParameters.Create());
    context.Commands.Add(
        name: "inventory give",
        factory: services => new GiveItemCommand(
            services.Resolve<IInventoryService>(),
            services.Resolve<IItemCatalog>()),
        providerId: "inventory",
        version: "1.0");
}
```

上例中的 `Commands`、`CommandConverters` 和 `CommandParameterGroups` 是 registration-only facade；它们只允许登记，不允许在 catalog seal 后修改。`Commands.Add` 的参数顺序以正式 API 为准：

```csharp
context.Commands.Add(
    name: "inventory give",
    factory: services => new GiveItemCommand(...),
    providerId: "inventory",
    version: "1.0");
```

plugin command 自己拥有 schema、Arguments、policy 和 ExecuteAsync；插件 module 只负责注册和构造。

注册时序固定为：

```text
plugin Configure
  → services/converters/groups/command factories
  → seal registration-only extension points
  → build service graph
  → create command instances
  → validate and seal catalog
  → initialize plugins
  → start dialogue
```

运行时开始后禁止新增 command、converter 或 parameter group。

plugin schema 的验证边界：

- 插件自己的 C# 类型错误：插件编译期发现；
- schema/converter/group 一致性：宿主启动期发现；
- Yarn 输入错误：invocation bind 期发现；
- 业务错误：dispatcher execution failure。

错误边界必须固定：schema composition/duplicate parameter/converter 缺失属于 catalog build failure；raw token 缺失、重复、未知或格式错误属于 binding failure；command/policy/manager 抛出的领域异常属于 execution failure。三类错误都保留现有 `RuntimeCommandException`、correlation 和 recoverable/abort 行为，但日志 `failurePhase` 必须明确记录为 `schema`、`binding` 或 `execution`。

## 9. `IServiceResolver` 决定

继续使用仓库现有的 `IServiceResolver` 和 `RuntimeServiceRegistry`。它们已负责依赖拓扑、循环依赖、生命周期和 RuntimeHost 集成。引入 Microsoft DI 会建立两套 service graph，增加 plugin 和 Godot host 的不一致风险。

规则：

- command factory 用 `IServiceResolver` 创建 command；
- command 构造函数接收普通业务依赖和 domain policy；
- ExecuteAsync 不通过 context lookup 普通业务服务；
- `CommandExecutionContext.Capabilities` 只能是 `ICommandInvocationCapabilities`，不得提供 `Get<T>()`、`Resolve(Type)` 或其它 service-locator API；
- 如需要动态能力，增加显式窄接口并由 bootstrap 构造，不能把完整 `IServiceResolver` 暴露给 command。

## 10. Editor catalog 与 Yarn 集成

同一个 `CommandSchema` 提供 runtime binding、Yarn adapter、usage 和 editor metadata。

不要让 command 或 plugin 直接覆盖主 `Majo Novel.json`。采用 manifest 合并：

```text
内置 assembly → manifest
plugin assembly → manifest
        ↓
Bootstrap/editor tool 明确合并
        ↓
最终 Yarn/editor catalog
```

manifest 至少记录 command path、provider/plugin id、schema version、usage、展开后的参数、类型、顺序、value hint 和 contract default。

每个 assembly/provider 生成独立的 `<provider>.majo.commands.json` manifest，由明确的 `RuntimeCommandManifestMerger` 按 provider id、command name 和参数顺序 deterministic 合并；command 不直接写主 `Majo Novel.json`。完整 command name 在全局 catalog 中不得重复，provider id 仅用于来源追踪。plugin disable 后必须重新构建 catalog，不能残留旧 manifest。

## 11. 迁移计划

允许明确标记的临时 adapter，但 adapter 只存在于迁移期，最终必须删除。

### Phase 0：语义基线

为 `camera pose`、`character`、`stage show`、`bgm` 和一个 plugin command 固化：

- 未提供 vs 显式提供；
- duration=0 vs 未提供；
- via 未提供 vs `via:cut`；
- beat 对 duration/ease 的覆盖；
- wait、settlement、cancellation；
- plugin converter、factory、catalog；
- editor manifest 与 runtime schema。

### Phase 1：建立最终 command core 和最小 plugin fixture

新增并测试：

- `IRuntimeCommand` / `RuntimeCommand<TArguments>`；
- `CommandSchema<TArguments>`；
- `CommandExecutionContext` / `CommandInput`；
- `CommandValue<T>`；
- `CommandParameterGroup<T>`；
- immutable Arguments factory；
- factory-based registry 和 startup validation。

本阶段必须同时加入一个独立 plugin fixture，覆盖 command factory、一个自定义 converter、一个 parameter group、provider metadata 和 catalog seal；plugin 不是最后阶段才验证的附加能力。

这一阶段不新增 source generator，不新增第二套 DSL，不保留 setter binder 作为最终路径。

### Phase 2：实现公共 groups 和 policy

先实现 `Transition`、`Timing`、`Wait`、`AudioFade`、`CameraTarget` 等语义组。每个 group 必须有 schema、binding、presence、metadata 和测试。

### Phase 3：迁移 Built-in、Audio、Demo

迁移顺序：save/auto-save → BGM/SFX/Voice → playback → demo/Manosaba。

每个 command 合并 schema、Arguments、policy dependency 和 ExecuteAsync，删除 `context.Binding` 使用点。

### Phase 4：迁移 Direction

顺序：stage → backdrop → character/leave → camera → shot/layout → beat。

所有公共 timing 参数通过 groups 获取；默认 transition 在 `IDirectionCommandPolicy` 中按 execution state 解析；不得复制 `duration/ease/via/wait` 声明。

### Phase 5：迁移 catalog/editor 与插件宿主

完成 plugin command factory、converter/group 注册、manifest 合并、provider/version 诊断和独立 plugin fixture；plugin fixture 已在 Phase 1 验证核心 contract，本阶段接入真实 Bootstrap/Yarn/editor pipeline。

### Phase 6：删除旧模型

删除旧 Binding context、default source、handler registration、writable property binder、迁移 adapter 和独立 `*CommandArguments` 文件。

最终搜索：

```sh
rg -n "CommandBindingContext|CommandDefaultSource|context\\.Binding|IRuntimeCommandHandler|Register<|CommandDescriptor.Create|Property.SetValue" \
  addons demos tests
```

## 12. 测试和门禁

### Parameter group / binding

- positional/named、duplicate、unknown、required、primary；
- parameter sets；
- converter 和 minimum；
- contract default；
- `CommandValue<T>.WasProvided` 和 Origin；
- immutable Arguments factory；
- schema/usage/editor metadata。

### Policy timing

- bind 阶段不读取 beat/runtime state；
- 未提供 duration/via 保留 unspecified；
- 显式 duration=0、via=cut 可区分；
- policy 正确处理 command、beat、当前 state；
- policy 不修改 Arguments。

### Registry / plugin / integration

- duplicate/reserved name；
- catalog seal；
- plugin factory、converter、parameter group；
- plugin 初始化失败回滚；
- unknown/binding/execution failure；
- activity、correlation、quiescence；
- Yarn/editor catalog 一致性。

```sh
dotnet build 'Majo Novel.csproj' --no-restore
dotnet test tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj --no-restore
./tools/test.sh --fast
./tools/test.sh --full
```

另行验证 Godot headless import/C# compilation、demo/Manosaba smoke、release/export smoke 和 catalog fixture。

## 13. 验收标准

- 每个 production command 都是 colocated 类型，拥有 schema、immutable Arguments、依赖和 ExecuteAsync；
- `duration/ease/via/wait` 通过 parameter groups 复用；
- bind 阶段保留 presence；runtime policy 在 execution 阶段解析；
- 删除 `CommandBindingContext`、`CommandDefaultSource`、`RuntimeCommandContext.Binding`；
- 删除旧 handler registration 和 writable-property binder；
- Dispatcher 不引用 Animation、Audio、Direction 配置类型；
- plugin command 可通过 factory、converter 和 parameter group 注册；
- catalog seal 后不可变；
- runtime/Yarn/editor metadata 来自同一 schema/manifest pipeline；
- schema 错误在 catalog build 阶段失败；
- catalog build 必须在 `RuntimeCommandCatalog` 暴露前完成所有 command factory、schema、group 和 converter validation；不存在“先暴露 catalog、首次 invocation 再发现 schema 错误”的路径；
- binding/execution 错误保持 recoverable/abort 语义；
- 全部 unit、headless、demo、Manosaba 和 release/export 门禁通过。

## 14. 最终形态

```text
Concrete RuntimeCommand
    ├── immutable Arguments
    ├── CommandSchema
    │     ├── command-specific parameters
    │     ├── reusable parameter groups
    │     ├── contract defaults
    │     └── presence-aware binder
    ├── domain policy/default dependencies
    └── ExecuteAsync

RuntimeCommandRegistry
    ├── factory registration
    ├── plugin/provider ownership
    ├── startup validation
    └── catalog seal

RuntimeCommandDispatcher
    ├── invocation context
    ├── logging/correlation
    ├── cancellation
    ├── recoverable/abort policy
    └── command quiescence
```

最终依赖方向：

```text
Project Configuration
        ↓
Domain Policy / Defaults
        ↓
Concrete Command + Reusable Parameter Groups
        ↓
Presence-aware immutable Arguments
        ↓
Execution-time policy resolution
        ↓
Command side effect
```

核心原则：command 自己拥有 schema、参数模型和行为；公共系统参数通过 parameter groups 提供；动态默认值在正确的执行时机解析；runtime 只负责调用 command。
