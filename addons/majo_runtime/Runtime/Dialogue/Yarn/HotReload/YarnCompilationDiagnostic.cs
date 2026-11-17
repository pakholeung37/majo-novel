#nullable enable

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed record YarnCompilationDiagnostic(
    string Severity,
    string? Code,
    string File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Message);
