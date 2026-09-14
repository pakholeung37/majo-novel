#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.AceAttorney;

public partial class AceAttorneyDialogueProbe : Node, IDialoguePresenter
{
	public int LineCount { get; private set; }

	public async Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
	{
		LineCount++;
		await Task.Delay(120, context.NextLineToken);
		context.RequestNextLine();
	}

	public Task<DialogueOption?> PresentOptionsAsync(
		IReadOnlyList<DialogueOption> options, CancellationToken cancellationToken) =>
		Task.FromResult(options.FirstOrDefault(option => option.IsAvailable));
}
