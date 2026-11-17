#nullable enable

using System;
using Google.Protobuf;
using Majo.YarnBridge;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Bridges;

internal static class YarnProjectBridge
{
    public static YarnProject WithNodeEntries(YarnProject imported)
    {
        // Keep the imported resource and its Direction fingerprint unchanged.
        // Godot duplicates exported properties, so the copy has a fresh Program cache.
        var project = (YarnProject)imported.Duplicate();
        project.CompiledYarnProgramBase64 = Convert.ToBase64String(
            YarnProgramBridge.WithNodeEntries(imported.Program).ToByteArray());
        return project;
    }
}
