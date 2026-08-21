using Majo.Runtime.Dialogue.Yarn;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class YarnDependencyValidatorTests
{
    private const string CompatibleManifest = """
        [plugin]
        name="YarnSpinner-Godot (C#)"
        version="0.3.22"
        """;

    private const string CompatibleProps = """
        <Project>
          <ItemGroup>
            <PackageReference Include="YarnSpinner" Version="3.2.0"/>
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void CompatibleVersionsPass()
    {
        var status = YarnDependencyValidator.ValidateContents(
            CompatibleManifest,
            CompatibleProps);

        Assert.True(status.IsCompatible, status.Message);
        Assert.Equal("0.3.22", status.DetectedPluginVersion);
        Assert.Equal("3.2.0", status.DetectedRuntimeVersion);
    }

    [Theory]
    [InlineData("0.3.21", "3.2.0", "YarnSpinner-Godot 0.3.22 is required")]
    [InlineData("0.3.22", "3.1.0", "Yarn Spinner 3.2.0 is required")]
    public void IncompatibleVersionsFail(
        string pluginVersion,
        string runtimeVersion,
        string expectedMessage)
    {
        var manifest = CompatibleManifest.Replace("0.3.22", pluginVersion);
        var props = CompatibleProps.Replace("3.2.0", runtimeVersion);

        var status = YarnDependencyValidator.ValidateContents(manifest, props);

        Assert.False(status.IsCompatible);
        Assert.Contains(expectedMessage, status.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[plugin]\nname=\"Yarn\"", "<Project />")]
    [InlineData("", "")]
    public void MalformedMetadataFails(string manifest, string props)
    {
        var status = YarnDependencyValidator.ValidateContents(manifest, props);

        Assert.False(status.IsCompatible);
        Assert.Contains("malformed or incomplete", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingProjectDependencyFailsWithoutThrowing()
    {
        var unavailableProjectPath = Path.Combine(
            Path.GetTempPath(),
            $"majo-missing-{Guid.NewGuid():N}");

        var status = YarnDependencyValidator.ValidateProject(unavailableProjectPath);

        Assert.False(status.IsCompatible);
        Assert.Contains("was not found", status.Message, StringComparison.Ordinal);
    }
}
