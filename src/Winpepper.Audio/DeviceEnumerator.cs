#if WINDOWS
using NAudio.CoreAudioApi;

namespace Winpepper.Audio;

public sealed record CaptureDevice(string Id, string FriendlyName, bool IsDefault);

public static class DeviceEnumerator
{
    public static IReadOnlyList<CaptureDevice> List()
    {
        var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No default capture endpoint configured (e.g., headless VM with no audio device).
        }
        var list = new List<CaptureDevice>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            list.Add(new CaptureDevice(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        return list;
    }
}
#endif
