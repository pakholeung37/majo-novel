#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.State;

namespace Majo.Demos.Manosaba;

public enum ManosabaFrameMode
{
	Default = 0,
	Normal = 0,
	Stream = 1,
}

public interface IManosabaUiService : IRuntimeService
{
	ManosabaFrameMode CurrentFrameMode { get; }
	void SetFrameMode(ManosabaFrameMode mode, double duration = 0);
}

public sealed class ManosabaUiService : IManosabaUiService, IStateContributor
{
	private ManosabaPresentation? presentation;

	public ManosabaFrameMode CurrentFrameMode { get; private set; } = ManosabaFrameMode.Default;

	public string Key => "manosaba.ui";
	public StateScope Scope => StateScope.Game;
	public int SchemaVersion => 1;
	public StateContributorCapability Capability => new(
		true,
		StateRollbackPolicy.Supported,
		StateRestoreRecordPolicy.UseDefault);

	public void Attach(ManosabaPresentation presentation)
	{
		this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
		this.presentation.SetFrameMode(CurrentFrameMode);
	}

	public void SetFrameMode(ManosabaFrameMode mode, double duration = 0)
	{
		CurrentFrameMode = mode;
		presentation?.SetFrameMode(mode, duration);
	}

	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var payload = new ManosabaUiStatePayload((int)CurrentFrameMode);
		return ValueTask.FromResult(
			new StateRecord(
				Key,
				Scope,
				SchemaVersion,
				StateJson.Serialize(payload)));
	}

	public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		_ = StateJson.Deserialize<ManosabaUiStatePayload>(record);
		return ValueTask.CompletedTask;
	}

	public ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var payload = StateJson.Deserialize<ManosabaUiStatePayload>(record);
		if (payload is not null)
		{
			SetFrameMode((ManosabaFrameMode)payload.FrameMode);
		}

		return ValueTask.CompletedTask;
	}

	ValueTask IStateContributor.ResetAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		SetFrameMode(ManosabaFrameMode.Default);
		return ValueTask.CompletedTask;
	}
}

public sealed record ManosabaUiStatePayload(int FrameMode);
