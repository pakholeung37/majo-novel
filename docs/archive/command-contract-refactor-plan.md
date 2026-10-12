# Command 契约、参数绑定与 converter 扩展重构方案

状态：待实施。本文定义最终架构和迁移顺序，不代表相关 API 已实现。
日期：2026-09-07。

## 1. 目标与决策

这次重构统一 command 的声明、绑定和工具描述，完成以下目标：

1. Arguments 是静态 command 参数声明的唯一事实来源；不再手写第二份参数表和逐字段构造映射。
2. Core 提供开放的参数转换协议，支持任意明确注册的 CLR 类型。
3. 同时支持 catalog 级默认 converter 和参数级 converter；领域实现与所属 command／模块 colocate。
4. command、parameter group、默认值、manifest 使用同一份解析后的契约。
5. catalog 构建时完成依赖解析和校验；执行阶段只消费不可变绑定计划。
6. 保留现有脚本语法、CommandValue<T> 来源语义和 command 执行行为。

采用 Arguments 声明标记 + incremental source generator。生成器生成 schema 描述和强类型构造代码；运行时绑定引擎仍只有一套。显式 builder 作为动态／第三方程序集的低层入口保留，不要求普通 command 作者使用。

不引入通用 CLI parser，不改变 Yarn 的分词边界，不在本次增加列表／对象字面量语法，不迁移 CameraFxCue 等下游领域协议，不重做 timing、dispatch、取消和错误策略。

“一步到位”指最终只有一套契约与绑定机制；可以分阶段提交，但合并完成前必须移除旧运行路径。

## 2. 当前实现与具体问题

| 位置 | 当前职责与问题 | 最终变化 |
| --- | --- | --- |
| `addons/majo_runtime/Runtime/Dialogue/Commands/CommandBinding.cs` | registry 构造函数注册 CameraShakeAxis、MotionCurve、EasingId 等领域类型；按 Type 静默覆盖；有 BindEasingRegistry 特例 | 拆成中立协议、注册表、基础 converters；领域注册移到模块 |
| `Core/CommandSchema.cs`（相对 Commands 目录） | 参数表、字符串查值 factory、反射构造 CommandValue；未 attach 时可回退 Default | 描述与绑定计划分离；生成强类型绑定；禁止隐式环境 |
| `Direction/CameraCommands.cs` | Schema、Build factory、Arguments 三处重复参数名称和类型 | Arguments 标记生成描述与构造器 |
| `Groups/CommandParameterGroups.cs` | 声明参数后，group binder 再读取 raw 并转换 | group 展开后每个参数只绑定一次 |
| `RuntimeCommandRegistry.cs` | 构建中逐个 attach schema，可能在后续失败前留下部分状态 | 本地构建并校验全部计划，成功后一次发布 catalog |
| `Manifest/RuntimeCommandManifest.cs` | 依赖 Type.Name、ToString 和手写 hints | 导出 resolved contract 的有效转换描述和默认值 |
| `Runtime/Plugins/IMajoPlugin.cs` | 注册发生在服务图构建之前 | 注册 converter factory，服务图完成后才实例化 |
| `Runtime/MajoRuntimeBootstrap.cs` | ConfigureAndSealPlugins 注册并封闭各 registry | 保留注册期封闭；与运行期计划冻结区分 |
| `Runtime/Bootstrap/Registration/MajoRuntimeBootstrap.Services.Dialogue.cs` | CreateYarnCommandAdapter 构建 catalog | 作为服务图就绪后的装配入口 |

现有泛型 API 已能接受自定义 CLR 类型。需要解决的是注册与归属、重复声明、元数据不一致，不是添加一个有限的参数类型 enum。

旧设计里的 Schema 与 factory 分别描述输入和构造 Arguments，职责分离是合理的；机械映射应由工具生成，而不是由作者重复维护。

## 3. 作者使用的目标 API

以下示例是实现契约；最终命名可在第一阶段统一调整，语义不能含糊。

