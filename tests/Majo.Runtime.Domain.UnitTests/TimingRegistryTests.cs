#nullable enable

using Majo.Runtime.Direction.Core;
using Majo.Runtime.Timing;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class TimingRegistryTests
{
    [Fact]
    public void BuiltInEasingsAreRegisteredWithExpectedDefaultsAndAliases()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        foreach (var def in BuiltInEasings.All)
        {
            Assert.True(registry.Contains(def.Id));
            Assert.NotNull(registry.GetRequired(def.Id));
            Assert.Equal(def.Id, registry.Resolve(def.Id.Value).Id);

            if (def.Aliases is not null)
            {
                foreach (var alias in def.Aliases)
                {
                    Assert.Equal(def.Id, registry.Resolve(alias).Id);
                    Assert.Equal(def.Id, registry.Resolve(alias.ToUpperInvariant()).Id);
                }
            }
        }
    }

    [Fact]
    public void BuiltInEasingsEvaluateFiniteOnDomainBounds()
    {
        foreach (var def in BuiltInEasings.All)
        {
            var v0 = def.Evaluate(0.0);
            var vHalf = def.Evaluate(0.5);
            var v1 = def.Evaluate(1.0);

            Assert.True(double.IsFinite(v0), $"{def.Id} evaluated non-finite at 0: {v0}");
            Assert.True(double.IsFinite(vHalf), $"{def.Id} evaluated non-finite at 0.5: {vHalf}");
            Assert.True(double.IsFinite(v1), $"{def.Id} evaluated non-finite at 1: {v1}");
        }
    }

    [Fact]
    public void RegisteringCustomEasingSucceedsAndCanBeResolved()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var elasticOutId = new EasingId("elastic-out");
        var custom = new EasingDefinition(
            elasticOutId,
            t => Math.Sin(-13.0 * (Math.PI / 2.0) * (t + 1.0)) * Math.Pow(2.0, -10.0 * t) + 1.0,
            new[] { "elasticout", "ElasticOut" });

        registry.Register(custom);

        Assert.True(registry.Contains(elasticOutId));
        Assert.Same(custom, registry.GetRequired(elasticOutId));
        Assert.Same(custom, registry.Resolve("elastic-out"));
        Assert.Same(custom, registry.Resolve("elasticout"));
        Assert.Same(custom, registry.Resolve("ELASTICOUT"));
        Assert.Same(custom, registry.Resolve("ElasticOut"));
    }

    [Fact]
    public void RegisteringDuplicateIdThrowsArgumentException()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var duplicate = new EasingDefinition(EasingId.Linear, t => t);
        var ex = Assert.Throws<ArgumentException>(() => registry.Register(duplicate));
        Assert.Contains("linear", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisteringDuplicateAliasThrowsArgumentException()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var conflicting = new EasingDefinition(
            new EasingId("custom-sine"),
            t => t,
            new[] { "sinein" }); // conflicts with built-in SineIn alias

        var ex = Assert.Throws<ArgumentException>(() => registry.Register(conflicting));
        Assert.Contains("sinein", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisteringInvalidDefinitionThrows()
    {
        var registry = new EasingRegistry();

        // Empty ID
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new EasingDefinition(new EasingId(""), t => t)));

        // Null delegate
        Assert.Throws<ArgumentNullException>(() =>
            registry.Register(new EasingDefinition(new EasingId("test"), null!)));

        // Non-finite evaluation
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new EasingDefinition(new EasingId("nan-ease"), t => double.NaN)));
    }

    [Fact]
    public void RegisteringAfterSealThrowsInvalidOperationException()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);
        registry.Seal();

        var custom = new EasingDefinition(new EasingId("custom"), t => t);
        Assert.Throws<InvalidOperationException>(() => registry.Register(custom));
    }

    [Fact]
    public void ResolveUnknownNameThrowsWithCandidateDiagnostics()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Resolve("nonexistent-ease"));
        Assert.Contains("nonexistent-ease", ex.Message);
        Assert.Contains("linear", ex.Message);
    }

    [Fact]
    public void TransitionSpecAcceptsCustomEasingWithoutEnumCheck()
    {
        var customId = new EasingId("custom-bounce");
        var spec = new DirectionTransitionSpec("fade", 1.0, customId);
        var validated = spec.Validate();

        Assert.Equal(customId, validated.Easing);
        Assert.Equal(1.0, validated.DurationSeconds);

        // Empty EasingId should fail
        var invalidSpec = new DirectionTransitionSpec(new DirectionTransitionId("fade"), 1.0, new EasingId(""));
        Assert.Throws<ArgumentException>(() => invalidSpec.Validate());
    }
}
