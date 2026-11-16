#nullable enable

using Godot;

namespace Majo.Demos.SGRE;

// A transparent Label mirrors RichTextLabel's layout so the original wait icon
// can sit after the last character, including when a line wraps.
public partial class SGREPresentation : Control
{
    private RichTextLabel text = null!;
    private Label layout = null!;
    private TextureRect waitIcon = null!;

    public override void _Ready()
    {
        text = GetNode<RichTextLabel>("LinePresenter/PresenterControl/LineText");
        layout = GetNode<Label>("LinePresenter/PresenterControl/LayoutMirror");
        waitIcon = GetNode<TextureRect>("LinePresenter/PresenterControl/WaitIcon");
    }

    public override void _Process(double delta)
    {
        var content = text.GetParsedText();
        if (layout.Text != content) layout.Text = content;
        var count = text.GetTotalCharacterCount();
        var visible = text.VisibleCharacters;
        waitIcon.Visible = text.IsVisibleInTree() && count > 0 &&
            (visible < 0 || visible >= count);
        if (!waitIcon.Visible) return;
        var last = layout.GetCharacterBounds(count - 1);
        var x = Mathf.Min(last.End.X + 8, layout.Size.X - waitIcon.Size.X);
        waitIcon.Position = layout.Position + new Vector2(x, last.Position.Y +
            (last.Size.Y - waitIcon.Size.Y) / 2);
    }
}
