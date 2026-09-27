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
    private sealed record SpeechTurnOption(Guid? TurnId, string Label);
    private readonly Storage _storage;
    private readonly AppData _data;
    private readonly AudioCapture _capture = new();
    private readonly TencentAsr _asr = new();
    private readonly AudioCapture _microphoneCapture = new(microphone: true);
    private readonly TencentAsr _microphoneAsr = new();
    private readonly DeepSeekClient _deepSeek = new();
    private readonly DispatcherTimer _settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _silencePauseTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan SilencePauseDelay = TimeSpan.FromMinutes(3);
    private CancellationTokenSource? _answerToken;
    private InterviewTurn? _activeTurn;
    private InterviewTurn? _pendingQuestionTurn;
    private Guid? _speechTargetTurnId;
    private byte[]? _pendingScreenImage;
    private bool _startNewQuestionOnNextSentence;
    private bool _listening;
    private bool _microphoneRecording;
    private bool _closing;
    private bool _usageLimitStopping;
    private bool _refreshingHistory;
    private int _playbackCountedFrames;
    private int _microphoneCountedFrames;
    private int _playbackUsageGeneration;
    private int _microphoneUsageGeneration;
    private DateTime _usageSavedAtUtc = DateTime.MinValue;
    private Guid? _usageWarnedSessionId;
    private CancellationTokenSource? _diagnosticToken;
    private Task? _diagnosticTask;
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
        InterviewMemoryCheck.IsChecked = _data.UseInterviewMemory;
        InterviewMemoryCheck.Checked += InterviewMemoryCheck_Changed;
        InterviewMemoryCheck.Unchecked += InterviewMemoryCheck_Changed;
        RecordMyVoiceCheck.IsChecked = _data.RecordMyVoice;
        RecordMyVoiceCheck.Checked += RecordMyVoiceCheck_Changed;
        RecordMyVoiceCheck.Unchecked += RecordMyVoiceCheck_Changed;
        AsrLimitCombo.SelectedItem = AsrLimitCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => (string?)item.Tag == _data.AsrLimitMinutes.ToString())
            ?? AsrLimitCombo.Items[2];
        AsrLimitCombo.SelectionChanged += AsrLimitCombo_SelectionChanged;
        RefreshAnswerRules();
        RefreshDocumentGroups();
        RefreshSessionViews(_data.ActiveSessionId);
        RefreshDevices();
        RefreshMicrophones();
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
        _asr.Progress += (sent, received) =>
        {
            var generation = Volatile.Read(ref _playbackUsageGeneration);
            Dispatcher.BeginInvoke(() =>
            {
                if (generation != _playbackUsageGeneration) return;
                RecordAsrUsage(microphone: false, sent);
                if (_listening) AsrHintText.Text = $"腾讯云识别：已发送 {sent / 5d:F0} 秒（有声 {Volatile.Read(ref _audibleFrames) / 5d:F1} 秒）· 收到 {received} 条文字结果";
            });
        };
        _asr.Error += error => Dispatcher.BeginInvoke(() => _ = HandleAsrErrorAsync(error));
        _microphoneCapture.PcmReady += pcm =>
        {
            _microphoneAsr.TrySend(pcm);
            var peak = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
            if (peak >= 328) Interlocked.Exchange(ref _lastAudibleTimestamp, Stopwatch.GetTimestamp());
            Dispatcher.BeginInvoke(() =>
            {
                if (_microphoneRecording) MicrophoneLevelBar.Value = Math.Min(100, peak * 100d / 32768);
            });
        };
        _microphoneAsr.StableSentence += sentence => Dispatcher.BeginInvoke(() => OnMyStableSentence(sentence));
        _microphoneAsr.Progress += (sent, _) =>
        {
            var generation = Volatile.Read(ref _microphoneUsageGeneration);
            Dispatcher.BeginInvoke(() =>
            {
                if (generation == _microphoneUsageGeneration) RecordAsrUsage(microphone: true, sent);
            });
        };
        _microphoneAsr.InterimSentence += sentence => Dispatcher.BeginInvoke(() =>
        {
            if (_microphoneRecording) MicrophoneHintText.Text = "我的回答识别中：" + sentence;
        });
        _microphoneAsr.Error += error => Dispatcher.BeginInvoke(() => _ = HandleMicrophoneErrorAsync(error));
    }

    private void RefreshDevices()
    {
        var devices = AudioCapture.Devices();
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(x => x.ID == _data.SelectedDeviceId) ?? devices.FirstOrDefault();
    }

    private void RefreshMicrophones()
    {
        var devices = AudioCapture.Devices(microphone: true);
        MicrophoneCombo.ItemsSource = devices;
        MicrophoneCombo.SelectedItem = devices.FirstOrDefault(x => x.ID == _data.SelectedMicrophoneId) ?? devices.FirstOrDefault();
        if (devices.Count == 0) MicrophoneHintText.Text = "没有找到可用的麦克风，请检查 Windows 麦克风权限。";
    }

    private void RefreshMicrophones_Click(object sender, RoutedEventArgs e) => RefreshMicrophones();

    private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MicrophoneCombo.SelectedItem is not MMDevice device) return;
        _data.SelectedMicrophoneId = device.ID;
        _storage.Save(_data);
    }

    private void AsrLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!int.TryParse(AsrLimitCombo.SelectedValue?.ToString(), out var minutes)) return;
        _data.AsrLimitMinutes = minutes;
        _storage.Save(_data);
        _usageWarnedSessionId = null;
        UpdateUsageText();
        var session = _data.Sessions.FirstOrDefault(item => item.Id == _data.ActiveSessionId);
        if (_listening && session is not null && AsrUsage.LimitReached(session, minutes) && !_usageLimitStopping)
            _ = StopForUsageLimitAsync();
    }

    private void RecordAsrUsage(bool microphone, int sentFrames)
    {
        var added = microphone
            ? AsrUsage.NewFrames(sentFrames, ref _microphoneCountedFrames)
            : AsrUsage.NewFrames(sentFrames, ref _playbackCountedFrames);
        if (added == 0) return;
        var session = _data.Sessions.FirstOrDefault(item => item.Id == _data.ActiveSessionId);
        if (session is null) return;
        if (microphone) session.MicrophoneAsrFrames += added;
        else session.PlaybackAsrFrames += added;
        UpdateUsageText();
        if (DateTime.UtcNow - _usageSavedAtUtc >= TimeSpan.FromSeconds(5))
        {
            _storage.Save(_data);
            _usageSavedAtUtc = DateTime.UtcNow;
        }
        if (AsrUsage.NearLimit(session, _data.AsrLimitMinutes) && _usageWarnedSessionId != session.Id)
        {
            _usageWarnedSessionId = session.Id;
            StatusText.Text = "本场识别时长已达到上限的 80%。";
        }
        if (_listening && AsrUsage.LimitReached(session, _data.AsrLimitMinutes) && !_usageLimitStopping)
            _ = StopForUsageLimitAsync();
    }

    private void UpdateUsageText()
    {
        var session = _data.Sessions.FirstOrDefault(item => item.Id == _data.ActiveSessionId)
            ?? SessionCombo.SelectedItem as InterviewSession;
        var playback = session?.PlaybackAsrFrames ?? 0;
        var microphone = session?.MicrophoneAsrFrames ?? 0;
        var limit = _data.AsrLimitMinutes == 0 ? "不限" : _data.AsrLimitMinutes + " 分钟";
        var near = session is not null && AsrUsage.NearLimit(session, _data.AsrLimitMinutes);
        UsageSummaryText.Text = $"本场已发送 {AsrUsage.FormatFrames(playback + microphone)} / {limit}" +
            (near ? " · 接近上限" : "");
        UsageSummaryText.Foreground = near
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 185, 92))
            : (System.Windows.Media.Brush)FindResource("AccentBrush");
        UsageText.Text = $"本场已发送：面试官 {AsrUsage.FormatFrames(playback)} + 麦克风 {AsrUsage.FormatFrames(microphone)} = {AsrUsage.FormatFrames(playback + microphone)}；上限 {limit}" +
            (near ? " · 接近或达到上限" : "") +
            $"\n识别测试累计：{AsrUsage.FormatFrames(_data.DiagnosticPlaybackFrames + _data.DiagnosticMicrophoneFrames)}（单独统计）";
        UsageText.Foreground = near
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 185, 92))
            : (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private async Task StopForUsageLimitAsync()
    {
        _usageLimitStopping = true;
        StartButton.IsEnabled = false;
        try
        {
            await StopListeningAsync();
            StatusText.Text = "本场语音识别已达到累计上限，已自动暂停。可调高上限或开始新面试。";
        }
        catch (Exception ex) { StatusText.Text = "暂停识别失败：" + ex.Message; }
        finally { StartButton.IsEnabled = true; _usageLimitStopping = false; }
    }

    private async void RecordMyVoiceCheck_Changed(object sender, RoutedEventArgs e)
    {
        _data.RecordMyVoice = RecordMyVoiceCheck.IsChecked == true;
        _storage.Save(_data);
        if (!_listening) return;
        try
        {
            if (_data.RecordMyVoice) await StartMicrophoneAsync();
            else await StopMicrophoneAsync();
        }
        catch (Exception ex) { MicrophoneHintText.Text = "麦克风启动失败：" + ex.Message; }
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

    private async void RecognitionTest_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticToken is not null)
        {
            _diagnosticToken.Cancel();
            RecognitionTestButton.Content = "正在结束测试…";
            RecognitionTestButton.IsEnabled = false;
            return;
        }
        if (_listening) { StatusText.Text = "请先暂停监听，再运行双路识别测试。"; return; }
        if (string.IsNullOrWhiteSpace(AppIdBox.Text) || string.IsNullOrWhiteSpace(SecretIdBox.Text) ||
            string.IsNullOrWhiteSpace(SecretKeyBox.Password))
        {
            StatusText.Text = "请先在 API 设置中填写腾讯云识别凭据。";
            return;
        }
        SaveSettings();
        var run = new CancellationTokenSource();
        _diagnosticToken = run;
        StartButton.IsEnabled = DetectDeviceButton.IsEnabled = false;
        DeviceCombo.IsEnabled = MicrophoneCombo.IsEnabled = false;
        RefreshDevicesButton.IsEnabled = RefreshMicrophonesButton.IsEnabled = false;
        RecognitionTestButton.Content = "结束测试";
        PlaybackTestText.Text = "播放设备测试：正在连接…";
        MicrophoneTestText.Text = "麦克风测试：正在连接…";
        StatusText.Text = "双路识别测试中；请让对方和自己分别说话。";
        try
        {
            void ShowPlayback(string message) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_diagnosticToken, run)) PlaybackTestText.Text = message;
            });
            void ShowMicrophone(string message) => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_diagnosticToken, run)) MicrophoneTestText.Text = message;
            });
            var playback = RecognitionDiagnostic.RunChannelAsync(DeviceCombo.SelectedItem as MMDevice, false,
                AppIdBox.Text.Trim(), SecretIdBox.Text.Trim(), SecretKeyBox.Password, ShowPlayback, run.Token);
            var microphone = RecognitionDiagnostic.RunChannelAsync(MicrophoneCombo.SelectedItem as MMDevice, true,
                AppIdBox.Text.Trim(), SecretIdBox.Text.Trim(), SecretKeyBox.Password, ShowMicrophone, run.Token);
            var combined = Task.WhenAll(playback, microphone);
            _diagnosticTask = combined;
            var results = await combined;
            _data.DiagnosticPlaybackFrames += results[0].SentFrames;
            _data.DiagnosticMicrophoneFrames += results[1].SentFrames;
            _storage.Save(_data);
            UpdateUsageText();
            PlaybackTestText.Text = results[0].Summary("播放设备测试");
            MicrophoneTestText.Text = results[1].Summary("麦克风测试");
            StatusText.Text = "测试结束；请查看两路音量、文字与连接状态。";
        }
        catch (Exception ex) { StatusText.Text = "识别测试失败：" + ex.Message; }
        finally
        {
            _diagnosticTask = null;
            _diagnosticToken = null;
            run.Dispose();
            RecognitionTestButton.Content = "测试两路识别 15 秒";
            RecognitionTestButton.IsEnabled = true;
            StartButton.IsEnabled = DetectDeviceButton.IsEnabled = true;
            DeviceCombo.IsEnabled = MicrophoneCombo.IsEnabled = true;
            RefreshDevicesButton.IsEnabled = RefreshMicrophonesButton.IsEnabled = true;
        }
    }

    private async Task StartListeningAsync()
    {
        if (_diagnosticToken is not null) throw new InvalidOperationException("请等待识别测试结束。");
        if (DeviceCombo.SelectedItem is not MMDevice device) throw new InvalidOperationException("没有可用的播放设备。");
        var active = _data.Sessions.FirstOrDefault(session => session.Id == _data.ActiveSessionId);
        if (active is not null && AsrUsage.LimitReached(active, _data.AsrLimitMinutes))
            throw new InvalidOperationException("本场识别时长已到上限；请调高上限或开始新面试。");
        SaveSettings();
        StatusText.Text = "正在连接语音识别…";
        AsrHintText.Text = "腾讯云识别：正在连接…";
        Interlocked.Increment(ref _playbackUsageGeneration);
        await _asr.StartAsync(AppIdBox.Text.Trim(), SecretIdBox.Text.Trim(), SecretKeyBox.Password);
        _usageLimitStopping = false;
        _playbackCountedFrames = 0;
        var session = SessionHistory.EnsureActive(_data, DateTime.UtcNow);
        _storage.Save(_data);
        RefreshSessionViews(session.Id);
        Interlocked.Exchange(ref _audibleFrames, 0);
        Interlocked.Exchange(ref _lastAudibleTimestamp, Stopwatch.GetTimestamp());
        try { _capture.Start(device); }
        catch { await _asr.StopAsync(); throw; }
        _listening = true;
        if (RecordMyVoiceCheck.IsChecked == true)
        {
            try { await StartMicrophoneAsync(); }
            catch (Exception ex) { MicrophoneHintText.Text = "麦克风启动失败：" + ex.Message; }
        }
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
        RecordAsrUsage(microphone: false, _asr.SentFrames);
        await _asr.StopAsync();
        RecordAsrUsage(microphone: false, _asr.SentFrames);
        await StopMicrophoneAsync();
        _storage.Save(_data);
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

    private async Task StartMicrophoneAsync()
    {
        if (_microphoneRecording) return;
        var active = _data.Sessions.FirstOrDefault(session => session.Id == _data.ActiveSessionId);
        if (active is not null && AsrUsage.LimitReached(active, _data.AsrLimitMinutes))
            throw new InvalidOperationException("本场识别时长已到上限；请调高上限或开始新面试。");
        if (MicrophoneCombo.SelectedItem is not MMDevice device)
            throw new InvalidOperationException("没有可用的麦克风，请检查设备与权限。");
        MicrophoneHintText.Text = "正在连接麦克风识别…";
        Interlocked.Increment(ref _microphoneUsageGeneration);
        await _microphoneAsr.StartAsync(AppIdBox.Text.Trim(), SecretIdBox.Text.Trim(), SecretKeyBox.Password);
        _microphoneCountedFrames = 0;
        try { _microphoneCapture.Start(device); }
        catch { await _microphoneAsr.StopAsync(); throw; }
        _microphoneRecording = true;
        MicrophoneCombo.IsEnabled = false;
        RefreshMicrophonesButton.IsEnabled = false;
        MicrophoneHintText.Text = "正在记录我的口述；只保存文字，不保存音频。";
    }

    private async Task StopMicrophoneAsync()
    {
        _microphoneRecording = false;
        _microphoneCapture.Stop();
        RecordAsrUsage(microphone: true, _microphoneAsr.SentFrames);
        await _microphoneAsr.StopAsync();
        RecordAsrUsage(microphone: true, _microphoneAsr.SentFrames);
        MicrophoneLevelBar.Value = 0;
        MicrophoneCombo.IsEnabled = true;
        RefreshMicrophonesButton.IsEnabled = true;
        MicrophoneHintText.Text = "麦克风已暂停；只保存已识别的文字。";
    }

    private async Task HandleMicrophoneErrorAsync(string error)
    {
        await StopMicrophoneAsync();
        MicrophoneHintText.Text = error;
    }

    private void OnMyStableSentence(string sentence)
    {
        if (!_microphoneRecording || string.IsNullOrWhiteSpace(sentence)) return;
        var session = SessionHistory.EnsureActive(_data, DateTime.UtcNow);
        var linkedTurn = _data.Turns.FirstOrDefault(turn => turn.Id == _speechTargetTurnId &&
            turn.SessionId == session.Id && !turn.Ignored);
        var note = new InterviewSpeech { SessionId = session.Id, TurnId = linkedTurn?.Id, Text = sentence.Trim() };
        _data.Speeches.Add(note);
        _storage.Save(_data);
        RefreshSessionViews(session.Id);
        UpdateSelectedSpokenText();
        MicrophoneHintText.Text = $"已记录我的口述（本场 { _data.Speeches.Count(item => item.SessionId == session.Id) } 句）";
    }

    private void OnStableSentence(string sentence)
    {
        if (!_listening || string.IsNullOrWhiteSpace(sentence)) return;
        InterimText.Text = "";
        if (_startNewQuestionOnNextSentence)
        {
            QuestionBox.Clear();
            ClearPendingScreenshot();
            _pendingQuestionTurn = null;
            _startNewQuestionOnNextSentence = false;
        }
        QuestionBox.Text = string.IsNullOrWhiteSpace(QuestionBox.Text)
            ? sentence.Trim() : QuestionBox.Text.TrimEnd() + " " + sentence.Trim();
        QuestionBox.CaretIndex = QuestionBox.Text.Length;
        QuestionBox.ScrollToEnd();
        var session = SessionHistory.EnsureActive(_data, DateTime.UtcNow);
        if (_pendingQuestionTurn is null || _pendingQuestionTurn.SessionId != session.Id)
        {
            _pendingQuestionTurn = new InterviewTurn { SessionId = session.Id, Completed = false };
            _data.Turns.Insert(0, _pendingQuestionTurn);
        }
        _pendingQuestionTurn.Question = QuestionBox.Text.Trim();
        _activeTurn = _pendingQuestionTurn;
        _speechTargetTurnId = _pendingQuestionTurn.Id;
        _storage.Save(_data);
        RefreshSessionViews(session.Id);
    }

    private async Task GenerateAsync(string question, DateTime? questionEndedAtUtc = null, Guid? excludedTurnId = null)
    {
        var effort = SelectedReasoningEffort;
        var screenshot = _pendingScreenImage;
        var customInstructions = AnswerRules.Compose(_data.AnswerRules!);
        _answerToken?.Cancel();
        _answerToken?.Dispose();
        _answerToken = new CancellationTokenSource();
        var token = _answerToken.Token;
        var session = SessionHistory.EnsureActive(_data, DateTime.UtcNow);
        var pending = ReferenceEquals(_activeTurn, _pendingQuestionTurn) &&
            _pendingQuestionTurn?.SessionId == session.Id && !_pendingQuestionTurn.Ignored
            ? _pendingQuestionTurn : null;
        var memory = _data.UseInterviewMemory
            ? InterviewMemory.Prepare(_data.Turns, session.Id, question, excludedTurnId ?? pending?.Id, _data.Speeches)
            : new InterviewMemoryContext("", question);
        var turn = pending ?? new InterviewTurn { SessionId = session.Id, Completed = false };
        turn.Question = question;
        if (pending is null) _data.Turns.Insert(0, turn);
        _pendingQuestionTurn = null;
        _activeTurn = turn;
        _speechTargetTurnId = turn.Id;
        _storage.Save(_data);
        RefreshSessionViews(session.Id);
        AnswerText.Text = "正在生成…";
        var effortLabel = (ReasoningEffortCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? effort;
        StatusText.Text = effort == "none" ? "正在生成回答" : $"DeepSeek 正在思考（{effortLabel}）…";
        var context = DocumentService.RelevantContext(_data.Documents, memory.SearchQuery);
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
            }), token, screenshot, customInstructions, memory.Conversation);
            if (_activeTurn != turn) return;
            turn.FirstTextLatencyMs ??= latency;
            turn.Completed = !string.IsNullOrWhiteSpace(turn.Answer);
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

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0) { StatusText.Text = "请先填写问题。"; return; }
        _startNewQuestionOnNextSentence = true;
        _ = GenerateAsync(question, DateTime.UtcNow, _activeTurn?.Id);
    }

    private void InterviewMemoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        _data.UseInterviewMemory = InterviewMemoryCheck.IsChecked == true;
        _storage.Save(_data);
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        _speechTargetTurnId = null;
        _startNewQuestionOnNextSentence = false;
        _answerToken?.Cancel();
        if (_pendingQuestionTurn is not null) _pendingQuestionTurn.Ignored = true;
        else if (_activeTurn is not null) _activeTurn.Ignored = true;
        _pendingQuestionTurn = null;
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
        if (_refreshingHistory) return;
        if (HistoryList.SelectedItem is not InterviewTurn turn) return;
        _startNewQuestionOnNextSentence = true;
        ClearPendingScreenshot();
        QuestionBox.Text = turn.Question;
        AnswerText.Text = string.IsNullOrWhiteSpace(turn.Answer) ? "这道题已记录，尚未生成答案。" : turn.Answer;
        _activeTurn = turn;
        UpdateSelectedSpokenText();
    }

    private void UpdateSelectedSpokenText()
    {
        var turn = HistoryList.SelectedItem as InterviewTurn;
        var notes = turn is null ? [] : _data.Speeches.Where(note => note.TurnId == turn.Id)
            .OrderBy(note => note.AtUtc).Select(note => note.Text).ToList();
        SelectedSpokenText.Text = notes.Count == 0 ? "尚无录到的口述" : string.Join(" ", notes);
    }

    private void SpeechSessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sessionId = (SpeechSessionCombo.SelectedItem as InterviewSession)?.Id;
        var notes = _data.Speeches.Where(note => note.SessionId == sessionId)
            .OrderBy(note => note.AtUtc).ToList();
        SpeechList.ItemsSource = notes;
        SpeechCountText.Text = sessionId is null ? "请先选择面试场次" : $"本场已记录 {notes.Count} 句口述";
        SpeechEditor.Clear();
        SaveSpeechButton.IsEnabled = DeleteSpeechButton.IsEnabled = false;
    }

    private void SpeechList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var note = SpeechList.SelectedItem as InterviewSpeech;
        SpeechEditor.Text = note?.Text ?? "";
        var options = new List<SpeechTurnOption> { new(null, "未关联具体问题") };
        if (note is not null)
            options.AddRange(_data.Turns.Where(turn => turn.SessionId == note.SessionId && !turn.Ignored)
                .OrderBy(turn => turn.AtUtc)
                .Select(turn => new SpeechTurnOption(turn.Id,
                    $"{turn.AtUtc.ToLocalTime():HH:mm:ss} · {(turn.Question.Length <= 95 ? turn.Question : turn.Question[..94] + "…")}")));
        SpeechTurnCombo.ItemsSource = options;
        SpeechTurnCombo.SelectedItem = options.FirstOrDefault(option => option.TurnId == note?.TurnId) ?? options[0];
        SpeechTurnCombo.IsEnabled = note is not null;
        SaveSpeechButton.IsEnabled = DeleteSpeechButton.IsEnabled = note is not null;
    }

    private void SaveSpeech_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechList.SelectedItem is not InterviewSpeech note) return;
        var text = SpeechEditor.Text.Trim();
        if (text.Length == 0) { StatusText.Text = "转写不能为空；可用“删除这句”移除。"; return; }
        note.Text = text;
        note.TurnId = (SpeechTurnCombo.SelectedItem as SpeechTurnOption)?.TurnId;
        _storage.Save(_data);
        RefreshSpeechViews(note.SessionId);
        SpeechList.SelectedItem = note;
        UpdateSelectedSpokenText();
        StatusText.Text = "已保存口述文字与问题关联，下次生成回答时生效。";
    }

    private void DeleteSpeech_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechList.SelectedItem is not InterviewSpeech note) return;
        _data.Speeches.Remove(note);
        _storage.Save(_data);
        RefreshSpeechViews(note.SessionId);
        UpdateSelectedSpokenText();
        StatusText.Text = "已删除这句口述。";
    }

    private void RefreshSpeechViews(Guid? selectId = null)
    {
        var sessions = _data.Sessions.OrderByDescending(session => session.StartedAtUtc).ToList();
        SpeechSessionCombo.ItemsSource = sessions;
        SpeechSessionCombo.SelectedItem = sessions.FirstOrDefault(session => session.Id == selectId)
            ?? sessions.FirstOrDefault();
        SpeechSessionCombo_SelectionChanged(SpeechSessionCombo, null!);
    }

    private void JumpToLatestSession_Click(object sender, RoutedEventArgs e)
    {
        var latest = _data.Sessions.OrderByDescending(session => session.StartedAtUtc).FirstOrDefault();
        if (latest is null) return;
        SessionCombo.SelectedItem = SessionCombo.Items.Cast<InterviewSession>()
            .FirstOrDefault(session => session.Id == latest.Id);
        SpeechSessionCombo.SelectedItem = SpeechSessionCombo.Items.Cast<InterviewSession>()
            .FirstOrDefault(session => session.Id == latest.Id);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        _answerToken?.Cancel();
        _speechTargetTurnId = null;
        _activeTurn = null;
        _pendingQuestionTurn = null;
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

    private async void EndSession_Click(object sender, RoutedEventArgs e)
    {
        if (_listening) await StopListeningAsync();
        _speechTargetTurnId = null;
        _pendingQuestionTurn = null;
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
        _data.Speeches.RemoveAll(note => note.SessionId == session.Id);
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
        var previouslySelected = HistoryList.SelectedItem as InterviewTurn;
        _refreshingHistory = true;
        try
        {
            HistoryList.ItemsSource = turns;
            if (previouslySelected is not null && turns.Contains(previouslySelected))
                HistoryList.SelectedItem = previouslySelected;
        }
        finally { _refreshingHistory = false; }
        HistoryCountText.Text = session is null ? "请先选择面试场次" : $"本场已记录 {turns.Count} 个问题";
        HistoryEmptyText.Visibility = turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectedSpokenText();
        UpdateLatencyStats();
        UpdateUsageText();
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
        SessionCombo_SelectionChanged(SessionCombo, null!);
        RefreshSpeechViews(selectId);
        var latest = sessions.FirstOrDefault();
        var questions = latest is null ? 0 : _data.Turns.Count(turn => turn.SessionId == latest.Id);
        var speeches = latest is null ? 0 : _data.Speeches.Count(note => note.SessionId == latest.Id);
        RecordSummaryText.Text = latest is null ? "还没有面试记录" : $"最近一场：{questions} 个问题 · {speeches} 句口述";
        RecordsExpander.Header = $"我的口述、资料与记录（{questions} 问 · {speeches} 句）";
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
        _data.UseInterviewMemory = InterviewMemoryCheck.IsChecked == true;
        _data.RecordMyVoice = RecordMyVoiceCheck.IsChecked == true;
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
        _diagnosticToken?.Cancel();
        if (_diagnosticTask is not null) try { await _diagnosticTask; } catch { }
        SaveSettings();
        _answerToken?.Cancel();
        await StopListeningAsync();
        _capture.Dispose();
        _microphoneCapture.Dispose();
        SessionHistory.End(_data, DateTime.UtcNow);
        _storage.Save(_data);
        Close();
    }
}