```csharp
[Command("camera fx", Category = "Direction")]
public sealed partial class CameraFxCommand
    : RuntimeCommand<CameraFxCommand.Arguments>
{
    [AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
    public sealed record Arguments(
        [Primary] string Effect,
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Option(Minimum = 0)] CommandValue<double> Duration,
        [Option(Minimum = 0)] CommandValue<double> Strength,
        [Option(Converter = typeof(CameraShakeAxisConverter))]
        CommandValue<CameraShakeAxis> Axis,
        [Option] CommandValue<int> Frequency,
        [Option] CommandValue<double> Zoom,
        [Group] WaitArguments Wait);

    // 构造函数继续注入运行时依赖；生成器不改写领域执行代码。
    protected override ValueTask ExecuteAsync(
        Arguments args, CommandExecutionContext context) { /* 领域逻辑 */ }
}
```

实施时将此声明纳入生成器编译测试，验证 record 合成属性的 nameof 引用与 attribute 目标；示例中的执行方法体省略，不是可直接运行的完整 command。

声明规则：

- 第一版支持非泛型 partial command、嵌套 sealed positional record Arguments；先迁移现有形状，不支持所有 C# 类型形态。
- Attributes 明确以构造参数为目标，生成器读取参数符号，避免 record 合成属性重复读取。
- `[Primary] T` 是必填位置参数，最多一个；`[Option] T` 是必填命名参数；`[Option] CommandValue<T>` 是可选参数。
- 可选参数统一使用 CommandValue<T>，不把 `T?` 或 C# 构造器默认值隐式解释成脚本可选语义。
- 脚本名默认采用 invariant 首字母小写规则，显式 `Name` 可覆盖。迁移时逐项比较旧名，不能借机重命名。
- 参数顺序来自构造器顺序，group 在所在位置展开；迁移中如 manifest 旧顺序有消费者，使用显式 Order 保持。
- 互斥／至少一个等约束引用 CLR 成员名，生成器解析为参数身份；不再维护另一份字符串脚本名表。
- usage 默认生成；保留独立 Examples／说明文字，不再手写结构性的完整参数列表。
- computed properties 不参与契约；Arguments 构造器只储存绑定结果，不承载依赖运行时状态的逻辑。

## 4. 中立转换协议与两级扩展

```csharp
public interface ICommandValueConverter<T>
{
    T Parse(string text); // 无副作用；非法输入抛 CommandValueFormatException。
}

public interface ICommandValueDescriptor<T>
{
    CommandValueDescription Description { get; }
    string Format(T value); // invariant；供默认值／工具展示使用。
}
```

Description 是可选能力，包含 display name、syntax、canonical values、aliases、examples；不得依赖 Godot、Camera 或 Yarn。只有使用者确实提供时才填充，不从任意 CLR 类型名推导合法值。converter 的输出类型必须严格匹配 T。

最终参数契约携带类型、converter 引用、可选描述与 validator。CLR Type 足够作为默认转换查找键；不强制每个类型注册独立 ID。确需跨进程识别的领域类型可提供有命名空间的 SemanticTypeId，并校验描述冲突；manifest 中的 CLR 类型名仅作诊断展示。

解析优先级固定为：

1. 参数显式指定的 converter 类型／低层 builder 提供的实例或工厂。
2. 当前 catalog 注册的 `T` 默认 converter。
3. 构建失败，错误包含 command、parameter、T 和 provider。

不保留隐式 Enum.Parse fallback。显式提供 `EnumCommandValueConverter<TEnum>` 辅助实现，默认只接受已定义名称、拒绝数字和未定义值；别名由领域定义。迁移前盘点现有 enum 输入，兼容性变化必须明确记录。

注册 API 示意：

```csharp
context.Converters.AddDefault<CameraShakeAxis>(
    _ => new CameraShakeAxisConverter(), providerId);
context.Converters.AddDefault<EasingId>(
    services => new EasingIdConverter(services.Get<IEasingRegistry>()),
    providerId);

// 参数用 typeof 指定、但构造需要依赖时，单独注册该实现的工厂。
context.Converters.AddFactory<EasingIdConverter>(
    services => new EasingIdConverter(services.Get<IEasingRegistry>()),
    providerId);
```

