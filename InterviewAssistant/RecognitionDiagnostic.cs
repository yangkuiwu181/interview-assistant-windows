using NAudio.CoreAudioApi;

namespace InterviewAssistant;

public sealed record RecognitionDiagnosticResult(bool Connected, int PeakPercent, int SentFrames,
    string StableText, string InterimText, string Error)
{
    public string Summary(string label)
    {
        var transcription = !string.IsNullOrWhiteSpace(StableText) ? StableText :
            !string.IsNullOrWhiteSpace(InterimText) ? "临时文字：" + InterimText :
            PeakPercent < 1 ? "未检测到明显声音，请检查设备并在测试时说话" :
            "检测到声音但未收到文字，请检查云端识别状态与语音内容";
        var state = !string.IsNullOrWhiteSpace(Error) ? Error : transcription;
        return $"{label}：{(Connected ? "已连接" : "未连接")} · 峰值 {PeakPercent}% · 已发送 {SentFrames / 5d:F1} 秒 · {state}";
    }
}

public static class RecognitionDiagnostic
{
    public static async Task<RecognitionDiagnosticResult> RunChannelAsync(MMDevice? device, bool microphone,
        string appId, string secretId, string secretKey, Action<string> onProgress, CancellationToken token)
    {
        var label = microphone ? "麦克风" : "播放设备";
        if (device is null)
            return new RecognitionDiagnosticResult(false, 0, 0, "", "", "未找到设备，请先选择设备。");

        using var capture = new AudioCapture(microphone);
        await using var asr = new TencentAsr();
        var gate = new object();
        var stable = new List<string>();
        var interim = "";
        var error = "";
        var peak = 0;
        var maxPeak = 0;
        var packets = 0;
        var connected = false;
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Report()
        {
            string words;
            lock (gate) words = stable.Count > 0 ? string.Join(" ", stable) : interim;
            if (words.Length > 110) words = words[^110..];
            var status = !string.IsNullOrEmpty(error) ? error :
                string.IsNullOrWhiteSpace(words) ? "等待文字" : words;
            onProgress($"{label}：{(connected ? "已连接" : "连接中")} · 音量 {peak * 100d / 32768:F0}% · 已发送 {asr.SentFrames / 5d:F1} 秒 · {status}");
        }

        asr.StableSentence += sentence =>
        {
            lock (gate) { stable.Add(sentence); interim = ""; }
            Report();
        };
        asr.InterimSentence += sentence => { lock (gate) interim = sentence; Report(); };
        asr.Progress += (_, _) => Report();
        asr.Error += message => { error = message; failed.TrySetResult(true); Report(); };
        capture.PcmReady += pcm =>
        {
            asr.TrySend(pcm);
            var current = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
                current = Math.Max(current, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
            peak = current;
            maxPeak = Math.Max(maxPeak, current);
            if (++packets % 5 == 0) Report();
        };

        try
        {
            onProgress($"{label}：正在连接腾讯云…");
            await asr.StartAsync(appId, secretId, secretKey, token);
            connected = true;
            capture.Start(device);
            Report();
            var completed = await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(15), token), failed.Task);
            token.ThrowIfCancellationRequested();
            capture.Stop();
            if (completed != failed.Task)
            {
                // A short silence lets the service finalize the last spoken sentence.
                for (var i = 0; i < 5; i++)
                {
                    asr.TrySend(new byte[6400]);
                    await Task.Delay(200, token);
                }
                await Task.Delay(700, token);
            }
        }
        catch (OperationCanceledException) { error = "测试已取消"; }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            capture.Stop();
            await asr.StopAsync();
        }

        string finalText;
        string finalInterim;
        lock (gate) { finalText = string.Join(" ", stable); finalInterim = interim; }
        return new RecognitionDiagnosticResult(connected, Math.Min(100, (int)(maxPeak * 100d / 32768)),
            asr.SentFrames, finalText, finalInterim, error);
    }
}
