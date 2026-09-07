# Direction command parameters

Yarn authoring syntax is unchanged:

```yarn
<<backdrop classroom>>
<<character Ema appearance:happy at:left>>
<<character Hiro at:70,0>>
<<mark witness at:37,0>>
<<character Ema at:witness via:move duration:0.3>>
```

`character at:` and `mark at:` bind to `PositionRef`: either a coordinate literal or a name. `PositionRefConverter` validates finite literal percentages between 0 and 100 and supplies syntax/examples to the command manifest. The node compiler uses the same parser for literal mark definitions and reports malformed coordinates with source locations. Named references remain unbound until execution; named mark definitions execute in script order.

Position lookup uses the current node's local marks, then project defaults. Unknown names report a reference error. Local names remain case-sensitive; project defaults are case-insensitive, preserving `left`/`Left` compatibility. Leaving a node or resetting the runtime clears local marks, while retaining project defaults. Omitting `at:` leaves the actor's position unchanged.

Project marks can be configured in the Inspector under `ActorConfiguration.Marks`, or in code before runtime initialization:

```csharp
configuration.Actors.Marks["left"] = new Vector2(25, 0);
configuration.Actors.Marks["witness"] = new Vector2(37, 0);
```

These values use scene percentages, with positive Y upward. The existing scene offset is applied by the rendering backend. For compatibility, missing `left`, `center`, and `right` entries are converted once from the existing pixel presets using `ReferenceResolution`. Without a positive reference resolution, only explicit percentage marks are registered. Runtime configuration changes require rebuilding these defaults; values are copied during initialization.

`backdrop` binds its primary argument to `BackdropId`. Its `ToResourceKey(backgroundsPath)` method is shared by command execution and background preloading, accepts an already-prefixed key without adding the directory again, and uses `ResourceKey` validation for logical paths. The bootstrap passes `ResourceConfiguration.BackgroundsPath` to `DirectionCommandModule`.

C# extension migration:

- `IDirectionPositionResolver` and `IDirectionResourceKeyResolver`, and the bootstrap properties exposing them, have been removed.
- Configure project positions through `ActorConfiguration.Marks`; custom scopes can still replace `IDirectionScopeResolver` through service registration.
- `CharacterCommand` and `DirectionMarkCommand` no longer require a position resolver. Their `At` arguments use `PositionRef`.
- `BackdropCommand` no longer requires a resource resolver. Its `Resource` argument uses `BackdropId`; an optional final `backgroundsPath` constructor argument controls the resource directory.
- Command converters handle syntax; scopes handle runtime names; the existing Patch/Beat execution path handles state changes.
