# Agent Instructions

- **Always ensure that the test suite fully passes before pushing.**
  - The full suite (all 9 test projects, including Windows-only TFMs) runs on a Windows host: build each project in `tests/` with `-c Release`, then execute via the xUnit v3 in-process runner (`dotnet exec <built test dll>`). Do not rely on `dotnet test` — the VSTest host is unreliable on some dev machines.
  - On Linux, the pure-managed subset runs the same way (provision the .NET 9 SDK locally if needed; `/.dotnet` is gitignored). A green Linux run is necessary but not sufficient — Windows-only code (WinUI, NAudio, DPAPI) only compiles and runs on Windows.
