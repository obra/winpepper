# UI Automation IDs

Winpepper exposes stable `AutomationProperties.AutomationId` values on every
control the end-to-end smoke test drives. These IDs are a **test-facing
compatibility contract**: external Windows UI Automation clients (FlaUI,
WinAppDriver, raw UIA) locate controls by `AutomationId`, so the IDs below
must not be renamed or removed without updating the smoke tests in the same
change. Visible text, layout, and `x:Name` values are *not* part of the
contract and may change freely.

Naming convention: PascalCase, prefixed by the page/area, suffixed by the
control role (`...Combo`, `...Button`, `...TextBox`, `...Toggle`, `...Label`,
`...List`, `...Slider`, `...Item`).

## Main window shell (`Views/MainWindow.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `MainNavView` | NavigationView | Left navigation pane |
| `NavRecordingItem` | NavigationViewItem | Navigate to Recording page |
| `NavCleanupItem` | NavigationViewItem | Navigate to Cleanup page |
| `NavCorrectionsItem` | NavigationViewItem | Navigate to Corrections page |
| `NavHistoryItem` | NavigationViewItem | Navigate to History page |
| `NavLabItem` | NavigationViewItem | Navigate to Lab page |
| `NavModelsItem` | NavigationViewItem | Navigate to Models page |
| `NavDiagnosticsItem` | NavigationViewItem | Navigate to Diagnostics page |
| `NavAboutItem` | NavigationViewItem (footer) | Open the About dialog |
| `MainContentFrame` | Frame | Hosts the current page |

## Onboarding (`Views/OnboardingPage.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `OnboardingMicCombo` | ComboBox | Pick the input device (step 1) |
| `OnboardingLevelMeter` | ProgressBar | Live mic level meter (step 1) |
| `OnboardingHoldHotkeyBox` | HotkeyRecorderBox | Hold-to-record hotkey (step 2) |
| `OnboardingToggleHotkeyBox` | HotkeyRecorderBox | Toggle-to-record hotkey (step 2) |
| `OnboardingDownloadProgressBar` | ProgressBar | Model download progress (step 3) |
| `OnboardingTestTextBox` | TextBox | Dictation target for the try-it step (step 4) |
| `OnboardingTestDoneCheckBox` | CheckBox | "That worked." confirmation (step 4) |
| `OnboardingSkipButton` | Button | Skip the current step |
| `OnboardingNextButton` | Button | Advance to the next step / finish |

## Recording settings (`Views/RecordingPage.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `RecordingHoldHotkeyBox` | HotkeyRecorderBox | Hold-to-record hotkey |
| `RecordingToggleHotkeyBox` | HotkeyRecorderBox | Toggle-to-record hotkey |
| `RecordingMicCombo` | ComboBox | Microphone selection |
| `RecordingLevelMeter` | ProgressBar | Live mic level meter |
| `RecordingSoundsToggle` | ToggleSwitch | Play start/stop sounds |
| `RecordingSpeakerFilterToggle` | ToggleSwitch | Speaker filter (experimental) |
| `RecordingAutostartToggle` | ToggleSwitch | Start with Windows |
| `RecordingTestTextBox` | TextBox | In-app dictation test target |
| `RecordingFocusTestBoxButton` | Button | Focus the test box |

## Hotkey recorder control (`Views/Controls/HotkeyRecorderBox.xaml`)

These IDs repeat inside every `HotkeyRecorderBox` instance. Locate the
instance first (e.g. `RecordingHoldHotkeyBox`), then search its descendants.

| AutomationId | Control | Purpose |
|---|---|---|
| `HotkeyCancelButton` | Button | HotkeyRecorderBox | Cancels an in-progress hotkey recording |
| `HotkeyRecordButton` | Button | Start recording a hotkey chord |
| `HotkeyChordLabel` | TextBlock | Currently bound chord text |

## Models (`Views/ModelsPage.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `ModelsAsrCombo` | ComboBox | ASR model selection |
| `ModelsAsrInstalledLabel` | TextBlock | Installed ASR model status |
| `ModelsCleanupCombo` | ComboBox | Cleanup model selection |
| `ModelsCleanupInstalledLabel` | TextBlock | Installed cleanup model status |
| `ModelsDownloadButton` | Button | Download missing models |

## Corrections (`Views/CorrectionsPage.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `CorrectionsPreferredList` | ListView | Preferred transcriptions list |
| `CorrectionsNewPreferredTextBox` | TextBox | New preferred transcription input |
| `CorrectionsAddPreferredButton` | Button | Add the preferred transcription |
| `CorrectionsPreferredErrorLabel` | TextBlock | Validation error for preferred input |
| `CorrectionsReplacementsList` | ListView | Misheard replacements list |
| `CorrectionsNewWrongTextBox` | TextBox | "wrong (heard)" input |
| `CorrectionsNewRightTextBox` | TextBox | "right (correct)" input |
| `CorrectionsAddReplacementButton` | Button | Add the replacement pair |
| `CorrectionsReplacementsErrorLabel` | TextBlock | Validation error for replacement inputs |

## Cleanup (`Views/CleanupPage.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `CleanupEnabledToggle` | ToggleSwitch | Enable cleanup LLM |
| `CleanupWindowContextToggle` | ToggleSwitch | Use window context (UIA + OCR) |
| `CleanupProfileCombo` | ComboBox | Prompt profile selection |
| `CleanupCustomPromptTextBox` | TextBox | Custom prompt editor |
| `CleanupMaxTokensSlider` | Slider | Max new tokens |
| `CleanupTimeoutSlider` | Slider | Cleanup timeout (ms) |

## Diagnostics (`Views/DiagnosticsPage.xaml`)

| AutomationId | Control | Purpose |
|---|---|---|
| `DiagnosticsOpenLogFolderButton` | Button | Open the log folder |
| `DiagnosticsCopyBundleButton` | Button | Copy a diagnostics bundle |
| `DiagnosticsLastBundleLabel` | TextBlock | Path/status of the last bundle |
| `DiagnosticsTailList` | ListView | Live log tail (most recent 2000 lines) |

## Notes for test authors

- `AutomationProperties.AutomationId` lives in the default WinUI 3 XAML
  namespace; no extra `xmlns` is required when adding new IDs.
- The tray icon (H.NotifyIcon) is a shell notification-area item, not a XAML
  element; drive it via the shell's notification area UIA tree or
  `Winpepper.exe --tray` / window activation instead.
- When adding a new control to an e2e flow, add an ID here and in the XAML in
  the same commit.
