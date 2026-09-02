#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Localization;

namespace Majo.Demo;

public sealed class DemoSessionService : IRuntimeService
{
	private readonly IRuntimeLifetime lifetime;
	private readonly IResourceManager? resources;
	private readonly ILocaleManager? locales;
	private IResourceGroupLease? preloadGroup;

	public DemoSessionService(
		IRuntimeLifetime lifetime,
		IResourceManager resources,
		ILocaleManager locales)
	{
		this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
		this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
		this.locales = locales ?? throw new ArgumentNullException(nameof(locales));
	}

	public DemoSessionService(IRuntimeLifetime lifetime)
	{
		this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
	}

	public string? SelectedRoute { get; private set; }

	public int InitializeCount { get; private set; }

	public int ResetCount { get; private set; }

	public int ShutdownCount { get; private set; }

	public bool RuntimeWasCancelledAtShutdown { get; private set; }

	public IResourceGroupLease? PreloadGroup => preloadGroup;

	public string VoiceLocale => locales?.VoiceLocale ?? "en";

	public Task InitializeAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		InitializeCount++;
		return Task.CompletedTask;
	}

	public Task ResetAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ResetCount++;
		SelectedRoute = null;
		preloadGroup?.Release();
		preloadGroup = null;
		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ShutdownCount++;
		RuntimeWasCancelledAtShutdown = lifetime.RuntimeCancellationToken.IsCancellationRequested;
		preloadGroup?.Release();
		preloadGroup = null;
		return Task.CompletedTask;
	}

	public void SelectRoute(string route)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(route);
		SelectedRoute = route;
	}

	public async Task SwitchVoiceLocaleAsync(
		string locale,
		CancellationToken cancellationToken)
	{
		var localeManager = locales ?? throw new RuntimeCommandException(
			"demo_voice_locale", "Locale manager is not available.");
		var result = await localeManager.ChangeVoiceLocaleAsync(locale, cancellationToken);
		if (!result.Success)
		{
			throw new RuntimeCommandException(
				"demo_voice_locale",
				$"Voice locale switch to '{locale}' failed: {result.FailureReason}");
		}
	}

	public async Task PreloadDemoAssetsAsync(
		string groupId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
		var resourceManager = resources ?? throw new RuntimeCommandException(
			"demo_preload", "Resource manager is not available.");
		var localeManager = locales ?? throw new RuntimeCommandException(
			"demo_preload", "Locale manager is not available.");
		preloadGroup?.Release();
		var manifest = new ResourcePreloadManifest(
			groupId,
			[
				ResourcePreloadEntry.Create<Texture2D>(
					new ResourceKey("backgrounds/backdrop/courtroom"),
					estimatedBytes: 512 * 1024),
				ResourcePreloadEntry.Create<Texture2D>(
					new ResourceKey("backgrounds/backdrop/spotlight"),
					estimatedBytes: 512 * 1024),
				ResourcePreloadEntry.Create<Texture2D>(
					new ResourceKey("characters/hero/calm"),
					estimatedBytes: 256 * 1024),
				ResourcePreloadEntry.Create<Texture2D>(
					new ResourceKey("characters/hero/determined"),
					estimatedBytes: 256 * 1024),
				ResourcePreloadEntry.Create<AudioStream>(
					new ResourceKey("audio/bgm/demo-theme"),
					estimatedBytes: 1024 * 1024),
				ResourcePreloadEntry.Create<AudioStream>(
					new ResourceKey("audio/bgm/demo-tension"),
					estimatedBytes: 1024 * 1024),
				ResourcePreloadEntry.Create<AudioStream>(
					new ResourceKey("audio/voice/narrator-welcome"),
					estimatedBytes: 1024 * 1024,
					localizedRoot: "audio/voice",
					localeChannel: ResourceLocaleChannel.Voice),
			]);
		preloadGroup = resourceManager.PreloadAsync(
			manifest,
			new ResourcePreloadContext(
				localeManager.DialogueLocale,
				localeManager.VoiceLocale,
				localeManager.GetDialogueFallbackChain(localeManager.DialogueLocale),
				localeManager.GetVoiceFallbackChain(localeManager.VoiceLocale)),
			cancellationToken);
		await preloadGroup.Completion.WaitAsync(cancellationToken);
	}

	public void ReleasePreload()
	{
		preloadGroup?.Release();
		preloadGroup = null;
	}
}

public static class RuntimeDemoComposition
{
	public const string DialogueProjectId = "main";

	public static MajoProjectConfiguration CreateConfiguration()
	{
		var configuration = MajoProjectConfiguration.CreateDefault();
		configuration.Resources!.RootPath = "res://demos/demo-01/assets";
		configuration.Presentation!.Enabled = true;
		configuration.Presentation.LettersPerSecond = 36;
		configuration.Auto!.ExtraDelaySeconds = 1;
		configuration.Operations!.DefaultAnimationDurationSeconds = 0.2;
		configuration.Diagnostics!.EnableOverlay = true;
		configuration.Diagnostics.EnableFileLogging = true;
		configuration.State!.StorageNamespace = "majo/runtime-demo";
		configuration.State.GameBuildId = "runtime-demo-phase-3";
		configuration.Save!.Enabled = true;
		configuration.Save.ManualSlotCapacity = 3;
		configuration.Save.QuickSlotCapacity = 2;
		configuration.Save.AutoSlotCapacity = 2;
		configuration.Save.ShowDefaultUi = true;
		configuration.Settings!.ShowDefaultUi = true;
		configuration.Settings.SkipDelaySeconds = 0.08;
		configuration.GlobalState!.Enabled = true;
		configuration.Actors!.ReferenceResolution = new Vector2I(1920, 1080);
		configuration.Actors!.LeftPosition = new Vector2(560, 610);
		configuration.Actors.CenterPosition = new Vector2(960, 610);
		configuration.Actors.RightPosition = new Vector2(1360, 610);
		configuration.Actors.Actors.Add(CreateActorDefinition(
			"hero",
			ActorFactoryIds.Character,
			"calm",
			CharacterPositionPreset.Right,
			configuration.Resources!.CharactersPath));
		return configuration;
	}

