namespace Winpepper.History.Lab;

/// <summary>Pure routing for a rerun request (Linux-tested).</summary>
public static class RerunModelRouter
{
    public enum Route { NemotronBatch, ParakeetSession, NotInstalled }
    public static Route Decide(bool isStreamingModelName, bool parakeetFilesPresent)
        => isStreamingModelName ? Route.NemotronBatch
         : parakeetFilesPresent ? Route.ParakeetSession
         : Route.NotInstalled;

    /// <summary>An engine may only serve a rerun for the model it actually
    /// loaded. Guards the wrong-model hazard: the shared holder engine serves
    /// whichever streaming model is CURRENTLY SELECTED for dictation, while
    /// the rerun stamps its result with the PICKED model name.</summary>
    public static bool EngineServes(string? engineModelName, string requestedModelName)
        => engineModelName is not null && engineModelName == requestedModelName;
}
