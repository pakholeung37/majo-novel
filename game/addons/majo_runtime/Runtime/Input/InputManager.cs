#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Input;

public sealed class InputManager : IInputManager
{
    private readonly object sync = new();
    private readonly IInputBackend backend;
    private readonly IRuntimeLogger logger;
    private readonly bool defaultEnabled;
    private readonly List<InputHandle> handles = new();
    private readonly Dictionary<object, OwnerRegistration> owners =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<InputActionId> actionsSeenThisFrame = new();
    private ulong currentFrame;
    private long nextOrder;
    private bool hasCurrentFrame;
    private bool isInitialized;
    private bool enabled;

    public InputManager(
        IInputBackend backend,
        bool enabled = true,
        IRuntimeLogger? logger = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        defaultEnabled = enabled;
        this.enabled = enabled;
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action<InputActivation>? Dispatched;

    public bool Enabled
    {
        get
        {
            lock (sync)
            {
                return enabled;
            }
        }
        set
        {
            lock (sync)
            {
                enabled = value;
            }
        }
    }

    public int ActiveHandleCount
    {
        get
        {
            lock (sync)
            {
                return handles.Count(handle => !handle.IsDisposed);
            }
        }
    }

    public IReadOnlyList<InputHandleSnapshot> ActiveHandles
    {
        get
        {
            lock (sync)
            {
                return handles
                    .Where(handle => !handle.IsDisposed)
                    .Select(handle => handle.CaptureSnapshot())
                    .ToArray();
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (isInitialized)
            {
                return Task.CompletedTask;
            }

            backend.Activated += HandleBackendActivation;
            enabled = defaultEnabled;
            isInitialized = true;
        }

        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseAllHandles();
        lock (sync)
        {
            enabled = defaultEnabled;
            ClearFrameDeduplicationLocked();
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseAllHandles();
        lock (sync)
        {
            if (!isInitialized)
            {
                return Task.CompletedTask;
            }

            backend.Activated -= HandleBackendActivation;
            enabled = false;
            ClearFrameDeduplicationLocked();
            isInitialized = false;
        }

        return Task.CompletedTask;
    }

    public IInputHandle AcquireScope(object owner, InputScopeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        options ??= new InputScopeOptions();
        lock (sync)
        {
            EnsureInitializedLocked();
            RegisterOwnerLocked(owner);
            var handle = new InputHandle(
                this,
                owner,
                ++nextOrder,
                options);
            handles.Add(handle);
            logger.Debug(
                "input",
                "input.scope_acquired",
                $"Input scope '{handle.Name ?? "<anon>"}' acquired (modal: {handle.IsModal}).");
            return handle;
        }
    }

    public IInputHandle AcquireMuter(object owner, InputMuteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        options ??= new InputMuteOptions();
        lock (sync)
        {
            EnsureInitializedLocked();
            RegisterOwnerLocked(owner);
            var handle = new InputHandle(
                this,
                owner,
                ++nextOrder,
                options);
            handles.Add(handle);
            logger.Debug(
                "input",
                "input.muter_acquired",
                $"Input muter '{handle.Name ?? "<anon>"}' acquired.");
            return handle;
        }
    }

    public void ReleaseOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        InputHandle[] ownedHandles;
        lock (sync)
        {
            ownedHandles = handles
                .Where(handle => ReferenceEquals(handle.Owner, owner))
                .ToArray();
        }

        foreach (var handle in ownedHandles)
        {
            handle.Dispose();
        }
    }

    private void HandleBackendActivation(InputActivation activation)
    {
        InputHandle? target;
        lock (sync)
        {
            if (!isInitialized || !enabled || IsDuplicateLocked(activation))
            {
                return;
            }

            target = ResolveTargetLocked(activation.Action);
        }

        if (target is null || !target.TryPublish(activation))
        {
            return;
        }

        Dispatched?.Invoke(activation);
    }

    private InputHandle? ResolveTargetLocked(InputActionId action)
    {
        var modal = handles
            .Where(handle =>
                !handle.IsDisposed &&
                handle.Enabled &&
                handle.Kind == InputHandleKind.Scope &&
                handle.IsModal)
            .MaxBy(handle => handle.Order);
        if (modal is not null)
        {
            return modal.Accepts(action) ? modal : null;
        }

        if (handles.Any(handle =>
            !handle.IsDisposed &&
            handle.Enabled &&
            handle.Kind == InputHandleKind.Muter &&
            handle.Mutes(action)))
        {
            return null;
        }

        return handles
            .Where(handle =>
                !handle.IsDisposed &&
                handle.Enabled &&
                handle.Kind == InputHandleKind.Scope &&
                handle.Accepts(action))
            .MaxBy(handle => handle.Order);
    }

    private bool IsDuplicateLocked(InputActivation activation)
    {
        if (!hasCurrentFrame || activation.Frame != currentFrame)
        {
            currentFrame = activation.Frame;
            hasCurrentFrame = true;
            actionsSeenThisFrame.Clear();
        }

        return !actionsSeenThisFrame.Add(activation.Action);
    }

    private void Release(InputHandle handle)
    {
        OwnerRegistration? ownerToDetach = null;
        lock (sync)
        {
            if (!handle.MarkDisposed())
            {
                return;
            }

            handles.Remove(handle);
            if (!handles.Any(candidate => ReferenceEquals(candidate.Owner, handle.Owner)) &&
                owners.Remove(handle.Owner, out var registration))
            {
                ownerToDetach = registration;
            }
        }

        ownerToDetach?.Detach();
        logger.Debug(
            "input",
            handle.Kind == InputHandleKind.Scope
                ? "input.scope_released"
                : "input.muter_released",
            $"Input {handle.Kind.ToString().ToLowerInvariant()} '{handle.Name ?? "<anon>"}' released.");
    }

    private void ReleaseAllHandles()
    {
        InputHandle[] snapshot;
        lock (sync)
        {
            snapshot = handles.ToArray();
        }

        foreach (var handle in snapshot)
        {
            handle.Dispose();
        }
    }

    private void RegisterOwnerLocked(object owner)
    {
        if (owners.ContainsKey(owner))
        {
            return;
        }

        owners.Add(owner, new OwnerRegistration(owner, () => ReleaseOwner(owner)));
    }

    private void EnsureInitializedLocked()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException(
                "Input Manager must be initialized before acquiring handles.");
        }
    }

