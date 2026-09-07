#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Yarn;
using Yarn.Compiler;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed class YarnProjectMaterializer
{
    public static YarnProjectVersion Materialize(
        long generation,
        YarnRuntimeCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (compilation.HasErrors || compilation.Result.Program is null)
        {
            throw new InvalidOperationException(
                "A Yarn project cannot be materialized from a failed compilation.");
        }

        var result = compilation.Result;
        var programBytes = result.Program.ToByteArray();
        var localization = new YarnSpinnerGodot.Localization
        {
            ResourceName = $"Hot Reload ({compilation.Snapshot.BaseLanguage})",
            LocaleCode = compilation.Snapshot.BaseLanguage,
        };
        var metadataEntries = new List<LineMetadataTableEntry>();
        var lineNodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var implicitIds = new HashSet<string>(StringComparer.Ordinal);
        if (result.StringTable is not null)
        {
            foreach (var pair in result.StringTable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var info = pair.Value;
                localization.AddLocalisedStringToAsset(pair.Key, new StringTableEntry
                {
                    ID = pair.Key,
                    Language = compilation.Snapshot.BaseLanguage,
                    Text = info.text ?? string.Empty,
                    Original = string.Empty,
                    File = info.fileName.Replace('\\', '/'),
                    Node = info.nodeName,
                    LineNumber = info.lineNumber.ToString(CultureInfo.InvariantCulture),
                    Lock = ShortHash(info.text ?? string.Empty),
                    Comment = string.Empty,
                });
                lineNodes.Add(pair.Key, info.nodeName);
                if (info.isImplicitTag)
                {
                    implicitIds.Add(pair.Key);
                }

                var metadata = info.metadata
                    .Where(value => !value.StartsWith("line:", StringComparison.Ordinal))
                    .ToArray();
                if (metadata.Length > 0)
                {
                    metadataEntries.Add(new LineMetadataTableEntry
                    {
                        ID = pair.Key,
                        File = info.fileName.Replace('\\', '/'),
                        Node = info.nodeName,
                        LineNumber = info.lineNumber.ToString(CultureInfo.InvariantCulture),
                        Metadata = metadata,
                    });
                }
            }
        }

        var declarations = result.Declarations
            .Where(declaration => !declaration.Name.StartsWith("$Yarn.Internal.", StringComparison.Ordinal))
            .Where(declaration => declaration.Type is not FunctionType)
            .Select(declaration =>
            {
                var serialized = new SerializedDeclaration();
                serialized.SetDeclaration(declaration);
                return serialized;
            })
            .ToArray();
        var project = new YarnProject
        {
            ResourceName = $"{compilation.Snapshot.ProjectId} (hot reload {generation})",
            JSONProjectPath = compilation.Snapshot.ManifestPath,
            CompiledYarnProgramBase64 = Convert.ToBase64String(programBytes),
            baseLocalization = localization,
            LineMetadata = new LineMetadata(metadataEntries),
            SerializedDeclarations = declarations,
            ProjectErrors = Array.Empty<YarnProjectError>(),
            IsSuccessfullyParsed = true,
            LastImportHadAnyStrings = lineNodes.Count > 0,
            LastImportHadImplicitStringIDs = implicitIds.Count > 0,
        };

        var roundTrip = project.Program.ToByteArray();
        if (!programBytes.AsSpan().SequenceEqual(roundTrip))
        {
            throw new InvalidOperationException(
                "The materialized Yarn program did not survive serialization round-trip.");
        }

        return new YarnProjectVersion(
            generation,
            compilation.Snapshot.ProjectId,
            compilation.Snapshot.SourceFingerprint,
            Hash(programBytes),
            project,
            new HashSet<string>(result.Program.Nodes.Keys, StringComparer.Ordinal),
            new HashSet<string>(lineNodes.Keys, StringComparer.Ordinal),
            lineNodes,
            implicitIds,
            compilation.Diagnostics,
            DateTimeOffset.UtcNow)
        {
            DirectionModules = compilation.DirectionModules,
        };
    }

    private static string ShortHash(string value) => Hash(Encoding.UTF8.GetBytes(value))[..8];

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
