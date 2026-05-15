namespace Winpepper.Corrections;

/// <summary>
/// Input validation rules for the Preferred and Replacements lists.
/// Spec §7.3: "no empty strings, no duplicates, no self-mappings, minimum length 2".
/// (Duplicate checking is done at the list level by <see cref="CorrectionStore"/>.)
/// </summary>
public static class CorrectionValidation
{
    public const int MinLength = 2;

    public static bool IsValidPreferred(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length < MinLength) return false;
        if (value.Trim().Length != value.Length) return false; // no leading/trailing whitespace
        return true;
    }

    public static bool IsValidReplacement(string? wrong, string? right)
    {
        if (string.IsNullOrWhiteSpace(wrong) || string.IsNullOrWhiteSpace(right)) return false;
        if (wrong.Length < MinLength || right.Length < MinLength) return false;
        if (wrong.Trim().Length != wrong.Length) return false;
        if (right.Trim().Length != right.Length) return false;
        if (string.Equals(wrong, right, StringComparison.Ordinal)) return false; // self-mapping
        return true;
    }
}