- 无参参数级 converter 可由生成代码提供静态构造工厂，无需额外注册。
- 有依赖的参数级 converter 必须存在显式工厂；不通过反射猜构造函数，不自动 service-locate。
- AddDefault 可以关联已注册的具体实现工厂；registry 内部统一保存工厂引用，避免两份独立实例配置。
- 全局默认仅作用于当前 catalog。两个 runtime 可对同一个 T 使用不同 converter。
- 默认重复注册失败；提供显式 ReplaceDefault，要求声明 expectedProviderId，防止插件加载顺序偷偷决定行为。
- 参数级覆盖不更改任何全局映射。
- converter 以 catalog 为生命周期，要求可重入、无每次调用可变状态；工厂不做资源 I/O，不创建需由 binder 释放的资源。外部服务资源由服务容器管理。
- 数字解析使用 invariant culture 并拒绝非有限值；bool 延续现有语义。

目录归属：基础数值／字符串 converter 放 Commands 的 Binding 下；Vector2 放 Godot 相关适配位置；CameraShakeAxisConverter 放 camera command 附近；MotionCurve、DirectionEase、DirectionTransitionId 放 Direction；EasingId 放 Timing。Core registry 不引用这些领域命名空间。

## 5. 契约、绑定计划与执行分层

采用三个明确的对象：

| 对象 | 内容 | 生命周期 |
| --- | --- | --- |
| CommandDefinition<TArguments> | 参数／group 描述、converter 引用、生成的强类型 binder factory | 可复用、不可变、不附着 runtime |
| BoundCommand<TArguments> | resolved converters、validators、索引、Arguments 构造委托、command 实例 | 单 catalog，不可变 |
| CommandInput / RawCommandArguments | 本次 token 与规范化输入 | 单次执行 |

RuntimeCommand<TArguments> 暴露 Definition 和领域 Execute；catalog entry 持有 BoundCommand，并桥接 protected Execute。dispatcher 仍按现有错误和取消策略调用 entry。禁止将 runtime 环境写回静态 Definition，避免多 runtime 相互污染。

绑定计划给每个展开参数分配稳定 slot。生成器为 T 生成 typed slot reader 与 `CommandValue<T>.Explicit/ContractDefault/Unspecified` 调用；最终直接 `new Arguments(...)`。内部可有类型擦除的接口，但不使用 MakeGenericType、Activator、Property.SetValue 或运行期字符串查值来构造每次 Arguments。

运行期步骤：

1. 按现有 token 规则生成规范化输入，拒绝重复／未知参数。
2. 根据“实际提供”的参数集合验证互斥等集合约束。
3. 每个显式参数调用其已解析 converter 一次，再执行参数 validator。
4. 缺省参数填入已经构建期验证过的契约默认值或 Unspecified。
5. 从已绑定 slot 构造 group，再构造 Arguments，调用领域 Execute。

绑定失败可恢复性延续现有 CommandFailurePhase.Binding。converter 约定的输入错误包装成包含原值、参数和 usage 的错误；非预期异常保留 inner exception 与现有上层处理策略，不把程序错误改写为“非法格式”。

## 6. 默认值、验证与 group

### 6.1 默认值与验证

- `[Option(DefaultText = "false")]` 表示契约默认值，只有 CommandValue<T> 允许使用。
- 构建时通过该参数最终选中的 converter 解析一次，并应用同一套 validators；失败属于 Schema 错误。
- 显式输入相同值仍为 Explicit；缺省使用默认值为 ContractDefault；无默认为 Unspecified。
- 禁止用 null 代表“没有默认值”；内部使用 HasDefault + value／显式 default descriptor。
- 第一版不新增脚本 null 字面量，拒绝可空绑定值声明；未来增加时必须另定语义。
- Minimum 是参数约束，不属于 converter；第一版允许受支持数值类型，生成器对已知不兼容声明报错，动态 builder 在 catalog 校验时报错。
- 可扩展 `ICommandArgumentValidator<TArguments>` 在构造后做纯跨字段校验；不得读取场景资源。目标存在与资源加载继续在执行阶段处理。
- runtime defaults 不写入 Arguments 契约。例如 camera fx 未传 duration 时现有 0.4 fallback 保持在执行逻辑。

