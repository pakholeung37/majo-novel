#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Majo.CommandGenerator;

public static class CommandParser
{
    private const string Prefix = "Majo.Runtime.Dialogue.Commands.";

    public static CommandModel? Parse(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol command || context.TargetNode is not ClassDeclarationSyntax syntax) return null;
        var diagnostics = new List<Diagnostic>();
        void Shape(string text) => diagnostics.Add(Diagnostic.Create(Diagnostics.UnsupportedShape, syntax.Identifier.GetLocation(), command.Name, text));
        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword)) Shape("must be declared as partial.");
        if (command.IsAbstract || command.IsGenericType || command.ContainingType is not null) Shape("must be a non-generic, non-nested concrete class.");
        var attr = Attribute(command.GetAttributes(), "CommandAttribute");
        var name = attr?.ConstructorArguments.FirstOrDefault().Value as string ?? command.Name;
        var args = command.GetTypeMembers("Arguments").FirstOrDefault();
        var baseType = command.BaseType;
        while (baseType is not null && baseType.OriginalDefinition.ToDisplayString() != Prefix + "RuntimeCommand<TArguments>") baseType = baseType.BaseType;
        if (baseType is null) Shape("must derive from RuntimeCommand<TArguments>.");
        args ??= baseType?.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        if (args is null || baseType is not null && !SymbolEqualityComparer.Default.Equals(args, baseType.TypeArguments[0])) Shape("Arguments must match the RuntimeCommand type argument.");
        var members = new List<ArgumentMemberModel>();
        var sets = new List<ParameterSetModel>();
        if (args is not null)
        {
            var parser = new MemberParser(name, diagnostics);
            members = parser.Parse(args, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
            sets = parser.ParseSets(args.GetAttributes().Concat(command.GetAttributes()), members);
        }
        return new CommandModel(name, Named<string>(attr, "Category"), Named<string>(attr, "Usage"),
            command.ContainingNamespace.IsGlobalNamespace ? "" : command.ContainingNamespace.ToDisplayString(),
            command.Name, args?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "", members, sets, diagnostics);
    }

    private sealed class MemberParser
    {
        private readonly string command;
        private readonly List<Diagnostic> diagnostics;
        private readonly HashSet<string> scriptNames = new(StringComparer.OrdinalIgnoreCase);
        private int nextOrder;
        private int primaryCount;
        public MemberParser(string command, List<Diagnostic> diagnostics) { this.command = command; this.diagnostics = diagnostics; }

        public List<ArgumentMemberModel> Parse(INamedTypeSymbol type, HashSet<ITypeSymbol> visited)
        {
            var result = new List<ArgumentMemberModel>();
            if (!visited.Add(type))
            {
                diagnostics.Add(Diagnostic.Create(Diagnostics.GroupConflict, type.Locations.FirstOrDefault(), type.Name, command, "causes a cyclic parameter group reference."));
                return result;
            }
            var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public &&
                !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type))).ToArray();
            if (!type.IsRecord || type.IsValueType || !type.IsSealed || constructors.Length != 1)
            {
                diagnostics.Add(Diagnostic.Create(Diagnostics.UnsupportedShape, type.Locations.FirstOrDefault(), command, "Arguments/groups must be sealed records with one public non-copy constructor."));
                visited.Remove(type);
                return result;
            }
            foreach (var parameter in constructors[0].Parameters)
            {
                var attrs = parameter.GetAttributes();
                var primary = Attribute(attrs, "PrimaryAttribute");
                var option = Attribute(attrs, "OptionAttribute");
                var group = Attribute(attrs, "GroupAttribute");
                var location = parameter.Locations.FirstOrDefault();
                void Invalid(string message) => diagnostics.Add(Diagnostic.Create(Diagnostics.IncompatibleDeclaration, location, parameter.Name, command, message));
                if (new[] { primary, option, group }.Count(a => a is not null) != 1)
                    Invalid("requires exactly one Primary, Option or Group attribute on the constructor parameter.");
                if (parameter.HasExplicitDefaultValue) Invalid("cannot use C# constructor defaults; use DefaultText.");
                if (parameter.Type.NullableAnnotation == NullableAnnotation.Annotated) Invalid("cannot be declared as nullable type. Use CommandValue<T> for optional parameters.");
                if (group is not null && parameter.Type is INamedTypeSymbol groupType)
                {
                    var order = Named<int?>(group, "Order") ?? nextOrder++;
                    var children = Parse(groupType, visited);
                    var config = Named<INamedTypeSymbol>(group, "Configuration");
                    if (config is not null && (!config.AllInterfaces.Any(i => i.ToDisplayString() == Prefix + "ICommandGroupConfiguration") ||
                        config.IsAbstract || !config.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0)))
                        Invalid("group Configuration must implement ICommandGroupConfiguration and have a public parameterless constructor.");
                    result.Add(new GroupModel(Named<string>(group,"Name") ?? Camel(parameter.Name), parameter.Name, order,
                        groupType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), config?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        children, ParseSets(groupType.GetAttributes(), children)));
                    continue;
                }
                var selected = primary ?? option;
                var inner = parameter.Type;
                var presence = inner is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == Prefix + "CommandValue<T>";
                if (presence) inner = ((INamedTypeSymbol)inner).TypeArguments[0];
                if (inner.NullableAnnotation == NullableAnnotation.Annotated || inner.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    Invalid("cannot bind nullable values.");
                var defaultText = Named<string>(option,"DefaultText");
                if (primary is not null && presence) Invalid("Primary parameter cannot be CommandValue<T>.");
                if (!presence && defaultText is not null) Invalid("Only optional CommandValue<T> parameters can declare defaults.");
                if (primary is not null && ++primaryCount > 1)
                    diagnostics.Add(Diagnostic.Create(Diagnostics.DuplicateParameter, location, command, "declares more than one Primary parameter."));
                var minimum = Named<double?>(selected,"Minimum");
                if (minimum.HasValue && (double.IsNaN(minimum.Value) || double.IsInfinity(minimum.Value) || !IsNumeric(inner.SpecialType)))
                    diagnostics.Add(Diagnostic.Create(Diagnostics.InvalidMinimum, location, parameter.Name, inner.ToDisplayString()));
                var converter = Named<INamedTypeSymbol>(selected,"Converter");
                if (converter is not null && (!converter.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == Prefix + "ICommandValueConverter<T>" && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], inner)) || converter.IsAbstract || converter.IsGenericType))
                    diagnostics.Add(Diagnostic.Create(Diagnostics.IncompatibleConverter, location, converter.Name, parameter.Name, inner.ToDisplayString()));
                var scriptName = Named<string>(selected,"Name") ?? Camel(parameter.Name);
                if (string.IsNullOrWhiteSpace(scriptName) || scriptName.Any(c => char.IsWhiteSpace(c) || c == ':')) Invalid("has an invalid script name.");
                if (!scriptNames.Add(scriptName)) diagnostics.Add(Diagnostic.Create(Diagnostics.DuplicateParameter, location, command, $"declares duplicate parameter name '{scriptName}'."));
                var model = new ParameterModel(scriptName, parameter.Name, Named<int?>(selected,"Order") ?? nextOrder++, primary is not null,
                    primary is not null || !presence, inner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), presence,
                    Named<string>(selected,"ValueHint") ?? "value", defaultText is not null, defaultText, minimum,
                    converter?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                model.HasParameterlessConverter = converter is not null && converter.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0);
                result.Add(model);
            }
            visited.Remove(type);
            return result;
        }

        public List<ParameterSetModel> ParseSets(IEnumerable<AttributeData> attributes, IReadOnlyList<ArgumentMemberModel> members)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            void Collect(IReadOnlyList<ArgumentMemberModel> list, string prefix)
            {
                foreach (var member in list)
                    if (member is GroupModel group) Collect(group.Members, prefix + group.MemberName + ".");
                    else known.Add(prefix + member.MemberName);
            }
            Collect(members, "");
            var result = new List<ParameterSetModel>();
            foreach (var attr in attributes)
            {
                var typeName = attr.AttributeClass?.ToDisplayString();
                if (typeName != Prefix+"AtMostOneAttribute" && typeName != Prefix+"ExactlyOneAttribute" && typeName != Prefix+"AtLeastOneAttribute") continue;
                var names = attr.ConstructorArguments[0].Values.Select(v => v.Value as string ?? "").ToArray();
                foreach (var name in names)
                    if (!known.Contains(name)) diagnostics.Add(Diagnostic.Create(Diagnostics.InvalidParameterSetMember, attr.ApplicationSyntaxReference?.GetSyntax().GetLocation(), command, name));
                if (names.Length < 2 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    diagnostics.Add(Diagnostic.Create(Diagnostics.InvalidParameterSetMember, attr.ApplicationSyntaxReference?.GetSyntax().GetLocation(), command, "parameter set requires at least two distinct members"));
                result.Add(new ParameterSetModel(attr.AttributeClass!.Name.Replace("Attribute", ""), names));
            }
            return result;
        }
    }

    private static AttributeData? Attribute(IEnumerable<AttributeData> attrs, string name) => attrs.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == Prefix + name);
    private static T? Named<T>(AttributeData? attr, string key)
    {
        if (attr is not null) foreach (var pair in attr.NamedArguments) if (pair.Key == key && pair.Value.Value is T value) return value;
        return default;
    }
    private static string Camel(string text) => char.ToLowerInvariant(text[0]) + text.Substring(1);
    private static bool IsNumeric(SpecialType type) => type is SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or
        SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or
        SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Decimal;
}
