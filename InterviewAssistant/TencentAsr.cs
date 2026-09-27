using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace InterviewAssistant;

public sealed class TencentAsr : IAsyncDisposable
{
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(30)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly Dictionary<int, string> _stable = [];
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _sender;
    private Task? _receiver;
    private TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sentFrames;
    private int _receivedTexts;
    public event Action<string>? StableSentence;
    public event Action<string>? InterimSentence;
    public event Action<string>? Error;
    public event Action<int, int>? Progress;
    public event Action<string>? ResponseShape;

    public async Task StartAsync(string appId, string secretId, string secretKey, CancellationToken cancellationToken = default)
    {
        if (_socket is not null) throw new InvalidOperationException("语音识别已启动。");
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("请先填写腾讯云 AppID、SecretID 和 SecretKey。");
        _stable.Clear();
        _sentFrames = _receivedTexts = 0;
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _socket = new ClientWebSocket();
        try
        {
            await _socket.ConnectAsync(BuildUri(appId, secretId, secretKey), _lifetime.Token);
            _receiver = ReceiveLoop(_lifetime.Token);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token);
            _sender = SendLoop(_lifetime.Token);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public bool TrySend(byte[] pcm) => _socket?.State == WebSocketState.Open && _audio.Writer.TryWrite(pcm);

    private async Task SendLoop(CancellationToken token)
    {
        try
        {
            await foreach (var pcm in _audio.Reader.ReadAllAsync(token))
            {
                await _socket!.SendAsync(pcm, WebSocketMessageType.Binary, true, token);
                var sent = Interlocked.Increment(ref _sentFrames);
                if (sent % 5 == 0) Progress?.Invoke(sent, Volatile.Read(ref _receivedTexts));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!token.IsCancellationRequested) { Error?.Invoke("发送音频失败：" + ex.Message); }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        var buffer = new byte[65536];
        try
        {
            while (_socket?.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (!token.IsCancellationRequested) Error?.Invoke("语音识别连接已关闭，请重新开始监听。");
                        return;
                    }
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var json = JsonDocument.Parse(stream.ToArray());
                var root = json.RootElement;
                ResponseShape?.Invoke(string.Join(", ", root.EnumerateObject().Select(p => p.Name + ":" + p.Value.ValueKind)));
                if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                {
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                    _ready.TrySetException(new InvalidOperationException(message));
                    Error?.Invoke("语音识别失败：" + message);
                    return;
                }
                if (root.TryGetProperty("voice_id", out _)) _ready.TrySetResult(true);
                foreach (var resultSentence in ParseSentences(root))
                {
                    var (id, type, sentence) = resultSentence;
                    if (!string.IsNullOrWhiteSpace(sentence))
                        Progress?.Invoke(Volatile.Read(ref _sentFrames), Interlocked.Increment(ref _receivedTexts));
                    if (type == 1 && id >= 0 && !_stable.ContainsKey(id))
                    {
                        _stable[id] = sentence;
                        StableSentence?.Invoke(sentence);
                    }
                    else if (type == 0) InterimSentence?.Invoke(sentence);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _ready.TrySetException(ex);
            Error?.Invoke("接收识别结果失败：" + ex.Message);
        }
    }

    public static IReadOnlyList<(int Id, int Type, string Text)> ParseSentences(JsonElement root)
    {
        if (root.TryGetProperty("result", out var legacyResult) && legacyResult.ValueKind == JsonValueKind.Object)
        {
            var id = legacyResult.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsedIndex) ? parsedIndex : -1;
            var sliceType = legacyResult.TryGetProperty("slice_type", out var sliceValue) && sliceValue.TryGetInt32(out var parsedSlice) ? parsedSlice : 0;
            var text = legacyResult.TryGetProperty("voice_text_str", out var voiceText) && voiceText.ValueKind == JsonValueKind.String
                ? voiceText.GetString() ?? "" : "";
            return string.IsNullOrWhiteSpace(text) ? [] : [(id, sliceType == 2 ? 1 : 0, text)];
        }
        if (!root.TryGetProperty("sentences", out var sentences)) return [];
        // The V2 API nests sentence_list inside sentences; older responses may use an array.
        if (sentences.ValueKind == JsonValueKind.Object &&
            sentences.TryGetProperty("sentence_list", out var list)) sentences = list;
        if (sentences.ValueKind != JsonValueKind.Array) return [];
        var result = new List<(int, int, string)>();
        foreach (var item in sentences.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.TryGetProperty("sentence_id", out var idValue) && idValue.TryGetInt32(out var parsedId) ? parsedId : -1;
            var type = item.TryGetProperty("sentence_type", out var typeValue) && typeValue.TryGetInt32(out var parsedType) ? parsedType : 0;
            var text = item.TryGetProperty("sentence", out var textValue) && textValue.ValueKind == JsonValueKind.String
                ? textValue.GetString() ?? "" : "";
            result.Add((id, type, text));
        }
        return result;
    }

    private static Uri BuildUri(string appId, string secretId, string secretKey)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine_model_type"] = "16k_zh_en_2.0",
            ["expired"] = (now + 3600).ToString(),
            ["needvad"] = "1",
            ["nonce"] = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(),
            ["secretid"] = secretId,
            ["timestamp"] = now.ToString(),
            ["voice_format"] = "1",
            ["voice_id"] = Guid.NewGuid().ToString()
        };
        var query = string.Join("&", values.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var path = $"asr.cloud.tencent.com/asr/v2/{appId}?{query}";
        var signature = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(path)));
        return new Uri($"wss://{path}&signature={Uri.EscapeDataString(signature)}");
    }

    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        if (_socket is not null)
        {
            _socket.Dispose();
            _socket = null;
        }
        if (_sender is not null) try { await _sender; } catch { }
        if (_receiver is not null) try { await _receiver; } catch { }
        _sender = _receiver = null;
        _lifetime?.Dispose();
        _lifetime = null;
        while (_audio.Reader.TryRead(out _)) { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