### 6.2 group 声明和使用点配置

group 使用同一种 record 参数声明，由生成器生成可组合 Definition。`[Group] WaitArguments Wait` 展开为其成员，命名与重复校验在展开后进行。

现有 TransitionParameterOptions、TimingParameterOptions 的 wait 默认值和顺序配置需要保留等价表达。使用类型化配置提供者，例如 `[Group(Configuration = typeof(NonBlockingWait))]`，配置对象只能覆盖既有 group 成员的默认值、顺序、hint，不能重新定义参数表或执行 raw 解析。提供者在构建期运行；member selector 必须引用已存在成员，错误在构建期报告。

group 递归展开需检测循环；同名展开参数默认报错，不隐式合并；两个 Wait group 不能因类型相同就共享 slot。内部身份使用成员路径，脚本名按展开结果校验。第一版不自动引入参数名前缀，避免改变脚本。

删除 `ICommandParameterGroup<T>.Bind(raw, environment)` 和 DelegateCommandParameterGroup 的 raw binder。低层动态 group 同样用 typed slots 构造结果，不能绕过主绑定流程。

## 7. 注册和原子构建

IRuntimeCommandModule 改为接收 CommandRegistrationContext，包含 Commands、Converters、Groups 三个注册接口。PluginRegistrationContext 暴露同一组 backing registries，不创建隐藏的替代 registry。

模块注册按事务处理：暂存三个注册表的变化，全部成功后提交；任何异常都不留下部分 converter／group／command 注册。替换操作也参与事务。

生命周期严格遵循现有启动约束：

1. 注册内置基础转换和领域模块。
2. 应用宿主配置及插件 Configure；此时服务图尚未完成，只保存工厂。
3. SealRegistrations 关闭注册入口；easing 等依赖目录按现有流程封闭。
4. 服务图构建完成后，在 CreateYarnCommandAdapter 路径调用 BuildCatalog。
5. 在本地临时状态创建 command、解析工厂、展开 group、验证全部定义、构造计划和 manifest。
6. 全部成功后返回不可变 catalog，再向 adapter 发布。失败不 attach Definition，不缓存半成品计划，不影响已有 catalog。

注册封闭与 catalog 构建是两件事：封闭注册后仍允许重新执行构建；每次构建创建独立运行期计划，Definition 可复用。构建工厂必须无外部副作用。禁止执行时退回默认 registry；单测提供显式 TestCatalogBuilder。

## 8. source generator 工程

新增独立项目 `addons/majo_runtime/SourceGenerators/Majo.CommandGenerator/`，不引用 Godot/runtime 程序集，只依赖 Roslyn 并通过 metadata name 查找声明。Attributes 和运行期协议留在 runtime，避免 runtime 引用生成器。

- 采用 IIncrementalGenerator；目标 netstandard2.0。Roslyn 包版本在实施时与实际 SDK 编译器验证兼容，不沿用 Yarn generator 的历史版本作为默认结论。
- `MajoRuntime.props` 排除生成器源码的主工程 Compile glob，并以 Analyzer ProjectReference 引入，设置 OutputItemType=Analyzer、ReferenceOutputAssembly=false。
- 主工程当前只排除了一个测试目录；新增 generator tests 时必须更新排除规则，防止测试源码被 Godot 主项目编译。
- 新增独立 generator tests 项目，使用 Roslyn compilation 测试 diagnostics 和生成代码能否编译；不需要启动 Godot。
- 插件独立编译工程必须显式获得 analyzer 引用／打包的 analyzer；普通 ProjectReference 不作为自动传递保证。文档提供接入样例。
- 输出稳定 hint name、全限定类型名、确定性顺序；不扫描运行中程序集，不反射发现 commands。

