using static Ufo.Device.HeadsetProtocol;

namespace Ufo.Device;

/// <summary>
/// High-level access to the headset. Every call is blocking and serialized; run it off the UI thread.
/// Reads return null and writes return false when the dongle is missing or the device refuses.
/// </summary>
public sealed class Headset
{
    private const int Attempts = 5;
    private const int Polls = 10;
    private const int PollDelayMs = 10;

    private readonly object gate = new();

    public DeviceInfo? GetDeviceInfo() => Query(Read, Op.DeviceInfo, 4, [0, 0, 245, 40, 0], ParseDeviceInfo);

    public bool? IsHeadsetLinked() => QueryValue(Read, Op.RfLink, 0, [], ParseRfLink);

    public BatteryState? GetBattery() => Query(Read, Op.Battery, 0, [0], ParseBattery);

    public AncState? GetAnc() => Query(Read, Op.GetAnc, 0, [], ParseAnc);

    public bool SetAnc(SoundMode mode, byte level)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxAncLevel);
        return Command(Op.SetAnc, 2, [0, (byte)mode, level]);
    }

    public MicProcessingState? GetMicProcessing() => Query(Read, Op.MicProcessing, 0, [], ParseMicProcessing);

    public bool SetNoiseReduction(bool isOn) => Command(Op.MicProcessing, 1, [0, ToByte(isOn)]);

    public bool SetSidetone(byte level)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxSidetoneLevel);
        return Command(Op.SetSidetone, 1, [0, level]);
    }

    public bool? IsMicEnabled() => QueryValue(Read, Op.GetMicEnabled, 0, [], ParseMicEnabled);

    public bool SetMicEnabled(bool isEnabled) => Command(Op.SetMicEnabled, 1, [0, ToByte(isEnabled)]);

    public bool? IsBoomMicAttached() => QueryValue(Read, Op.BoomMic, 0, [], ParseBoomMic);

    public SleepState? GetSleep() => Query(Read, Op.Sleep, 2, [], ParseSleep);

    public bool SetSleep(bool isEnabled, int minutes)
    {
        if (minutes is < SleepStepMinutes or > 4 * SleepStepMinutes || minutes % SleepStepMinutes != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Use 15, 30, 45 or 60.");
        }
        return Command(Op.Sleep, 2, [0, ToByte(isEnabled), (byte)(minutes / SleepStepMinutes)]);
    }

    public EnduranceState? GetEndurance() => Query(Read, Op.Endurance, 0, [], ParseEndurance);

    public bool SetEndurance(bool isEnabled, byte thresholdPercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdPercent, (byte)5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(thresholdPercent, (byte)100);
        return Command(Op.Endurance, 2, [0, ToByte(isEnabled), thresholdPercent]);
    }

    private bool Command(byte subCommand, byte length, byte[] payload) =>
        Exchange(Write, subCommand, length, payload) is not null;

    private T? Query<T>(byte command, byte subCommand, byte length, byte[] payload, Func<byte[], T> parse) where T : class =>
        Exchange(command, subCommand, length, payload) is { } response ? parse(response) : null;

    private T? QueryValue<T>(byte command, byte subCommand, byte length, byte[] payload, Func<byte[], T> parse) where T : struct =>
        Exchange(command, subCommand, length, payload) is { } response ? parse(response) : null;

    /// <summary>Send/poll loop of the official plugin: up to 5 sends, 10 polls each, 10 ms apart.</summary>
    private byte[]? Exchange(byte command, byte subCommand, byte length, byte[] payload)
    {
        lock (gate)
        {
            using HidChannel? channel = HidChannel.Open();
            if (channel is null)
            {
                return null;
            }
            byte[] request = BuildRequest(command, subCommand, length, payload);
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                Thread.Sleep(PollDelayMs);
                if (!channel.Send(request))
                {
                    continue;
                }
                for (int poll = 0; poll < Polls; poll++)
                {
                    Thread.Sleep(PollDelayMs);
                    byte[] response = new byte[ReportLength];
                    response[0] = InputReportId;
                    if (channel.Receive(response) && IsResponseTo(response, command, subCommand))
                    {
                        return IsRejected(response) ? null : response;
                    }
                }
            }
            return null;
        }
    }

    private static byte ToByte(bool value) => value ? (byte)1 : (byte)0;
}
