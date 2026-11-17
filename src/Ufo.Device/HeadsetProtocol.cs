namespace Ufo.Device;

/// <summary>Sound mode of the ear cups.</summary>
public enum SoundMode : byte
{
    Off = 0,
    NoiseCancelling = 1,
    Transparency = 2,
}

public sealed record AncState(SoundMode Mode, byte Level);

public sealed record MicProcessingState(bool IsNoiseReductionOn, byte SidetoneLevel);

public sealed record BatteryState(int Level, bool IsCharging, bool IsEnduranceOn);

public sealed record SleepState(bool IsEnabled, int Minutes);

public sealed record EnduranceState(bool IsEnabled, byte ThresholdPercent);

public sealed record DeviceInfo(string HeadsetFirmware, string DongleFirmware);

/// <summary>
/// Vendor HID protocol of the AW Pro headset dongle (VID 413C, PID A529), as used by the
/// official plugin: 62-byte output report 6 = <c>06 cmd sub length 00 values…</c>, answered by
/// input report 7 echoing <c>cmd sub</c>. Byte 6 = 0xFA means the device refused the request.
/// </summary>
public static class HeadsetProtocol
{
    public const int ReportLength = 62;
    public const byte OutputReportId = 6;
    public const byte InputReportId = 7;
    public const byte Read = 0xC0;
    public const byte Write = 0x40;
    private const byte Rejected = 0xFA;
    private const byte RfLinked = 0xCC;

    /// <summary>Sub-commands; a few are shared by read and write.</summary>
    public static class Op
    {
        public const byte DeviceInfo = 0x00;
        public const byte RfLink = 0x09;
        public const byte Battery = 0x0A;
        public const byte Endurance = 0x15;
        public const byte FactoryReset = 0x62;
        public const byte SetMicEnabled = 0x73;
        public const byte SetAnc = 0x75;
        public const byte GetMicEnabled = 0x76;
        public const byte GetAnc = 0x77;
        public const byte MicProcessing = 0x80;
        public const byte SetSidetone = 0x8A;
        public const byte Sleep = 0x8D;
        public const byte BoomMic = 0x8F;
    }

    public const byte MaxAncLevel = 5;
    public const byte MaxSidetoneLevel = 5;
    public const int SleepStepMinutes = 15;

    /// <summary>Builds a padded output report.</summary>
    public static byte[] BuildRequest(byte command, byte subCommand, byte length, params byte[] payload)
    {
        byte[] report = new byte[ReportLength];
        report[0] = OutputReportId;
        report[1] = command;
        report[2] = subCommand;
        report[3] = length;
        payload.CopyTo(report, 4);
        return report;
    }

    public static bool IsResponseTo(byte[] response, byte command, byte subCommand) =>
        response.Length == ReportLength && response[1] == command && response[2] == subCommand;

    public static bool IsRejected(byte[] response) => response[6] == Rejected;

    public static DeviceInfo ParseDeviceInfo(byte[] r) =>
        new($"{r[5]:X}.{r[6]:X}", $"{r[13]:X}.{r[14]:X}");

    public static bool ParseRfLink(byte[] r) => r[5] == RfLinked;

    /// <summary>Same rules as the official plugin, including its low-battery clamp.</summary>
    public static BatteryState ParseBattery(byte[] r)
    {
        int level = r[11];
        if (r[6] <= 10 && level > r[6])
        {
            level = r[6];
        }
        if (r[8] == 1)
        {
            level = r[11];
        }
        return new BatteryState(level, IsCharging: r[7] == 1 && r[12] == 0, IsEnduranceOn: r[13] == 1);
    }

    public static AncState ParseAnc(byte[] r) => new((SoundMode)r[5], r[6]);

    public static MicProcessingState ParseMicProcessing(byte[] r) => new(r[5] != 0, r[8]);

    public static bool ParseMicEnabled(byte[] r) => r[5] != 0;

    public static bool ParseBoomMic(byte[] r) => r[5] != 0;

    // The official plugin reads bytes 6/7 here, which disagrees with its own write layout
    // (0, status, step); the device answers in the write layout, so bytes 5/6 are used.
    public static SleepState ParseSleep(byte[] r) => new(r[5] != 0, r[6] * SleepStepMinutes);

    public static EnduranceState ParseEndurance(byte[] r) => new(r[5] == 1, r[6]);
}
