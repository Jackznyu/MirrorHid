using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MirrorHid.Probe;
using Point = System.Windows.Point;

namespace MirrorHid.App;

public partial class MainWindow : Window
{
    private const double DefaultWindowWidth = 430;
    private const double DefaultWindowHeight = 800;
    private const byte DirectDragButtonMask = 0x02;
    private const byte CustomRightButtonMask = 0x04;

    private enum HotkeyCaptureTarget
    {
        None,
        ToggleControl,
        RecordMode,
        Macro1,
        Macro2,
        Macro3,
        Macro4,
        Macro5,
        Macro6
    }

    private static readonly TimeSpan MacroPointerSettleDelay =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MacroButtonHoldDelay =
        TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan MacroMenuAnimationDelay =
        TimeSpan.FromMilliseconds(700);

    private readonly record struct NormalizedPoint(double X, double Y);

    private sealed record AssistiveTouchMenuCalibration(
        NormalizedPoint MenuButton,
        NormalizedPoint CustomButton,
        NormalizedPoint HoldAndDragButton);

    private sealed record LegacyHotkeySettings(
        Key ToggleControl,
        Key ReplayMacro,
        Key Calibrate,
        Key ResetCalibration);

    private sealed record StoredMacroSlot(
        string Name,
        Key Hotkey,
        double Speed,
        List<NormalizedPoint> Steps);

    private sealed record StoredMacroConfiguration(
        Key ToggleControlHotkey,
        Key RecordModeHotkey,
        List<StoredMacroSlot> Slots);

    private sealed record StoredWindowPlacement(
        double Left,
        double Top,
        double Width,
        double Height);

    private sealed class MacroSlot
    {
        public required string Name { get; set; }
        public required Key Hotkey { get; set; }
        public required double Speed { get; set; }
        public required List<NormalizedPoint> Steps { get; set; }
    }

    private readonly record struct ReportFrame(
        byte Buttons,
        int RelativeX,
        int RelativeY,
        ushort AbsoluteX,
        ushort AbsoluteY,
        bool IsTransition);

    private readonly object _reportSync = new();
    private readonly LinkedList<ReportFrame> _reportFrames = new();
    private readonly CancellationTokenSource _reportPumpCancellation = new();
    private BleHidMouse? _mouse;
    private Task? _reportPump;
    private Point? _lastPointer;
    private int _pendingWheel;
    private ushort _absoluteX;
    private ushort _absoluteY;
    private bool _absolutePositionDirty;
    private bool _absoluteMode = true;
    private byte _desiredButtons;
    private bool _controlEnabled;
    private bool _leftButtonDown;
    private bool _rightButtonDown;
    private bool _assistiveTouchMenuMacroRunning;
    private int _assistiveTouchMenuMacroGeneration;
    private NormalizedPoint? _assistiveTouchMacroRestorePoint;
    private bool _solidSetupBackground;
    private Key _toggleControlHotkey = Key.F8;
    private Key _recordModeHotkey = Key.F10;
    private readonly MacroSlot[] _macroSlots = CreateDefaultMacroSlots();
    private HotkeyCaptureTarget _hotkeyCaptureTarget;
    private bool _recordAwaitingSlot;
    private int? _recordingSlotIndex;
    private readonly List<NormalizedPoint> _pendingRecordingSteps = new();
    private bool _updatingMacroUi;
    private bool _macroUiReady;
    private bool _isResizing;
    private Point _resizeStartScreen;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartDpiScaleX = 1;
    private double _resizeStartDpiScaleY = 1;
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

