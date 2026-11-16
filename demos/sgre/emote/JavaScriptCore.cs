#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Majo.Demos.SGRE.Emote;

/// <summary>Small JavaScriptCore host for the bundled real-time E-mote evaluator.</summary>
internal sealed class JavaScriptCore : IDisposable
{
    private const string Library = "/System/Library/Frameworks/JavaScriptCore.framework/JavaScriptCore";
    private readonly IntPtr context;
    private bool disposed;

    public JavaScriptCore()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("This E-mote demo currently requires macOS JavaScriptCore.");
        context = JSGlobalContextCreate(IntPtr.Zero);
        if (context == IntPtr.Zero) throw new InvalidOperationException("Could not create JavaScriptCore context.");
    }

    public IntPtr Evaluate(string source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var script = JSStringCreateWithUTF8CString(Encoding.UTF8.GetBytes(source + "\0"));
        try
        {
            var result = JSEvaluateScript(context, script, IntPtr.Zero, IntPtr.Zero, 1, out var error);
            if (error != IntPtr.Zero) throw new InvalidOperationException("E-mote JavaScript: " + ValueToString(error));
            return result;
        }
        finally { JSStringRelease(script); }
    }

    public string EvaluateString(string source) => ValueToString(Evaluate(source));

    public float[] EvaluateFloatArray(string source)
    {
        var value = Evaluate(source);
        var length = checked((int)JSObjectGetTypedArrayLength(context, value, out var error));
        if (error != IntPtr.Zero) throw new InvalidOperationException(ValueToString(error));
        var pointer = JSObjectGetTypedArrayBytesPtr(context, value, out error);
        if (error != IntPtr.Zero) throw new InvalidOperationException(ValueToString(error));
        var output = new float[length];
        Marshal.Copy(pointer, output, 0, length);
        return output;
    }

    private string ValueToString(IntPtr value)
    {
        var str = JSValueToStringCopy(context, value, out var error);
        if (error != IntPtr.Zero || str == IntPtr.Zero) return "<unprintable JavaScript error>";
        try
        {
            var size = checked((int)JSStringGetMaximumUTF8CStringSize(str));
            var buffer = new byte[size];
            var count = checked((int)JSStringGetUTF8CString(str, buffer, (nuint)buffer.Length));
            return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, count - 1));
        }
        finally { JSStringRelease(str); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        JSGlobalContextRelease(context);
    }

    [DllImport(Library)] private static extern IntPtr JSGlobalContextCreate(IntPtr globalObjectClass);
    [DllImport(Library)] private static extern void JSGlobalContextRelease(IntPtr context);
    [DllImport(Library)] private static extern IntPtr JSStringCreateWithUTF8CString(byte[] value);
    [DllImport(Library)] private static extern void JSStringRelease(IntPtr value);
    [DllImport(Library)] private static extern nuint JSStringGetMaximumUTF8CStringSize(IntPtr value);
    [DllImport(Library)] private static extern nuint JSStringGetUTF8CString(IntPtr value, byte[] buffer, nuint size);
    [DllImport(Library)] private static extern IntPtr JSEvaluateScript(IntPtr context, IntPtr script, IntPtr thisObject, IntPtr sourceUrl, int line, out IntPtr exception);
    [DllImport(Library)] private static extern IntPtr JSValueToStringCopy(IntPtr context, IntPtr value, out IntPtr exception);
    [DllImport(Library)] private static extern nuint JSObjectGetTypedArrayLength(IntPtr context, IntPtr obj, out IntPtr exception);
    [DllImport(Library)] private static extern IntPtr JSObjectGetTypedArrayBytesPtr(IntPtr context, IntPtr obj, out IntPtr exception);
}
