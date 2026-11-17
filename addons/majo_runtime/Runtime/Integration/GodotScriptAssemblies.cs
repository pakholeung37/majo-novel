using System;
using System.Collections.Generic;
using System.Reflection;
using Godot.Bridge;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Input;
using YarnSpinnerGodot;

namespace Majo.Runtime.Integration;

/// <summary>Registers library script paths in the current Godot project load context.</summary>
public static class GodotScriptAssemblies
{
    private static readonly object Gate = new();
    private static readonly HashSet<Assembly> Registered = new();
    private static readonly HashSet<Assembly> Pending = new();
    private static bool initialized;

    /// <summary>Called by the tool-enabled host autoload before plugins and scenes load.</summary>
    public static void Initialize()
    {
        Register(typeof(DialogueRunner).Assembly);
        Register(typeof(IInputManager).Assembly);
        Register(typeof(YarnDialogueBackend).Assembly);
        Register(typeof(MajoRuntimeBootstrap).Assembly);
        lock (Gate)
        {
            initialized = true;
            foreach (var assembly in Pending)
                RegisterCore(assembly);
            Pending.Clear();
        }
    }

    public static void Register(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            if (initialized) RegisterCore(assembly);
            else Pending.Add(assembly);
        }
    }

    private static void RegisterCore(Assembly assembly)
    {
        if (!Registered.Add(assembly)) return;
        try { ScriptManagerBridge.LookupScriptsInAssembly(assembly); }
        catch
        {
            Registered.Remove(assembly);
            throw;
        }
    }
}
