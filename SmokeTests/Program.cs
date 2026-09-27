using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using InterviewAssistant;
using System.Text.Json;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

var directory = Path.Combine(Path.GetTempPath(), "InterviewAssistantSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var txt = Path.Combine(directory, "resume.txt");
    File.WriteAllText(txt, "我负责支付系统的性能优化，接口延迟下降百分之三十。");
    Assert(DocumentService.ExtractText(txt).Contains("支付系统"), "TXT 导入");

    var docx = Path.Combine(directory, "job.docx");
    using (var document = WordprocessingDocument.Create(docx, WordprocessingDocumentType.Document))
    {
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(new Paragraph(new Run(new Text("岗位要求：熟悉性能优化")))));
        main.Document.Save();
    }
    Assert(DocumentService.ExtractText(docx).Contains("性能优化"), "DOCX 导入");

    var pdf = Path.Combine(directory, "notes.pdf");
    var builder = new PdfDocumentBuilder();
    var page = builder.AddPage(500, 500);
    var font = builder.AddStandard14Font(Standard14Font.Helvetica);
    page.AddText("Interview notes", 14, new PdfPoint(40, 450), font);
    File.WriteAllBytes(pdf, builder.Build());
    Assert(DocumentService.ExtractText(pdf).Contains("Interview notes"), "PDF 导入");

    var context = DocumentService.RelevantContext([
        new SourceDocument { Kind = DocumentKind.Resume, Name = "resume.txt", Text = DocumentService.ExtractText(txt) },
        new SourceDocument { Kind = DocumentKind.JobDescription, Name = "job.docx", Text = DocumentService.ExtractText(docx) }
    ], "请讲讲性能优化经历");
    Assert(context.Contains("支付系统") && context.Contains("岗位要求"), "资料检索");
    Assert(QuestionDetector.LooksLikePrompt("请介绍一下你负责的支付项目"), "中文提问触发");
    Assert(QuestionDetector.LooksLikePrompt("你怎么看 API latency？"), "中英混合提问触发");
    Assert(!QuestionDetector.LooksLikePrompt("今天的面试开始了。"), "普通陈述忽略");
    using (var response = JsonDocument.Parse("""{"code":0,"sentences":{"sentence_list":[{"sentence_id":1,"sentence_type":0,"sentence":"请介绍一下"},{"sentence_id":1,"sentence_type":1,"sentence":"请介绍一下你自己。"}]}}"""))
    {
        var sentences = TencentAsr.ParseSentences(response.RootElement);
        Assert(sentences.Count == 2 && sentences[0].Type == 0 && sentences[1].Type == 1 && sentences[1].Text == "请介绍一下你自己。", "腾讯云 V2 转写结果解析");
    }
    using (var response = JsonDocument.Parse("""{"code":0,"result":{"slice_type":2,"index":0,"voice_text_str":"请介绍一下你自己。"}}"""))
    {
        var sentences = TencentAsr.ParseSentences(response.RootElement);
        Assert(sentences.Count == 1 && sentences[0].Id == 0 && sentences[0].Type == 1 && sentences[0].Text == "请介绍一下你自己。", "腾讯云 result 转写结果解析");
    }
    var store = new Storage(Path.Combine(directory, "store"));
    store.Save(new AppData { DeepSeekReasoningEffort = "high", Documents = [new SourceDocument { Name = "resume.txt", Text = "支付系统" }] });
    Assert(store.Load().Documents.Single().Text == "支付系统" && store.Load().DeepSeekReasoningEffort == "high", "本地资料和思考强度持久化");
    var oldSettings = JsonSerializer.Deserialize<AppData>("{}")!;
    Assert(AnswerRules.Migrate(oldSettings) && oldSettings.AnswerRules!.Count == 3 &&
        oldSettings.AnswerRules[0].Text.Contains("优先使用资料里的原话回答"), "旧设置自动拆分预设回答要求");
    oldSettings.AnswerRules = null;
    oldSettings.AiAnswerInstructions = "回答控制在三句话以内。\n\n先给结论。";
    Assert(AnswerRules.Migrate(oldSettings) && oldSettings.AnswerRules!.Count == 2 &&
        oldSettings.AnswerRules[0].Text == "回答控制在三句话以内。" && oldSettings.AnswerRules[1].Text == "先给结论。", "已有自定义内容按段落拆分并保留顺序");
    store.Save(oldSettings);
    var restoredRules = store.Load().AnswerRules!;
    Assert(restoredRules.Count == 2 && restoredRules[0].Text == oldSettings.AnswerRules![0].Text && restoredRules[1].Text == oldSettings.AnswerRules[1].Text, "逐条回答要求持久化");
    var composedRules = AnswerRules.Compose(restoredRules);
    using (var customized = JsonDocument.Parse(DeepSeekClient.BuildRequestJson("请介绍自己", "", "none", customInstructions: composedRules)))
        Assert(customized.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains(composedRules), "逐条要求进入回答请求");
    var interviewData = new AppData { Turns = [new InterviewTurn { Question = "旧问题" }] };
    Assert(SessionHistory.MigrateLegacyTurns(interviewData), "旧问答迁移到独立场次");
    var legacyId = interviewData.Turns.Single().SessionId;
    Assert(interviewData.Sessions.Single().Id == legacyId && !SessionHistory.MigrateLegacyTurns(interviewData), "旧问答迁移不重复");
    var firstSession = SessionHistory.Start(interviewData, DateTime.UtcNow);
    interviewData.Turns.Add(new InterviewTurn { SessionId = firstSession.Id, Question = "第一场问题" });
    var secondSession = SessionHistory.Start(interviewData, DateTime.UtcNow.AddMinutes(1));
    interviewData.Turns.Add(new InterviewTurn { SessionId = secondSession.Id, Question = "第二场问题" });
    Assert(firstSession.EndedAtUtc.HasValue && interviewData.ActiveSessionId == secondSession.Id, "新面试结束上一场");
    Assert(interviewData.Turns.Count(turn => turn.SessionId == firstSession.Id) == 1 &&
        interviewData.Turns.Count(turn => turn.SessionId == secondSession.Id) == 1, "不同面试问答分开");
    SessionHistory.End(interviewData, DateTime.UtcNow.AddMinutes(2));
    store.Save(interviewData);
    var restored = store.Load();
    Assert(restored.Sessions.Count == 3 && restored.ActiveSessionId is null && restored.Turns.Single(turn => turn.Question == "旧问题").SessionId == legacyId, "面试场次持久化");
    var screenshot = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l3sAAAAASUVORK5CYII=");
    using (var vision = JsonDocument.Parse(DeepSeekClient.BuildRequestJson("请写 SQL", "", "high", screenshot)))
    {
        var messages = vision.RootElement.GetProperty("messages");
        var content = messages[1].GetProperty("content");
        Assert(content[0].GetProperty("type").GetString() == "text" &&
            content[1].GetProperty("type").GetString() == "image_url" &&
            content[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"), "截图发送格式");
    }
    using (var textOnly = JsonDocument.Parse(DeepSeekClient.BuildRequestJson("请介绍自己", "", "none")))
        Assert(textOnly.RootElement.GetProperty("messages")[1].GetProperty("content").ValueKind == JsonValueKind.String, "无截图时维持文字请求");
    var pixelRegion = ScreenCaptureWindow.ToPixelRect(new System.Windows.Point(50, 40), new System.Windows.Point(250, 140), 500, 250, 1000, 500);
    Assert(pixelRegion.X == 100 && pixelRegion.Y == 80 && pixelRegion.Width == 400 && pixelRegion.Height == 200, "框选区域像素映射");
    store.SaveSecret("test", "private-key-example");
    Assert(store.ReadSecret("test") == "private-key-example", "API 密钥加密回读");
    Assert(!File.ReadAllText(Path.Combine(store.DirectoryPath, "test.secret")).Contains("private-key-example"), "密钥未明文保存");
    Console.WriteLine("PASS: TXT、DOCX、PDF 导入、资料检索、提问判断和本地保存");

    if (args.Contains("--audio"))
    {
        var device = AudioCapture.Devices().FirstOrDefault() ?? throw new InvalidOperationException("没有可用的系统播放设备");
        using var capture = new AudioCapture();
        var packets = 0;
        capture.PcmReady += pcm => { Assert(pcm.Length == 6400, "PCM 分片长度"); Interlocked.Increment(ref packets); };
        capture.Start(device);
        await Task.Delay(1200);
        capture.Stop();
        Assert(packets >= 2, "系统音频采集或静音保活");
        Console.WriteLine($"PASS: WASAPI 设备 {device.FriendlyName}，收到 {packets} 个 PCM 分片");
    }

    var liveIndex = Array.IndexOf(args, "--asr-live");
    if (liveIndex >= 0)
    {
        Assert(liveIndex + 1 < args.Length, "测试 PCM 文件路径");
        var settings = new Storage();
        var saved = settings.Load();
        var key = settings.ReadSecret("tencent");
        Assert(!string.IsNullOrWhiteSpace(saved.TencentAppId) && !string.IsNullOrWhiteSpace(saved.TencentSecretId) && !string.IsNullOrWhiteSpace(key), "腾讯云凭据已配置");
        await using var asr = new TencentAsr();
        var stable = new List<string>();
        var interim = 0;
        var errors = new List<string>();
        asr.StableSentence += value => { lock (stable) stable.Add(value); };
        asr.InterimSentence += _ => Interlocked.Increment(ref interim);
        asr.Error += value => { lock (errors) errors.Add(value); };
        asr.ResponseShape += value => Console.WriteLine("ASR 响应字段：" + value);
        asr.Progress += (sent, received) => { if (sent % 10 == 0) Console.WriteLine($"ASR 音频帧 {sent}，文字 {received}"); };
        await asr.StartAsync(saved.TencentAppId, saved.TencentSecretId, key);
        var pcm = File.ReadAllBytes(args[liveIndex + 1]);
        for (var offset = 0; offset < pcm.Length; offset += 6400)
        {
            var frame = new byte[6400];
            Array.Copy(pcm, offset, frame, 0, Math.Min(frame.Length, pcm.Length - offset));
            Assert(asr.TrySend(frame), "测试音频已发送");
            await Task.Delay(200);
        }
        for (var i = 0; i < 15; i++)
        {
            Assert(asr.TrySend(new byte[6400]), "测试静音帧已发送");
            await Task.Delay(200);
        }
        await Task.Delay(1500);
        lock (errors) Assert(errors.Count == 0, "实时识别无报错：" + string.Join("; ", errors));
        lock (stable) Assert(stable.Count > 0, $"腾讯云返回确定文字（临时结果次数 {interim}）");
        Console.WriteLine($"PASS: 腾讯云端到端识别，确定文字 {string.Join(" ", stable)}");
    }
    if (args.Contains("--deepseek-live"))
    {
        var key = new Storage().ReadSecret("deepseek");
        Assert(!string.IsNullOrWhiteSpace(key), "DeepSeek 凭据已配置");
        var output = new System.Text.StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var effort = Array.IndexOf(args, "--deepseek-live") + 1 < args.Length ? args[Array.IndexOf(args, "--deepseek-live") + 1] : "none";
        var latency = await new DeepSeekClient().StreamAnswerAsync(key, "请介绍一下你自己。", "", effort, delta => output.Append(delta), timeout.Token);
        Assert(output.Length > 0 && latency.HasValue, "DeepSeek 流式回答");
        Console.WriteLine($"PASS: DeepSeek 流式回答，首段 {latency.GetValueOrDefault() / 1000:F1} 秒，正文 {output.Length} 字");
    }
}
finally
{
    var fullPath = Path.GetFullPath(directory);
    if (fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(fullPath, true);
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name + " 测试失败");
}
