using System;
using System.IO;
using System.Linq;
using Majo.Demos.Manosaba;
using Majo.Runtime.Dialogue.Yarn;
using Xunit;
using Yarn.Compiler;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.UnitTests;

public sealed class ManosabaScriptCompilationTests
{
	[Fact]
	public void CharacterNamesResolveByLocaleAndFallback()
	{
		var catalog = LoadCharacterNames();

		Assert.Equal("樱羽艾玛", catalog.Resolve("Ema", "zh-CN"));
		Assert.Equal("櫻羽艾瑪", catalog.Resolve("Ema", "zh-TW"));
		Assert.Equal("Ema Sakuraba", catalog.Resolve("Ema", "en-US"));
		Assert.Equal("桜羽エマ", catalog.Resolve("Ema", "fr-FR"));
		Assert.Equal("MissingId", catalog.Resolve("MissingId", "zh-Hans"));
	}

	[Fact]
	public void YarnLineConversionUsesLocalizedSpeakerName()
	{
		var catalog = LoadCharacterNames();
		var parser = new LineParser();
		var line = new LocalizedLine
		{
			TextID = "line:test",
			RawText = "Ema: 你好",
			Text = parser.ParseString("Ema: 你好", "zh-Hans"),
		};

		var converted = YarnLineConverter.Convert(line, "zh-Hans", catalog.Resolve);

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

    [Fact]
    public void ManosabaYarnFilesCompileSuccessfully()
    {
        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../demos/manosaba/assets/scripts"));
        var yarnPaths = Directory.GetFiles(
            basePath,
            "*.yarn",
            SearchOption.AllDirectories);

        Assert.NotEmpty(yarnPaths);

        var job = CompilationJob.CreateFromFiles(yarnPaths);
        var result = Compiler.Compile(job);

        var errors = result.Diagnostics
            .Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        Assert.Empty(errors);
        Assert.NotNull(result.Program);
        Assert.True(result.Program.Nodes.ContainsKey("Start"));
        Assert.True(result.Program.Nodes.ContainsKey("Act01_Chapter01_Adv01"));
        Assert.True(result.Program.Nodes.ContainsKey("Act01_Chapter01_Adv02"));
        Assert.True(result.Program.Nodes.ContainsKey("EndAdv"));
    }

	private static ManosabaCharacterNameCatalog LoadCharacterNames()
	{
		var path = Path.GetFullPath(Path.Combine(
			AppContext.BaseDirectory,
			"../../../../../demos/manosaba/assets/localization/character_names.json"));
		return ManosabaCharacterNameCatalog.Parse(File.ReadAllText(path), path);
	}
}
