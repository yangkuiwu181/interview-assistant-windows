using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace InterviewAssistant;

public sealed class AudioCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly List<byte> _pcm = [];
    private double _inputPosition;
    private double _nextOutputPosition;
    private float _lastSample;
    private Timer? _silenceTimer;
    private long _lastPacketTicks;
    public event Action<byte[]>? PcmReady;
    public static IReadOnlyList<MMDevice> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public void Start(MMDevice device)
    {
        Stop();
        _inputPosition = _nextOutputPosition = 0;
        _lastSample = 0;
        _pcm.Clear();
        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => { };
        try { _capture.StartRecording(); }
        catch { Stop(); throw; }
        Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());
        _silenceTimer = new Timer(_ =>
        {
            if (_capture is null) return;
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacketTicks), now) < TimeSpan.FromMilliseconds(230)) return;
            Interlocked.Exchange(ref _lastPacketTicks, now);
            PcmReady?.Invoke(new byte[6400]);
        }, null, 200, 200);
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        var format = _capture?.WaveFormat;
        if (format is null || format.Channels < 1 || format.SampleRate <= 0) return;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extended)
            encoding = extended.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71")
                ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm;
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = bytesPerSample * format.Channels;
        if (frameSize == 0) return;
        var step = (double)format.SampleRate / 16000;
        var frames = args.BytesRecorded / frameSize;
        for (var frame = 0; frame < frames; frame++)
        {
            var mono = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * frameSize + channel * bytesPerSample;
                mono += encoding switch
                {
                    WaveFormatEncoding.IeeeFloat when bytesPerSample == 4 => BitConverter.ToSingle(args.Buffer, offset),
                    WaveFormatEncoding.Pcm when bytesPerSample == 2 => BitConverter.ToInt16(args.Buffer, offset) / 32768f,
                    WaveFormatEncoding.Pcm when bytesPerSample == 3 => (int)((args.Buffer[offset] | (args.Buffer[offset + 1] << 8) | (args.Buffer[offset + 2] << 16)) << 8) / 2147483648f,
                    WaveFormatEncoding.Pcm when bytesPerSample == 4 => BitConverter.ToInt32(args.Buffer, offset) / 2147483648f,
                    _ => 0f
                };
            }
            mono /= format.Channels;
            while (_nextOutputPosition <= _inputPosition)
            {
                var fraction = _nextOutputPosition - (_inputPosition - 1);
                var sample = _lastSample + (mono - _lastSample) * (float)Math.Clamp(fraction, 0, 1);
                var pcm = (short)Math.Clamp(sample * 32767, short.MinValue, short.MaxValue);
                _pcm.Add((byte)(pcm & 0xff));
                _pcm.Add((byte)((pcm >> 8) & 0xff));
                _nextOutputPosition += step;
            }
            _lastSample = mono;
            _inputPosition++;
        }
        // Tencent's 16 kHz PCM stream accepts 200 ms frames (6400 bytes).
        while (_pcm.Count >= 6400)
        {
            Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());
            PcmReady?.Invoke(_pcm.GetRange(0, 6400).ToArray());
            _pcm.RemoveRange(0, 6400);
        }
    }

    public void Stop()
    {
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        if (_capture is null) return;
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _capture = null;
        _pcm.Clear();
    }

    public void Dispose() => Stop();
}
