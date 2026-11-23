# Handoff Alert Plugin for vatSys
A vatSys plugin to toggle a sound when an inbound FDR handoff is received (played every five seconds until the tag is accepted) or an outgoing handoff is accepted.

Handoff alerts are disabled by default. Click the Handoff Alert button on the VSCS Setup page to enable them.

**Integrated into the [Say Again](https://github.com/Verbjimtim/Say-Again) plugin, do not install this plugin.**

<img width="619" height="324" alt="Handoff Plugin" src="https://github.com/user-attachments/assets/189b6666-7357-40a1-9c1f-3722f8bcaff7" />

## Build
Run the following command in the project directory:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build -c Release
```

Then copy `bin\Release\net48\HandoffAlertPlugin.dll` to your plugins folder.

## Audio
A custom `HandoffAlert.wav` beside the plugin DLL is optional; the plugin generates a tone with subtle second and third harmonics when no file is present. Playback uses vatSys's configured alert audio when available, with Windows default audio as a fallback.

When another controller accepts an outgoing handoff, the plugin plays the inverse tone (high-to-low instead of low-to-high) if alerts are enabled. If a recipient rejects the handoff, a separate tone plays when the tag returns to your jurisdiction. Custom `HandoffAccepted.wav` and `HandoffRejected.wav` files beside the plugin DLL can replace those sounds.
