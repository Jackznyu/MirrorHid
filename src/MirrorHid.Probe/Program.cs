using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MirrorHid.Probe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("MirrorHid BLE capability probe");
        Console.WriteLine("Creating the standard HID-over-GATT service (0x1812)...");

        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                throw new InvalidOperationException("Windows did not return a default Bluetooth adapter.");
            }

            Console.WriteLine($"Adapter address: 0x{adapter.BluetoothAddress:X12}");
            Console.WriteLine($"Low Energy supported: {adapter.IsLowEnergySupported}");
            Console.WriteLine($"Peripheral role supported: {adapter.IsPeripheralRoleSupported}");
            Console.WriteLine($"Central role supported: {adapter.IsCentralRoleSupported}");

            await using var mouse = await BleHidMouse.CreateAsync(Console.WriteLine);
            mouse.StartAdvertising();

            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("Waiting five seconds for the adapter to report advertising status...");
                await Task.Delay(TimeSpan.FromSeconds(5));
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("Advertising started. On the iPhone:");
            Console.WriteLine("  Settings > Accessibility > Touch > AssistiveTouch");
            Console.WriteLine("  Turn AssistiveTouch on, then Devices > Bluetooth Devices");
            Console.WriteLine("  Select this PC when it appears.");
            Console.WriteLine();
            Console.WriteLine("After the iPhone subscribes, press arrow keys to move its pointer.");
            Console.WriteLine("Press Space to click, or Q to quit.");

            while (true)
            {
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.Q:
                        return 0;
                    case ConsoleKey.LeftArrow:
                        await mouse.SendAsync(0, -12, 0, 0);
                        break;
                    case ConsoleKey.RightArrow:
                        await mouse.SendAsync(0, 12, 0, 0);
                        break;
                    case ConsoleKey.UpArrow:
                        await mouse.SendAsync(0, 0, -12, 0);
                        break;
                    case ConsoleKey.DownArrow:
                        await mouse.SendAsync(0, 0, 12, 0);
                        break;
                    case ConsoleKey.Spacebar:
                        await mouse.SendAsync(1, 0, 0, 0);
                        await Task.Delay(35);
                        await mouse.SendAsync(0, 0, 0, 0);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Probe failed: {exception}");
            return 1;
        }
    }
}

public sealed class BleHidMouse : IHidMouse
{
    private const ushort ReportReferenceDescriptor = 0x2908;

    private readonly Action<string> _log;
    private readonly GattServiceProvider _hidProvider;
    private readonly GattServiceProvider _batteryProvider;
    private readonly GattLocalCharacteristic _inputReport;
    private readonly GattLocalCharacteristic _absoluteInputReport;

    public bool IsAbsolutePointerSubscribed =>
        _absoluteInputReport.SubscribedClients.Count > 0;

    private BleHidMouse(
        Action<string> log,
        GattServiceProvider hidProvider,
        GattServiceProvider batteryProvider,
        GattLocalCharacteristic inputReport,
        GattLocalCharacteristic absoluteInputReport)
    {
        _log = log;
        _hidProvider = hidProvider;
        _batteryProvider = batteryProvider;
        _inputReport = inputReport;
        _absoluteInputReport = absoluteInputReport;

        _hidProvider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
        _batteryProvider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
        _inputReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _absoluteInputReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
    }

    public static async Task<BleHidMouse> CreateAsync(Action<string> log)
    {
        var providerResult =
            await GattServiceProvider.CreateAsync(GattServiceUuids.HumanInterfaceDevice);
        EnsureSuccess(providerResult.Error, "create HID service");

        var provider = providerResult.ServiceProvider;
        var service = provider.Service;

        var inputResult = await service.CreateCharacteristicAsync(
            GattCharacteristicUuids.Report,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties =
                    GattCharacteristicProperties.Read |
                    GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
            });
        EnsureSuccess(inputResult.Error, "create input report characteristic");
        var inputReport = inputResult.Characteristic;

        var reportReferenceResult = await inputReport.CreateDescriptorAsync(
            BluetoothUuidHelper.FromShortId(ReportReferenceDescriptor),
            new GattLocalDescriptorParameters
            {
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                StaticValue = ToBuffer([1, 1]) // report id 1, input report
            });
        EnsureSuccess(reportReferenceResult.Error, "create report-reference descriptor");

