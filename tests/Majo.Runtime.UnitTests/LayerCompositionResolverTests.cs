using Majo.Runtime.Actors;
using Majo.Demos.Manosaba.Actors.Layered;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class LayerCompositionResolverTests
{
    [Fact]
    public void DefaultThenSurprisedResolvesCompleteEmaComposition()
    {
        var resolver = new LayerCompositionResolver(CreateEmaManifest());
        var state = resolver.CreateDefaultState();

        resolver.Apply(state, "Surprised1");

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Body",
                "Angle01/ArmR/ArmR01",
                "Angle01/ArmL/ArmL01",
                "Angle01/Head01/FacialLineDrawing01",
                "Angle01/Head01/HairClippingMask01",
                "Angle01/Head01/HeadBase01",
                "Angle01/Head01/Facial01/Sweat01/Sweat01_01",
                "Angle01/Head01/Facial01/Cheeks01/Cheeks01_Normal",
                "Angle01/Head01/Facial01/Eyes01/Eyes01_Surprised_Open01",
                "Angle01/Head01/Facial01/Mouth01/Mouth01_Surprised_Open",
            },
            state);
    }

    [Fact]
    public void AppearanceMutationsPreserveUnspecifiedArmState()
    {
        var resolver = new LayerCompositionResolver(CreateEmaManifest());
        var state = resolver.CreateDefaultState();

        resolver.Apply(state, "Normal1,ArmR3");
        resolver.Apply(state, "Fearful1");

        Assert.Contains("Angle01/ArmR/ArmR03", state);
        Assert.DoesNotContain("Angle01/ArmR/ArmR01", state);
        Assert.Contains("Angle01/Head01/Facial01/Eyes01/Eyes01_Fearful_Open01", state);
    }

    [Fact]
    public void CanonicalAppearanceRoundTripsWithoutPriorState()
    {
        var resolver = new LayerCompositionResolver(CreateEmaManifest());
        var original = resolver.CreateDefaultState();
        resolver.Apply(original, "Surprised1,ArmR3");
        var canonical = resolver.Canonicalize(original);
        var restored = resolver.CreateDefaultState();

        resolver.Apply(restored, canonical);

        Assert.Equal(original, restored);
        Assert.StartsWith("-,+Body", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectPathOverrideAndInvalidAliasesAreDeterministic()
    {
        var resolver = new LayerCompositionResolver(CreateEmaManifest());
        var state = resolver.CreateDefaultState();

        resolver.Apply(
            state,
            "Angle01/Head01/Facial01/Eyes01~Eyes01_Surprised_Open01");

        Assert.Contains("Angle01/Head01/Facial01/Eyes01/Eyes01_Surprised_Open01", state);
        Assert.DoesNotContain("Angle01/Head01/Facial01/Eyes01/Eyes01_Normal_Open01", state);
        Assert.Throws<ActorException>(() => resolver.Apply(state, "MissingAlias"));
    }

    private static LayeredCharacterManifest CreateEmaManifest()
    {
        var layers = new[]
        {
            Layer("Body", true, 1),
            Layer("Angle01/Head01/HeadBase01", true, 29),
            Layer("Angle01/Head01/Facial01/Cheeks01/Cheeks01_Normal", true, 31),
            Layer("Angle01/Head01/Facial01/Mouth01/Mouth01_Fearful_Open", false, 39),
            Layer("Angle01/Head01/Facial01/Mouth01/Mouth01_Surprised_Open", false, 40),
            Layer("Angle01/Head01/Facial01/Mouth01/Mouth01_Normal_Closed", true, 55),
            Layer("Angle01/Head01/Facial01/Eyes01/Eyes01_Fearful_Open01", false, 64),
            Layer("Angle01/Head01/Facial01/Eyes01/Eyes01_Surprised_Open01", false, 67),
            Layer("Angle01/Head01/Facial01/Eyes01/Eyes01_Normal_Open01", true, 100),
            Layer("Angle01/Head01/Facial01/Sweat01/Sweat01_01", false, 102),
            Layer("Angle01/Head01/HairClippingMask01", true, 109),
            Layer("Angle01/Head01/FacialLineDrawing01", true, 110),
            Layer("Angle01/ArmL/ArmL01", true, 127),
            Layer("Angle01/ArmR/ArmR03", false, 139),
            Layer("Angle01/ArmR/ArmR01", true, 142),
        };
        return new LayeredCharacterManifest
        {
            SchemaVersion = 1,
            Id = "Ema",
            DefaultAppearance = "ArmR1,ArmL1,Default",
            Layers = [.. layers],
            CompositionMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ArmR1"] = "Angle01/ArmR>ArmR01",
                ["ArmR3"] = "Angle01/ArmR>ArmR03",
                ["ArmL1"] = "Angle01/ArmL>ArmL01",
                ["Head1"] = "Angle01/Head01+FacialLineDrawing01,Angle01/Head01+HairClippingMask01,Angle01/Head01+HeadBase01",
                ["Normal1"] = "Head1,Sweat1-Off,Cheeks1-Normal,Angle01/Head01/Facial01/Eyes01>Eyes01_Normal_Open01,Angle01/Head01/Facial01/Mouth01>Mouth01_Normal_Closed",
                ["Surprised1"] = "Head1,Sweat1-1,Cheeks1-Normal,Angle01/Head01/Facial01/Eyes01>Eyes01_Surprised_Open01,Angle01/Head01/Facial01/Mouth01>Mouth01_Surprised_Open",
                ["Fearful1"] = "Head1,Sweat1-Off,Cheeks1-Normal,Angle01/Head01/Facial01/Eyes01>Eyes01_Fearful_Open01,Angle01/Head01/Facial01/Mouth01>Mouth01_Fearful_Open",
                ["Sweat1-1"] = "Angle01/Head01/Facial01/Sweat01>Sweat01_01",
                ["Sweat1-Off"] = "Angle01/Head01/Facial01/Sweat01-",
                ["Cheeks1-Normal"] = "Angle01/Head01/Facial01/Cheeks01>Cheeks01_Normal",
                ["Default"] = "Normal1",
            },
        };
    }

    private static LayeredCharacterLayer Layer(string path, bool enabled, int order) => new()
    {
        Path = path,
        Atlas = "ema.png",
        Enabled = enabled,
        SortingOrder = order,
        Region = new LayeredCharacterRegion { Width = 1, Height = 1 },
    };
}
