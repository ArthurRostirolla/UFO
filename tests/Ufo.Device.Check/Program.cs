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

// M274 settings file: layout must match m274/apo/ipc.h (magic, even sequence, then the bands).
using (var client = new M274Client())
{
    client.Send(fps, new SoundSettings());
}
// Shared open: the running app and the M274 keep this file mapped.
using var sentFile = new FileStream(Path.Combine(M274Client.DataDirectory, "m274.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
static float[] Spectrum(double bassExcessDb) => Enumerable.Range(0, M274Client.SpectrumBands).Select(band =>
{
    double frequency = 20 * Math.Pow(1000, (band + 0.5) / M274Client.SpectrumBands);
    return (float)(-30 + PlaylistAnalysis.ReferenceDb(frequency) + (frequency < 160 ? bassExcessDb : 0));
}).ToArray();

var balanced = new PlaylistAnalysis();
var bassy = new PlaylistAnalysis();
for (int frame = 0; frame < 400; frame++)
{
    balanced.Add(Spectrum(0), 0.7);
    bassy.Add(Spectrum(14), 0.7);
}
balanced.Add(Enumerable.Repeat(-120f, M274Client.SpectrumBands).ToArray(), 0); // silence is ignored
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

// ── Clips ───────────────────────────────────────────────────────────────────────────────────────
{
    var clips = new Ufo.Clips.ClipSettings();
    Expect(clips.OutputSize(2560, 1440), (2560, 1440), "native resolution by default");
    Expect((clips with { Height = 1080 }).OutputSize(3440, 1440), (2580, 1080), "ultrawide keeps its shape, even width");
    Expect((clips with { Height = 2160 }).OutputSize(1920, 1080), (1920, 1080), "never scales up");
    Expect(clips.BitrateKbps(1920, 1080), 30000, "high quality at 1080p60");
    Expect((clips with { Quality = 0, FramesPerSecond = 30 }).BitrateKbps(1280, 720) < 4000, true, "low 720p30 stays small");
    Expect((clips with { Codec = "hevc" }).BitrateKbps(3840, 2160) > 30000, true, "4K needs more than 1080p even in HEVC");

    Expect(string.Join(",", Ufo.Clips.ClipRecorder.LastSegments([1, 2, 3, 4, 5], 3)), "3,4,5", "last three seconds");
    Expect(string.Join(",", Ufo.Clips.ClipRecorder.LastSegments([1, 2], 30)), "1,2", "short buffer gives what it has");

    var first = Ufo.Clips.ClipRecorder.Candidates(clips, null).First();
    Expect(first, new Ufo.Clips.CapturePipeline("ddagrab=output_idx=0:framerate=60", "h264_nvenc"), "GPU-only pipeline first");
    Expect(Ufo.Clips.ClipRecorder.Candidates(clips, (1280, 720)).Count(pipeline => pipeline.Filter.Contains("1280")), 8, "every scaled pipeline scales");
    string[] record = Ufo.Clips.ClipRecorder.RecordArguments(clips with { SyncMilliseconds = -40 }, first, 1920, 1080, "seg%06d.ts");
    Expect(record[Array.IndexOf(record, "-itsoffset") + 1], "-0.04", "sync offset in seconds, invariant culture");
    Expect(record[Array.IndexOf(record, "-g") + 1], "60", "a keyframe per second");

    // Triggers: two checks in a row fire once, then stay quiet for the cooldown.
    var gate = new Ufo.Clips.TriggerGate(checksInARow: 2, cooldown: TimeSpan.FromSeconds(30));
    TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
    Expect(gate.Update(0.9, 0.5, At(0)), false, "one high score is not enough");
    Expect(gate.Update(0.2, 0.5, At(0.5)), false, "a dip resets the streak");
    Expect(gate.Update(0.9, 0.5, At(1)), false, "streak restarts");
    Expect(gate.Update(0.9, 0.5, At(1.5)), true, "two in a row fire");
    Expect(gate.Update(0.9, 0.5, At(2)) || gate.Update(0.9, 0.5, At(2.5)), false, "quiet during the cooldown");
    Expect(gate.Update(0.9, 0.5, At(40)) || gate.Update(0.9, 0.5, At(40.5)), true, "fires again after it");

    float[] loud = [.. Enumerable.Range(0, 16000).Select(i => i < 8000 ? 0f : 0.5f * MathF.Sin(i * 0.3f))];
    Expect(Math.Round(Ufo.Clips.ClipTriggers.LevelDb(loud, 0.5)), -9.0, "RMS of a half-scale sine over the last half second");
    Expect(Ufo.Clips.ClipTriggers.LevelDb(new float[16000], 0.5) < -100, true, "silence");

    // Resampler: 16 kHz up to 48 kHz triples the samples and keeps a ramp a ramp across blocks.
    var resampler = new Ufo.Clips.Resampler(16000, 48000);
    var resampled = new List<float>();
    resampler.Process([0f, 3f, 6f], 1, resampled, isMono: true);
    resampler.Process([9f, 12f, 15f], 1, resampled, isMono: true);
    Expect(string.Join(",", resampled.Select(sample => Math.Round(sample, 3))), "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15", "linear upsampling is continuous across blocks");

    // Library: names, trigger tags and the space limit.
    string clipPath = Ufo.Clips.ClipLibrary.NewPath("Elden: Ring?", "risada", new DateTime(2026, 11, 17, 21, 3, 12));
    Expect(Path.GetFileName(clipPath), "Elden Ring 2026-11-17 21-03-12 risada.mp4", "clip file name");
    Ufo.Clips.ClipFile File(string name, long bytes) => new(name, "Jogo", "atalho", DateTime.Now, bytes);
    Expect(string.Join(",", Ufo.Clips.ClipLibrary.OverLimit([File("new", 40), File("mid", 40), File("old", 40)], 90).Select(clip => clip.Path)), "old", "oldest goes first, only as many as needed");

    Console.WriteLine("Clips check passed.");
}

// Live smoke test of the replay buffer (needs a screen and ffmpeg): dotnet run --project tests/Ufo.Device.Check -- --clip <output.mp4>
if (args is ["--clip", string clipOutput])
{
    using var audio = new Ufo.Clips.ClipAudio();
    using var recorder = new Ufo.Clips.ClipRecorder(audio);
    recorder.Start(new Ufo.Clips.ClipSettings(), 1920, 1080);
    Console.WriteLine($"Recording: {recorder.Description}");
    await Task.Delay(8000);
    Ufo.Clips.SavedClip? saved = await recorder.SaveClipAsync(5, clipOutput);
    Console.WriteLine(saved is null ? "No clip." : $"Saved {saved.Seconds} s to {saved.Path}; mic level {Ufo.Clips.ClipTriggers.LevelDb(audio.LastMicrophoneSecond(), 1):0} dBFS");
}
