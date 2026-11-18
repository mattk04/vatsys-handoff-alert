# Handoff Alert Plugin for vatSys
A vatSys plugin to toggle a sound when a FDR handoff is received, similar to existing EuroScope functionality.

<img width="619" height="324" alt="Handoff Plugin" src="https://github.com/user-attachments/assets/189b6666-7357-40a1-9c1f-3722f8bcaff7" />

## Build
Run the following command in the project directory:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build -c Release
```

Then copy `bin\Release\net48\HandoffAlertPlugin.dll` to your plugins folder.

## Audio test
Right-click the Handoff Alert button on the VSCS Setup page to play a test tone. A custom `HandoffAlert.wav` beside the plugin DLL is optional; the plugin generates a tone when no file is present. Playback uses vatSys's configured alert audio when available, with Windows default audio as a fallback.
