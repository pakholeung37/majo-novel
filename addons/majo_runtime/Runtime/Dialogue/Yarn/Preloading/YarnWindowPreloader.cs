#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Localization;
using Majo.Runtime.Preloading;
using Majo.Runtime.Resources;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Preloading;

public sealed class YarnWindowPreloader : IDisposable
{
    private readonly IActorManager actors;
    private readonly ActorDefinitionResolver? directionDefinitions;
    private readonly IRuntimeLogger logger;
    private readonly IResourceManager resources;
    private readonly ResourceConfiguration configuration;
    private readonly AudioConfiguration audio;
    private readonly ILocaleManager locales;
    private readonly ILocalizedResourceResolver localized;
    private readonly ResourceLookAheadWindow window;
    private readonly YarnPreloadPlanner planner = new();
    private readonly Dictionary<string, long> textureEstimates = new(StringComparer.Ordinal);
    private YarnProject? project;
    private readonly List<YarnPreloadReference> beatResources = new();
    private bool inBeat;
    private string? preparedRestoreNode;
    private string? currentNode;
    private readonly IDirectionTransactionCoordinator? direction;
    private readonly IDirectionScopeResolver? scopes;

    public async Task PrepareStartAsync(YarnProject value, string node, CancellationToken token, string? restoreLine = null)
    {
        inBeat = false;
        beatResources.Clear();
        if (!ReferenceEquals(project, value)) textureEstimates.Clear();
        project = value;
        currentNode = node;
        planner.Enter(value.Program.Nodes.TryGetValue(node, out var found) ? found : null);
        preparedRestoreNode = restoreLine is null ? null : node;
        if (restoreLine is not null) planner.Line(restoreLine);
        await MoveAsync(restoreLine is null ? Array.Empty<YarnPreloadReference>() :
            new[] { new YarnPreloadReference("line", restoreLine) }, token, waitForAhead: true);
    }

    public YarnWindowPreloader(IResourceManager resources, ResourceConfiguration configuration,
        AudioConfiguration audio, ILocaleManager locales, ILocalizedResourceResolver localized,
        IActorManager actors, IRuntimeLogger? logger = null,
        IDirectionTransactionCoordinator? direction = null,
        IDirectionScopeResolver? scopes = null,
        ActorDefinitionResolver? directionDefinitions = null)
    {
        this.resources = resources;
        this.directionDefinitions = directionDefinitions;
        this.actors = actors;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        this.configuration = configuration;
        this.audio = audio;
        this.locales = locales;
        this.localized = localized;
        this.direction = direction;
        this.scopes = scopes;
        window = new ResourceLookAheadWindow(resources, logger);
        locales.VoiceLocaleChanged += OnLocaleChanged;
        locales.DialogueLocaleChanged += OnLocaleChanged;
    }

    public void EnterNode(YarnProject value, string node)
    {
        if (ReferenceEquals(project, value) && preparedRestoreNode == node)
        {
            preparedRestoreNode = null;
            return; // The saved line window was prepared before starting the VM.
        }
        preparedRestoreNode = null;
        inBeat = false;
        beatResources.Clear();
        if (!ReferenceEquals(project, value)) textureEstimates.Clear();
        project = value;
        currentNode = node;
        planner.Enter(value.Program.Nodes.TryGetValue(node, out var found) ? found : null);
        // Node-start is synchronous. The first command/line supplies the barrier.
        _ = PrimeAsync();
    }

    private Task PrimeAsync() => MoveAsync(inBeat ? beatResources.ToArray() : Array.Empty<YarnPreloadReference>(),
        CancellationToken.None);

    public Task BeforeCommandAsync(string root, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var tokens = new[] { root }.Concat(arguments).ToArray();
        planner.Command(tokens);
        var required = YarnPreloadPlanner.Resources(tokens);
        if (tokens.Length >= 2 && tokens[0] == "beat" && tokens[1] == "begin")
        {
            inBeat = true;
            beatResources.Clear();
        }
        if (inBeat)
        {
            beatResources.AddRange(required);
            required = beatResources.ToArray();
            if (tokens.Length >= 2 && tokens[0] == "beat" && tokens[1] == "end") inBeat = false;
        }
        return MoveAsync(required, token);
    }

    public Task BeforeLineAsync(DialogueLine line, CancellationToken token)
    {
        planner.Line(line.Id);
        var required = inBeat ? beatResources.ToList() : new List<YarnPreloadReference>();
        if (line.Origin != DialoguePresentationOrigin.HotReloadReplay)
            required.Add(new YarnPreloadReference("line", line.Id));
        return MoveAsync(required, token);
    }