    private void ClearFrameDeduplicationLocked()
    {
        hasCurrentFrame = false;
        currentFrame = 0;
        actionsSeenThisFrame.Clear();
    }

    private sealed class InputHandle : IInputHandle
    {
        private readonly InputManager manager;
        private readonly InputScopeOptions? scopeOptions;
        private readonly InputMuteOptions? muteOptions;
        private bool enabled = true;
        private bool isDisposed;

        public InputHandle(
            InputManager manager,
            object owner,
            long order,
            InputScopeOptions options)
        {
            this.manager = manager;
            Owner = owner;
            Order = order;
            scopeOptions = options;
            Kind = InputHandleKind.Scope;
            Name = options.Name;
            IsModal = options.IsModal;
        }

        public InputHandle(
            InputManager manager,
            object owner,
            long order,
            InputMuteOptions options)
        {
            this.manager = manager;
            Owner = owner;
            Order = order;
            muteOptions = options;
            Kind = InputHandleKind.Muter;
            Name = options.Name;
        }

        public event Action<InputActivation>? Activated;

        public object Owner { get; }

        public long Order { get; }

        public InputHandleKind Kind { get; }

        public string? Name { get; }

        public bool IsModal { get; }

        public bool Enabled
        {
            get
            {
                lock (manager.sync)
                {
                    return enabled;
                }
            }
            set
            {
                lock (manager.sync)
                {
                    if (isDisposed)
                    {
                        throw new ObjectDisposedException(nameof(IInputHandle));
                    }

                    enabled = value;
                }
            }
        }

        public bool IsDisposed
        {
            get
            {
                lock (manager.sync)
                {
                    return isDisposed;
                }
            }
        }

        public InputHandleSnapshot CaptureSnapshot()
        {
            var actions = scopeOptions?.Actions ?? muteOptions?.Actions;
            return new InputHandleSnapshot(
                Kind,
                Name,
                IsModal,
                Owner.GetType().Name,
                actions?.Select(action => action.Value).ToArray() ?? Array.Empty<string>());
        }

        public void Dispose() => manager.Release(this);

        public bool TryPublish(InputActivation activation)
        {
            Action<InputActivation>? handler;
            lock (manager.sync)
            {
                if (isDisposed || !enabled)
                {
                    return false;
                }

                handler = Activated;
            }

            handler?.Invoke(activation);
            return true;
        }

        public bool Accepts(InputActionId action) => scopeOptions?.Accepts(action) == true;

        public bool Mutes(InputActionId action) => muteOptions?.Mutes(action) == true;

        public bool MarkDisposed()
        {
            if (isDisposed)
            {
                return false;
            }

            isDisposed = true;
            enabled = false;
            Activated = null;
            return true;
        }
    }

    private sealed class OwnerRegistration
    {
        private readonly Node? node;
        private readonly Action? treeExitingHandler;

        public OwnerRegistration(object owner, Action released)
        {
            if (owner is not Node ownerNode)
            {
                return;
            }

            node = ownerNode;
            treeExitingHandler = released;
            node.TreeExiting += treeExitingHandler;
        }

        public void Detach()
        {
            if (node is not null &&
                treeExitingHandler is not null &&
                GodotObject.IsInstanceValid(node))
            {
                node.TreeExiting -= treeExitingHandler;
            }
        }
    }
}
