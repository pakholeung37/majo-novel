#nullable enable

using Microsoft.CodeAnalysis;

namespace Majo.CommandGenerator;

public static class Diagnostics
{
    public static readonly DiagnosticDescriptor UnsupportedShape = new(
        id: "MAJOCMD001",
        title: "Unsupported command or Arguments shape",
        messageFormat: "Command '{0}' {1}",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateParameter = new(
        id: "MAJOCMD002",
        title: "Duplicate parameter name or multiple Primary parameters",
        messageFormat: "Command '{0}' {1}",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IncompatibleDeclaration = new(
        id: "MAJOCMD003",
        title: "Incompatible parameter attribute declaration",
        messageFormat: "Parameter '{0}' on command '{1}' {2}",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IncompatibleConverter = new(
        id: "MAJOCMD004",
        title: "Incompatible converter type",
        messageFormat: "Converter '{0}' on parameter '{1}' does not implement ICommandValueConverter<{2}>",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidParameterSetMember = new(
        id: "MAJOCMD005",
        title: "Parameter set references unknown member",
        messageFormat: "Parameter set on command '{0}' references unknown member '{1}'",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GroupConflict = new(
        id: "MAJOCMD006",
        title: "Parameter group conflict or recursive reference",
        messageFormat: "Group '{0}' on command '{1}' {2}",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMinimum = new(
        id: "MAJOCMD007",
        title: "Minimum constraint applied to non-numeric parameter",
        messageFormat: "Parameter '{0}' of type '{1}' cannot specify Minimum",
        category: "Majo.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
