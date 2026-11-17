using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ufo.Clips;

/// <summary>
/// Sound of a clip: what the computer plays (loopback) plus the default microphone, mixed to
/// 48 kHz stereo float and written at a steady pace to a stream (the recorder's ffmpeg). This class
/// is the clock: it writes exactly as many samples as time has passed, silence included, so the
/// audio track never drifts from the video. It also keeps the last second of the microphone at
/// 16 kHz mono for the automatic triggers.
/// </summary>
public sealed class ClipAudio : IDisposable
{
    public const int SampleRate = 48000;
    public const int TriggerRate = 16000;

    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Source system = new();
    private readonly Source microphone = new();
    private readonly float[] heard = new float[TriggerRate]; // ring: the last second of the microphone
    private int heardAt;
    private Thread? mixer;
    private volatile bool isRunning;

    public double SystemVolume { get; set; } = 1;
    public double MicrophoneVolume { get; set; } = 1;

    /// <summary>Starts capturing and feeding <paramref name="sink"/> until <see cref="Stop"/> or the sink breaks.</summary>
    public void Start(Stream sink)
    {
        Stop();
        isRunning = true;
        mixer = new Thread(() => Mix(sink)) { IsBackground = true, Name = "UFO clip audio", Priority = ThreadPriority.AboveNormal };
        mixer.Start();
    }

    public void Stop()
    {
        isRunning = false;
        mixer?.Join(1000);
        mixer = null;
        system.Close();
        microphone.Close();
    }

    public void Dispose() => Stop();

    /// <summary>The last second heard by the microphone, 16 kHz mono, oldest first.</summary>
    public float[] LastMicrophoneSecond()
    {
        float[] copy = new float[TriggerRate];
        lock (heard)
        {
            heard.AsSpan(heardAt).CopyTo(copy);
            heard.AsSpan(0, heardAt).CopyTo(copy.AsSpan(TriggerRate - heardAt));
        }
        return copy;
    }