至少提供以下诊断：

| 编号 | 错误 |
| --- | --- |
| MAJOCMD001 | 不支持的 command／Arguments 形状或缺少 partial |
| MAJOCMD002 | 重复脚本参数名、多个 Primary |
| MAJOCMD003 | Option／Primary 与 CommandValue／nullable／默认声明矛盾 |
| MAJOCMD004 | converter 不实现匹配的 ICommandValueConverter<T> |
| MAJOCMD005 | 参数集合约束引用不存在的成员 |
| MAJOCMD006 | group 递归或静态可见的展开冲突 |
| MAJOCMD007 | validator／Minimum 与类型不匹配 |

缺少运行期注册、依赖服务或工厂、动态 group 冲突在 catalog 构建时报告，生成器不能假装知道插件配置。

## 9. Manifest 和语法兼容性

Manifest 从 BoundCommand 的 resolved contract 生成，包含参数名、顺序、required／primary、presence/default、group、约束，以及最终 converter 的可选描述。参数级覆盖后的合法值必须覆盖默认 converter 的描述。

加入显式格式版本。旧 ValueType 可暂时保留为展示字段，但消费者不能依赖 Type.Name 判断合法输入。默认值使用 converter formatter；没有 formatter 时保留声明的 DefaultText，并标注它是输入文本，不声称是规范形式。动态 builder 的 typed default 如需导出必须提供 formatter。

NaN 等“未设值”不进入 JSON；Minimum 等改为可空字段。同一 catalog 的输出顺序确定，读写往返可验证。先搜索所有 manifest 消费者与 fixture，统一迁移后再移除兼容字段。

脚本兼容基线：

- command path、别名、provider、参数名和大小写比较保持。
- 延续首个冒号分隔命名参数、额外位置参数报错、未知／重复参数报错的现有规则。
- 空白值和 `-` 当前被拒绝；本次保持，不让 converter 重构偷偷改变缺省语义。
- 所有 camera axis 别名（both、diag 等）保持。
- 合法数字格式、finite 检查、当前 culture 无关性保持。
- 枚举 numeric fallback 的收紧列为明确兼容变更；盘点脚本后决定提供局部兼容 converter 还是记录拒绝，不静默改变。
- Yarn adapter 继续负责来源适配；不要在新绑定层重新分词或引入另一套引号／转义规则。

## 10. 实施阶段与退出条件

### P0：兼容基线与 API 定稿

盘点所有 commands、枚举、parameter groups、插件注册、bootstrap hooks 和 manifest 消费者。重点读取 RuntimeCommandTests、TimingRegistryTests、DirectionPatchTests、StageCommandTests、ManosabaCommandTests 及 dispatcher tests。记录有效脚本样例与绑定结果／presence，不只快照实现细节。

退出条件：迁移清单完整；本文示例由最小编译样例验证；组默认值、顺序、enum 数字输入的处理决策明确。

### P1：中立 converter 和装配层

实现协议、工厂、两级选择、重复／替换规则、模块事务和封闭生命周期。移动领域 converters，删除 registry 的领域默认构造和 BindEasingRegistry。短期旧 Schema 可以通过适配器调用新解析器，但适配器不能复制转换实现。

退出条件：CameraShakeAxis 在 command 附近实现；自定义 struct 全局绑定成功；局部覆盖不影响其他参数；服务依赖在构建期解析。

### P2：Definition 与绑定计划

引入不可变 Definition／BoundCommand、typed slots 和原子构建。迁移 group 到一次绑定，移除隐式 Default；为测试提供显式 catalog 工具。适配 RuntimeCommand／catalog entry／dispatcher 边界，保持执行策略。

退出条件：构建失败无部分 attach；同一 Definition 可用于两个 converter 配置不同的 catalog；group 参数显式输入仅转换一次。

