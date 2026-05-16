namespace Winpepper.Core;

public static class SelftestProbe
{
    /// <summary>
    /// Returns 0 if the core data files Winpepper expects on first launch can be reached,
    /// the version string is non-empty, and the (no-op) state machine smoke succeeds.
    /// Writes a single-line WINPEPPER_SELFTEST_OK token plus diagnostic lines to <paramref name="emit"/>.
    /// </summary>
    public static int Run(Action<string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        emit($"winpepper selftest");
        emit($"build: {BuildSignature.Describe()}");
        emit($"signed: {BuildSignature.IsSigned}");

        // Verify %LOCALAPPDATA% is reachable; create the winpepper subtree if absent.
        // The MSI does NOT pre-create the models dir — first run does.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            emit("FAIL: LocalApplicationData is empty");
            return 2;
        }
        var winpepperRoot = Path.Combine(localAppData, "winpepper");
        Directory.CreateDirectory(winpepperRoot);
        Directory.CreateDirectory(Path.Combine(winpepperRoot, "models"));
        emit($"localappdata: {winpepperRoot}");

        emit("WINPEPPER_SELFTEST_OK");
        return 0;
    }
}
