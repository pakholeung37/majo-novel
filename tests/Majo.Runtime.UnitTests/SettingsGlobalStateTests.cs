using System.Text.Json;
using Majo.Runtime.Settings;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SettingsGlobalStateTests
{
    [Fact]
    public async Task SettingsProjectImmediatelyAndPersistAcrossRuntimeRestart()
    {
        using var directory = new TemporaryDirectory();
        var projection = new RecordingProjection();
        var first = CreateSettings(directory.Path, projection);
        await first.InitializeAsync(default);

        var changed = first.Current with
        {
            MasterVolume = 0.25f,
            LettersPerSecond = 77,
            AutoExtraDelaySeconds = 2.5,
        };
        first.Update(changed);

        Assert.Equal(changed, projection.Last);
        Assert.True(first.IsDirty);
        await first.FlushAsync();
        Assert.False(first.IsDirty);
        await first.ShutdownAsync(default);

        var secondProjection = new RecordingProjection();
        var second = CreateSettings(directory.Path, secondProjection);
        await second.InitializeAsync(default);

        Assert.Equal(changed, second.Current);
        Assert.Equal(changed, secondProjection.Last);
        Assert.Equal(ScopedStateRecoveryKind.Loaded, second.RecoveryStatus.Kind);
    }

    [Fact]
    public async Task CorruptSettingsAreQuarantinedAndDefaultsRemainUsable()
    {
        using var directory = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(
            directory.Path, "test", "scopes", "settings.state");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
        await File.WriteAllTextAsync(statePath, "not-json");

        var manager = CreateSettings(directory.Path, new RecordingProjection());
        await manager.InitializeAsync(default);

        Assert.Equal(manager.Defaults, manager.Current);
        Assert.Equal(
            ScopedStateRecoveryKind.RecoveredFromCorruption,
            manager.RecoveryStatus.Kind);
        Assert.NotNull(manager.RecoveryStatus.QuarantineKey);
        Assert.False(File.Exists(statePath));
        Assert.Single(Directory.GetFiles(
            System.IO.Path.GetDirectoryName(statePath)!,
            "settings.state.corrupt-*"));
    }

    [Fact]
    public async Task GlobalSoftRecoveryQuarantinesButStrictPolicyFailsWithoutOverwriting()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "test", "scopes", "global.state");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "broken");
        var contributor = new IntegerContributor(StateScope.Global);

        var strict = CreateGlobal(directory.Path, contributor, strict: true);
        await Assert.ThrowsAnyAsync<Exception>(() => strict.InitializeAsync(default));
        Assert.Equal(ScopedStateRecoveryKind.StrictFailure, strict.RecoveryStatus.Kind);
        Assert.True(File.Exists(path));

        var soft = CreateGlobal(directory.Path, contributor, strict: false);
        await soft.InitializeAsync(default);
        Assert.Equal(ScopedStateRecoveryKind.RecoveredFromCorruption, soft.RecoveryStatus.Kind);
        Assert.Equal(0, contributor.Value);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FailedFlushKeepsDirtyStateAndReportsFailure()
    {
        var contributor = new IntegerContributor(StateScope.Global) { Value = 42 };
        var manager = new GlobalStateManager(
            [contributor],
            new CanonicalJsonStateSerializer(),
            new FailingWriteStorage(),
            new StateMigrationRegistry(),
            "1.0.0",
            "build",
            "test",
            TimeSpan.FromHours(1));
        await manager.InitializeAsync(default);
        contributor.Value = 43;
        manager.MarkDirty();

        await Assert.ThrowsAsync<StateStorageException>(() => manager.FlushAsync());

        Assert.True(manager.IsDirty);
        Assert.NotNull(manager.LastPersistenceError);
    }

    [Fact]
    public async Task CustomGlobalContributorUsesStateCorePersistence()
    {
        using var directory = new TemporaryDirectory();
        var firstContributor = new IntegerContributor(StateScope.Global) { Value = 7 };
        var first = CreateGlobal(directory.Path, firstContributor, strict: false);
        await first.InitializeAsync(default);
        firstContributor.Value = 91;
        first.MarkDirty();
        await first.FlushAsync();

        var secondContributor = new IntegerContributor(StateScope.Global);
        var second = CreateGlobal(directory.Path, secondContributor, strict: false);
        await second.InitializeAsync(default);

        Assert.Equal(91, secondContributor.Value);
    }

    [Fact]
    public async Task DebounceCoalescesRapidSettingsUpdatesIntoOneCommit()
    {
        var storage = new RecordingStorage();
        var manager = new SettingsManager(
            new RuntimeSettings(),
            [],
            [new RecordingProjection()],
            new CanonicalJsonStateSerializer(),
            storage,
            new StateMigrationRegistry(),
            "1.0.0",
            "build",
            "test",
            TimeSpan.FromMilliseconds(20));
        await manager.InitializeAsync(default);
        manager.Update(manager.Current with { MasterVolume = 0.9f });
        manager.Update(manager.Current with { MasterVolume = 0.8f });
        manager.Update(manager.Current with { MasterVolume = 0.7f });

        await Task.Delay(100);

        Assert.Equal(1, storage.WriteCount);
        Assert.False(manager.IsDirty);
    }

    [Fact]
    public async Task GlobalContributorUsesRegisteredSchemaMigration()
    {
        using var directory = new TemporaryDirectory();
        var versionOne = new IntegerContributor(StateScope.Global, schemaVersion: 1);
        var first = CreateGlobal(directory.Path, versionOne, strict: false);
        await first.InitializeAsync(default);
        versionOne.Value = 12;
        first.MarkDirty();
        await first.FlushAsync();

        var migrations = new StateMigrationRegistry().RegisterContributor(
            new DelegateStateContributorMigration(
                "global.integer.v1-v2",
                "global.integer",
                1,
                "global.integer",
                2,
                payload => JsonSerializer.SerializeToElement(new
                {
                    Value = payload.GetProperty("Value").GetInt32() + 1,
                })));
        var versionTwo = new IntegerContributor(StateScope.Global, schemaVersion: 2);
        var second = new GlobalStateManager(
            [versionTwo],
            new CanonicalJsonStateSerializer(migrations),
            new FileStateStorage(directory.Path, "test"),
            migrations,
            "1.0.0",
            "build",
            "test",
            TimeSpan.FromHours(1));

        await second.InitializeAsync(default);

        Assert.Equal(13, versionTwo.Value);
    }

    [Fact]
    public void ContributorRegistryCanResolveBuiltInScopeDependencies()
    {
        var contributor = new IntegerContributor(
            StateScope.Settings,
            dependencies: ["runtime.settings"]);

        var ordered = new StateContributorRegistry()
            .Register(contributor)
            .Build(["runtime.settings"]);

        Assert.Same(contributor, Assert.Single(ordered));
    }

    private static SettingsManager CreateSettings(string root, ISettingsProjection projection)
    {
        var defaults = new RuntimeSettings(Language: "zh-Hans");
        return new SettingsManager(
            defaults,
            [],
            [projection],
            new CanonicalJsonStateSerializer(),
            new FileStateStorage(root, "test"),
            new StateMigrationRegistry(),
            "1.0.0",
            "build",
            "test",
            TimeSpan.FromHours(1));
    }

    private static GlobalStateManager CreateGlobal(
        string root,
        IntegerContributor contributor,
        bool strict) => new(
            [contributor],
            new CanonicalJsonStateSerializer(),
            new FileStateStorage(root, "test"),
            new StateMigrationRegistry(),
            "1.0.0",
            "build",
            "test",
            TimeSpan.FromHours(1),
            strict);

    private sealed class RecordingProjection : ISettingsProjection
    {
        public RuntimeSettings? Last { get; private set; }
        public void Apply(RuntimeSettings settings) => Last = settings;
    }

    private sealed class IntegerContributor : IStateContributor
    {
        public IntegerContributor(
            StateScope scope,
            int schemaVersion = 1,
            IReadOnlyCollection<string>? dependencies = null)
        {
            Scope = scope;
            SchemaVersion = schemaVersion;
            Dependencies = dependencies ?? [];
        }
        public int Value { get; set; }
        public string Key => $"{Scope.ToString().ToLowerInvariant()}.integer";
        public StateScope Scope { get; }
        public int SchemaVersion { get; }
        public StateContributorCapability Capability => StateContributorCapability.Durable;
        public IReadOnlyCollection<string> Dependencies { get; }
        public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StateRecord(
                Key,
                Scope,
                SchemaVersion,
                JsonSerializer.SerializeToElement(new Payload(Value))));
        public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
        {
            _ = record.Payload.Deserialize<Payload>() ?? throw new InvalidOperationException();
            return ValueTask.CompletedTask;
        }
        public ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
        {
            Value = record.Payload.Deserialize<Payload>()!.Value;
            return ValueTask.CompletedTask;
        }
        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            Value = 0;
            return ValueTask.CompletedTask;
        }
        private sealed record Payload(int Value);
    }

    private sealed class FailingWriteStorage : IStateStorage
    {
        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromException<byte[]>(new StateStorageNotFoundException(key));
        public Task WriteAsync(
            string key,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new StateStorageException("write failed"));
        public Task<IReadOnlyList<string>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingStorage : IStateStorage
    {
        public int WriteCount { get; private set; }
        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromException<byte[]>(new StateStorageNotFoundException(key));
        public Task WriteAsync(string key, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"majo-m14-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
