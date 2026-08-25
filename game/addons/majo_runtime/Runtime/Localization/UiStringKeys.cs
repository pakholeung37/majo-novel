#nullable enable

namespace Majo.Runtime.Localization;

// Stable metadata used by the fixed English reference overlays. Runtime does
// not resolve or translate these keys; projects own UI localization.
public static class UiStringKeys
{
    public const string SettingsTitle = "ui.settings.title";
    public const string SettingsReset = "ui.settings.reset";
    public const string SettingsClose = "ui.settings.close";
    public const string SettingsMaster = "ui.settings.master";
    public const string SettingsBgm = "ui.settings.bgm";
    public const string SettingsSfx = "ui.settings.sfx";
    public const string SettingsVoice = "ui.settings.voice";
    public const string SettingsMuteMaster = "ui.settings.mute_master";
    public const string SettingsMuteBgm = "ui.settings.mute_bgm";
    public const string SettingsMuteSfx = "ui.settings.mute_sfx";
    public const string SettingsMuteVoice = "ui.settings.mute_voice";
    public const string SettingsAutoWaitVoice = "ui.settings.auto_wait_voice";
    public const string SettingsLettersPerSecond = "ui.settings.letters_per_second";
    public const string SettingsAutoDelay = "ui.settings.auto_delay";
    public const string SettingsLanguage = "ui.settings.language";
    public const string SettingsFontScale = "ui.settings.font_scale";
    public const string SettingsLineSpacing = "ui.settings.line_spacing";
    public const string SettingsStatusSaved = "ui.settings.status_saved";
    public const string SettingsStatusSaving = "ui.settings.status_saving";
    public const string SettingsStatusCorrupt = "ui.settings.status_corrupt";
    public const string HistoryTitle = "ui.history.title";
    public const string HistoryClose = "ui.history.close";
    public const string HistoryEmpty = "ui.history.empty";
    public const string HistoryNoVoice = "ui.history.no_voice";
    public const string HistoryReplayVoice = "ui.history.replay_voice";
    public const string SaveLoadTitle = "ui.saveload.title";
    public const string SaveLoadQuickSave = "ui.saveload.quick_save";
    public const string SaveLoadClose = "ui.saveload.close";
    public const string SaveLoadReady = "ui.saveload.ready";
    public const string SaveLoadSectionManual = "ui.saveload.section_manual";
    public const string SaveLoadSectionQuick = "ui.saveload.section_quick";
    public const string SaveLoadSectionAuto = "ui.saveload.section_auto";
    public const string SaveLoadEmptySaves = "ui.saveload.empty_saves";
    public const string SaveLoadSlotEmpty = "ui.saveload.slot_empty";
    public const string SaveLoadBtnSave = "ui.saveload.btn_save";
    public const string SaveLoadBtnOverwrite = "ui.saveload.btn_overwrite";
    public const string SaveLoadBtnLoad = "ui.saveload.btn_load";
    public const string SaveLoadBtnRename = "ui.saveload.btn_rename";
    public const string SaveLoadBtnDelete = "ui.saveload.btn_delete";
    public const string SaveLoadPlaceholderName = "ui.saveload.placeholder_name";
    public const string SaveLoadConfirm = "ui.saveload.confirm";
    public const string DebugTitle = "ui.debug.title";
    public const string DebugClose = "ui.debug.close";
    public const string DebugRefresh = "ui.debug.refresh";
    public const string DebugCopy = "ui.debug.copy";
    public const string DebugExport = "ui.debug.export";
    public const string DebugRecentLogs = "ui.debug.recent_logs";
}
