using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MirrorHid.App;
using MirrorHid.Probe;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var frame = new DispatcherFrame();
        var tests = RunAsync();
        CompleteAsync();
        Dispatcher.PushFrame(frame);
        return tests.GetAwaiter().GetResult();

        async void CompleteAsync()
        {
            try { await tests; }
            finally { frame.Continue = false; }
        }
    }

    private static async Task<int> RunAsync()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Cancel before macro press", CancelBeforePressAsync),
            ("Cancel during macro hold releases all buttons", CancelDuringHoldAsync),
            ("Cancelled macro cannot release a newer drag", RestartDuringHoldAsync),
            ("Cancel between macro steps", CancelBetweenStepsAsync),
            ("Release discards queued clicks and wheel input", ReleaseDiscardsQueueAsync),
            ("Text editing does not trigger configured hotkeys", TextEntryAsync),
            ("Escape releases control from a text editor", EscapeFromEditorAsync),
            ("Explicit hotkey capture still accepts input", HotkeyCaptureAsync),
            ("Reports continue while the dispatcher is blocked", ReportPumpAsync)
        ];
        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetBaseException().Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} regression checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static MainWindow CreateWindow()
    {
        // Never show the window or raise Loaded: these tests do not start Bluetooth.
        var window = new MainWindow();
        Set(window, "_mouse", new FakeMouse());
        Set(window, "_controlEnabled", true);
        Set(window, "_assistiveTouchMenuMacroRunning", true);
        Set(window, "_assistiveTouchMenuMacroGeneration", 1);
        return window;
    }

    private static Task ClickAsync(MainWindow window) => (Task)Call(
        window, "DirectPhoneClickAsync", Point(), 4.0, 1)!;

    private static object Point() => Activator.CreateInstance(
        typeof(MainWindow).GetNestedType("NormalizedPoint", BindingFlags.NonPublic)!,
        0.25, 0.75)!;

    private static async Task CancelBeforePressAsync()
    {
        var window = CreateWindow();
        var click = ClickAsync(window);
        Call(window, "SetControlEnabled", false);
        await click;
        Require(Buttons(window).SequenceEqual(new byte[] { 0 }), "A cancelled click was queued.");
    }

    private static async Task CancelDuringHoldAsync()
    {
        var window = CreateWindow();
        var click = ClickAsync(window);
        await UntilAsync(() => Get<byte>(window, "_desiredButtons") == 1);
        Call(window, "SetControlEnabled", false);
        Require(Get<byte>(window, "_desiredButtons") == 0, "Button stayed held after release.");
        await click;
        Require(Buttons(window).SequenceEqual(new byte[] { 0 }), "Stale input survived cancellation.");
    }

    private static async Task RestartDuringHoldAsync()
    {
        var window = CreateWindow();
        var oldClick = ClickAsync(window);
        await UntilAsync(() => Get<byte>(window, "_desiredButtons") == 1);
        Call(window, "SetControlEnabled", false);
        Call(window, "SetControlEnabled", true);
        Call(window, "QueueButtonState", (byte)2);
        var queued = Buttons(window).ToArray();
        await oldClick;
        Require(Get<byte>(window, "_desiredButtons") == 2, "Old macro released a new drag.");
        Require(Buttons(window).SequenceEqual(queued), "Old macro appended new reports.");
    }

    private static async Task CancelBetweenStepsAsync()
    {
        var window = CreateWindow();
        Set(window, "_assistiveTouchMenuMacroRunning", false);
        var slot = Get<Array>(window, "_macroSlots").GetValue(0)!;
        slot.GetType().GetProperty("Speed")!.SetValue(slot, 4.0);
        var steps = (IList)slot.GetType().GetProperty("Steps")!.GetValue(slot)!;
        steps.Add(Point());
        steps.Add(Point());
        var macro = (Task)Call(window, "RunMacroAsync", 0)!;
        await UntilAsync(() => Get<byte>(window, "_desiredButtons") == 1);
        await UntilAsync(() => Get<byte>(window, "_desiredButtons") == 0);
        Call(window, "SetControlEnabled", false);
        await macro;
        Require(Buttons(window).SequenceEqual(new byte[] { 0 }), "A later macro step ran after release.");
    }

    private static Task ReleaseDiscardsQueueAsync()
    {
        var window = CreateWindow();
        Call(window, "QueueButtonState", (byte)2);
        Call(window, "QueueButtonState", (byte)0);
        Call(window, "QueueButtonState", (byte)4);
        Call(window, "QueueWheel", 3);
        Call(window, "SetControlEnabled", false);
        Require(Buttons(window).SequenceEqual(new byte[] { 0 }), "Old presses remain queued.");
        Require(Get<int>(window, "_pendingWheel") == 0, "Wheel input survived release.");
        return Task.CompletedTask;
    }

    private static Task TextEntryAsync()
    {
        var window = CreateWindow();
        Set(window, "_toggleControlHotkey", Key.A);
        var key = KeyEvent(Key.A, new TextBox());
        Call(window, "Window_KeyDown", window, key);
        Require(Get<bool>(window, "_controlEnabled"), "Typing toggled control.");
        Require(!key.Handled, "Text input was consumed.");
        return Task.CompletedTask;
    }

    private static Task EscapeFromEditorAsync()
    {
        var window = CreateWindow();
        var key = KeyEvent(Key.Escape, new TextBox());
        Call(window, "Window_KeyDown", window, key);
        Require(!Get<bool>(window, "_controlEnabled") && key.Handled, "Escape failed to release control.");
        return Task.CompletedTask;
    }

    private static Task HotkeyCaptureAsync()
    {
        var window = CreateWindow();
        var field = typeof(MainWindow).GetField("_hotkeyCaptureTarget", PrivateInstance)!;
        field.SetValue(window, Enum.Parse(field.FieldType, "ToggleControl"));
        // A reserved key exercises capture without persisting any user settings.
        var key = KeyEvent(Key.Return, new TextBox());
        Call(window, "Window_KeyDown", window, key);
        Require(key.Handled, "Explicit capture was bypassed by the editor guard.");
        return Task.CompletedTask;
    }

    private static async Task ReportPumpAsync()
    {
        var window = CreateWindow();
        var mouse = new FakeMouse();
        using var cancellation = new CancellationTokenSource();
        Call(window, "QueueButtonState", (byte)2);
        Call(window, "QueueButtonState", (byte)0);
        var uiThread = Environment.CurrentManagedThreadId;
        // Start on the dispatcher deliberately, then prevent it processing continuations.
        var pump = (Task)Call(window, "RunReportPumpAsync", mouse, cancellation.Token)!;
        try
        {
            Require(mouse.Released.Task.Wait(TimeSpan.FromSeconds(3)), "Reports depend on the blocked UI dispatcher.");
            Require(mouse.Reports.All(report => report.Thread != uiThread), "Report ran on the UI thread.");
            Require(mouse.Reports.Select(report => report.Buttons).SequenceEqual(new byte[] { 2, 2, 0 }),
                "Press/final-held-position/release ordering changed.");
        }
        finally
        {
            cancellation.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static KeyEventArgs KeyEvent(Key key, object source) => new(
        Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyDownEvent, Source = source };

    private static IEnumerable<byte> Buttons(MainWindow window) =>
        Get<IEnumerable>(window, "_reportFrames").Cast<object>()
            .Select(frame => (byte)frame.GetType().GetProperty("Buttons")!.GetValue(frame)!);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            Require(DateTime.UtcNow < timeout, "Timed out waiting for macro state.");
            await Task.Delay(2);
        }
    }

    private static T Get<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, arguments);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class FakeMouse : IHidMouse
    {
        public bool IsAbsolutePointerSubscribed => true;
        public ConcurrentQueue<(byte Buttons, int Thread)> Reports { get; } = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void StartAdvertising() => throw new InvalidOperationException("Tests must not start advertising.");
        public Task SendAsync(byte buttons, int x, int y, int wheel) => SendAsync(buttons);
        public Task SendAbsoluteAsync(byte buttons, ushort x, ushort y, int wheel) => SendAsync(buttons);
        private async Task SendAsync(byte buttons)
        {
            await Task.Delay(1).ConfigureAwait(false);
            Reports.Enqueue((buttons, Environment.CurrentManagedThreadId));
            if (buttons == 0) Released.TrySetResult();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
