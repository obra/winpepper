namespace Winpepper.Core.Settings;

public interface ISettingsWriter
{
    void Queue(Func<AppSettings, AppSettings> mutator);
    Task FlushAsync();
}
