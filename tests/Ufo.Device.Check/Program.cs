using Ufo.Audio;
using Ufo.Device;
using static Ufo.Device.HeadsetProtocol;

// Protocol self-check against responses captured from the real headset (2026-11-16).
// Run: dotnet run --project tests/Ufo.Device.Check

static byte[] Report(string hex)
{
    byte[] report = new byte[ReportLength];
    Convert.FromHexString(hex.Replace(" ", "")).CopyTo(report, 0);
    return report;
}

static void Expect<T>(T actual, T expected, string label)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        throw new Exception($"{label}: expected {expected}, got {actual}");
    }
}

byte[] request = BuildRequest(Write, Op.SetAnc, 2, 0, 1, 3);
Expect(request.Length, ReportLength, "request length");
Expect(Convert.ToHexString(request, 0, 7), "06407502000103", "SetAnc request");

Expect(ParseDeviceInfo(Report("07 C0 00 0C 00 04 01 01 00 28 A5 FF 01 03 00")), new DeviceInfo("4.1", "3.0"), "device info");
Expect(ParseRfLink(Report("07 C0 09 01 00 CC")), true, "rf link");
Expect(ParseBattery(Report("07 C0 0A 0A 00 00 21 00 00 FC 0D 21 00 02 F9")), new BatteryState(33, false, false), "battery");
Expect(ParseAnc(Report("07 C0 77 02 00 01 03")), new AncState(SoundMode.NoiseCancelling, 3), "anc");
Expect(ParseMicProcessing(Report("07 C0 80 04 00 01 00 00 00")), new MicProcessingState(true, 0), "mic processing");
Expect(ParseMicEnabled(Report("07 C0 76 01 00 01")), true, "mic enabled");
Expect(ParseSleep(Report("07 C0 8D 02 00 01 02")), new SleepState(true, 30), "sleep");
Expect(ParseEndurance(Report("07 C0 15 02 00 00 14")), new EnduranceState(false, 20), "endurance");

Expect(IsResponseTo(Report("07 40 75 03 00 EC AC"), Write, Op.SetAnc), true, "write echo");
Expect(IsRejected(Report("07 40 75 03 00 00 FA")), true, "rejected");

Console.WriteLine("Protocol check passed.");

// Equalizer: headroom, response curve and APO rendering.
EqualizerSettings fps = Equalizer.FromPreset("FPS");
Expect(Equalizer.Preamp(fps.Bands) < -6.0, true, "headroom covers overlapping boosts, not just the tallest band");
Expect(Math.Round(Equalizer.Preamp([new EqBand(1000, 6)]), 2), -6.0, "a single boost needs exactly its own gain");
Expect(Equalizer.Preamp([new EqBand(1000, -8)]), 0.0, "cuts need no headroom");

EqualizerSettings flat = Equalizer.Default;
Expect(Math.Round(Equalizer.ResponseDb(flat.Bands, 1000), 3), 0.0, "flat response");

EqBand[] singleBoost = [new EqBand(1000, 6)];
Expect(Math.Round(Equalizer.ResponseDb(singleBoost, 1000), 2), 0.0, "boost at its center is cancelled by the preamp");
if (Equalizer.ResponseDb(singleBoost, 100) > -5.5)
{
    throw new Exception("far from the boost the curve should sit near the -6 dB preamp");
}

// Engine settings file: layout must match engine/apo/ipc.h (magic, even sequence, then the bands).
using (var client = new EngineClient())
{
    client.Send(fps, new SoundSettings());
}
// Shared open: the running app and the engine keep this file mapped.
using var sentFile = new FileStream(Path.Combine(EngineClient.DataDirectory, "engine.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
byte[] sent = new byte[sentFile.Length];
sentFile.ReadExactly(sent);
Expect(sent.Length, 568, "settings file size");
Expect(BitConverter.ToUInt32(sent, 0), 0x324F4655u, "settings magic");
Expect(BitConverter.ToUInt32(sent, 4) % 2, 0u, "sequence is even once the write is complete");
Expect(BitConverter.ToUInt32(sent, 8), 1u, "enabled flag");
Expect(BitConverter.ToUInt32(sent, 12), 10u, "band count");
Expect(BitConverter.ToSingle(sent, 56 + 5 * 16 + 4), 1000f, "sixth band frequency");
Expect(BitConverter.ToSingle(sent, 56 + 5 * 16 + 8), 3f, "sixth band gain");

// Playlist analysis: music on the reference tilt needs nothing; bass-heavy music keeps its bass and gains clarity.
static float[] Spectrum(double bassExcessDb) => Enumerable.Range(0, EngineClient.SpectrumBands).Select(band =>
{
    double frequency = 20 * Math.Pow(1000, (band + 0.5) / EngineClient.SpectrumBands);
    return (float)(-30 + PlaylistAnalysis.ReferenceDb(frequency) + (frequency < 160 ? bassExcessDb : 0));
}).ToArray();

var balanced = new PlaylistAnalysis();
var bassy = new PlaylistAnalysis();
for (int frame = 0; frame < 400; frame++)
{
    balanced.Add(Spectrum(0), 0.7);
    bassy.Add(Spectrum(14), 0.7);
}
balanced.Add(Enumerable.Repeat(-120f, EngineClient.SpectrumBands).ToArray(), 0); // silence is ignored
Expect(balanced.SecondsHeard(0.1), 40.0, "silent frames are not counted");
Expect(balanced.Propose().Bands.Count, 0, "balanced music needs no correction");
PlaylistProposal bassFix = bassy.Propose();
Expect(bassFix.Bands.Count, 2, "bass-heavy music gets the two clarity moves");
Expect(bassFix.Bands.Any(band => band.Frequency < 200), false, "the bass itself is never touched");
Expect(bassFix.Bands.Max(band => Math.Abs(band.Gain)) <= 2.5, true, "every move is small");
Expect(bassFix.Night, 0.0, "steady level needs no night mode");
Expect(bassFix.TrimDb, 1.5, "volume makes up for the headroom the boost costs");

// Loudness stays subtle even at a very low Windows volume.
Expect(new SoundSettings(IsLoudnessOn: true).ExtraBands(-37).Max(band => band.Gain) <= 4, true, "loudness adds at most 4 dB");

Console.WriteLine("Equalizer check passed.");
