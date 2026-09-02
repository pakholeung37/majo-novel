using System;
using Majo.Runtime.Dialogue.Yarn;
using Xunit;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.UnitTests;

public sealed class YarnLineConverterTests
{
    [Fact]
    public void YarnLineConversionUsesLocalizedSpeakerNameResolver()
    {
        var parser = new LineParser();
        var line = new LocalizedLine
        {
            TextID = "line:test",
            RawText = "Ema: 你好",
            Text = parser.ParseString("Ema: 你好", "zh-Hans"),
        };

        var converted = YarnLineConverter.Convert(
            line,
            "zh-Hans",
            (id, locale) => id == "Ema" && locale == "zh-Hans" ? "樱羽艾玛" : id);

        Assert.Equal("樱羽艾玛", converted.Speaker);
        Assert.Equal("你好", converted.Text.Text);
    }

    [Fact]
    public void SelfClosingLineBreakMarkupProducesALineFeed()
    {
        const string rawText = "Ema: 昨天是想到明天我就要上高中了……[br /]　很开心地睡着了才对";
        var parser = new LineParser();
        parser.RegisterMarkerProcessor("br", new MajoLineBreakMarkupReplacer());
        var parsedText = parser.ParseString(rawText, "zh-Hans");
        var line = new LocalizedLine
        {
            TextID = "line:test-line-break",
            RawText = rawText,
            Text = parsedText,
        };

        var converted = YarnLineConverter.Convert(line);

        Assert.Equal("Ema", converted.Speaker);
        Assert.Equal("昨天是想到明天我就要上高中了……\n　很开心地睡着了才对", converted.Text.Text);
        Assert.DoesNotContain("[character", converted.Text.Text, StringComparison.Ordinal);
    }
}
