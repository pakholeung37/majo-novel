#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.History;

public interface IHistoryManager : IRuntimeService
{
    event Action<HistoryOpenStateChanged>? OpenStateChanged;

    event Action<HistoryEntriesChanged>? EntriesChanged;

    IReadOnlyList<HistoryEntry> Entries { get; }

    int Capacity { get; }

    bool IsOpen { get; }

    void Open();

    void Close();

    void Toggle();

    void Clear();

    Task<bool> ReplayVoiceAsync(
        long sequence,
        CancellationToken cancellationToken = default);
}

public interface IHistoryRestoreSink
{
    void CommitLoadedLine(Majo.Runtime.Dialogue.DialogueLine line);
}
