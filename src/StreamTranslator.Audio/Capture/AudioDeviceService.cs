using NAudio.CoreAudioApi;

namespace StreamTranslator.Audio.Capture;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultDevice.ID))
            .ToArray();
    }

    public MMDevice GetDevice(string deviceId, bool followDefaultDevice)
    {
        var enumerator = new MMDeviceEnumerator();
        if (followDefaultDevice || string.Equals(deviceId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        return enumerator.GetDevice(deviceId);
    }
}