    private static MacroSlot[] CreateDefaultMacroSlots() =>
    [
        new() { Name = "Hold and Drag", Hotkey = Key.F9, Speed = 1.5, Steps = [] },
        new() { Name = "Macro 2", Hotkey = Key.F6, Speed = 1.5, Steps = [] },
        new() { Name = "Macro 3", Hotkey = Key.F7, Speed = 1.5, Steps = [] },
        new() { Name = "Macro 4", Hotkey = Key.F11, Speed = 1.5, Steps = [] },
        new() { Name = "Macro 5", Hotkey = Key.F12, Speed = 1.5, Steps = [] },
        new() { Name = "Macro 6", Hotkey = Key.Insert, Speed = 1.5, Steps = [] }
    ];

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowPlacement();
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(Window_PreviewMouseDown),
            handledEventsToo: true);
        AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(Window_PreviewMouseUp),
            handledEventsToo: true);
        AddHandler(
            Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(Window_PreviewMouseMove),
            handledEventsToo: true);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadMacroConfiguration();
        UpdateMacroUi();
        _macroUiReady = true;
        try
        {
            _mouse = await BleHidMouse.CreateAsync(SetStatus);
            _mouse.StartAdvertising();
            _reportPump = RunReportPumpAsync(_reportPumpCancellation.Token);
            SetStatus("Advertising; waiting for iPhone…");
        }
        catch (Exception exception)
        {
            SetStatus($"Bluetooth error: {exception.Message}");
            ToggleButton.IsEnabled = false;
        }
    }

    private void SetStatus(string message)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = message switch
            {
                var text when text.Contains("absolute=1") =>
                    "iPhone connected • pointer sync ready",
                var text when
                    text.Contains("relative=1") &&
                    text.Contains("absolute=0") =>
                    "Reconnect iPhone once for pointer sync",
                var text when
                    text.Contains("relative=0") &&
                    text.Contains("absolute=0") =>
                    "Waiting for iPhone…",
                _ => message
            };
        });
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e) =>
        SetControlEnabled(!_controlEnabled);

    private void OpacityButton_Click(object sender, RoutedEventArgs e)
    {
        _solidSetupBackground = !_solidSetupBackground;
        OpacityButton.Content = _solidSetupBackground
            ? "Transparent"
            : "Solid UI";
        UpdateControlSurfaceBackground();
    }

    private void UpdateControlSurfaceBackground()
    {
        var color = _controlEnabled
            ? Color.FromArgb(1, 0, 0, 0)
            : _solidSetupBackground
                ? Color.FromRgb(24, 32, 42)
                : Color.FromArgb(48, 0, 120, 212);
        ControlSurface.Background = new SolidColorBrush(color);
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.Tag is not string targetText ||
            !Enum.TryParse(targetText, out HotkeyCaptureTarget target))
        {
            return;
        }

        _hotkeyCaptureTarget = target;
        UpdateMacroUi();
        button.Content = "Press a key…";
        SetStatus("Press a key; Escape cancels hotkey capture");
        Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Windows treats F10 as a system-menu key. WPF exposes it through
        // SystemKey rather than Key, so normalize it before matching hotkeys.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_hotkeyCaptureTarget != HotkeyCaptureTarget.None)
        {
            CaptureHotkey(key, e);
            return;
        }

        if (key == Key.Escape &&
            (_recordAwaitingSlot || _recordingSlotIndex is not null))
        {
            CancelMacroRecording("Recording cancelled; previous macro preserved");
            e.Handled = true;
            return;
        }

        if (key == _toggleControlHotkey)
        {
            SetControlEnabled(!_controlEnabled);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape && _controlEnabled)
        {
            SetControlEnabled(false);
            e.Handled = true;
            return;
        }

        if (key == _recordModeHotkey)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                ToggleMacroRecording();
            }
            return;
        }

        if (_recordAwaitingSlot)
        {
            e.Handled = true;
            if (!e.IsRepeat && FindMacroSlotByHotkey(key) is { } slotIndex)
            {
                BeginRecordingSlot(slotIndex);
            }
            return;
        }

        if (_recordingSlotIndex is not null)
        {
            if (key == Key.Return)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    RecordMacroStep();
                }
            }
            else if (key == Key.Back)
            {
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    UndoMacroStep();
                }
            }
            return;
        }

        if (_controlEnabled && FindMacroSlotByHotkey(key) is { } replaySlot)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                _ = RunMacroAsync(replaySlot);
            }
        }
    }

    private void CaptureHotkey(Key key, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.IsRepeat)
        {
            return;
        }

        if (key == Key.Escape)
        {
            _hotkeyCaptureTarget = HotkeyCaptureTarget.None;
            UpdateMacroUi();
            SetStatus("Hotkey capture cancelled");
            return;
        }

        if (IsReservedHotkey(key))
        {
            SetStatus("Enter, Backspace, Escape, and modifiers are reserved");
            return;
        }

        if (IsHotkeyAlreadyAssigned(key, _hotkeyCaptureTarget))
        {
            SetStatus($"{FormatHotkey(key)} is already assigned");
            return;
        }

        AssignHotkey(_hotkeyCaptureTarget, key);
        _hotkeyCaptureTarget = HotkeyCaptureTarget.None;
        SaveMacroConfiguration();
        UpdateMacroUi();
        SetStatus($"Hotkey saved: {FormatHotkey(key)}");
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftAlt or Key.RightAlt or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or Key.System or Key.None;

    private static bool IsReservedHotkey(Key key) =>
        IsModifierKey(key) || key is Key.Return or Key.Back or Key.Escape;

    private bool IsHotkeyAlreadyAssigned(
        Key key,
        HotkeyCaptureTarget except)
    {
        if (except != HotkeyCaptureTarget.ToggleControl &&
            key == _toggleControlHotkey)
        {
            return true;
        }
        if (except != HotkeyCaptureTarget.RecordMode &&
            key == _recordModeHotkey)
        {
            return true;
        }

        var exceptSlot = HotkeyTargetToSlotIndex(except);
        return _macroSlots.Where((_, index) => index != exceptSlot)
            .Any(slot => slot.Hotkey == key);
    }

    private void AssignHotkey(HotkeyCaptureTarget target, Key key)
    {
        if (target == HotkeyCaptureTarget.ToggleControl)
        {
            _toggleControlHotkey = key;
        }
        else if (target == HotkeyCaptureTarget.RecordMode)
        {
            _recordModeHotkey = key;
        }
        else if (HotkeyTargetToSlotIndex(target) is { } slotIndex)
        {
            _macroSlots[slotIndex].Hotkey = key;
        }
    }

    private static int? HotkeyTargetToSlotIndex(
        HotkeyCaptureTarget target) => target switch
    {
        HotkeyCaptureTarget.Macro1 => 0,
        HotkeyCaptureTarget.Macro2 => 1,
        HotkeyCaptureTarget.Macro3 => 2,
        HotkeyCaptureTarget.Macro4 => 3,
        HotkeyCaptureTarget.Macro5 => 4,
        HotkeyCaptureTarget.Macro6 => 5,
        _ => null
    };

    private int? FindMacroSlotByHotkey(Key key)
    {
        for (var index = 0; index < _macroSlots.Length; index++)
        {
            if (_macroSlots[index].Hotkey == key)
            {
                return index;
            }
        }
        return null;
    }

    private void ToggleMacroRecording()
    {
        if (_assistiveTouchMenuMacroRunning)
        {
            return;
        }

        if (_recordingSlotIndex is { } slotIndex)
        {
            if (_pendingRecordingSteps.Count == 0)
            {
                CancelMacroRecording("No steps recorded; previous macro preserved");
                return;
            }

            _macroSlots[slotIndex].Steps = [.. _pendingRecordingSteps];
            _pendingRecordingSteps.Clear();
            _recordingSlotIndex = null;
            SaveMacroConfiguration();
            UpdateMacroUi();
            ActiveBannerText.Text =
                $"SAVED {_macroSlots[slotIndex].Name}  •  " +
                $"{_macroSlots[slotIndex].Steps.Count} steps";
            return;
        }

        if (_recordAwaitingSlot)
        {
            CancelMacroRecording("Recording cancelled");
            return;
        }

        if (!_controlEnabled)
        {
            SetControlEnabled(true);
        }
        _recordAwaitingSlot = true;
        ActiveBannerText.Text =
            "RECORD MODE  •  Press the target macro hotkey";
    }

    private void BeginRecordingSlot(int slotIndex)
    {
        _recordAwaitingSlot = false;
        _recordingSlotIndex = slotIndex;
        _pendingRecordingSteps.Clear();
        ActiveBannerText.Text =
            $"RECORDING {_macroSlots[slotIndex].Name}  •  " +
            "Enter adds/clicks  •  Backspace undoes  •  Record key saves";
    }

    private void RecordMacroStep()
    {
        if (_recordingSlotIndex is not { } slotIndex)
        {
            return;
        }

        var current = Mouse.GetPosition(ControlSurface);
        _pendingRecordingSteps.Add(NormalizeControlPoint(current));
        QueuePrimaryClickAt(current);
        ActiveBannerText.Text =
            $"RECORDING {_macroSlots[slotIndex].Name}  •  " +
            $"{_pendingRecordingSteps.Count} steps  •  Enter adds next";
    }

    private void UndoMacroStep()
    {
        if (_recordingSlotIndex is not { } slotIndex ||
            _pendingRecordingSteps.Count == 0)
        {
            return;
        }

        _pendingRecordingSteps.RemoveAt(_pendingRecordingSteps.Count - 1);
        ActiveBannerText.Text =
            $"RECORDING {_macroSlots[slotIndex].Name}  •  " +
            $"{_pendingRecordingSteps.Count} steps after undo";
    }

    private void CancelMacroRecording(string message)
    {
        _recordAwaitingSlot = false;
        _recordingSlotIndex = null;
        _pendingRecordingSteps.Clear();
        ActiveBannerText.Text = message;
    }

    private void MacroName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_updatingMacroUi || sender is not TextBox textBox ||
            !int.TryParse(textBox.Tag?.ToString(), out var slotIndex) ||
            slotIndex < 0 || slotIndex >= _macroSlots.Length)
        {
            return;
        }

        var name = textBox.Text.Trim();
        _macroSlots[slotIndex].Name = string.IsNullOrWhiteSpace(name)
            ? $"Macro {slotIndex + 1}"
            : name;
        SaveMacroConfiguration();
        UpdateMacroUi();
    }

    private void MacroSpeedSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_macroUiReady || _updatingMacroUi || sender is not Slider slider ||
            !int.TryParse(slider.Tag?.ToString(), out var slotIndex) ||
            slotIndex < 0 || slotIndex >= _macroSlots.Length)
        {
            return;
        }

        _macroSlots[slotIndex].Speed = slider.Value;
        SaveMacroConfiguration();
    }

    private void ClearMacroButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), out var slotIndex) ||
            slotIndex < 0 || slotIndex >= _macroSlots.Length)
        {
            return;
        }

        _macroSlots[slotIndex].Steps.Clear();
        SaveMacroConfiguration();
        UpdateMacroUi();
        SetStatus($"Cleared {_macroSlots[slotIndex].Name}");
    }

    private void UpdateMacroUi()
    {
        _updatingMacroUi = true;
        try
        {
            ToggleControlHotkeyButton.Content =
                FormatHotkey(_toggleControlHotkey);
            RecordModeHotkeyButton.Content =
                FormatHotkey(_recordModeHotkey);
            ToggleButton.Content =
                $"Enable  {FormatHotkey(_toggleControlHotkey)}";
            HotkeySummaryText.Text =
                $"{FormatHotkey(_recordModeHotkey)} starts/finishes recording; " +
                "choose a slot with its macro hotkey, then press Enter for each step.";

            var nameBoxes = GetMacroNameBoxes();
            var hotkeyButtons = GetMacroHotkeyButtons();
            var speedSliders = GetMacroSpeedSliders();
            var stepLabels = GetMacroStepLabels();
            for (var index = 0; index < _macroSlots.Length; index++)
            {
                nameBoxes[index].Text = _macroSlots[index].Name;
                hotkeyButtons[index].Content =
                    FormatHotkey(_macroSlots[index].Hotkey);
                speedSliders[index].Value = _macroSlots[index].Speed;
                stepLabels[index].Text =
                    $"{_macroSlots[index].Steps.Count} step" +
                    (_macroSlots[index].Steps.Count == 1 ? "" : "s");
            }

        }
        finally
        {
            _updatingMacroUi = false;
        }
    }

    private TextBox[] GetMacroNameBoxes() =>
        [Macro1NameBox, Macro2NameBox, Macro3NameBox,
         Macro4NameBox, Macro5NameBox, Macro6NameBox];

    private Button[] GetMacroHotkeyButtons() =>
        [Macro1HotkeyButton, Macro2HotkeyButton, Macro3HotkeyButton,
         Macro4HotkeyButton, Macro5HotkeyButton, Macro6HotkeyButton];

    private Slider[] GetMacroSpeedSliders() =>
        [Macro1SpeedSlider, Macro2SpeedSlider, Macro3SpeedSlider,
         Macro4SpeedSlider, Macro5SpeedSlider, Macro6SpeedSlider];

    private TextBlock[] GetMacroStepLabels() =>
        [Macro1StepLabel, Macro2StepLabel, Macro3StepLabel,
         Macro4StepLabel, Macro5StepLabel, Macro6StepLabel];

    private static string FormatHotkey(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        _ => key.ToString()
    };

    private static string MacroConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MirrorHid",
        "macro-slots.json");

    private void LoadMacroConfiguration()
    {
        try
        {
            if (File.Exists(MacroConfigurationPath))
            {
                var stored = JsonSerializer.Deserialize<StoredMacroConfiguration>(
                    File.ReadAllText(MacroConfigurationPath));
                if (stored is not null && stored.Slots.Count == _macroSlots.Length)
                {
                    _toggleControlHotkey = stored.ToggleControlHotkey;
                    _recordModeHotkey = stored.RecordModeHotkey;
                    for (var index = 0; index < _macroSlots.Length; index++)
                    {
                        var source = stored.Slots[index];
                        _macroSlots[index].Name =
                            string.IsNullOrWhiteSpace(source.Name)
                                ? $"Macro {index + 1}"
                                : source.Name;
                        _macroSlots[index].Hotkey = source.Hotkey;
                        _macroSlots[index].Speed =
                            Math.Clamp(source.Speed, 0.5, 4.0);
                        _macroSlots[index].Steps = source.Steps ?? [];
                    }
                    return;
                }
            }

            ImportLegacySingleMacro();
            SaveMacroConfiguration();
        }
        catch (Exception exception)
        {
            SetStatus($"Macro settings load failed: {exception.Message}");
        }
    }

    private void ImportLegacySingleMacro()
    {
        var legacyHotkeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MirrorHid",
            "hotkeys.json");
        try
        {
            if (File.Exists(legacyHotkeyPath) &&
                JsonSerializer.Deserialize<LegacyHotkeySettings>(
                    File.ReadAllText(legacyHotkeyPath)) is { } hotkeys)
            {
                _toggleControlHotkey = hotkeys.ToggleControl;
                _recordModeHotkey = hotkeys.Calibrate;
                _macroSlots[0].Hotkey = hotkeys.ReplayMacro;
            }
        }
        catch
        {
            // Keep defaults when legacy hotkeys are invalid.
        }

        try
        {
            if (File.Exists(AssistiveTouchCalibrationPath) &&
                JsonSerializer.Deserialize<AssistiveTouchMenuCalibration>(
                    File.ReadAllText(AssistiveTouchCalibrationPath)) is { } calibration)
            {
                _macroSlots[0].Steps =
                [
                    calibration.MenuButton,
                    calibration.CustomButton,
                    calibration.HoldAndDragButton
                ];
            }
        }
        catch
        {
            // The slot remains empty if migration is unavailable.
        }
    }

    private void SaveMacroConfiguration()
    {
        try
        {
            var directory = Path.GetDirectoryName(MacroConfigurationPath)!;
            Directory.CreateDirectory(directory);
            var stored = new StoredMacroConfiguration(
                _toggleControlHotkey,
                _recordModeHotkey,
                _macroSlots.Select(slot => new StoredMacroSlot(
                    slot.Name,
                    slot.Hotkey,
                    slot.Speed,
                    [.. slot.Steps])).ToList());
            File.WriteAllText(
                MacroConfigurationPath,
                JsonSerializer.Serialize(stored));
        }
        catch (Exception exception)
        {
            SetStatus($"Macro settings save failed: {exception.Message}");
        }
    }

    private async Task RunMacroAsync(int slotIndex)
    {
        if (_assistiveTouchMenuMacroRunning ||
            slotIndex < 0 || slotIndex >= _macroSlots.Length)
        {
            return;
        }

        var slot = _macroSlots[slotIndex];
        if (slot.Steps.Count == 0)
        {
            ActiveBannerText.Text =
                $"{slot.Name.ToUpperInvariant()} IS EMPTY  •  " +
                $"{FormatHotkey(_recordModeHotkey)} records";
            return;
        }

        var steps = slot.Steps.ToList();
        var speed = slot.Speed;
        _assistiveTouchMenuMacroRunning = true;
        _assistiveTouchMacroRestorePoint = NormalizeControlPoint(
            Mouse.GetPosition(ControlSurface));
        var generation = ++_assistiveTouchMenuMacroGeneration;
        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                ActiveBannerText.Text =
                    $"RUNNING {slot.Name.ToUpperInvariant()}  •  " +
                    $"Step {index + 1}/{steps.Count}";
                await DirectPhoneClickAsync(steps[index], speed);
                if (index < steps.Count - 1)
                {
                    await Task.Delay(ScaleMacroDelay(
                        MacroMenuAnimationDelay,
                        minimumMilliseconds: 120,
                        speed));
                }
                if (!IsAssistiveTouchMenuMacroCurrent(generation))
                {
                    return;
                }
            }

            await Task.Delay(ScaleMacroDelay(
                MacroPointerSettleDelay,
                minimumMilliseconds: 50,
                speed));
            if (IsAssistiveTouchMenuMacroCurrent(generation))
            {
                var restorePoint = _assistiveTouchMacroRestorePoint ??
                    NormalizeControlPoint(Mouse.GetPosition(ControlSurface));
                QueueAbsolutePosition(DenormalizeControlPoint(restorePoint));
                await Task.Delay(ScaleMacroDelay(
                    MacroPointerSettleDelay,
                    minimumMilliseconds: 50,
                    speed));
                ActiveBannerText.Text =
                    $"{slot.Name.ToUpperInvariant()} SELECTED";
            }
        }
        finally
        {
            if (generation == _assistiveTouchMenuMacroGeneration)
            {
                _assistiveTouchMenuMacroRunning = false;
                _assistiveTouchMacroRestorePoint = null;
                _lastPointer = null;
            }
        }
    }

    private bool IsAssistiveTouchMenuMacroCurrent(int generation) =>
        _controlEnabled &&
        _assistiveTouchMenuMacroRunning &&
        generation == _assistiveTouchMenuMacroGeneration;

    private async Task DirectPhoneClickAsync(
        NormalizedPoint normalized,
        double speed)
    {
        var controlPoint = DenormalizeControlPoint(normalized);
        // Move only the iPhone pointer. Live Windows-cursor alignment is paused
        // by the macro-running guard until all menu clicks have completed.
        QueueAbsolutePosition(controlPoint);
        await Task.Delay(ScaleMacroDelay(
            MacroPointerSettleDelay,
            minimumMilliseconds: 50,
            speed));

        QueueButtonState(1);
        await Task.Delay(ScaleMacroDelay(
            MacroButtonHoldDelay,
            minimumMilliseconds: 35,
            speed));
        QueueButtonState(0);
        await Task.Delay(ScaleMacroDelay(
            MacroPointerSettleDelay,
            minimumMilliseconds: 50,
            speed));
    }

    private TimeSpan ScaleMacroDelay(
        TimeSpan baseDelay,
        double minimumMilliseconds,
        double speed)
    {
        speed = Math.Max(0.25, speed);
        return TimeSpan.FromMilliseconds(Math.Max(
            minimumMilliseconds,
            baseDelay.TotalMilliseconds / speed));
    }

    private void QueuePrimaryClickAt(Point point)
    {
        if (_absoluteMode)
        {
            QueueAbsolutePosition(point);
        }
        QueueButtonState(1);
        QueueButtonState(0);
        _lastPointer = point;
    }

    private NormalizedPoint NormalizeControlPoint(Point point)
    {
        var width = Math.Max(1.0, ControlSurface.ActualWidth);
        var height = Math.Max(1.0, ControlSurface.ActualHeight);
        return new NormalizedPoint(
            Math.Clamp(point.X / width, 0.0, 1.0),
            Math.Clamp(point.Y / height, 0.0, 1.0));
    }

    private Point DenormalizeControlPoint(NormalizedPoint point) =>
        new(
            point.X * Math.Max(1.0, ControlSurface.ActualWidth),
            point.Y * Math.Max(1.0, ControlSurface.ActualHeight));

    private static string AssistiveTouchCalibrationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MirrorHid",
        "assistivetouch-menu.json");

    private void SetControlEnabled(bool enabled)
    {
        if (_mouse is null)
        {
            return;
        }

        _controlEnabled = enabled;
        _lastPointer = null;

        // Hide the toolbar while controlling, but keep its 42-pixel row reserved.
        // The mirrored phone begins below that row, so absolute Y=0 must remain at
        // the top of ControlSurface rather than moving to the window's top edge.
        HeaderBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ActiveBanner.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ActiveBannerText.Text =
            $"{FormatHotkey(_recordModeHotkey)} RECORDS  •  " +
            "Macro hotkeys replay saved gestures";
        HeaderRow.Height = new GridLength(42);
        UpdateLayout();

        Instructions.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        UpdateControlSurfaceBackground();
        // Keep the Windows pointer visible: it is much easier to see than the
        // translucent AssistiveTouch pointer in a mirrored video stream.
        ControlSurface.Cursor = Cursors.Arrow;
        ToggleButton.Content = enabled
            ? $"Release  {FormatHotkey(_toggleControlHotkey)}"
            : $"Enable  {FormatHotkey(_toggleControlHotkey)}";
        OpacityButton.IsEnabled = !enabled;
        ControlSurface.Focus();

        if (enabled && _absoluteMode)
        {
            QueueAbsolutePosition(Mouse.GetPosition(ControlSurface));
        }

        if (!enabled && _leftButtonDown)
        {
            _leftButtonDown = false;
            QueueButtonState(0);
            Mouse.Capture(null);
        }

        if (!enabled && _rightButtonDown)
        {
            _rightButtonDown = false;
            QueueButtonState(0);
            Mouse.Capture(null);
        }

        if (!enabled)
        {
            _recordAwaitingSlot = false;
            _recordingSlotIndex = null;
            _pendingRecordingSteps.Clear();
            _assistiveTouchMenuMacroRunning = false;
            _assistiveTouchMenuMacroGeneration++;
        }
    }

    private void ControlSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_controlEnabled)
        {
            _lastPointer = e.GetPosition(ControlSurface);
        }
    }

    private void ControlSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_leftButtonDown && !_rightButtonDown)
        {
            _lastPointer = null;
        }
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_controlEnabled)
        {
            return;
        }

        var current = e.GetPosition(ControlSurface);
        if (_assistiveTouchMenuMacroRunning)
        {
            _assistiveTouchMacroRestorePoint = NormalizeControlPoint(current);
            _lastPointer = current;
            return;
        }

        // Physical state on movement independently recovers transitions that
        // can occasionally be swallowed by a transparent window.
        if (e.LeftButton == MouseButtonState.Pressed &&
            !_leftButtonDown &&
            ControlSurface.IsMouseOver)
        {
            BeginLeftPress(current);
        }
        else if (e.LeftButton == MouseButtonState.Released && _leftButtonDown)
        {
            EndLeftPress(current);
        }

        if (e.RightButton == MouseButtonState.Pressed &&
            !_rightButtonDown &&
            ControlSurface.IsMouseOver)
        {
            BeginRightPress(current);
        }
        else if (e.RightButton == MouseButtonState.Released && _rightButtonDown)
        {
            EndRightPress(current);
        }
        if (_absoluteMode)
        {
            QueueAbsolutePosition(current);
        }
        else if (_lastPointer is { } previous)
        {
            var sensitivity = SensitivitySlider.Value;
            var dx = (int)Math.Round((current.X - previous.X) * sensitivity);
            var dy = (int)Math.Round((current.Y - previous.Y) * sensitivity);
            if (dx != 0 || dy != 0)
            {
                QueueMovement(dx, dy);
            }
        }

        _lastPointer = current;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_controlEnabled ||
            (e.ChangedButton != MouseButton.Left &&
                e.ChangedButton != MouseButton.Right) ||
            !ControlSurface.IsMouseOver)
        {
            return;
        }

        if (_assistiveTouchMenuMacroRunning)
        {
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(ControlSurface);
        if (e.ChangedButton == MouseButton.Left)
        {
            BeginLeftPress(current);
            e.Handled = true;
            return;
        }

        BeginRightPress(current);
        e.Handled = true;
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_controlEnabled ||
            (e.ChangedButton != MouseButton.Left &&
                e.ChangedButton != MouseButton.Right))
        {
            return;
        }

        if (_assistiveTouchMenuMacroRunning)
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            EndLeftPress(e.GetPosition(ControlSurface));
            e.Handled = true;
            return;
        }

        EndRightPress(e.GetPosition(ControlSurface));
        e.Handled = true;
    }

    private void BeginLeftPress(Point current)
    {
        if (_leftButtonDown)
        {
            return;
        }

        _leftButtonDown = true;
        ActiveBannerText.Text = "LEFT HELD  •  Move to drag  •  Release to drop";
        ControlSurface.CaptureMouse();
        _lastPointer = current;
        if (_absoluteMode)
        {
            QueueAbsolutePosition(current);
        }
        QueueButtonState((byte)(_desiredButtons | DirectDragButtonMask));
    }

    private void EndLeftPress(Point current)
    {
        if (!_leftButtonDown)
        {
            return;
        }

        _leftButtonDown = false;
        ActiveBannerText.Text =
            "CONTROL ACTIVE  •  LEFT B2  •  RIGHT B3  •  WHEEL B4/B5";
        if (_absoluteMode)
        {
            QueueAbsolutePosition(current);
        }
        QueueButtonState((byte)(_desiredButtons & ~DirectDragButtonMask));
        if (!_rightButtonDown)
        {
            Mouse.Capture(null);
        }
        _lastPointer = current;
    }

    private void BeginRightPress(Point current)
    {
        if (_rightButtonDown)
        {
            return;
        }

        _rightButtonDown = true;
        ControlSurface.CaptureMouse();
        _lastPointer = current;
        if (_absoluteMode)
        {
            QueueAbsolutePosition(current);
        }
        QueueButtonState((byte)(_desiredButtons | CustomRightButtonMask));
    }

    private void EndRightPress(Point current)
    {
        if (!_rightButtonDown)
        {
            return;
        }

        _rightButtonDown = false;
        if (_absoluteMode)
        {
            QueueAbsolutePosition(current);
        }
        QueueButtonState((byte)(_desiredButtons & ~CustomRightButtonMask));
        if (!_leftButtonDown)
        {
            Mouse.Capture(null);
        }
        _lastPointer = current;
    }

    private void ControlSurface_MouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (!_controlEnabled)
        {
            return;
        }

        e.Handled = true;
        var direction = Math.Sign(e.Delta);
        var wheelButton = direction > 0 ? (byte)0x08 : (byte)0x10;
        var previousButtons = _desiredButtons;
        QueueButtonState((byte)(previousButtons | wheelButton));
        QueueButtonState(previousButtons);
    }

    private void QueueMovement(int dx, int dy)
    {
        lock (_reportSync)
        {
            EnqueueMotionFrameLocked(
                new ReportFrame(
                    _desiredButtons,
                    dx,
                    dy,
                    _absoluteX,
                    _absoluteY,
                    IsTransition: false));
        }
    }

    private void QueueAbsolutePosition(Point position)
    {
        var width = Math.Max(1.0, ControlSurface.ActualWidth);
        var height = Math.Max(1.0, ControlSurface.ActualHeight);
        var normalizedX = Math.Clamp(position.X / width, 0.0, 1.0);
        var normalizedY = Math.Clamp(position.Y / height, 0.0, 1.0);

        lock (_reportSync)
        {
            _absoluteX = (ushort)Math.Round(normalizedX * 32767.0);
            _absoluteY = (ushort)Math.Round(normalizedY * 32767.0);
            _absolutePositionDirty = true;
        }
    }

    private void QueueWheel(int wheel)
    {
        lock (_reportSync)
        {
            _pendingWheel = SaturatingAdd(_pendingWheel, wheel);
        }
    }

    private void QueueButtonState(byte buttons)
    {
        lock (_reportSync)
        {
            if (_desiredButtons == buttons)
            {
                return;
            }

            if (buttons != 0)
            {
                // Do not let stale hover movement delay the press edge,
                // especially when relative reports are being used.
                TrimQueuedMotionFramesLocked(_desiredButtons, maximumToKeep: 0);
            }

            // Preserve the pointer coordinates that belong to every button edge.
            // Otherwise fast movement can overwrite the down position before the
            // BLE pump sends it, turning a drag into an ordinary click.
            if (buttons == 0 && _desiredButtons != 0)
            {
                // Most queued path points have already served their purpose. Keep
                // the newest two so the end of the gesture remains shaped, while
                // ensuring the release reaches iOS without a long BLE backlog.
                TrimQueuedMotionFramesLocked(_desiredButtons, maximumToKeep: 2);

                // Send the final drag position once while still held, then release
                // at that same position. This also makes very short drags reliable.
                _reportFrames.AddLast(
                    new ReportFrame(
                        _desiredButtons,
                        0,
                        0,
                        _absoluteX,
                        _absoluteY,
                        IsTransition: true));
            }

            _desiredButtons = buttons;
            _reportFrames.AddLast(
                new ReportFrame(
                    buttons,
                    0,
                    0,
                    _absoluteX,
                    _absoluteY,
                    IsTransition: true));
        }
    }

    private void TrimQueuedMotionFramesLocked(byte buttons, int maximumToKeep)
    {
        var matchingNodes = new List<LinkedListNode<ReportFrame>>();
        for (var node = _reportFrames.First; node is not null; node = node.Next)
        {
            if (!node.Value.IsTransition && node.Value.Buttons == buttons)
            {
                matchingNodes.Add(node);
            }
        }

        var removeCount = matchingNodes.Count - maximumToKeep;
        for (var index = 0; index < removeCount; index++)
        {
            _reportFrames.Remove(matchingNodes[index]);
        }
    }

    private void EnqueueMotionFrameLocked(ReportFrame frame)
    {
        // BLE notifications are serialized. A deep queue reproduces the path but
        // makes the phone trail far behind the mouse, so retain only a few points
        // and use geometric compaction to keep the important turns.
        const int maximumQueuedFrames = 6;

        while (_reportFrames.Count >= maximumQueuedFrames)
        {
            var madeRoom = _absoluteMode
                ? RemoveLeastImportantAbsolutePointLocked()
                : MergeMostSimilarRelativeFramesLocked();
            if (!madeRoom)
            {
                return;
            }
        }

        _reportFrames.AddLast(frame);
    }

    private bool RemoveLeastImportantAbsolutePointLocked()
    {
        var frames = _reportFrames.ToList();
        var removeAt = -1;
        var lowestDetour = double.MaxValue;

        // Removing the point with the smallest path detour preserves corners,
        // reversals and loops while collapsing redundant points on straight runs.
        for (var index = 1; index < frames.Count - 1; index++)
        {
            if (frames[index].IsTransition)
            {
                continue;
            }

            var previous = frames[index - 1];
            var current = frames[index];
            var next = frames[index + 1];
            if (previous.Buttons != current.Buttons ||
                next.Buttons != current.Buttons)
            {
                continue;
            }

            var detour = Distance(previous, current) +
                Distance(current, next) -
                Distance(previous, next);
            if (detour < lowestDetour)
            {
                lowestDetour = detour;
                removeAt = index;
            }
        }

        if (removeAt < 0)
        {
            return false;
        }

        frames.RemoveAt(removeAt);
        ReplaceReportFramesLocked(frames);
        return true;
    }

    private bool MergeMostSimilarRelativeFramesLocked()
    {
        var frames = _reportFrames.ToList();
        var mergeAt = -1;
        var lowestTurnCost = double.MaxValue;

        // Relative deltas used to be summed indiscriminately, so opposite legs
        // of a back-and-forth gesture cancelled out. Only adjacent path segments
        // are compacted now, favoring the pair with the most similar direction.
        for (var index = 0; index < frames.Count - 1; index++)
        {
            var first = frames[index];
            var second = frames[index + 1];
            if (first.IsTransition || second.IsTransition ||
                first.Buttons != second.Buttons)
            {
                continue;
            }

            var firstLength = Math.Sqrt(
                (double)first.RelativeX * first.RelativeX +
                (double)first.RelativeY * first.RelativeY);
            var secondLength = Math.Sqrt(
                (double)second.RelativeX * second.RelativeX +
                (double)second.RelativeY * second.RelativeY);
            if (firstLength == 0 || secondLength == 0)
            {
                mergeAt = index;
                break;
            }

            var cosine = (
                (double)first.RelativeX * second.RelativeX +
                (double)first.RelativeY * second.RelativeY) /
                (firstLength * secondLength);
            var turnCost = 1.0 - Math.Clamp(cosine, -1.0, 1.0);
            if (turnCost < lowestTurnCost)
            {
                lowestTurnCost = turnCost;
                mergeAt = index;
            }
        }

        if (mergeAt < 0)
        {
            return false;
        }

        var firstFrame = frames[mergeAt];
        var secondFrame = frames[mergeAt + 1];
        frames[mergeAt] = firstFrame with
        {
            RelativeX = AddMotion(firstFrame.RelativeX, secondFrame.RelativeX),
            RelativeY = AddMotion(firstFrame.RelativeY, secondFrame.RelativeY)
        };
        frames.RemoveAt(mergeAt + 1);
        ReplaceReportFramesLocked(frames);
        return true;
    }

    private void ReplaceReportFramesLocked(IEnumerable<ReportFrame> frames)
    {
        _reportFrames.Clear();
        foreach (var frame in frames)
        {
            _reportFrames.AddLast(frame);
        }
    }

    private static double Distance(ReportFrame first, ReportFrame second)
    {
        var dx = (double)first.AbsoluteX - second.AbsoluteX;
        var dy = (double)first.AbsoluteY - second.AbsoluteY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private async Task RunReportPumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                BleHidMouse? mouse = _mouse;
                if (mouse is null)
                {
                    continue;
                }

                byte buttons;
                int x;
                int y;
                int wheel;
                ushort absoluteX;
                ushort absoluteY;
                bool absoluteMode;
                ReportFrame? reportFrame;

                lock (_reportSync)
                {
                    absoluteMode = _absoluteMode;
                    if (_pendingWheel == 0 &&
                        !_absolutePositionDirty &&
                        _reportFrames.Count == 0)
                    {
                        continue;
                    }

                    reportFrame = null;
                    if (_reportFrames.First is { } firstNode)
                    {
                        var queued = firstNode.Value;
                        reportFrame = queued;
                        (x, y) = TakeRelativeChunk(
                            queued.RelativeX,
                            queued.RelativeY);

                        var remainingX = queued.RelativeX - x;
                        var remainingY = queued.RelativeY - y;
                        if (!_absoluteMode && (remainingX != 0 || remainingY != 0))
                        {
                            firstNode.Value = queued with
                            {
                                RelativeX = remainingX,
                                RelativeY = remainingY
                            };
                        }
                        else
                        {
                            _reportFrames.RemoveFirst();
                        }
                    }
                    else
                    {
                        x = 0;
                        y = 0;
                    }

                    buttons = reportFrame?.Buttons ?? _desiredButtons;
                    wheel = Math.Clamp(_pendingWheel, -127, 127);
                    absoluteX = reportFrame?.AbsoluteX ?? _absoluteX;
                    absoluteY = reportFrame?.AbsoluteY ?? _absoluteY;
                    _pendingWheel = 0;
                    // A queued frame uses its own position snapshot. Keep a newer
                    // coalesced position pending for the next report.
                    if (reportFrame is null)
                    {
                        _absolutePositionDirty = false;
                    }

                }

                try
                {
                    if (absoluteMode && mouse.IsAbsolutePointerSubscribed)
                    {
                        await mouse.SendAbsoluteAsync(
                            buttons,
                            absoluteX,
                            absoluteY,
                            wheel);
                    }
                    else
                    {
                        await mouse.SendAsync(buttons, x, y, wheel);
                    }
                }
                catch (Exception exception)
                {
                    SetStatus($"Send failed: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void SyncPointersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = SyncPointersCheckBox.IsChecked == true;
        lock (_reportSync)
        {
            _absoluteMode = enabled;
            _reportFrames.Clear();
            _absolutePositionDirty = enabled;
        }

        if (enabled && IsLoaded)
        {
            QueueAbsolutePosition(Mouse.GetPosition(ControlSurface));
        }
    }

    private static int SaturatingAdd(int current, int delta)
    {
        var sum = (long)current + delta;
        return (int)Math.Clamp(sum, -4096L, 4096L);
    }

    private static int AddMotion(int current, int delta)
    {
        var sum = (long)current + delta;
        return (int)Math.Clamp(sum, int.MinValue, int.MaxValue);
    }

    private static (int X, int Y) TakeRelativeChunk(int x, int y)
    {
        var largestAxis = Math.Max(Math.Abs((long)x), Math.Abs((long)y));
        if (largestAxis <= 127)
        {
            return (x, y);
        }

        // Keep the original vector's slope when a compacted segment needs more
        // than one signed-byte HID report. Independent clamping bends diagonals.
        var scale = 127.0 / largestAxis;
        var chunkX = (int)Math.Round(x * scale);
        var chunkY = (int)Math.Round(y * scale);
        if (chunkX == 0 && x != 0)
        {
            chunkX = Math.Sign(x);
        }
        if (chunkY == 0 && y != 0)
        {
            chunkY = Math.Sign(y);
        }

        return (chunkX, chunkY);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            return;
        }

        DragMove();
        SaveWindowPlacement();
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeStartScreen = PointToScreen(e.GetPosition(this));
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        var dpi = VisualTreeHelper.GetDpi(this);
        _resizeStartDpiScaleX = dpi.DpiScaleX;
        _resizeStartDpiScaleY = dpi.DpiScaleY;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        var current = PointToScreen(e.GetPosition(this));
        var deltaX = (current.X - _resizeStartScreen.X) / _resizeStartDpiScaleX;
        var deltaY = (current.Y - _resizeStartScreen.Y) / _resizeStartDpiScaleY;
        Width = Math.Max(MinWidth, _resizeStartWidth + deltaX);
        Height = Math.Max(MinHeight, _resizeStartHeight + deltaY);
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isResizing = false;
        ResizeGrip.ReleaseMouseCapture();
        SaveWindowPlacement();
        e.Handled = true;
    }

    private static string WindowPlacementPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MirrorHid",
        "window-placement.json");

    private void LoadWindowPlacement()
    {
        try
        {
            if (!File.Exists(WindowPlacementPath) ||
                JsonSerializer.Deserialize<StoredWindowPlacement>(
                    File.ReadAllText(WindowPlacementPath)) is not { } placement ||
                !IsFinite(placement.Left) ||
                !IsFinite(placement.Top) ||
                !IsFinite(placement.Width) ||
                !IsFinite(placement.Height) ||
                placement.Width < MinWidth ||
                placement.Height < MinHeight)
            {
                return;
            }

            Width = placement.Width;
            Height = placement.Height;

            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            var savedHeader = new Rect(
                placement.Left,
                placement.Top,
                placement.Width,
                42);
            if (savedHeader.IntersectsWith(virtualScreen))
            {
                Left = placement.Left;
                Top = placement.Top;
            }
        }
        catch
        {
            // Invalid or unavailable placement data falls back to WPF defaults.
        }
    }

    private void SaveWindowPlacement()
    {
        if (!IsFinite(Left) ||
            !IsFinite(Top) ||
            !IsFinite(Width) ||
            !IsFinite(Height))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(WindowPlacementPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                WindowPlacementPath,
                JsonSerializer.Serialize(new StoredWindowPlacement(
                    Left,
                    Top,
                    Width,
                    Height)));
        }
        catch (Exception exception)
        {
            SetStatus($"Window placement save failed: {exception.Message}");
        }
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private void ResetSizeButton_Click(object sender, RoutedEventArgs e)
    {
        Width = DefaultWindowWidth;
        Height = DefaultWindowHeight;
        SaveWindowPlacement();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        try
        {
            _reportPumpCancellation.Cancel();
            if (_reportPump is not null)
            {
                await _reportPump;
            }

            BleHidMouse? mouse = _mouse;
            _mouse = null;
            if (mouse is not null)
            {
                try
                {
                    if (_absoluteMode && mouse.IsAbsolutePointerSubscribed)
                    {
                        await mouse.SendAbsoluteAsync(
                            0,
                            _absoluteX,
                            _absoluteY,
                            0);
                    }
                    else
                    {
                        await mouse.SendAsync(0, 0, 0, 0);
                    }
                }
                finally
                {
                    await mouse.DisposeAsync();
                }
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Shutdown failed: {exception.Message}");
        }
        finally
        {
            _reportPumpCancellation.Dispose();
            _shutdownInProgress = false;
            _shutdownCompleted = true;
            Close();
        }
    }
}
