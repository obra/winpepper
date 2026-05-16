namespace Winpepper.Core.Learning;

/// <summary>
/// Narrow write surface over <c>Winpepper.Corrections.CorrectionStore</c> used
/// by <see cref="PostPasteWatcher"/>. Keeps <c>Winpepper.Core</c> from having
/// to take a project reference on <c>Winpepper.Corrections</c>.
/// </summary>
public interface ICorrectionWriter
{
    bool AddReplacement(string wrong, string right);
    bool AddPreferred(string value);
}
