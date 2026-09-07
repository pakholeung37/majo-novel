#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Majo.CommandGenerator.Tests;

public class CommandGeneratorTests
{
    private const string RuntimeAttributesStub = @"
namespace Majo.Runtime.Dialogue.Commands
{
    using System;
    using System.Collections.Generic;

    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class CommandAttribute : Attribute
    {
        public CommandAttribute(string name) { Name = name; }
        public CommandAttribute() { }
        public string? Name { get; }
        public string? Category { get; set; }
        public string? Usage { get; set; }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class PrimaryAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? ValueHint { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public Type? Converter { get; set; }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class OptionAttribute : Attribute
    {
        public string? Name { get; set; }
        public string? ValueHint { get; set; }
        public string? DefaultText { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public int Order { get; set; } = -1;
        public Type? Converter { get; set; }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class GroupAttribute : Attribute
    {
        public string? Name { get; set; }
        public int Order { get; set; } = -1;
        public Type? Configuration { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class AtMostOneAttribute : Attribute
    {
        public AtMostOneAttribute(params string[] parameters) { Parameters = parameters; }
        public string[] Parameters { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ExactlyOneAttribute : Attribute
    {
        public ExactlyOneAttribute(params string[] parameters) { Parameters = parameters; }
        public string[] Parameters { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class AtLeastOneAttribute : Attribute
    {
        public AtLeastOneAttribute(params string[] parameters) { Parameters = parameters; }
        public string[] Parameters { get; }
    }

    public readonly struct CommandValue<T>
    {
        public bool WasProvided { get; }
        public T Value { get; }
    }

    public interface ICommandValueConverter { }
    public interface ICommandValueConverter<T> : ICommandValueConverter { }

    public abstract class RuntimeCommand<TArguments> where TArguments : class
    {
        public virtual CommandDefinition<TArguments> Definition => null!;
    }

    public sealed class CommandDefinition<TArguments> where TArguments : class
    {
        public static CommandDefinitionBuilder<TArguments> Create(string name, string? category = null, string? usage = null) => null!;
    }

    public sealed class CommandDefinitionBuilder<TArguments> where TArguments : class
    {
        public CommandDefinitionBuilder<TArguments> Primary<T>(string name, string? memberName = null, string? valueHint = null, double? minimum = null, Type? converter = null, Func<ICommandValueConverter>? converterFactory = null, int? order = null) => this;
        public CommandDefinitionBuilder<TArguments> Required<T>(string name, string? memberName = null, string? valueHint = null, double? minimum = null, Type? converter = null, Func<ICommandValueConverter>? converterFactory = null, int? order = null) => this;
        public CommandDefinitionBuilder<TArguments> Optional<T>(string name, string? memberName = null, string? valueHint = null, string? defaultText = null, object? defaultValue = null, double? minimum = null, Type? converter = null, Func<ICommandValueConverter>? converterFactory = null, int? order = null) => this;
        public CommandDefinitionBuilder<TArguments> Group(string name, string? memberName = null, Type groupType = null!, Type? configuration = null, Func<ICommandGroupConfiguration>? configurationFactory = null, CommandParameterDeclaration[]? groupParameters = null, CommandGroupDeclaration[]? subGroups = null, CommandParameterSetSpec[]? parameterSets = null, int? order = null) => this;
        public CommandDefinitionBuilder<TArguments> Group(CommandGroupDeclaration declaration) => this;
        public CommandDefinitionBuilder<TArguments> ParameterSet(CommandParameterSetRule rule, params string[] parameters) => this;
        public CommandDefinition<TArguments> Build(Func<BindingResolutionContext, Func<BindingSlotReader, TArguments>> binderFactory) => null!;
    }

    public enum CommandParameterSetRule { AtMostOne, ExactlyOne, AtLeastOne }

    public sealed class BindingResolutionContext
    {
        public int GetSlot(string name) => 0;
        public int GetMemberSlot(string name) => 0;
    }

    public sealed class BindingSlotReader
    {
        public T ReadRequired<T>(int slot) => default!;
        public CommandValue<T> ReadOptional<T>(int slot) => default!;
    }

    public interface ICommandGroupConfiguration { }

    public sealed record CommandParameterDeclaration(
        string Name, string MemberName, int Order, bool Primary, bool Required, Type ValueType, bool IsCommandValue, string ValueHint,
        bool HasDefault = false, string? DefaultText = null, object? DefaultValue = null, double? Minimum = null, Type? ConverterType = null, string? GroupPath = null, Func<ICommandValueConverter>? ConverterFactory = null);

    public sealed record CommandGroupDeclaration(
        string Name, string MemberName, int Order, Type GroupType, Type? ConfigurationType = null, Func<object>? ConfigurationFactory = null,
        IReadOnlyList<CommandParameterDeclaration>? Parameters = null, IReadOnlyList<CommandGroupDeclaration>? SubGroups = null, IReadOnlyList<CommandParameterSetSpec>? ParameterSets = null);

    public sealed record CommandParameterSetSpec(CommandParameterSetRule Rule, IReadOnlyList<string> Parameters);
}
";

    private static (ImmutableArray<Diagnostic> Diagnostics, string? GeneratedSource) RunGenerator(
        string sourceCode,
        Action<ImmutableArray<Diagnostic>>? checkCompilationDiagnostics = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var attributesTree = CSharpSyntaxTree.ParseText(RuntimeAttributesStub);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)
        };

        var compilation = CSharpCompilation.Create(
            "TestCompilation",
            new[] { syntaxTree, attributesTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new CommandIncrementalGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        checkCompilationDiagnostics?.Invoke(outputCompilation.GetDiagnostics());

        var runResult = driver.GetRunResult();
        var generatedSource = runResult.GeneratedTrees.FirstOrDefault()?.ToString();

        return (generatorDiagnostics, generatedSource);
    }

    [Fact]
    public void GeneratesExpectedDefinition_ForValidCommand()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

public sealed record WaitArguments(
    [Option(ValueHint = ""bool"", DefaultText = ""false"")]
    CommandValue<bool> Wait);

[Command(""camera fx"", Category = ""Direction"", Usage = ""camera fx <effect> [options]"")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
public partial class CameraFxCommand : RuntimeCommand<CameraFxCommand.Arguments>
{
    public sealed record Arguments(
        [Primary] string Effect,
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Option(Minimum = 0)] CommandValue<double> Duration,
        [Group] WaitArguments Wait);
}
";
        var (diagnostics, generatedSource) = RunGenerator(source, compDiagnostics =>
        {
            var compileErrors = compDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.Empty(compileErrors);
        });
        Assert.NotNull(generatedSource);
        Assert.Contains("partial class CameraFxCommand", generatedSource);
        Assert.Contains("StaticDefinition =", generatedSource);
        Assert.Contains(".Primary<string>", generatedSource);
        Assert.Contains(".Optional<string>", generatedSource);
        Assert.Contains(".Group(", generatedSource);
        Assert.Contains(".ParameterSet(global::Majo.Runtime.Dialogue.Commands.CommandParameterSetRule.AtMostOne", generatedSource);
        Assert.Contains("reader.ReadRequired", generatedSource);
        Assert.Contains("new global::TestNamespace.WaitArguments(", generatedSource);
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD001_WhenClassNotPartial()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public class NotPartialCommand : RuntimeCommand<NotPartialCommand.Arguments>
{
    public sealed record Arguments(string Target);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD001" && d.GetMessage().Contains("must be declared as partial"));
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD002_WhenMultiplePrimaryParameters()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public partial class TwoPrimaryCommand : RuntimeCommand<TwoPrimaryCommand.Arguments>
{
    public sealed record Arguments(
        [Primary] string First,
        [Primary] string Second);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD002" && d.GetMessage().Contains("more than one Primary"));
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD002_WhenDuplicateParameterNames()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public partial class DuplicateParamCommand : RuntimeCommand<DuplicateParamCommand.Arguments>
{
    public sealed record Arguments(
        [Option(Name = ""count"")] CommandValue<int> Count1,
        [Option(Name = ""count"")] CommandValue<int> Count2);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD002" && d.GetMessage().Contains("duplicate parameter name"));
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD003_WhenNullableTypeUsed()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public partial class NullableCommand : RuntimeCommand<NullableCommand.Arguments>
{
    public sealed record Arguments(
        [Option] string? BadOption);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD003" && d.GetMessage().Contains("cannot be declared as nullable type"));
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD003_WhenPrimaryHasDefaultText()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public partial class PrimaryDefaultCommand : RuntimeCommand<PrimaryDefaultCommand.Arguments>
{
    public sealed record Arguments(
        [Primary(ValueHint = ""val"")] string Target);
}
";
        // Valid primary
        var (diagnostics1, _) = RunGenerator(source);
        Assert.DoesNotContain(diagnostics1, d => d.Id == "MAJOCMD003");

        const string badSource = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public partial class PrimaryDefaultCommand : RuntimeCommand<PrimaryDefaultCommand.Arguments>
{
    public sealed record Arguments(
        [Primary, Option(DefaultText = ""def"")] string Target);
}
";
        var (diagnostics2, _) = RunGenerator(badSource);
        Assert.Contains(diagnostics2, d => d.Id == "MAJOCMD003");
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD004_WhenConverterDoesNotImplementInterface()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

public class WrongConverter { }

[Command(""test"")]
public partial class BadConverterCommand : RuntimeCommand<BadConverterCommand.Arguments>
{
    public sealed record Arguments(
        [Option(Converter = typeof(WrongConverter))] CommandValue<int> Val);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD004");
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD005_WhenParameterSetReferencesUnknownMember()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
[AtMostOne(""UnknownParam"", ""Val"")]
public partial class BadSetCommand : RuntimeCommand<BadSetCommand.Arguments>
{
    public sealed record Arguments(
        [Option] CommandValue<int> Val);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD005");
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD006_WhenGroupHasCycle()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

public sealed record GroupA(
    [Group] GroupB B);

public sealed record GroupB(
    [Group] GroupA A);

[Command(""test"")]
public partial class CyclicGroupCommand : RuntimeCommand<CyclicGroupCommand.Arguments>
{
    public sealed record Arguments(
        [Group] GroupA A);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD006");
    }

    [Fact]
    public void ReportsDiagnostic_MAJOCMD007_WhenMinimumAppliedToNonNumeric()
    {
        const string source = @"
using Majo.Runtime.Dialogue.Commands;

namespace TestNamespace;

[Command(""test"")]
public partial class NonNumericMinCommand : RuntimeCommand<NonNumericMinCommand.Arguments>
{
    public sealed record Arguments(
        [Option(Minimum = 5)] CommandValue<string> Str);
}
";
        var (diagnostics, _) = RunGenerator(source);

        Assert.Contains(diagnostics, d => d.Id == "MAJOCMD007");
    }
}
