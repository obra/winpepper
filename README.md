# Winpepper

Native Windows 11 local dictation. Companion to pepper-x (Linux).

Hold a hotkey, speak, release — your words appear in the focused app. Everything runs locally.

See `docs/superpowers/specs/2026-05-15-winpepper-design.md` for the design.

## Build

```sh
dotnet build
dotnet test
```

Windows-specific tests require the dev VM described in `docs/manual-test.md`.
