#nullable enable

using System;
using System.Text;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public sealed class GodotConsoleSink : IRuntimeLogSink
{
	private readonly bool includeCorrelation;

	public GodotConsoleSink(bool includeCorrelation = false)
	{
		this.includeCorrelation = includeCorrelation;
	}

	public bool IncludeCorrelation => includeCorrelation;

	public void Emit(RuntimeLogEvent logEvent)
	{
		ArgumentNullException.ThrowIfNull(logEvent);

		var formatted = FormatEvent(logEvent, includeCorrelation);
		switch (logEvent.Level)
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

	public static string FormatEvent(
		RuntimeLogEvent logEvent,
		bool includeCorrelation = false)
	{
		var builder = new StringBuilder();
		builder.Append('[')
			.Append(logEvent.Timestamp.ToString("HH:mm:ss.fff"))
			.Append("] [")
			.Append(logEvent.Level)
			.Append("] [")
			.Append(logEvent.Category)
			.Append("] (")
			.Append(logEvent.EventId)
			.Append(") ")
			.Append(logEvent.Message);

		if (includeCorrelation &&
			logEvent.Correlation != default &&
			!string.IsNullOrEmpty(logEvent.Correlation.RuntimeId))
		{
			builder.Append(" [Correlation: ");
			builder.Append("runtime=").Append(logEvent.Correlation.RuntimeId);
			if (!string.IsNullOrEmpty(logEvent.Correlation.SessionId))
			{
				builder.Append(", session=").Append(logEvent.Correlation.SessionId);
			}
			if (!string.IsNullOrEmpty(logEvent.Correlation.DialogueSessionId))
			{
				builder.Append(", dialogue=").Append(logEvent.Correlation.DialogueSessionId);
			}
			if (!string.IsNullOrEmpty(logEvent.Correlation.CommandId))
			{
				builder.Append(", cmd=").Append(logEvent.Correlation.CommandId);
			}
			if (!string.IsNullOrEmpty(logEvent.Correlation.OperationId))
			{
				builder.Append(", op=").Append(logEvent.Correlation.OperationId);
			}
			builder.Append(']');
		}

		if (logEvent.Exception is not null)
		{
			builder.AppendLine().Append(logEvent.Exception);
		}

		return builder.ToString();
	}

	public void Dispose()
	{
	}
}
