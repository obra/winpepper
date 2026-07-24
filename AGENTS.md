# Agent Instructions

- **Always ensure all tests are green before committing, and the full suite passes before pushing.**
  - Before EVERY commit: run the test suite available on the current machine and require 0 failures. On Linux that is the pure-managed subset of all 9 test projects; commits that touch only Windows-only code (WinUI XAML/code-behind) must still get a green Linux run to prove nothing shared broke, and must be Windows-verified before pushing.
  - Before pushing: the FULL suite (all 9 test projects, including Windows-only TFMs) must pass on a Windows host.
  - How to run: build each project in `tests/` with `-c Release`, then execute via the xUnit v3 in-process runner (`dotnet exec <built test dll>`). Do not rely on `dotnet test` — the VSTest host is unreliable on some dev machines.
  - On Linux, provision the .NET 9 SDK locally if needed (`/.dotnet` is gitignored). A green Linux run is necessary but not sufficient — Windows-only code (WinUI, NAudio, DPAPI) only compiles and runs on Windows.
