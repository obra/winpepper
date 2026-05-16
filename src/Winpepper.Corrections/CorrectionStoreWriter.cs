using Winpepper.Core.Learning;

namespace Winpepper.Corrections;

public sealed class CorrectionStoreWriter : ICorrectionWriter
{
    private readonly CorrectionStore _store;
    public CorrectionStoreWriter(CorrectionStore store) { _store = store; }
    public bool AddReplacement(string wrong, string right) => _store.AddReplacement(wrong, right);
    public bool AddPreferred(string value) => _store.AddPreferred(value);
}
