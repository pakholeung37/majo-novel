using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionAnimationSlotTests
{
    [Fact]
    public async Task SupersededWriterCannotApplyOrFinishAndItsWaitEnds()
    {
        var slot = new DirectionAnimationSlot();
        var position = 0f;
        var old = slot.Replace(p => position = p * 100);
        old.Apply(.4f);
        var start = position;
        var next = slot.Replace(p => position = start + p * 20);
        await old.Completion;
        old.Apply(1);
        old.Finish();
        Assert.Equal(40, position);
        next.Finish();
        Assert.Equal(60, position);
        Assert.Equal(DirectionActionEnd.Superseded, old.EndReason);
    }

    [Fact]
    public void AppearanceReplacementCompletesOldMixButIndependentSlotsContinue()
    {
        var appearance = new DirectionAnimationSlot();
        var space = new DirectionAnimationSlot();
        var progress = 0f;
        var mix = appearance.Replace(p => progress = p);
        var movement = space.Replace(_ => { });
        appearance.Replace(_ => { }, finishPrevious: true);
        Assert.Equal(1, progress);
        Assert.True(mix.Completion.IsCompletedSuccessfully);
        Assert.False(movement.Completion.IsCompleted);
    }
}
