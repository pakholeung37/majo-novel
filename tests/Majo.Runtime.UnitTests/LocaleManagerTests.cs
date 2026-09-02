#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Majo.Runtime.Audio;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Localization;
using Majo.Runtime.Played;
using Majo.Runtime.Resources;
using Majo.Runtime.Settings;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class LocaleManagerTests
{
    [Fact]
    public void GetDialogueFallbackChain_GeneratesExpectedHierarchy()
    {
        var manager = new LocaleManager("en", ["en", "zh-CN", "zh-Hans", "zh", "ja"]);

        var chainZhCn = manager.GetDialogueFallbackChain("zh-CN");
        Assert.Equal(["zh-CN", "zh", "en"], chainZhCn);

        var chainZhHansCn = manager.GetDialogueFallbackChain("zh-Hans-CN");
        Assert.Equal(["zh-Hans-CN", "zh-Hans", "zh", "en"], chainZhHansCn);

        var chainJaJp = manager.GetDialogueFallbackChain("ja-JP");
        Assert.Equal(["ja-JP", "ja", "en"], chainJaJp);

        var chainEn = manager.GetDialogueFallbackChain("en");
        Assert.Equal(["en"], chainEn);

        var chainEnUs = manager.GetDialogueFallbackChain("en-US");
        Assert.Equal(["en-US", "en"], chainEnUs);
    }

    [Fact]
    public async Task DialogueAndVoiceLocales_AreIndependent()
    {
        var manager = new LocaleManager(
            "en",
            ["en", "zh-CN", "ja"],
            defaultVoiceLocale: "ja");

        await manager.ChangeDialogueLocaleAsync("zh-CN");
        Assert.Equal("zh-CN", manager.DialogueLocale);
        Assert.Equal("ja", manager.VoiceLocale);

        await manager.ChangeVoiceLocaleAsync("en");
        Assert.Equal("zh-CN", manager.DialogueLocale);
        Assert.Equal("en", manager.VoiceLocale);

        Assert.Equal(["zh-CN", "zh", "ja"], manager.GetVoiceFallbackChain("zh-CN"));
    }

    [Fact]
    public void LocalizedResourceResolver_UsesHierarchyThenFlatProjectFallback()
    {
        var resolver = new LocalizedResourceResolver(new ResolverOnlyResourceManager());
        var manager = new LocaleManager("en", ["en", "zh-Hans", "zh"]);
        var chain = manager.GetDialogueFallbackChain("zh-Hans-CN");

        var parent = resolver.ResolvePath(
            "line-1", "zh-Hans-CN", chain,
            path => path == "zh-Hans/line-1");
        Assert.Equal("zh-Hans/line-1", parent.ResolvedPath);
        Assert.Equal(LocalizedResourceFallbackSource.Parent, parent.FallbackSource);

        var defaultLocale = resolver.ResolvePath(
            "line-1", "zh-Hans-CN", chain,
            path => path == "en/line-1");
        Assert.Equal("en/line-1", defaultLocale.ResolvedPath);
        Assert.Equal(LocalizedResourceFallbackSource.Default, defaultLocale.FallbackSource);

        var flat = resolver.ResolvePath(
            "line-1", "zh-Hans-CN", chain,
            path => path == "line-1");
        Assert.Equal("line-1", flat.ResolvedPath);
        Assert.Equal(LocalizedResourceFallbackSource.Unlocalized, flat.FallbackSource);

        var missing = resolver.ResolvePath(
            "line-1", "zh-Hans-CN", chain,
            _ => false);
        Assert.False(missing.IsResolved);
        Assert.Equal(LocalizedResourceFallbackSource.Missing, missing.FallbackSource);
    }

    [Fact]
    public async Task ChangeDialogueLocaleAsync_TransactionalSuccessAndFailureRollback()
    {
        var manager = new LocaleManager("en", ["en", "zh-CN"]);
        LocaleChangedEvent? changedEvent = null;
        LocaleSwitchFailedEvent? failedEvent = null;

        manager.DialogueLocaleChanged += e => changedEvent = e;
        manager.SwitchFailed += e => failedEvent = e;

        // Successful change
        var result = await manager.ChangeDialogueLocaleAsync("zh-CN");
        Assert.True(result.Success);
        Assert.Equal("en", result.PreviousLocale);
        Assert.Equal("zh-CN", result.AppliedLocale);
        Assert.Equal("zh-CN", manager.DialogueLocale);
        Assert.NotNull(changedEvent);
        Assert.Equal("zh-CN", changedEvent!.CurrentLocale);

        // Attempt unsupported locale with no fallback to available
        var failResult = await manager.ChangeDialogueLocaleAsync("fr-FR");
        // fr-FR -> fr -> en (en is available, so it falls back)
        Assert.True(failResult.Success);
        Assert.Equal("fr-FR", manager.DialogueLocale);
    }

    [Fact]
    public async Task ChangeDialogueLocaleAsync_WaitsForSafeBoundaryBeforeApplying()
    {
        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isBusy = true;
        var applied = new List<string>();
        var manager = new LocaleManager(
            "en",
            ["en", "zh-CN"],
            isBusyCheck: () => isBusy,
            onLocaleApplied: locale =>
            {
                applied.Add(locale);
                return Task.CompletedTask;
            },
            waitForSafeBoundary: token => boundary.Task.WaitAsync(token));

        var change = manager.ChangeDialogueLocaleAsync("zh-CN");
        await Task.Yield();

        Assert.False(change.IsCompleted);
        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(LocaleSwitchState.PendingPresentationBoundary, manager.SwitchState);
        Assert.Empty(applied);

        isBusy = false;
        boundary.SetResult();
        var result = await change;

        Assert.True(result.Success);
        Assert.Equal("zh-CN", manager.DialogueLocale);
        Assert.Equal(["zh-CN"], applied);
    }

    [Fact]
    public async Task ChangeDialogueLocaleAsync_ApplyFailureRestoresEnginePresentationAndIdentity()
    {
        var engineLocales = new List<string>();
        var presentationLocales = new List<string>();
        LocaleChangedEvent? changed = null;
        LocaleSwitchFailedEvent? failed = null;
        var manager = new LocaleManager(
            "en",
            ["en", "zh-CN"],
            onLocaleApplied: locale =>
            {
                presentationLocales.Add(locale);
                if (locale == "zh-CN")
                {
                    throw new InvalidOperationException("catalog unavailable");
                }
                return Task.CompletedTask;
            },
            applyEngineLocale: engineLocales.Add);
        manager.DialogueLocaleChanged += value => changed = value;
        manager.SwitchFailed += value => failed = value;

        var result = await manager.ChangeDialogueLocaleAsync("zh-CN");

        Assert.False(result.Success);
        Assert.Equal("en", manager.DialogueLocale);
        Assert.Null(changed);
        Assert.NotNull(failed);
        Assert.Equal(["zh-CN", "en"], engineLocales);
        Assert.Equal(["zh-CN", "en"], presentationLocales);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
    }

    [Fact]
    public async Task ChangeDialogueLocaleAsync_ApplyCancellationRollsBackBeforePropagating()
    {
        var engineLocales = new List<string>();
        var presentationLocales = new List<string>();
        var manager = new LocaleManager(
            "en",
            ["en", "zh-CN"],
            onLocaleApplied: locale =>
            {
                presentationLocales.Add(locale);
                if (locale == "zh-CN")
                {
                    throw new OperationCanceledException("presentation canceled");
                }

                return Task.CompletedTask;
            },
            applyEngineLocale: engineLocales.Add);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => manager.ChangeDialogueLocaleAsync("zh-CN"));

        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(["zh-CN", "en"], engineLocales);
        Assert.Equal(["zh-CN", "en"], presentationLocales);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
    }

    [Fact]
    public async Task ChangeDialogueLocaleAsync_PreflightFailureDoesNotMutateLocale()
    {
        var applyCount = 0;
        var manager = new LocaleManager(
            "en",
            ["en", "zh-CN"],
            onLocaleApplied: _ =>
            {
                applyCount++;
                return Task.CompletedTask;
            },
            preflightLocale: (locale, _) => throw new InvalidOperationException($"Missing font for {locale}"));

        var result = await manager.ChangeDialogueLocaleAsync("zh-CN");

        Assert.False(result.Success);
        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(0, applyCount);
    }

    [Fact]
    public async Task ResetAsync_CancelsQueuedLocaleTransaction()
    {
        var manager = new LocaleManager(
            "en",
            ["en", "zh-CN"],
            isBusyCheck: () => true,
            waitForSafeBoundary: token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        await manager.InitializeAsync(default);

        var change = manager.ChangeDialogueLocaleAsync("zh-CN");
        await Task.Yield();
        await manager.ResetAsync(default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        Assert.Equal("en", manager.DialogueLocale);
        Assert.Equal(LocaleSwitchState.Idle, manager.SwitchState);
    }

    [Fact]
    public void FontCatalog_TypographyChangedEventAndValidation()
    {
        var fonts = new FontCatalog();
        var eventCount = 0;
        fonts.TypographyChanged += () => eventCount++;

        fonts.FontScale = 1.25;
        Assert.Equal(1, eventCount);
        Assert.Equal(1.25, fonts.FontScale);

        fonts.LineSpacing = 4;
        Assert.Equal(2, eventCount);
        Assert.Equal(4, fonts.LineSpacing);

        fonts.SetTypography(1.5, 8);
        Assert.Equal(3, eventCount);
        Assert.Equal(1.5, fonts.FontScale);
        Assert.Equal(8, fonts.LineSpacing);

        Assert.Throws<ArgumentOutOfRangeException>(() => fonts.FontScale = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fonts.FontScale = -1);
    }

    [Fact]
    public void DialogueLine_CarriesResolvedLocale()
    {
        var line = new DialogueLine(
            "line_101",
            "Speaker",
            new DialogueText("Hello", Array.Empty<DialogueMarkupSpan>()),
            Array.Empty<string>());
        var localized = new DialogueLine(
            "test_key",
            "Speaker",
            new DialogueText("不对！", Array.Empty<DialogueMarkupSpan>()),
            ["voice:custom_voice"],
            ResolvedLocale: "zh-CN");

        Assert.Equal("zh-CN", localized.ResolvedLocale);
        Assert.Equal("custom_voice", DialogueVoiceResolver.ResolveVoiceId(localized));

        var yarnLine = new DialogueLine(
            "line:0101Adv02_Ema001",
            "Ema",
            new DialogueText("不对！", Array.Empty<DialogueMarkupSpan>()),
            []);
        Assert.Equal("0101Adv02_Ema001", DialogueVoiceResolver.ResolveVoiceId(yarnLine));
    }

    [Fact]
    public void HistoryEntry_PreservesHistoricalLocaleAndText()
    {
        var entryEn = new HistoryEntry(1, "line_1", "Narrator", "Welcome!", "voice_1", "en");
        var entryZh = new HistoryEntry(2, "line_2", "Narrator", "欢迎！", "voice_2", "zh-CN");

        Assert.Equal("en", entryEn.Locale);
        Assert.Equal("Welcome!", entryEn.Text);

        Assert.Equal("zh-CN", entryZh.Locale);
        Assert.Equal("欢迎！", entryZh.Text);
    }

    [Fact]
    public void RuntimeSettings_ValidationForLocalizationAndAccessibility()
    {
        var valid = new RuntimeSettings(
            DialogueLocale: "zh-CN",
            VoiceLocale: "ja",
            FontScale: 1.2,
            LineSpacing: 4).Validate();

        Assert.Equal("zh-CN", valid.DialogueLocale);
        Assert.Equal("ja", valid.VoiceLocale);
        Assert.Equal(1.2, valid.FontScale);
        Assert.Equal(4, valid.LineSpacing);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RuntimeSettings(FontScale: 0).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RuntimeSettings(LineSpacing: -1).Validate());

        Assert.Throws<ArgumentException>(() =>
            new RuntimeSettings(DialogueLocale: "   ").Validate());
    }

    [Fact]
    public void Diagnostics_CapturesLocalizationSnapshot()
    {
        var manager = new LocaleManager("en", ["en", "zh-CN"]);
        manager.RecordMissingKey("test.missing", "en");

        var snapshot = manager.CaptureDiagnostics();
        Assert.Equal("en", snapshot.DialogueLocale);
        Assert.Equal("en", snapshot.VoiceLocale);
        Assert.Equal("en", snapshot.DefaultLocale);
        Assert.Contains("zh-CN", snapshot.AvailableLocales);
        Assert.Equal(LocaleSwitchState.Idle, snapshot.SwitchState);
        Assert.Equal(1, snapshot.MissingKeyCount);
    }

    private sealed class ResolverOnlyResourceManager : IResourceManager
    {
        public int LoadedResourceCount => 0;

        public TResource Load<TResource>(ResourceKey key, string? overridePath = null)
            where TResource : class => throw new NotSupportedException();

        public Task<TResource> LoadAsync<TResource>(
            ResourceKey key,
            string? overridePath = null,
            CancellationToken cancellationToken = default)
            where TResource : class => Task.FromException<TResource>(new NotSupportedException());

        public bool Exists<TResource>(ResourceKey key, string? overridePath = null)
            where TResource : class => false;

        public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
            where TResource : class => throw new NotSupportedException();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