    private void Mix(Stream sink)
    {
        var clock = Stopwatch.StartNew();
        long written = 0; // frames
        long nextDeviceCheck = 0;
        float[] mix = new float[SampleRate / 5 * 2];
        float[] part = new float[mix.Length];
        while (isRunning)
        {
            if (clock.ElapsedMilliseconds >= nextDeviceCheck)
            {
                // Follows the default devices: a headset that comes back or a changed default is picked up here.
                nextDeviceCheck = clock.ElapsedMilliseconds + 3000;
                system.Follow(enumerator, DataFlow.Render, null);
                microphone.Follow(enumerator, DataFlow.Capture, Hear);
            }
            int frames = (int)Math.Min(clock.ElapsedMilliseconds * SampleRate / 1000 - written, mix.Length / 2);
            if (frames > 0)
            {
                Span<float> block = mix.AsSpan(0, frames * 2);
                block.Clear();
                Add(block, part, system, SystemVolume);
                Add(block, part, microphone, MicrophoneVolume);
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] = Math.Clamp(block[i], -1, 1);
                }
                try
                {
                    sink.Write(MemoryMarshal.AsBytes(block));
                    sink.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    break; // ffmpeg ended
                }
                written += frames;
            }
            Thread.Sleep(10);
        }
    }

    private static void Add(Span<float> block, float[] scratch, Source source, double volume)
    {
        Span<float> taken = scratch.AsSpan(0, block.Length);
        source.Take(taken);
        for (int i = 0; i < taken.Length; i++)
        {
            block[i] += (float)(taken[i] * volume);
        }
    }

    private void Hear(ReadOnlySpan<float> mono)
    {
        lock (heard)
        {
            foreach (float sample in mono)
            {
                heard[heardAt] = sample;
                heardAt = (heardAt + 1) % TriggerRate;
            }
        }
    }

    private delegate void MonoListener(ReadOnlySpan<float> mono);

    /// <summary>One capture device and its queue of 48 kHz stereo samples waiting to be mixed.</summary>
    private sealed class Source
    {
        private const int Capacity = SampleRate * 2; // one second of stereo
        private readonly float[] ring = new float[Capacity];
        private int readAt, count;
        private WasapiRecorder? recorder;
        private string? deviceId;
        private bool hasStopped;

        public void Follow(MMDeviceEnumerator enumerator, DataFlow flow, MonoListener? listener)
        {
            try
            {
                if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                {
                    Close();
                    return;
                }
                MMDevice device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                if (device.ID == deviceId && !hasStopped)
                {
                    return;
                }
                Close();
                WasapiRecorderBuilder builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode();
                WasapiRecorder opened = (flow == DataFlow.Render ? builder.WithLoopbackCapture() : builder).Build();
                Resampler? toMix = null, toTrigger = null;
                float[] floats = [];
                var stereo = new List<float>();
                var mono = new List<float>();
                opened.DataAvailable += (buffer, _, _, _) =>
                {
                    WaveFormat format = opened.WaveFormat;
                    toMix ??= new Resampler(format.SampleRate, SampleRate);
                    ReadOnlySpan<float> samples = ToFloats(buffer, format, ref floats);
                    stereo.Clear();
                    toMix.Process(samples, format.Channels, stereo, isMono: false);
                    Put(CollectionsMarshal.AsSpan(stereo));
                    if (listener is not null)
                    {
                        toTrigger ??= new Resampler(format.SampleRate, TriggerRate);
                        mono.Clear();
                        toTrigger.Process(samples, format.Channels, mono, isMono: true);
                        listener(CollectionsMarshal.AsSpan(mono));
                    }
                };
                opened.RecordingStopped += (_, _) => hasStopped = true;
                opened.StartRecording();
                recorder = opened;
                deviceId = device.ID;
                hasStopped = false;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                Close(); // device busy or gone; tried again at the next check
            }
        }

        public void Close()
        {
            try
            {
                recorder?.Dispose();
            }
            catch (COMException)
            {
                // the device was already gone
            }
            recorder = null;
            deviceId = null;
        }

        /// <summary>Shared-mode capture delivers 32-bit float or 16-bit integers.</summary>
        private static ReadOnlySpan<float> ToFloats(ReadOnlySpan<byte> buffer, WaveFormat format, ref float[] scratch)
        {
            if (format.BitsPerSample == 32)
            {
                return MemoryMarshal.Cast<byte, float>(buffer);
            }
            ReadOnlySpan<short> integers = MemoryMarshal.Cast<byte, short>(buffer);
            if (scratch.Length < integers.Length)
            {
                scratch = new float[integers.Length];
            }
            for (int i = 0; i < integers.Length; i++)
            {
                scratch[i] = integers[i] / 32768f;
            }
            return scratch.AsSpan(0, integers.Length);
        }

        private void Put(ReadOnlySpan<float> samples)
        {
            lock (ring)
            {
                foreach (float sample in samples)
                {
                    if (count == Capacity)
                    {
                        // The mixer fell behind (or the device clock runs fast): drop the oldest.
                        readAt = (readAt + 1) % Capacity;
                        count--;
                    }
                    ring[(readAt + count) % Capacity] = sample;
                    count++;
                }
            }
        }

        /// <summary>Fills <paramref name="target"/> with queued samples; whatever is missing stays silent.</summary>
        public void Take(Span<float> target)
        {
            target.Clear();
            lock (ring)
            {
                int taken = Math.Min(count, target.Length);
                for (int i = 0; i < taken; i++)
                {
                    target[i] = ring[(readAt + i) % Capacity];
                }
                readAt = (readAt + taken) % Capacity;
                count -= taken;
            }
        }
    }
}

/// <summary>
/// Streaming linear-interpolation resampler from interleaved input to stereo or mono output.
/// </summary>
/// <remarks>
/// ponytail: linear interpolation, no anti-alias filter. Fine for the usual cases (48 kHz system
/// audio passes through untouched, the 16 kHz microphone only goes up); swap for NAudio's
/// WdlResampler if a 96 kHz device ever sounds harsh.
/// </remarks>
public sealed class Resampler(int sourceRate, int targetRate)
{
    private readonly double step = (double)sourceRate / targetRate;
    private double position; // in source frames; -1 is the last frame of the previous block
    private float lastLeft, lastRight;

    public void Process(ReadOnlySpan<float> input, int channels, List<float> output, bool isMono)
    {
        int frames = input.Length / channels;
        if (frames == 0)
        {
            return;
        }
        for (; position < frames - 1; position += step)
        {
            int index = (int)Math.Floor(position);
            float mix = (float)(position - index);
            (float fromLeft, float fromRight) = index < 0 ? (lastLeft, lastRight) : Frame(input, index, channels);
            (float toLeft, float toRight) = Frame(input, index + 1, channels);
            float left = fromLeft + (toLeft - fromLeft) * mix;
            float right = fromRight + (toRight - fromRight) * mix;
            if (isMono)
            {
                output.Add((left + right) / 2);
            }
            else
            {
                output.Add(left);
                output.Add(right);
            }
        }
        position -= frames;
        (lastLeft, lastRight) = Frame(input, frames - 1, channels);
    }

    private static (float Left, float Right) Frame(ReadOnlySpan<float> input, int index, int channels) =>
        (input[index * channels], input[index * channels + (channels > 1 ? 1 : 0)]);
}
