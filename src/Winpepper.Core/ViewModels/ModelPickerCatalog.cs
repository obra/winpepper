namespace Winpepper.Core.ViewModels;

/// <summary>Registry facts the picker needs, supplied by the page (Core has
/// no Models reference by design — names+bytes travel as plain data).</summary>
public sealed record ModelPickerCatalog(
    string EnglishName, long EnglishBytes,
    string MultilingualName, long MultilingualBytes,
    string BackupName, long BackupBytes,
    string CleanupName, long CleanupBytes);
