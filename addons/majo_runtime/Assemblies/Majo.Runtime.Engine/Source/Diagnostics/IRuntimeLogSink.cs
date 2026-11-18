#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public interface IRuntimeLogSink : IDisposable
{
    void Emit(RuntimeLogEvent logEvent);
}
