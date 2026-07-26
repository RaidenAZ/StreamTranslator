using NAudio.CoreAudioApi;

namespace StreamTranslator.Audio.Capture;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        // Obtain the default-device ID then immediately Dispose the temporary
        // handle; enumerate live devices, project to plain data, then Dispose all.
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var defaultId = defaultDevice.ID;
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();
        try
        {
            return devices
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId))
                .ToArray();
        }
        finally
        {
            foreach (var d in devices)
            {
                d.Dispose();
            }
        }
    }

    public MMDevice GetDevice(string deviceId, bool followDefaultDevice)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (followDefaultDevice || string.Equals(deviceId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        return enumerator.GetDevice(deviceId);
    }
}

