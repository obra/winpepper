namespace Winpepper.Core.ViewModels;

/// <summary>How the status pill should animate for the current session stage.</summary>
public enum PillAnimationMode
{
    /// <summary>No animation (static). Idle when hidden; Error stays steady.</summary>
    None,
    /// <summary>Live voice meter driven by InputLevel while recording.</summary>
    VoiceLevel,
    /// <summary>Gentle indeterminate pulse while the app works after release.</summary>
    Thinking,
}
