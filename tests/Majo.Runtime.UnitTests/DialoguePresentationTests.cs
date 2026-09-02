using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DialoguePresentationTests
{
    [Fact]
    public void MarkupHandlerRegistryPreservesOrderAndRejectsDuplicateInstance()
    {
        var first = new ProbeMarkupHandler();
        var second = new ProbeMarkupHandler();
        var registry = new DialogueMarkupHandlerRegistry()
            .Register(first)
            .Register(second);

        Assert.Equal([first, second], registry.Handlers);
        Assert.Throws<DialogueException>(() => registry.Register(first));
    }

    private sealed class ProbeMarkupHandler : IDialogueMarkupHandler;
}
