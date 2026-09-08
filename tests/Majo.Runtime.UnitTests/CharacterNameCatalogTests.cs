using Majo.Demos.Manosaba;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CharacterNameCatalogTests
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

    private static ManosabaCharacterNameCatalog LoadCharacterNames() =>
        ManosabaCharacterNameCatalog.Parse("""
            {
              "fallbackLocale": "ja",
              "names": {
                "Ema": {
                  "ja": "桜羽エマ", "en": "Ema Sakuraba",
                  "zh-Hans": "樱羽艾玛", "zh-Hant": "櫻羽艾瑪"
                }
              }
            }
            """);
}