        var absoluteInputResult = await service.CreateCharacteristicAsync(
            GattCharacteristicUuids.Report,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties =
                    GattCharacteristicProperties.Read |
                    GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
            });
        EnsureSuccess(
            absoluteInputResult.Error,
            "create absolute input report characteristic");
        var absoluteInputReport = absoluteInputResult.Characteristic;

        var absoluteReportReferenceResult =
            await absoluteInputReport.CreateDescriptorAsync(
                BluetoothUuidHelper.FromShortId(ReportReferenceDescriptor),
                new GattLocalDescriptorParameters
                {
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                    StaticValue = ToBuffer([2, 1]) // report id 2, input report
                });
        EnsureSuccess(
            absoluteReportReferenceResult.Error,
            "create absolute report-reference descriptor");

        var reportMapResult = await service.CreateCharacteristicAsync(
            GattCharacteristicUuids.ReportMap,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                StaticValue = ToBuffer(
                [
                    0x05, 0x01,       // Usage Page (Generic Desktop)
                    0x09, 0x02,       // Usage (Mouse)
                    0xA1, 0x01,       // Collection (Application)
                    0x85, 0x01,       //   Report ID (1)
                    0x09, 0x01,       //   Usage (Pointer)
                    0xA1, 0x00,       //   Collection (Physical)
                    0x05, 0x09,       //     Usage Page (Buttons)
                    0x19, 0x01,       //     Usage Minimum (1)
                    0x29, 0x05,       //     Usage Maximum (5)
                    0x15, 0x00,       //     Logical Minimum (0)
                    0x25, 0x01,       //     Logical Maximum (1)
                    0x95, 0x05,       //     Report Count (5)
                    0x75, 0x01,       //     Report Size (1)
                    0x81, 0x02,       //     Input (Data, Variable, Absolute)
                    0x95, 0x01,       //     Report Count (1)
                    0x75, 0x03,       //     Report Size (3)
                    0x81, 0x01,       //     Input (Constant)
                    0x05, 0x01,       //     Usage Page (Generic Desktop)
                    0x09, 0x30,       //     Usage X
                    0x09, 0x31,       //     Usage Y
                    0x09, 0x38,       //     Usage Wheel
                    0x15, 0x81,       //     Logical Minimum (-127)
                    0x25, 0x7F,       //     Logical Maximum (127)
                    0x75, 0x08,       //     Report Size (8)
                    0x95, 0x03,       //     Report Count (3)
                    0x81, 0x06,       //     Input (Data, Variable, Relative)
                    0xC0,             //   End Collection
                    0xC0,             // End Collection

                    0x05, 0x01,       // Usage Page (Generic Desktop)
                    0x09, 0x02,       // Usage (Mouse)
                    0xA1, 0x01,       // Collection (Application)
                    0x85, 0x02,       //   Report ID (2)
                    0x09, 0x01,       //   Usage (Pointer)
                    0xA1, 0x00,       //   Collection (Physical)
                    0x05, 0x09,       //     Usage Page (Buttons)
                    0x19, 0x01,       //     Usage Minimum (1)
                    0x29, 0x05,       //     Usage Maximum (5)
                    0x15, 0x00,       //     Logical Minimum (0)
                    0x25, 0x01,       //     Logical Maximum (1)
                    0x95, 0x05,       //     Report Count (5)
                    0x75, 0x01,       //     Report Size (1)
                    0x81, 0x02,       //     Input (Data, Variable, Absolute)
                    0x95, 0x01,       //     Report Count (1)
                    0x75, 0x03,       //     Report Size (3)
                    0x81, 0x01,       //     Input (Constant)
                    0x05, 0x01,       //     Usage Page (Generic Desktop)
                    0x09, 0x30,       //     Usage X
                    0x09, 0x31,       //     Usage Y
                    0x16, 0x00, 0x00, //     Logical Minimum (0)
                    0x26, 0xFF, 0x7F, //     Logical Maximum (32767)
                    0x75, 0x10,       //     Report Size (16)
                    0x95, 0x02,       //     Report Count (2)
                    0x81, 0x02,       //     Input (Data, Variable, Absolute)
                    0x09, 0x38,       //     Usage Wheel
                    0x15, 0x81,       //     Logical Minimum (-127)
                    0x25, 0x7F,       //     Logical Maximum (127)
                    0x75, 0x08,       //     Report Size (8)
                    0x95, 0x01,       //     Report Count (1)
                    0x81, 0x06,       //     Input (Data, Variable, Relative)
                    0xC0,             //   End Collection
                    0xC0              // End Collection
                ])
            });
        EnsureSuccess(reportMapResult.Error, "create report-map characteristic");

        var informationResult = await service.CreateCharacteristicAsync(
            GattCharacteristicUuids.HidInformation,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                StaticValue = ToBuffer(
                [
                    0x11, 0x01, // HID version 1.11
                    0x00,       // country code
                    0x03        // normally connectable + remote wake
                ])
            });
        EnsureSuccess(informationResult.Error, "create HID-information characteristic");

        var controlPointResult = await service.CreateCharacteristicAsync(
            GattCharacteristicUuids.HidControlPoint,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
            });
        EnsureSuccess(controlPointResult.Error, "create HID-control-point characteristic");
        controlPointResult.Characteristic.WriteRequested += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                _ = await args.GetRequestAsync();
            }
            finally
            {
                deferral.Complete();
            }
        };

        var batteryProvider = await CreateServiceAsync(
            GattServiceUuids.Battery,
            "create battery service");
        await CreateStaticCharacteristicAsync(
            batteryProvider.Service,
            GattCharacteristicUuids.BatteryLevel,
            [100],
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            "create battery-level characteristic");

        // Windows owns the standard Device Information service and rejects an
        // application-created instance with DisabledByPolicy. The platform's
        // GAP/GATT identity is used alongside these app-hosted services.
        log("HID and Battery services created; Windows supplies device identity.");
        return new BleHidMouse(
            log,
            provider,
            batteryProvider,
            inputReport,
            absoluteInputReport);
    }

    public void StartAdvertising()
    {
        var parameters = new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true
        };

        _batteryProvider.StartAdvertising(parameters);
        _hidProvider.StartAdvertising(parameters);
    }

    public async Task SendAsync(byte buttons, int x, int y, int wheel)
    {
        if (_inputReport.SubscribedClients.Count == 0)
        {
            _log("No iPhone has subscribed yet.");
            return;
        }

        var report = new byte[]
        {
            (byte)(buttons & 0x1F),
            unchecked((byte)Math.Clamp(x, -127, 127)),
            unchecked((byte)Math.Clamp(y, -127, 127)),
            unchecked((byte)Math.Clamp(wheel, -127, 127))
        };

        var results = await _inputReport.NotifyValueAsync(ToBuffer(report));
        var failures = results.Count(result =>
            result.Status != GattCommunicationStatus.Success);
        if (failures > 0)
        {
            _log($"Mouse report failed for {failures} subscribed client(s).");
        }
    }

    public async Task SendAbsoluteAsync(
        byte buttons,
        ushort x,
        ushort y,
        int wheel)
    {
        if (_absoluteInputReport.SubscribedClients.Count == 0)
        {
            _log("No iPhone has subscribed to absolute pointer reports yet.");
            return;
        }

        var report = new byte[]
        {
            (byte)(buttons & 0x1F),
            (byte)(x & 0xFF),
            (byte)(x >> 8),
            (byte)(y & 0xFF),
            (byte)(y >> 8),
            unchecked((byte)Math.Clamp(wheel, -127, 127))
        };

        var results = await _absoluteInputReport.NotifyValueAsync(ToBuffer(report));
        var failures = results.Count(result =>
            result.Status != GattCommunicationStatus.Success);
        if (failures > 0)
        {
            _log(
                $"Absolute pointer report failed for {failures} subscribed client(s).");
        }
    }

    public ValueTask DisposeAsync()
    {
        StopAdvertising(_hidProvider);
        StopAdvertising(_batteryProvider);
        _hidProvider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        _batteryProvider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        _inputReport.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        _absoluteInputReport.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        return ValueTask.CompletedTask;
    }

    private void OnAdvertisementStatusChanged(
        GattServiceProvider sender,
        GattServiceProviderAdvertisementStatusChangedEventArgs args) =>
        _log(
            $"Advertisement {ShortUuid(sender.Service.Uuid)}: " +
            $"{args.Status} (error: {args.Error})");

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args) =>
        _log(
            "Subscribed HID clients: " +
            $"relative={_inputReport.SubscribedClients.Count}, " +
            $"absolute={_absoluteInputReport.SubscribedClients.Count}");

    private static async Task<GattServiceProvider> CreateServiceAsync(
        Guid uuid,
        string operation)
    {
        var result = await GattServiceProvider.CreateAsync(uuid);
        EnsureSuccess(result.Error, operation);
        return result.ServiceProvider;
    }

    private static async Task CreateStaticCharacteristicAsync(
        GattLocalService service,
        Guid uuid,
        byte[] value,
        GattCharacteristicProperties properties,
        string operation)
    {
        var result = await service.CreateCharacteristicAsync(
            uuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = properties,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                StaticValue = ToBuffer(value)
            });
        EnsureSuccess(result.Error, operation);
    }

    private static void StopAdvertising(GattServiceProvider provider)
    {
        if (provider.AdvertisementStatus is
            GattServiceProviderAdvertisementStatus.Started or
            GattServiceProviderAdvertisementStatus.Aborted)
        {
            provider.StopAdvertising();
        }
    }

    private static string ShortUuid(Guid uuid)
    {
        var text = uuid.ToString();
        return text.StartsWith("0000", StringComparison.OrdinalIgnoreCase)
            ? $"0x{text.Substring(4, 4).ToUpperInvariant()}"
            : text;
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static void EnsureSuccess(BluetoothError error, string operation)
    {
        if (error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"Could not {operation}: {error}");
        }
    }
}
