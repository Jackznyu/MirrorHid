# MirrorHid

MirrorHid is an experimental Windows-to-iPhone assistive mouse bridge. It
places a transparent control surface over an existing iPhone mirroring window
and sends pointer input to the phone using the standard Bluetooth Low Energy
HID-over-GATT profile.

MirrorHid does not inject touch events, modify the iPhone, provide screen
mirroring, or require a jailbreak. It is an independent project and is not
affiliated with Apple or Microsoft.

## Features

- Transparent, always-on-top overlay for an existing mirrored iPhone screen.
- Absolute pointer alignment or relative mouse movement.
- Natural left-click taps, long presses, and direct press-move-release dragging.
- Left and right clicks exposed as distinct, assignable AssistiveTouch buttons.
- Wheel-up and wheel-down exposed as assignable AssistiveTouch buttons 4 and 5.
- Six configurable macros for navigating to saved AssistiveTouch gestures.
- Configurable hotkeys and macro playback speed.
- Remembered overlay position and size, plus Hide and Reset Size controls.

## Requirements

- 64-bit Windows 10 version 1809 or later.
- A Bluetooth Low Energy adapter and driver that support the Windows peripheral
  role. Not every Windows Bluetooth adapter supports acting as a BLE HID device.
- An iPhone with AssistiveTouch enabled.
- A separate iPhone screen-mirroring application.
- The .NET 10 SDK when building from source.

## Quick start

1. Clone the repository and run `run-overlay.cmd`. On first use, the script
   publishes a release build to `dist\MirrorHid` and then launches it.
2. Start the application you normally use to mirror the iPhone screen.
3. On the iPhone, open
   `Settings > Accessibility > Touch > AssistiveTouch > Devices > Bluetooth Devices`.
4. Pair with the Windows PC. Windows supplies the Bluetooth device identity, so
   the displayed name may be the computer name rather than MirrorHid.
5. Drag the overlay by its header and resize it from the lower-right corner.
   Align the area below the 42-pixel toolbar with the visible iPhone screen.
6. Select **Enable** or press **F8**.
7. Press **F8** or **Escape** to release control.

The overlay remembers its position and dimensions. **Reset Size** restores the
original 430x800 dimensions without moving it, and **Hide** minimizes it until
you select MirrorHid from the Windows taskbar.

## Pointer and drag setup

Keep **Align iPhone pointer with Windows cursor** enabled to use absolute
pointer reports. The overlay maps the Windows cursor directly onto the visible
iPhone screen. Disable it to use relative movement; the sensitivity slider only
applies in relative mode.

Dragging is direct and does not require the AssistiveTouch **Drag Lock** option.
Press and hold the left mouse button, move to the destination while continuing
to hold it, then release to drop. A quick left press and release remains a
normal tap. Left and right buttons now use the same live press-move-release
report path; they differ only in the HID button number sent to the iPhone.

## Assign right-click and wheel gestures

MirrorHid reports physical left-click as mouse button 1 and physical right-click
as mouse button 2. Both preserve their held state while the pointer moves and
remain independently assignable. Wheel-up and wheel-down are reported as
buttons 4 and 5 instead of ordinary scrolling. This lets iOS bind each input
directly to an AssistiveTouch action or custom gesture.

1. Create and save the desired gestures under
   `Settings > Accessibility > Touch > AssistiveTouch > Create New Gesture`.
2. Open `AssistiveTouch > Devices`, select the Windows PC / MirrorHid device,
   and choose **Customize Additional Buttons**.
3. When iOS asks for an input, left- or right-click and assign the desired action
   or saved custom gesture.
4. Repeat with wheel-up and wheel-down for actions such as Zoom In and Zoom Out.

If upgrading from a build with a different HID descriptor, forget the device
on the iPhone and pair it again before configuring the additional buttons.

## AssistiveTouch macro slots

The six macro slots replay sequences of pointer clicks. They are intended to
navigate the onscreen AssistiveTouch menu and select saved gestures.

To record or replace a macro:

1. Press the global Record hotkey (**F10** by default).
2. Press the replay hotkey for the desired slot.
3. Hover a menu target and press **Enter**. MirrorHid records the position and
   clicks it so the next menu opens.
4. Repeat Enter for every required menu target.
5. Press the Record hotkey again to save.

**Backspace** removes the latest pending step. **Escape** cancels recording and
preserves the previous macro. Each slot has a configurable name, replay hotkey,
and 0.5x-4x playback speed.

The setup toolbar also provides **Solid UI** for adjusting controls against an
opaque background. Control mode remains transparent.

## Settings and privacy

MirrorHid stores macro configuration and window placement in
`%LOCALAPPDATA%\MirrorHid`. Delete that folder to reset all saved settings.

The application does not contain telemetry or an internet client. Building may
contact NuGet to restore the Windows SDK dependencies used by .NET.

## Build from source

Build the application:

```powershell
dotnet build .\src\MirrorHid.App\MirrorHid.App.csproj --configuration Release
```

Create the same stable package used by the launcher:

```powershell
dotnet publish .\src\MirrorHid.App\MirrorHid.App.csproj --configuration Release --output .\dist\MirrorHid
```

The project targets `net10.0-windows10.0.19041.0` and x64. A GitHub Actions
workflow builds the release configuration on Windows for pushes and pull
requests.

## Releases

Pushing a version tag runs the Release workflow, creates a self-contained
Windows x64 package, and publishes `MirrorHid-win-x64.zip` as a GitHub Release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Users of the release ZIP do not need to install the .NET runtime. Creating a
tag is intentionally separate from ordinary pushes so every commit does not
produce a public release.

## Bluetooth capability probe

`MirrorHid.Probe` is a console application for checking Windows Bluetooth
peripheral-role support and testing basic HID reports:

```powershell
dotnet run --project .\src\MirrorHid.Probe\MirrorHid.Probe.csproj
```

After pairing from the iPhone's AssistiveTouch Bluetooth Devices screen, use
the arrow keys to move the pointer, Space to click, and Q to quit.

## Known limitations

- Bluetooth peripheral-role support depends on the Windows adapter and driver.
- iOS may cache the HID report descriptor; descriptor changes generally require
  forgetting and re-pairing the device.
- Macro coordinates depend on overlay alignment, phone orientation, and the
  current AssistiveTouch menu layout.
- Extremely fast pointer movement may be coalesced because BLE notifications
  are serialized, although the final held position is preserved before release.
- MirrorHid is experimental and has not been tested across every Windows
  adapter, iPhone model, iOS version, or mirroring application.

## License

MirrorHid is available under the [MIT License](LICENSE).

## Acknowledgements

Initial feasibility research reviewed Microsoft's
[BluetoothLEExplorer](https://github.com/microsoft/BluetoothLEExplorer) and
[VirtualDrivers/VirtualBT](https://github.com/VirtualDrivers/VirtualBT).
VirtualBT is GPL-3.0 licensed and is not included in this repository. MirrorHid
uses an independent implementation based on the public Windows Bluetooth GATT
APIs and the Bluetooth HID specification.
