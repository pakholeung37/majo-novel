using Majo.Runtime.Configuration;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ConfigurationValidationTests
{
    private sealed class MockSectionConfiguration : IValidatableConfiguration
    {
        public int Value { get; set; }

        public void Validate(ConfigurationValidationContext context)
        {
            if (Value <= 0)
            {
                context.AddIssue(nameof(Value), "Value must be positive.");
            }
        }
    }

    private sealed class MockCompositeConfiguration : IValidatableConfiguration
    {
        public MockSectionConfiguration? Child { get; set; }

        public void Validate(ConfigurationValidationContext context)
        {
            if (Child is null)
            {
                context.AddIssue(nameof(Child), "Configuration is required.");
                return;
            }

            Child.Validate(context.ForSection(nameof(Child)));
        }
    }

    [Fact]
    public void ValidationContext_ForSection_BuildsHierarchicalPath()
    {
        var issues = new List<ConfigurationIssue>();
        var rootContext = new ConfigurationValidationContext(null, _ => true, issues);

        var sectionContext = rootContext.ForSection("Actors").ForSection("Appearances[0]");
        sectionContext.AddIssue("TexturePath", "File not found.");

        Assert.Single(issues);
        Assert.Equal("Actors.Appearances[0].TexturePath", issues[0].Path);
        Assert.Equal("File not found.", issues[0].Message);
    }

    [Fact]
    public void ValidationContext_EmptyProperty_UsesSectionPrefix()
    {
        var issues = new List<ConfigurationIssue>();
        var context = new ConfigurationValidationContext(null, _ => true, issues, "Dialogue.HotReload");

        context.AddIssue(string.Empty, "Timing must be positive.");

        Assert.Single(issues);
        Assert.Equal("Dialogue.HotReload", issues[0].Path);
    }

    [Fact]
    public void ValidatableConfiguration_ExtensionsValidate_ReturnsValidResult()
    {
        var config = new MockSectionConfiguration { Value = 42 };
        var result = config.Validate();

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidatableConfiguration_ExtensionsValidate_CapturesIssues()
    {
        var config = new MockSectionConfiguration { Value = -1 };
        var result = config.Validate();

        Assert.False(result.IsValid);
        Assert.Single(result.Issues);
        Assert.Equal("Value", result.Issues[0].Path);
        Assert.Equal("Value must be positive.", result.Issues[0].Message);
    }

    [Fact]
    public void CompositeValidation_ChainsChildValidationCorrectly()
    {
        var composite = new MockCompositeConfiguration
        {
            Child = new MockSectionConfiguration { Value = 0 }
        };

        var result = composite.Validate();

        Assert.False(result.IsValid);
        Assert.Single(result.Issues);
        Assert.Equal("Child.Value", result.Issues[0].Path);
        Assert.Equal("Value must be positive.", result.Issues[0].Message);
    }

    [Fact]
    public void CompositeValidation_ReportsNullChildRequired()
    {
        var composite = new MockCompositeConfiguration { Child = null };

        var result = composite.Validate();

        Assert.False(result.IsValid);
        Assert.Single(result.Issues);
        Assert.Equal("Child", result.Issues[0].Path);
        Assert.Equal("Configuration is required.", result.Issues[0].Message);
    }
}
