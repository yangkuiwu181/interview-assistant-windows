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
    Exception? recordingError = null;
    var recordingThread = new Thread(() =>
    {
        try
        {
            var recordingStore = new Storage(Path.Combine(directory, "recording"));
            var window = new MainWindow(recordingStore);
            typeof(MainWindow).GetField("_listening", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(window, true);
            typeof(MainWindow).GetField("_microphoneRecording", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(window, true);
            var heard = typeof(MainWindow).GetMethod("OnStableSentence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            heard.Invoke(window, ["请介绍一下"]);
            heard.Invoke(window, ["你最近的项目。"]);
            var spoken = typeof(MainWindow).GetMethod("OnMyStableSentence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            spoken.Invoke(window, ["我最近负责支付系统。"]);
            var saved = recordingStore.Load();
            Assert(saved.Sessions.Count == 1 && saved.Turns.Count == 1 &&
                saved.Turns[0].Question.Contains("你最近的项目") && saved.Turns[0].Completed == false &&
                saved.Speeches.Count == 1 && saved.Speeches[0].TurnId == saved.Turns[0].Id,
                "识别到问题和我的口述后无需生成答案也会按场次保存");
            var grouped = InterviewRecords.ForSession(saved, saved.Sessions[0].Id);
            Assert(grouped.Count == 1 && grouped[0].SpokenAnswer.Contains("我最近负责支付系统") &&
                InterviewRecords.Unlinked(saved, saved.Sessions[0].Id).Count == 0,
                "面试记录把实际口述放到对应问题下");
            heard.Invoke(window, ["那你后来怎么优化的？"]);
            saved = recordingStore.Load();
            grouped = InterviewRecords.ForSession(saved, saved.Sessions[0].Id);
            Assert(grouped.Count == 2 && grouped[0].SpokenAnswer.Contains("支付系统") &&
                grouped[1].Question.Contains("后来怎么优化") &&
                grouped[1].SpokenAnswer == "尚未录到我的回答",
                "我的回答结束后下一次面试官提问单独成题");
        }
        catch (Exception ex) { recordingError = ex; }
    });
    recordingThread.SetApartmentState(ApartmentState.STA);
    recordingThread.Start();
    recordingThread.Join();
    if (recordingError is not null) throw recordingError;
    var legacyDirectory = Path.Combine(directory, "legacy-data");
    var sharedDirectory = Path.Combine(directory, "shared-data");
    Directory.CreateDirectory(legacyDirectory);
    File.WriteAllText(Path.Combine(legacyDirectory, "data.json"), JsonSerializer.Serialize(new AppData
    {
        Documents = [new SourceDocument { Kind = DocumentKind.Knowledge, Name = "notes.txt", Text = "原有资料" }]
    }));
    File.WriteAllBytes(Path.Combine(legacyDirectory, "deepseek.secret"), [1, 2, 3]);
    var migrate = typeof(Storage).GetMethod("CopyLegacyData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    migrate.Invoke(null, [legacyDirectory, sharedDirectory]);
    Assert(new Storage(sharedDirectory).Load().Documents.Single().Text == "原有资料" &&
        File.ReadAllBytes(Path.Combine(sharedDirectory, "deepseek.secret")).SequenceEqual(new byte[] { 1, 2, 3 }), "旧资料和密钥迁移");
    migrate.Invoke(null, [legacyDirectory, sharedDirectory]);
    Assert(new Storage(sharedDirectory).Load().Documents.Count == 1, "重复迁移不覆盖已有资料");
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
    var memorySession = Guid.NewGuid();
    var anotherSession = Guid.NewGuid();
    var memoryTurns = Enumerable.Range(1, 7).Select(index => new InterviewTurn
    {
        SessionId = memorySession, AtUtc = DateTime.UtcNow.AddMinutes(index),
        Question = $"问题{index}", Answer = $"回答{index}", Completed = index == 5 ? null : true
    }).ToList();
    memoryTurns.Add(new InterviewTurn { SessionId = anotherSession, Question = "别场问题", Answer = "别场回答", Completed = true });
    memoryTurns.Add(new InterviewTurn { SessionId = memorySession, AtUtc = DateTime.UtcNow.AddMinutes(10), Question = "忽略问题", Answer = "忽略回答", Ignored = true });
    memoryTurns.Add(new InterviewTurn { SessionId = memorySession, AtUtc = DateTime.UtcNow.AddMinutes(11), Question = "中断问题", Answer = "不完整回答", Completed = false });
    var memory = InterviewMemory.Prepare(memoryTurns, memorySession, "这个指标怎么算？");
    Assert(memory.Conversation.Contains("回答3") && memory.Conversation.Contains("回答7") &&
        memory.Conversation.Contains("问题1") && !memory.Conversation.Contains("回答1") &&
        !memory.Conversation.Contains("回答2") && !memory.Conversation.Contains("别场回答") &&
        !memory.Conversation.Contains("忽略回答") && !memory.Conversation.Contains("不完整回答") &&
        memory.SearchQuery.Contains("问题7"), "同场最近五轮、早期话题和资料检索补充");
    Assert(InterviewMemory.Prepare(memoryTurns, anotherSession, "新问题").Conversation.Contains("别场回答") &&
        !InterviewMemory.Prepare(memoryTurns, Guid.NewGuid(), "新问题").Conversation.Contains("回答7"), "不同面试场次隔离");
    Assert(!InterviewMemory.Prepare(memoryTurns, memorySession, "问题7", memoryTurns[6].Id).Conversation.Contains("回答7"), "重新生成不引用被替换的回答");
    var spokenNotes = new List<InterviewSpeech>
    {
        new() { SessionId = memorySession, TurnId = memoryTurns[6].Id, Text = "我实际说了支付接口耗时下降三成" },
        new() { SessionId = memorySession, TurnId = memoryTurns[6].Id, AtUtc = DateTime.UtcNow.AddSeconds(1), Text = "先检查慢查询" },
        new() { SessionId = anotherSession, TurnId = memoryTurns[6].Id, Text = "别场口述不能混入" },
        new() { SessionId = memorySession, Text = "未关联问题的口述" }
    };
    var spokenMemory = InterviewMemory.Prepare(memoryTurns, memorySession, "之后怎么做？", speeches: spokenNotes);
    Assert(spokenMemory.Conversation.Contains("我实际说了支付接口耗时下降三成 先检查慢查询") &&
        !spokenMemory.Conversation.Contains("回答7") && !spokenMemory.Conversation.Contains("别场口述") &&
        spokenMemory.Conversation.Contains("未关联问题的口述"), "实际口述优先、同场隔离及近期未关联口述");
    Assert(InterviewMemory.Prepare([], memorySession, "追问", speeches: spokenNotes).Conversation.Contains("未关联问题的口述"),
        "没有生成上一题稿子时仍可参考近期口述");
    var recordData = new AppData { Turns = memoryTurns, Speeches = spokenNotes };
    var unlinkedRecords = InterviewRecords.Unlinked(recordData, memorySession);
    Assert(unlinkedRecords.Count == 1 && unlinkedRecords[0].Text == "未关联问题的口述" &&
        InterviewRecords.ForSession(recordData, memorySession).All(item =>
            !item.SpokenAnswer.Contains("别场口述") && !item.SpokenAnswer.Contains("未关联问题的口述")),
        "未关联口述保留在本场单独区域，别场口述不会混入问题");
    spokenNotes[0].TurnId = memoryTurns[5].Id;
    spokenNotes[1].TurnId = memoryTurns[5].Id;
    var correctedLink = InterviewMemory.Prepare(memoryTurns, memorySession, "继续追问", speeches: spokenNotes);
    Assert(correctedLink.Conversation.Contains("候选人实际口述（语音转写）：我实际说了支付接口耗时下降三成 先检查慢查询") &&
        correctedLink.Conversation.Contains("助手之前的回答（未必实际说出）：回答7"), "修改口述关联后连续记忆使用新归属");
    var usageSession = new InterviewSession { PlaybackAsrFrames = 5 * 42 * 60, MicrophoneAsrFrames = 5 * 55 * 60 };
    var counted = 0;
    Assert(AsrUsage.NewFrames(15, ref counted) == 15 && AsrUsage.NewFrames(20, ref counted) == 5 &&
        AsrUsage.NewFrames(3, ref counted) == 0, "迟到的进度回调不重复计量");
    counted = 0;
    Assert(AsrUsage.NewFrames(3, ref counted) == 3, "新连接从零累计");
    Assert(AsrUsage.NearLimit(usageSession, 120) && !AsrUsage.LimitReached(usageSession, 120) &&
        AsrUsage.LimitReached(usageSession, 60) && !AsrUsage.LimitReached(usageSession, 0), "双路累计提醒和上限");
    Assert((await RecognitionDiagnostic.RunChannelAsync(null, false, "", "", "", _ => { }, CancellationToken.None)).Error.Contains("未找到设备"),
        "识别测试对无设备给出可恢复提示");
    store.Save(new AppData { Speeches = spokenNotes, RecordMyVoice = true });
    Assert(store.Load().Speeches.Count == 4 && store.Load().RecordMyVoice, "口述记录和开关持久化");
    Assert(!JsonSerializer.Deserialize<AppData>("{}")!.RecordMyVoice, "麦克风录制默认关闭");
    using (var withMemory = JsonDocument.Parse(DeepSeekClient.BuildRequestJson("追问", "真实资料", "none", interviewMemory: memory.Conversation)))
    {
        var messages = withMemory.RootElement.GetProperty("messages");
        Assert(messages[1].GetProperty("content").GetString()!.Contains("回答7") &&
            messages[0].GetProperty("content").GetString()!.Contains("之前由助手生成的答案未经验证"), "历史仅作为对话线索进入请求");
    }
    using (var withoutMemory = JsonDocument.Parse(DeepSeekClient.BuildRequestJson("追问", "真实资料", "none")))
        Assert(!withoutMemory.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("同场面试上下文"), "关闭记忆不发送历史问答");
    Assert(JsonSerializer.Deserialize<AppData>("{}")!.UseInterviewMemory &&
        !JsonSerializer.Deserialize<AppData>("{\"UseInterviewMemory\":false}")!.UseInterviewMemory, "上下文开关默认值和持久化");
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

    if (args.Contains("--microphone"))
    {
        var device = AudioCapture.Devices(microphone: true).FirstOrDefault()
            ?? throw new InvalidOperationException("没有可用的麦克风");
        using var capture = new AudioCapture(microphone: true);
        var packets = 0;
        capture.PcmReady += pcm => { Assert(pcm.Length == 6400, "麦克风 PCM 分片长度"); Interlocked.Increment(ref packets); };
        capture.Start(device);
        await Task.Delay(1500);
        capture.Stop();
        Assert(packets >= 2, "麦克风音频采集或静音保活");
        Console.WriteLine($"PASS: 麦克风 PCM 分片 {packets} 个");
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
