#nullable enable

using System;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime;

internal sealed class GodotRuntimeLogger : IRuntimeLogger
{
    public void Log(RuntimeLogLevel level, string message, Exception? exception = null)
    {
        var formatted = exception is null
            ? message
            : $"{message}{System.Environment.NewLine}{exception}";
        switch (level)
        {
            case RuntimeLogLevel.Warning:
                GD.PushWarning(formatted);
                break;
            case RuntimeLogLevel.Error:
                GD.PushError(formatted);
                break;
            default:
                GD.Print(formatted);
                break;
        }
    }
}
