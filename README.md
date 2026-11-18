# Handoff Alert Plugin for vatSys
A vatSys plugin to toggle a sound when a FDR handoff is received, similar to existing EuroScope functionality.

## Build
Run the following command in the project directory:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build -c Release
```

Then copy `bin\Release\net48\HandoffAlertPlugin.dll` to your plugins folder.