    private async Task MoveAsync(IReadOnlyList<YarnPreloadReference> required, CancellationToken token, bool waitForAhead = false)
    {
        if (!configuration.EnableYarnPreloading) return;
        try
        {
            var voices = new Dictionary<string, ResourcePreloadEntry[]>(StringComparer.Ordinal);
            var ahead = planner.LookAhead(configuration.PreloadVoiceLines, hasVoice: id =>
            {
                if (!voices.TryGetValue(id, out var entries))
                {
                    entries = Resolve(new[] { new YarnPreloadReference("line", id) });
                    voices.Add(id, entries);
                }
                return entries.Length > 0;
            });
            // Preserve authored order and the required prefix when deriving
            // incremental appearances. Deduplicate resources only after resolution.
            var aheadEntries = Resolve(required.Concat(ahead));
            await window.MoveAsync(Resolve(required), aheadEntries,
                (long)configuration.PreloadBudgetMiB * 1024 * 1024, configuration.PreloadMaxResources, waitForAhead, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.Warning("preload", "preload.window_fallback", $"Preload failed; using normal resource loading: {exception.Message}");
        }
    }

    private ResourcePreloadEntry[] Resolve(IEnumerable<YarnPreloadReference> references)
    {
        var result = new List<ResourcePreloadEntry>();
        var definitions = new Dictionary<(string Stage, string Instance), string>();
        var backdrops = new Dictionary<StageId, (string Definition, string Factory, string? Appearance)>();
        foreach (var reference in references)
        {
            var kind = reference.Kind;
            var id = reference.Id;
            var lineVoice = kind == "line";
            if (kind == "line")
            {
                var metadata = project?.LineMetadata?.GetMetadata(id) ?? Array.Empty<string>();
                var voice = DialogueVoiceResolver.ResolveVoiceId(new DialogueLine(id, null,
                    new DialogueText(string.Empty, Array.Empty<DialogueMarkupSpan>()), metadata));
                if (voice is null) continue;
                id = voice;
                kind = "voice";
            }
            var root = kind switch
            {
                "backdrop" => configuration.BackgroundsPath,
                "character" => configuration.CharactersPath,
                "object" => configuration.ObjectsPath,
                "voice" => audio.VoicePath,
                "bgm" => audio.BgmPath,
                "sfx" => audio.SfxPath,
                _ => null,
            };
            if (root is null || ResourcePath.IsRooted(id)) continue;
            if (kind is "backdrop" or "character" or "object")
            {
                var entries = kind == "object"
                    ? actors.GetPreloadResources(new ActorSpawnRequest(id, ActorFactoryIds.SceneObject, reference.Appearance ?? "Default")
                    { DefinitionId = reference.Asset })
                    : kind == "character"
                    ? actors.GetPreloadResources(new ActorSpawnRequest(
                        id,
                        ActorFactoryIds.Character,
                        reference.Appearance ?? "Default")
                    {
                        DefinitionId = ResolveCharacterDefinition(reference, definitions),
                        UseDefinitionDefaultAppearance = reference.Appearance is null,
                    })
                    : ResolveBackdropResources(reference, backdrops);
                foreach (var entry in entries)
                {
                    if (entry.ResourceType != typeof(Texture2D))
                    {
                        logger.Warning("preload", "preload.unsupported_actor_resource",
                            $"Actor resource '{entry.Key}' has unsupported preload type '{entry.ResourceType}'.");
                        continue;
                    }
                    var resolved = entry;
                    if (entry.Key.Path.StartsWith($"{root}/", StringComparison.Ordinal))
                    {
                        var resolution = localized.Resolve<Texture2D>(entry.Key, root, locales.DialogueLocale,
                            locales.GetDialogueFallbackChain(locales.DialogueLocale), entry.OverridePath);
                        if (!resolution.IsResolved) continue;
                        resolved = entry with { Key = resolution.ResolvedKey!.Value, OverridePath = resolution.ResolvedOverridePath };
                    }
                    if (resources.Exists<Texture2D>(resolved.Key, resolved.OverridePath))
                        result.Add(resolved with { EstimatedBytes = EstimateTexture(resolved) });
                }
            }
            else
            {
                if (kind == "voice" && (lineVoice || !id.Contains('/')))
                {
                    var resolution = localized.ResolvePath(id, locales.VoiceLocale,
                        locales.GetVoiceFallbackChain(locales.VoiceLocale),
                        candidate => resources.Exists<AudioStream>(ResourceKey.Combine(root, candidate)));
                    if (resolution.ResolvedPath is null) continue;
                    id = resolution.ResolvedPath;
                }
                var key = ResourceKey.Combine(root, id);
                if (resources.Exists<AudioStream>(key))
                    result.Add(ResourcePreloadEntry.Create<AudioStream>(key,
                        estimatedBytes: (long)configuration.PreloadAudioEstimateKiB * 1024));
            }
        }
        // Render owners keep these resources alive independently of this window.
        // Do not charge shared, already-active textures to speculative capacity.
        return result.Distinct().Where(entry => entry.ResourceType != typeof(Texture2D) ||
            !resources.IsPinned<Texture2D>(entry.Key, entry.OverridePath)).ToArray();
    }

    private IReadOnlyList<ResourcePreloadEntry> ResolveBackdropResources(YarnPreloadReference reference,
        Dictionary<StageId, (string Definition, string Factory, string? Appearance)> known)
    {
        var resolver = directionDefinitions ?? throw new ActorException("Direction backdrop metadata is unavailable.");
        var stage = scopes is not null && currentNode is not null
            ? scopes.ResolveStage(currentNode, reference.Stage ?? StageId.Main.Value)
            : new StageId(reference.Stage ?? StageId.Main.Value);
        var initial = resolver.ResolveBackdropAppearance(reference.Id);
        var hasPrevious = known.TryGetValue(stage, out var previous);
        if (!hasPrevious && direction?.State.Stages.TryGetValue(stage, out var current) == true && current.Backdrop is { } stored)
        {
            previous = (stored.DefinitionId, stored.FactoryId, stored.Appearance);
            hasPrevious = true;
        }
        var same = hasPrevious && previous.Definition == reference.Id && previous.Factory == initial.Factory.FactoryId;
        var baseline = same ? previous.Appearance : hasPrevious ? initial.Appearance : null;
        if (reference.Appearance is { } authored && initial.Factory.IsIncrementalAppearance(authored) && baseline is null)
        {
            known[stage] = (reference.Id, initial.Factory.FactoryId, null);
            return Array.Empty<ResourcePreloadEntry>();
        }
        var resolved = resolver.ResolveBackdropAppearance(reference.Id, reference.Appearance, baseline);
        known[stage] = (reference.Id, resolved.Factory.FactoryId, resolved.Appearance);
        return resolved.Factory.GetPreloadResources(resolved.Definition, resolved.Appearance);
    }

    private string ResolveCharacterDefinition(YarnPreloadReference reference,
        Dictionary<(string Stage, string Instance), string> definitions)
    {
        var stage = scopes is not null && currentNode is not null
            ? scopes.ResolveStage(currentNode, reference.Stage ?? StageId.Main.Value)
            : new StageId(reference.Stage ?? StageId.Main.Value);
        var key = (stage.Value, reference.Id);
        if (!string.IsNullOrWhiteSpace(reference.Asset))
            return definitions[key] = reference.Asset;
        if (definitions.TryGetValue(key, out var known)) return known;
        // An open beat's earlier explicit definitions are not in DirectionState yet.
        // The per-resolution map above handles them without leaking across stages.
        return direction is not null && direction.State.Stages.TryGetValue(stage, out var stageState) &&
            stageState.Actors.TryGetValue(new ActorId(reference.Id), out var actor)
                ? actor.DefinitionId ?? actor.Id.Value
                : reference.Id;
    }

    private long EstimateTexture(ResourcePreloadEntry entry)
    {
        var fallback = Math.Max(entry.EstimatedBytes, (long)configuration.PreloadTextureEstimateMiB * 1024 * 1024);
        var path = entry.OverridePath ?? ResourcePath.Combine(configuration.RootPath, entry.Key.Path);
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        if (textureEstimates.TryGetValue(path, out var estimate)) return Math.Max(estimate, fallback);
        if (FileAccess.FileExists(path))
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            using var import = new ConfigFile();
            var mipmaps = true;
            if (import.Load(path + ".import") == Error.Ok)
                mipmaps = import.GetValue("params", "mipmaps/generate", true).AsBool();
            if (file is not null) fallback = PreloadSizeEstimator.EstimatePng(file.GetBuffer(24), fallback, mipmaps);
        }
        textureEstimates[path] = fallback;
        return fallback;
    }

    public void OptionsStarting()
    {
        planner.Invalidate();
        if (!inBeat) window.Clear();
    }

    public void Clear()
    {
        preparedRestoreNode = null;
        inBeat = false;
        beatResources.Clear();
        planner.Enter(null);
        window.Clear();
    }

    private void OnLocaleChanged(LocaleChangedEvent change)
    {
        window.Clear();
        _ = PrimeAsync();
    }

    public void Dispose()
    {
        locales.VoiceLocaleChanged -= OnLocaleChanged;
        locales.DialogueLocaleChanged -= OnLocaleChanged;
        window.Dispose();
    }
}