### P3：生成器与纵向迁移

实现 attributes、生成器、diagnostics、analyzer 集成。先完整迁移 CameraFxCommand，再迁移一个使用 Transition group 的 command 和一个使用依赖型 Easing converter 的 command。验证结构生成、默认值覆盖和 manifest。

退出条件：三个切片无手写 Schema 参数表或 factory；生成代码编译通过，Godot 主工程可构建；缺失 analyzer 会导致明确构建失败而非运行期反射回退。

### P4：全量迁移和消费者更新

迁移 BuiltIn、Audio、Direction、项目／插件 commands、groups、bootstrap 配置 hooks 和全部测试 fixture。Manifest 与 usage 消费者改用 resolved contract。独立插件编译一个自定义参数类型样例，验证公共扩展能力。

退出条件：命令清单全部覆盖；脚本行为基线通过；没有剩余旧 binder 使用者。

### P5：删除兼容层和最终验收

删除旧 CommandArgumentValues 字符串读取映射、raw group binder、schema attach／Default fallback、反射 presence 构造和静默覆盖接口。旧的独立 registry hooks 合并到统一 CommandRegistrationContext，项目内调用一起迁移；属于插件源码 API 破坏性变更，更新迁移说明，不承诺未验证的二进制兼容。

退出条件：下述验收矩阵全部满足，旧路径搜索无活动代码命中。

## 11. 验收与测试矩阵

| 范围 | 必须验证的行为 |
| --- | --- |
| 扩展 | 自定义 record struct 全局转换；同 CLR 类型局部覆盖；不同 catalog 隔离 |
| 注册 | 重复报错、显式替换检查 provider、封闭后拒绝修改、模块失败完整回滚 |
| 依赖 | Configure 不解析服务；Build 才创建依赖 converter；缺失依赖给出 command／参数定位 |
| presence | Unspecified、Explicit、ContractDefault 区别；false、0 不当成缺省 |
| 默认值 | 默认文本使用最终局部 converter；无效默认构建失败；运行时 fallback 行为不变 |
| groups | 转换一次、默认覆盖、顺序、展开冲突、循环、互斥按 WasProvided 判断 |
| 输入 | 大小写、冒号、重复、未知、缺失、空白、`-`、非 finite 数字、axis 别名 |
| 生成器 | 正确代码编译；诊断定位；类型不匹配；record attribute 目标；稳定输出 |
| catalog | 中途失败不发布状态；Definition 多 catalog 复用；未绑定不能执行 |
| manifest | 使用有效 converter 描述；default 格式；版本、确定性、JSON 往返，无 NaN |
| 执行 | 原有 timing／wait、错误阶段、取消、dispatcher 和 Yarn 集成测试保持通过 |
| 工程 | Godot 主工程、runtime tests、generator tests、独立插件 analyzer 接入都可构建 |

建议验证命令（新增项目路径按本方案）：

```sh
dotnet build 'Majo Novel.csproj'
dotnet test tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj
dotnet test tests/Majo.CommandGenerator.Tests/Majo.CommandGenerator.Tests.csproj
rg -n 'BindEasingRegistry|CommandArgumentValues|AttachEnvironment|CommandBindingEnvironment.Default|MakeGenericType|Activator.CreateInstance' addons/majo_runtime/Runtime/Dialogue/Commands
```

执行顺序先聚焦转换／绑定／生成器测试，再运行现有完整 runtime suite。上述命令是实施后的验收要求，本文编写阶段未执行或声称通过。

## 12. 完成定义

普通 command 作者只维护 Arguments 契约、必要的领域 converter 和 Execute。全局扩展与局部覆盖走同一协议，Core 不认识 Camera／Direction；group 不重新解析输入；manifest 与执行契约一致；所有绑定计划在 catalog 构建完成后不可变。

新增一个领域参数类型时，不需要修改 Core、生成器或集中式 switch；新增一个参数时，不需要修改另一份手写 factory。这两项是最终架构是否达标的直接判据。
