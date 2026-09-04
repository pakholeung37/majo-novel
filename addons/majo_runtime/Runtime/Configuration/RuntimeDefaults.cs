namespace Majo.Runtime.Configuration;

public static class RuntimeDefaults
{
    public const double AnimationDurationSeconds = 0.1;

    public const double TransitionDurationSeconds = 0.5;


    public const double CurtainDurationSeconds = 0.4;

    public const double PresentationFadeDurationSeconds = 0.1;
    public const double CommandBindingCharacterAnimationDurationSeconds = 0.1;
    public const double AutoExtraDelaySeconds = 1;
    public const double SkipDelaySeconds = 0.05;
    public const double SettingsFlushDebounceSeconds = 0.35;
    public const double GlobalStateFlushDebounceSeconds = 0.5;
    public const double LifecycleTimeoutSeconds = 10;

    public const double ImmediateDurationSeconds = 0;

    public const double DiagnosticsFlushTimeoutMilliseconds = 500;
    public const double DiagnosticsShutdownTimeoutSeconds = 2;
    public const double DiagnosticsForcedShutdownTimeoutMilliseconds = 500;
}