	public static void ConfigureRuntime(
		MajoRuntimeBootstrap runtime,
		bool useFakeAudio = false)
	{
		ArgumentNullException.ThrowIfNull(runtime);
		runtime.ConfigureServices += services =>
		{
			services.AddSingleton<DemoSessionService>(
				resolver => new DemoSessionService(
					resolver.Get<IRuntimeLifetime>(),
					resolver.Get<IResourceManager>(),
					resolver.Get<ILocaleManager>()),
				typeof(IRuntimeLifetime),
				typeof(IResourceManager),
				typeof(ILocaleManager));
			if (!useFakeAudio)
			{
				return;
			}

			var audio = new FakeAudioClipLoader("generated://runtime-demo")
				.Register("demo-theme")
				.Register("demo-tension")
				.Register("demo-chime")
				.Register("en/narrator-welcome")
				.Register("en/maya-ready");
			services.ReplaceSingleton<IAudioManager>(
				resolver => new AudioManager(
					new FakeAudioBackend(),
					resolver.Get<IOperationScheduler>(),
					audio,
					audio,
					audio,
					new AudioSettings(),
					runtime.Logger,
					resolver.Get<IMissingResourceReporter>()),
				typeof(IOperationScheduler),
				typeof(IMissingResourceReporter));
		};
		runtime.ConfigureCommands += RegisterCommands;
	}

	public static void RegisterCommands(RuntimeCommandRegistry commands)
	{
		ArgumentNullException.ThrowIfNull(commands);
		commands.Register<DemoRouteCommandArguments>(SelectRouteAsync);
		commands.Register<DemoVoiceLocaleCommandArguments>(SwitchVoiceLocaleAsync);
		commands.Register<DemoPreloadCommandArguments>(PreloadAsync);
		commands.Register<DemoReleasePreloadCommandArguments>(ReleasePreloadAsync);
	}

	private static Task SelectRouteAsync(
		DemoRouteCommandArguments arguments,
		RuntimeCommandContext context)
	{
		if (arguments.Route is not "press" and not "evidence")
		{
			throw new RuntimeCommandException(
				"demo_route", $"Unknown route '{arguments.Route}'.");
		}

		var services = context.Require(context.Services, "ServiceResolver");
		services.Get<DemoSessionService>().SelectRoute(arguments.Route);
		return Task.CompletedTask;
	}

	private static Task SwitchVoiceLocaleAsync(
		DemoVoiceLocaleCommandArguments arguments,
		RuntimeCommandContext context) =>
		context.Require(context.Services, "ServiceResolver")
			.Get<DemoSessionService>()
			.SwitchVoiceLocaleAsync(arguments.Locale, context.CancellationToken);

	private static Task PreloadAsync(
		DemoPreloadCommandArguments arguments,
		RuntimeCommandContext context) =>
		context.Require(context.Services, "ServiceResolver")
			.Get<DemoSessionService>()
			.PreloadDemoAssetsAsync(arguments.Group, context.CancellationToken);

	private static Task ReleasePreloadAsync(
		DemoReleasePreloadCommandArguments arguments,
		RuntimeCommandContext context)
	{
		_ = arguments;
		context.Require(context.Services, "ServiceResolver")
			.Get<DemoSessionService>()
			.ReleasePreload();
		return Task.CompletedTask;
	}

	private static ActorDefinitionConfiguration CreateActorDefinition(
		string id,
		string factoryId,
		string defaultAppearance,
		CharacterPositionPreset initialPosition,
		string resourcePathPrefix = "")
	{
		var definition = new ActorDefinitionConfiguration
		{
			Id = id,
			FactoryId = factoryId,
			DefaultAppearance = defaultAppearance,
			InitialPosition = initialPosition,
			ResourcePathPrefix = resourcePathPrefix,
		};
		return definition;
	}
}

[RuntimeCommand(
	"demo_route",
	Category = "Demo",
	Usage = "demo_route <press|evidence>")]
public sealed class DemoRouteCommandArguments
{
	[CommandParameter("route", 0, Primary = true, Required = true)]
	public string Route { get; set; } = string.Empty;
}

[RuntimeCommand(
	"demo_voice_locale",
	Category = "M17 Demo",
	Usage = "demo_voice_locale <locale>")]
public sealed class DemoVoiceLocaleCommandArguments
{
	[CommandParameter("locale", 0, Primary = true, Required = true)]
	public string Locale { get; set; } = string.Empty;
}

[RuntimeCommand(
	"demo_preload",
	Category = "M18 Demo",
	Usage = "demo_preload <group>")]
public sealed class DemoPreloadCommandArguments
{
	[CommandParameter("group", 0, Primary = true, Required = true)]
	public string Group { get; set; } = string.Empty;
}

[RuntimeCommand(
	"demo_release_preload",
	Category = "M18 Demo",
	Usage = "demo_release_preload")]
public sealed class DemoReleasePreloadCommandArguments
{
}
