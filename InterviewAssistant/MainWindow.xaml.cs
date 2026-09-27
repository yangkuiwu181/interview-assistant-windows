using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace InterviewAssistant;

public partial class MainWindow : Window
{
    private readonly Storage _storage;
    private readonly AppData _data;
    private readonly AudioCapture _capture = new();
    private readonly TencentAsr _asr = new();
    private readonly DeepSeekClient _deepSeek = new();
    private readonly DispatcherTimer _settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _silencePauseTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan SilencePauseDelay = TimeSpan.FromMinutes(3);
    private CancellationTokenSource? _answerToken;
    private InterviewTurn? _activeTurn;
    private byte[]? _pendingScreenImage;
    private bool _startNewQuestionOnNextSentence;
    private bool _listening;
    private bool _closing;
    private long _lastAudibleTimestamp;
    private int _audibleFrames;

    public MainWindow() : this(new Storage()) { }

    public MainWindow(Storage storage)
    {
        _storage = storage;
        InitializeComponent();
        _data = _storage.Load();
        var migrated = SessionHistory.MigrateLegacyTurns(_data) | AnswerRules.Migrate(_data);
        if (migrated) _storage.Save(_data);
        AppIdBox.Text = _data.TencentAppId;
        SecretIdBox.Text = _data.TencentSecretId;
        SecretKeyBox.Password = _storage.ReadSecret("tencent");
        DeepSeekKeyBox.Password = _storage.ReadSecret("deepseek");
        ReasoningEffortCombo.SelectedItem = ReasoningEffortCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => (string?)item.Tag == _data.DeepSeekReasoningEffort)
            ?? ReasoningEffortCombo.Items[0];
        ReasoningEffortCombo.SelectionChanged += (_, _) =>
        {
            _data.DeepSeekReasoningEffort = SelectedReasoningEffort;
            _storage.Save(_data);
        };
        RefreshAnswerRules();
        RefreshDocumentGroups();
        RefreshSessionViews(_data.ActiveSessionId);
        RefreshDevices();
        AppIdBox.TextChanged += Settings_Changed;
        SecretIdBox.TextChanged += Settings_Changed;
        SecretKeyBox.PasswordChanged += Settings_Changed;
        DeepSeekKeyBox.PasswordChanged += Settings_Changed;
        _settingsTimer.Tick += (_, _) => { _settingsTimer.Stop(); SaveSettings(); };
        _silencePauseTimer.Tick += SilencePauseTimer_Tick;
        _capture.PcmReady += pcm =>
        {
            _asr.TrySend(pcm);
            var peak = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
            if (peak >= 328)
            {
                Interlocked.Increment(ref _audibleFrames);
                Interlocked.Exchange(ref _lastAudibleTimestamp, Stopwatch.GetTimestamp());
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (!_listening) return;
                var level = Math.Min(100, peak * 100d / 32768);
                AudioLevelBar.Value = level;
                var silence = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudibleTimestamp));
                AudioHintText.Text = silence > TimeSpan.FromSeconds(4)
                    ? $"声音检测：静音 {silence.Minutes:00}:{silence.Seconds:00} / 03:00 · 本次有声 {Volatile.Read(ref _audibleFrames) / 5d:F1} 秒"
                    : $"声音检测：收到声音（音量 {level:F0}%）· 本次有声 {Volatile.Read(ref _audibleFrames) / 5d:F1} 秒";
            });
        };
        _asr.StableSentence += sentence => Dispatcher.BeginInvoke(() => OnStableSentence(sentence));
        _asr.InterimSentence += sentence => Dispatcher.BeginInvoke(() => InterimText.Text = sentence);
        _asr.Progress += (sent, received) => Dispatcher.BeginInvoke(() =>
        {
            if (_listening) AsrHintText.Text = $"腾讯云识别：已发送 {sent / 5d:F0} 秒（有声 {Volatile.Read(ref _audibleFrames) / 5d:F1} 秒）· 收到 {received} 条文字结果";
        });
        _asr.Error += error => Dispatcher.BeginInvoke(() => _ = HandleAsrErrorAsync(error));
    }

    private void RefreshDevices()
    {
        var devices = AudioCapture.Devices();
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(x => x.ID == _data.SelectedDeviceId) ?? devices.FirstOrDefault();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private async void DetectDevice_Click(object sender, RoutedEventArgs e)
    {
        var resumeListening = _listening;
        if (resumeListening) await StopListeningAsync();
        var devices = AudioCapture.Devices();
        if (devices.Count == 0) { DeviceHintText.Text = "没有找到可用的播放设备。"; return; }
        DetectDeviceButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        DeviceHintText.Text = "正在检测 3 秒，请让微信通话对方持续说话…";
        var peaks = devices.ToDictionary(device => device.ID, _ => 0d);
        try
        {
            for (var i = 0; i < 30; i++)
            {
                foreach (var device in devices)
                {
                    try { peaks[device.ID] = Math.Max(peaks[device.ID], device.AudioMeterInformation.MasterPeakValue); }
                    catch { /* A device may disappear during a call. */ }
                }
                await Task.Delay(100);
                if (_closing) return;
            }
            var strongest = devices.OrderByDescending(device => peaks[device.ID]).First();
            var level = peaks[strongest.ID];
            if (level < 0.015)
            {
                DeviceHintText.Text = "没有检测到播放声音。请让对方说话，并确认微信没有静音。";
                return;
            }
            RefreshDevices();
            DeviceCombo.SelectedItem = ((IEnumerable<MMDevice>)DeviceCombo.ItemsSource)
                .FirstOrDefault(device => device.ID == strongest.ID);
            DeviceHintText.Text = $"已选中：{strongest.FriendlyName}（峰值 {level:P0}）。";
            if (resumeListening)
            {
                await StartListeningAsync();
                DeviceHintText.Text += " 已自动恢复监听。";
            }
            else DeviceHintText.Text += " 现在点“开始监听”。";
        }
        catch (Exception ex)
        {
            DeviceHintText.Text = "设备检测或恢复监听失败：" + ex.Message;
            StatusText.Text = "已暂停，请检查提示后重试。";
        }
        finally { DetectDeviceButton.IsEnabled = StartButton.IsEnabled = true; }
    }
    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is MMDevice device)
        {
            _data.SelectedDeviceId = device.ID;
            _storage.Save(_data);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            if (_listening) await StopListeningAsync();
            else await StartListeningAsync();
        }
        catch (Exception ex) { StatusText.Text = "启动失败：" + ex.Message; }
        finally { StartButton.IsEnabled = true; }
    }

    private async Task StartListeningAsync()
    {
        if (DeviceCombo.SelectedItem is not MMDevice device) throw new InvalidOperationException("没有可用的播放设备。");
        SaveSettings();
        StatusText.Text = "正在连接语音识别…";
        AsrHintText.Text = "腾讯云识别：正在连接…";
        await _asr.StartAsync(AppIdBox.Text.Trim(), SecretIdBox.Text.Trim(), SecretKeyBox.Password);
        Interlocked.Exchange(ref _audibleFrames, 0);
        Interlocked.Exchange(ref _lastAudibleTimestamp, Stopwatch.GetTimestamp());
        try { _capture.Start(device); }
        catch { await _asr.StopAsync(); throw; }
        _listening = true;
        _silencePauseTimer.Start();
        AsrHintText.Text = "腾讯云识别：已连接，等待识别文字";
        DeviceCombo.IsEnabled = false;
        RefreshDevicesButton.IsEnabled = false;
        DetectDeviceButton.IsEnabled = false;
        StartButton.Content = "暂停监听";
        StartButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(163, 101, 57));
        StartButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(215, 151, 91));
        StatusText.Text = "正在监听电脑播放声音";
    }

    private async Task StopListeningAsync()
    {
        _silencePauseTimer.Stop();
        _listening = false;
        _capture.Stop();
        await _asr.StopAsync();
        AsrHintText.Text = "腾讯云识别：已暂停";
        AudioLevelBar.Value = 0;
        AudioHintText.Text = "声音检测：已暂停";
        DeviceCombo.IsEnabled = true;
        RefreshDevicesButton.IsEnabled = true;
        DetectDeviceButton.IsEnabled = true;
        StartButton.Content = "开始监听";
        StartButton.Background = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
        StartButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 169, 165));
        if (!_closing) StatusText.Text = "已暂停";
    }

    private async void SilencePauseTimer_Tick(object? sender, EventArgs e)
    {
        if (!_listening || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudibleTimestamp)) < SilencePauseDelay) return;
        _silencePauseTimer.Stop();
        StartButton.IsEnabled = false;
        try
        {
            await StopListeningAsync();
            AudioHintText.Text = "声音检测：连续 3 分钟静音，已自动暂停";
            StatusText.Text = "已自动暂停；需要时点击“开始监听”。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "自动暂停失败：" + ex.Message;
        }
        finally { StartButton.IsEnabled = true; }
    }

    private async Task HandleAsrErrorAsync(string error)
    {
        await StopListeningAsync();
        StatusText.Text = error;
    }

    private void OnStableSentence(string sentence)
    {
        if (!_listening || string.IsNullOrWhiteSpace(sentence)) return;
        InterimText.Text = "";
        if (_startNewQuestionOnNextSentence)
        {
            QuestionBox.Clear();
            ClearPendingScreenshot();
            _startNewQuestionOnNextSentence = false;
        }
        QuestionBox.Text = string.IsNullOrWhiteSpace(QuestionBox.Text)
            ? sentence.Trim() : QuestionBox.Text.TrimEnd() + " " + sentence.Trim();
        QuestionBox.CaretIndex = QuestionBox.Text.Length;
        QuestionBox.ScrollToEnd();
    }

    private async Task GenerateAsync(string question, DateTime? questionEndedAtUtc = null)
    {
        var effort = SelectedReasoningEffort;
        var screenshot = _pendingScreenImage;
        var customInstructions = AnswerRules.Compose(_data.AnswerRules!);
        _answerToken?.Cancel();
        _answerToken?.Dispose();
        _answerToken = new CancellationTokenSource();
        var token = _answerToken.Token;
        var session = SessionHistory.EnsureActive(_data, DateTime.UtcNow);
        var turn = new InterviewTurn { Question = question, SessionId = session.Id };
        _activeTurn = turn;
        _data.Turns.Insert(0, turn);
        _storage.Save(_data);
        RefreshSessionViews(session.Id);
        AnswerText.Text = "正在生成…";
        var effortLabel = (ReasoningEffortCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? effort;
        StatusText.Text = effort == "none" ? "正在生成回答" : $"DeepSeek 正在思考（{effortLabel}）…";
        var context = DocumentService.RelevantContext(_data.Documents, question);
        var first = true;
        try
        {
            var latency = await _deepSeek.StreamAnswerAsync(DeepSeekKeyBox.Password, question, context, effort, delta => Dispatcher.Invoke(() =>
            {
                if (_activeTurn != turn) return;
                if (first)
                {
                    AnswerText.Text = "";
                    StatusText.Text = "回答正在显示…";
                    first = false;
                    if (questionEndedAtUtc.HasValue)
                        turn.FirstTextLatencyMs = (DateTime.UtcNow - questionEndedAtUtc.Value).TotalMilliseconds;
                }
                turn.Answer += delta;
                AnswerText.Text += delta;
            }), token, screenshot, customInstructions);
            if (_activeTurn != turn) return;
            turn.FirstTextLatencyMs ??= latency;
            StatusText.Text = turn.FirstTextLatencyMs.HasValue ? $"回答完成 · 首段 {turn.FirstTextLatencyMs.Value / 1000:F1} 秒" : "回答完成（无正文）";
            _storage.Save(_data);
            UpdateLatencyStats();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            StatusText.Text = "生成超时，请检查网络后重新生成。";
            _storage.Save(_data);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_activeTurn != turn) return;
            StatusText.Text = "生成失败：" + ex.Message;
            if (first) AnswerText.Text = "生成失败，请检查网络和 API 设置，然后重新生成。";
            _storage.Save(_data);
        }
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0) { StatusText.Text = "请先填写问题。"; return; }
        _startNewQuestionOnNextSentence = true;
        _ = GenerateAsync(question, DateTime.UtcNow);
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e) => Generate_Click(sender, e);

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        _startNewQuestionOnNextSentence = false;
        _answerToken?.Cancel();
        if (_activeTurn is not null) _activeTurn.Ignored = true;
        _storage.Save(_data);
        QuestionBox.Clear();
        ClearPendingScreenshot();
        InterimText.Text = "";
        AnswerText.Text = "已忽略本次问题。";
        StatusText.Text = _listening ? "正在监听电脑播放声音" : "已暂停";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_activeTurn?.Answer)) Clipboard.SetText(_activeTurn.Answer);
    }

    private async void CaptureScreen_Click(object sender, RoutedEventArgs e)
    {
        CaptureScreenButton.IsEnabled = false;
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        Hide();
        try
        {
            await Task.Delay(220);
            var picker = new ScreenCaptureWindow(screen);
            if (picker.ShowDialog() != true || picker.SelectedPng is not { } png) return;
            if (png.Length > 32 * 1024 * 1024)
            {
                StatusText.Text = "截图过大，请框选更小的题目区域。";
                return;
            }
            _pendingScreenImage = png;
            var preview = new BitmapImage();
            preview.BeginInit();
            preview.CacheOption = BitmapCacheOption.OnLoad;
            preview.StreamSource = new MemoryStream(png);
            preview.EndInit();
            preview.Freeze();
            ScreenshotPreview.Source = preview;
            ScreenshotPreview.Visibility = Visibility.Visible;
            ClearScreenshotButton.IsEnabled = true;
            ScreenshotHintText.Text = "已框选屏幕题目。检查或补充问题，点击“生成回答”时会把截图发给 DeepSeek；本机不保存截图。";
            if (string.IsNullOrWhiteSpace(QuestionBox.Text) || _startNewQuestionOnNextSentence)
                QuestionBox.Text = "请根据截图解答其中的题目；如果是 SQL 题，给出可执行 SQL 和简要思路。";
            _startNewQuestionOnNextSentence = false;
            StatusText.Text = "截图已添加，等待你点击“生成回答”。";
        }
        catch (Exception ex) { StatusText.Text = "截屏失败：" + ex.Message; }
        finally
        {
            Show();
            Activate();
            CaptureScreenButton.IsEnabled = true;
        }
    }

    private void ClearScreenshot_Click(object sender, RoutedEventArgs e) => ClearPendingScreenshot();

    private void ClearPendingScreenshot()
    {
        _pendingScreenImage = null;
        ScreenshotPreview.Source = null;
        ScreenshotPreview.Visibility = Visibility.Collapsed;
        ClearScreenshotButton.IsEnabled = false;
        ScreenshotHintText.Text = "需要看投屏题目时，点击截取屏幕题目并框选；截图仅在点击生成回答时发送。";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<DocumentKind>(button.Tag?.ToString(), out var kind)) return;
        var dialog = new OpenFileDialog { Filter = "资料文件|*.pdf;*.docx;*.txt", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var imported = 0;
        foreach (var path in dialog.FileNames)
        {
            try
            {
                var content = DocumentService.ExtractText(path).Trim();
                if (content.Length == 0) throw new InvalidDataException("文件中没有可提取的文字。扫描版 PDF 需要先进行 OCR。");
                _data.Documents.Add(new SourceDocument { Kind = kind, Name = Path.GetFileName(path), Text = content });
                imported++;
            }
            catch (Exception ex) { MessageBox.Show($"无法导入 {Path.GetFileName(path)}：{ex.Message}", "导入失败"); }
        }
        _storage.Save(_data);
        RefreshDocumentGroups();
        if (imported > 0) SectionFor(kind).IsExpanded = true;
        StatusText.Text = $"已导入 {imported} 份资料";
    }

    private void DeleteDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<DocumentKind>(button.Tag?.ToString(), out var kind)) return;
        if (ListFor(kind).SelectedItem is not SourceDocument document) return;
        _data.Documents.Remove(document);
        _storage.Save(_data);
        RefreshDocumentGroups();
    }

    private void DocumentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ResumeList)) ResumeDeleteButton.IsEnabled = ResumeList.SelectedItem is not null;
        else if (ReferenceEquals(sender, JobList)) JobDeleteButton.IsEnabled = JobList.SelectedItem is not null;
        else if (ReferenceEquals(sender, KnowledgeList)) KnowledgeDeleteButton.IsEnabled = KnowledgeList.SelectedItem is not null;
    }

    private void RefreshDocumentGroups()
    {
        RefreshDocumentGroup(DocumentKind.Resume, "简历", ResumeSection, ResumeList, ResumeEmpty, ResumeDeleteButton);
        RefreshDocumentGroup(DocumentKind.JobDescription, "岗位 JD", JobSection, JobList, JobEmpty, JobDeleteButton);
        RefreshDocumentGroup(DocumentKind.Knowledge, "自定义资料", KnowledgeSection, KnowledgeList, KnowledgeEmpty, KnowledgeDeleteButton);
    }

    private void RefreshDocumentGroup(DocumentKind kind, string title, Expander section, ListBox list, TextBlock empty, Button delete)
    {
        var documents = _data.Documents.Where(doc => doc.Kind == kind).ToList();
        section.Header = $"{title}（{documents.Count}）";
        list.ItemsSource = documents;
        list.Visibility = documents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        delete.IsEnabled = false;
    }

    private Expander SectionFor(DocumentKind kind) => kind switch
    {
        DocumentKind.Resume => ResumeSection,
        DocumentKind.JobDescription => JobSection,
        _ => KnowledgeSection
    };

    private ListBox ListFor(DocumentKind kind) => kind switch
    {
        DocumentKind.Resume => ResumeList,
        DocumentKind.JobDescription => JobList,
        _ => KnowledgeList
    };

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not InterviewTurn turn) return;
        _startNewQuestionOnNextSentence = true;
        ClearPendingScreenshot();
        QuestionBox.Text = turn.Question;
        AnswerText.Text = turn.Answer;
        _activeTurn = turn;
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        _answerToken?.Cancel();
        _activeTurn = null;
        QuestionBox.Clear();
        ClearPendingScreenshot();
        InterimText.Text = "";
        AnswerText.Text = "等面试官说完，点击“生成回答”后显示答案。";
        _startNewQuestionOnNextSentence = false;
        var session = SessionHistory.Start(_data, DateTime.UtcNow);
        _storage.Save(_data);
        RefreshSessionViews(session.Id);
        StatusText.Text = "已开始新面试";
    }

    private void EndSession_Click(object sender, RoutedEventArgs e)
    {
        SessionHistory.End(_data, DateTime.UtcNow);
        _storage.Save(_data);
        UpdateCurrentSessionText();
        StatusText.Text = "本场面试已结束；下次生成回答会建立新场次。";
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionCombo.SelectedItem is not InterviewSession session) return;
        var answer = MessageBox.Show($"确定删除“{session.Title}”及其中的全部问答记录吗？", "删除面试记录", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        if (_activeTurn?.SessionId == session.Id) _answerToken?.Cancel();
        if (_data.ActiveSessionId == session.Id) SessionHistory.End(_data, DateTime.UtcNow);
        _data.Turns.RemoveAll(turn => turn.SessionId == session.Id);
        _data.Sessions.Remove(session);
        _storage.Save(_data);
        _activeTurn = null;
        RefreshSessionViews();
    }

    private void SessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var session = SessionCombo.SelectedItem as InterviewSession;
        SessionTitleBox.Tag = session?.Id;
        SessionTitleBox.Text = session?.Title ?? "";
        SessionTitleBox.IsEnabled = session is not null;
        DeleteSessionButton.IsEnabled = session is not null;
        var turns = session is null ? [] : _data.Turns.Where(turn => turn.SessionId == session.Id).ToList();
        HistoryList.ItemsSource = turns;
        HistoryEmptyText.Visibility = turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLatencyStats();
    }

    private void SessionTitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SessionTitleBox.Tag is not Guid id) return;
        var session = _data.Sessions.FirstOrDefault(item => item.Id == id);
        if (session is null) return;
        var title = SessionTitleBox.Text.Trim();
        if (title.Length == 0 || title == session.Title) return;
        session.Title = title;
        _storage.Save(_data);
        RefreshSessionViews(id);
    }

    private void RefreshSessionViews(Guid? selectId = null)
    {
        var sessions = _data.Sessions.OrderByDescending(session => session.StartedAtUtc).ToList();
        SessionCombo.ItemsSource = sessions;
        SessionCombo.SelectedItem = sessions.FirstOrDefault(session => session.Id == selectId) ?? sessions.FirstOrDefault();
        UpdateCurrentSessionText();
    }

    private void UpdateCurrentSessionText()
    {
        var active = _data.Sessions.FirstOrDefault(session => session.Id == _data.ActiveSessionId);
        CurrentSessionText.Text = active is null ? "当前没有进行中的面试；生成回答时会自动建立新场次。" : $"正在记录：{active.Title}";
        EndSessionButton.IsEnabled = active is not null;
    }

    private void UpdateLatencyStats()
    {
        var selectedId = (SessionCombo.SelectedItem as InterviewSession)?.Id;
        var values = _data.Turns.Where(turn => turn.SessionId == selectedId && !turn.Ignored && turn.FirstTextLatencyMs.HasValue)
            .Select(turn => turn.FirstTextLatencyMs!.Value).Order().ToArray();
        if (values.Length == 0) { LatencyStatsText.Text = "首段延迟：暂无记录"; return; }
        var median = values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
        var p95 = values[(int)Math.Ceiling(values.Length * 0.95) - 1];
        LatencyStatsText.Text = $"首段延迟 {values.Length} 次 · 中位 {median / 1000:F1}s · P95 {p95 / 1000:F1}s";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) { SaveSettings(); StatusText.Text = "设置已保存"; }

    private void RefreshAnswerRules()
    {
        var rules = _data.AnswerRules!;
        for (var i = 0; i < rules.Count; i++)
        {
            rules[i].Position = i + 1;
            rules[i].TotalCount = rules.Count;
        }
        AnswerRulesItems.ItemsSource = null;
        AnswerRulesItems.ItemsSource = rules.ToList();
        RulesEmptyText.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RuleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: AnswerRule rule } box) return;
        rule.Text = box.Text;
        Settings_Changed(sender, e);
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new AnswerRule();
        _data.AnswerRules!.Add(rule);
        RefreshAnswerRules();
        SaveSettings();
        AnswerRulesItems.UpdateLayout();
        if (AnswerRulesItems.ItemContainerGenerator.ContainerFromItem(rule) is ContentPresenter item)
        {
            item.ApplyTemplate();
            (item.ContentTemplate?.FindName("RuleEditor", item) as TextBox)?.Focus();
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AnswerRule rule }) return;
        _data.AnswerRules!.Remove(rule);
        RefreshAnswerRules();
        SaveSettings();
    }

    private void MoveRuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(sender, -1);
    private void MoveRuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(sender, 1);

    private void MoveRule(object sender, int direction)
    {
        if (sender is not Button { DataContext: AnswerRule rule }) return;
        var rules = _data.AnswerRules!;
        var index = rules.IndexOf(rule);
        var next = index + direction;
        if (index < 0 || next < 0 || next >= rules.Count) return;
        (rules[index], rules[next]) = (rules[next], rules[index]);
        RefreshAnswerRules();
        SaveSettings();
    }

    private void RestoreRules_Click(object sender, RoutedEventArgs e)
    {
        _data.AnswerRules = AnswerRules.Defaults();
        RefreshAnswerRules();
        SaveSettings();
        StatusText.Text = "已恢复默认回答要求";
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private void SaveSettings()
    {
        _data.TencentAppId = AppIdBox.Text.Trim();
        _data.TencentSecretId = SecretIdBox.Text.Trim();
        _data.DeepSeekReasoningEffort = SelectedReasoningEffort;
        _data.AiAnswerInstructions = null;
        _storage.Save(_data);
        _storage.SaveSecret("tencent", SecretKeyBox.Password);
        _storage.SaveSecret("deepseek", DeepSeekKeyBox.Password);
    }

    private string SelectedReasoningEffort => ReasoningEffortCombo.SelectedValue?.ToString() ?? "none";

    private void PageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        PageScrollViewer.ScrollToVerticalOffset(PageScrollViewer.VerticalOffset - e.Delta / 120d * 72);
        e.Handled = true;
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e) => Topmost = TopmostCheck.IsChecked == true;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        _settingsTimer.Stop();
        SaveSettings();
        _answerToken?.Cancel();
        await StopListeningAsync();
        _capture.Dispose();
        SessionHistory.End(_data, DateTime.UtcNow);
        _storage.Save(_data);
        Close();
    }
}
