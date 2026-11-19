using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WristMusic.Services;

namespace WristMusic;

public sealed class MainViewModel : Observable, IDisposable
{
    public PlayerService Players { get; } = new();
    public MediaService Media { get; }
    private readonly VolumeService _volumeService = new();
    private readonly SettingsStore _store;
    private readonly IAutoStartClient _autoStart;
    public UserSettings Settings { get; }
    public ObservableCollection<AppEntry> Sources { get; }
    public ActionCommand MediaCommand { get; }
    public ActionCommand OpenCommand { get; }
    public ActionCommand CloseCommand { get; }
    public ActionCommand ConfigureCommand { get; }
    public ActionCommand PageCommand { get; }
    public ActionCommand WristCommand { get; }
    public ActionCommand ResetCommand { get; }
    public ActionCommand ReconnectCommand { get; }
    public ActionCommand RefreshSteamVrStartupCommand { get; }
    public ActionCommand RepairSteamVrStartupCommand { get; }
    public ActionCommand InstallNeteaseBridgeCommand { get; }
    public ActionCommand NeteaseBridgeGuideCommand { get; }
    public ActionCommand VrSettingsCommand { get; }
    public ActionCommand VrSettingsTabCommand { get; }
    public ActionCommand HideVrPanelCommand { get; }
    internal int VrOpenRequest { get; private set; }
    private readonly ListeningTimeTracker _listening;
    private readonly Stopwatch _mediaFreshness = Stopwatch.StartNew();
    private double _lastListeningSave;
    public OverlayService? Overlay { get; private set; }
    private readonly BackgroundUiTimer _poll;
    private readonly BackgroundUiTimer _clock;
    private readonly BackgroundUiTimer _save;
    private MediaState? _state;
    private bool _polling, _busy, _disposed, _seeking;
    private int _demoIndex;
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;
    private static readonly (string Title, string Artist, int Duration)[] DemoTracks = [("夜航", "星海 · 演示曲目", 228), ("月光漫游", "星海 · 演示曲目", 256), ("晚风来信", "星海 · 演示曲目", 193)];

    public MainViewModel(bool demo = false, SettingsStore? store = null, IAutoStartClient? autoStartClient = null)
    {
        Media = new(Players.Apps);
        _store = store ?? new(); Settings = _store.Load();
        _listening = new(Settings.TotalListeningSeconds); _lastListeningSave = Settings.TotalListeningSeconds;
        _autoStart = autoStartClient ?? new SteamVrAutoStartService();
        ThemeService.Apply(Settings.DarkMode);
        Sources = [new("auto", "自动识别音乐软件", "♫", "#B79AFF", []), .. Players.Apps];
        _selectedSource = Sources.FirstOrDefault(a => a.Id == Settings.SelectedApp) ?? Sources[0];
        _demo = demo;
        MediaCommand = new(p => _ = RunMedia(p?.ToString() ?? "toggle"), p => !_busy && (p?.ToString() switch { "previous" => CanPrevious, "next" => CanNext, "toggle" => CanToggle, _ => false }));
        OpenCommand = new(p => _ = OpenPlayer(p as AppEntry));
        CloseCommand = new(p => RunSafe(() => _demo ? "演示模式：已模拟关闭播放器。" : Players.Close(p as AppEntry ?? TargetPlayer, ForceClosePlayer)));
        ConfigureCommand = new(p => Configure(p as AppEntry));
        PageCommand = new(p => Page = p?.ToString() ?? "player");
        WristCommand = new(p => RightWrist = p?.ToString() == "right");
        VrSettingsCommand = new(p => VrSettingsOpen = p?.ToString() == "close" ? false : !VrSettingsOpen);
        VrSettingsTabCommand = new(p => VrSettingsTab = p?.ToString() switch { "general" => "general", "tests" => "tests", "about" => "about", _ => "wrist" });
        HideVrPanelCommand = new(_ => OverlayEnabled = false);
        ResetCommand = new(_ => { Scale = 100; Opacity = 95; OffsetX = 0; OffsetY = 8; OffsetZ = 6; Tilt = -75; Status = "已恢复默认面板位置。"; });
        ReconnectCommand = new(_ => Overlay?.Reconnect());
        RefreshSteamVrStartupCommand = new(_ => _ = RefreshSteamVrStartup(), _ => CanChangeSteamVrStartup);
        RepairSteamVrStartupCommand = new(_ => _ = SetFollowSteamVr(true), _ => CanChangeSteamVrStartup && NeedsSteamVrStartupRepair);
        InstallNeteaseBridgeCommand = new(_ => _ = InstallNeteaseBridge(), _ => !UseDemo && !_bridgeSetupBusy);
        NeteaseBridgeGuideCommand = new(_ => RunSafe(() => { Process.Start(new ProcessStartInfo(NeteaseBridgeSetup.GuideUrl) { UseShellExecute = true }); return "已打开插件作者的安装说明。"; }), _ => !UseDemo);
        _poll = new(TimeSpan.FromSeconds(1), () => _ = Refresh(), "Media polling");
        _clock = new(TimeSpan.FromMilliseconds(250), TickClock, "Playback clock");
        _save = new(TimeSpan.FromMilliseconds(500), SavePending, "Settings save");
        if (_demo) { SetDemoTrack(); Status = "演示模式 · 操作仅影响模拟播放状态"; }
    }

    public async Task Initialize(bool startTimers = true, bool connectVr = true)
    {
        Players.Discover(Settings);
        await Media.Initialize();
        if (!_demo) await Refresh();
        if (connectVr) { Overlay = new(this); Overlay.Start(); }
        if (startTimers) { _poll.Start(); _clock.Start(); }
        if (!_demo) _ = RefreshSteamVrStartup(false);
        if (_store.LoadError is not null) Status = _store.LoadError;
    }

    private string _page = "player";
    public string Page { get => _page; set { if (Set(ref _page, value)) { Notify(nameof(PageTitle)); Notify(nameof(PageSubtitle)); } } }
    public string PageTitle => DarkMode ? Page switch { "apps" => "你的音乐，随时就绪", "wrist" => "找到舒适的抬腕角度", "hotkeys" => "控制，快一步", "settings" => "让一切合你心意", _ => "让音乐，留在你的世界里" } : Page switch { "apps" => "播放器", "wrist" => "手腕面板", "hotkeys" => "快捷键", "settings" => "设置", _ => "音乐控制台" };
    public string PageSubtitle => DarkMode ? Page switch { "apps" => "连接桌面播放器，在虚拟世界里延续你的歌单。", "wrist" => "调整佩戴位置、大小与显示方式。", "hotkeys" => "即使没有打开面板，也能快速控制播放。", "settings" => "连接方式与兼容选项。设置会自动保存。", _ => "不必摘下头显，也不必离开此刻。" } : Page switch { "apps" => "管理桌面播放器的路径、启动与关闭。", "wrist" => "设置佩戴位置、面板尺寸与显示方式。", "hotkeys" => "查看播放控制与面板显示快捷键。", "settings" => "管理界面外观、媒体控制与设备连接。", _ => "连接桌面播放器，抬腕即可控制。" };
    public bool DarkMode { get => Settings.DarkMode; set { if (Settings.DarkMode == value) return; Settings.DarkMode = value; ThemeService.Apply(value); Changed(); Notify(nameof(PageTitle)); Notify(nameof(PageSubtitle)); Notify(nameof(Artwork)); } }
    public bool ForceClosePlayer
    {
        get => Settings.ForceClosePlayer;
        set { if (Settings.ForceClosePlayer != value) { Settings.ForceClosePlayer = value; Changed(); } }
    }
    internal UpdateService Updates { get; } = new();
    private CancellationTokenSource? _updateLoop;
    private bool _updatesStarted;
    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool AutoUpdate
    {
        get => Settings.AutoUpdate;
        set
        {
            if (Settings.AutoUpdate == value) return;
            Settings.AutoUpdate = value;
            Notify(); Save();
            if (_updatesStarted) RestartUpdates();
        }
    }
    internal void StartUpdates() { _updatesStarted = true; RestartUpdates(); }
    private void RestartUpdates()
    {
        _updateLoop?.Cancel();
        _updateLoop = null;
        if (!AutoUpdate) { UpdateStatus = "已关闭"; return; }
        _updateLoop = new();
        _ = CheckUpdates(_updateLoop);
    }
    private async Task CheckUpdates(CancellationTokenSource loop)
    {
        var token = loop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                UpdateStatus = "正在检查 GitHub 发行版…";
                try
                {
                    var result = await Task.Run(() => Updates.Check(token), token);
                    if (!token.IsCancellationRequested) UpdateStatus = result;
                }
                catch (Exception) when (!token.IsCancellationRequested) { UpdateStatus = "暂时无法检查或下载更新，稍后自动重试"; }
                await Task.Delay(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_updateLoop, loop)) _updateLoop = null; loop.Dispose(); }
    }
    public bool OpenLastPlayer
    {
        get => Settings.OpenLastPlayer;
        set { if (Settings.OpenLastPlayer != value) { Settings.OpenLastPlayer = value; Changed(); } }
    }
    internal AppEntry? ResolveOpenTarget(AppEntry? explicitPlayer)
    {
        if (explicitPlayer is not null) return explicitPlayer;
        if (OpenLastPlayer && Players.Apps.FirstOrDefault(a => a.Id == Settings.LastPlayerId) is { } last) return last;
        return TargetPlayer;
    }
    private async Task OpenPlayer(AppEntry? explicitPlayer)
    {
        if (UseDemo) { Status = "演示模式：已模拟打开播放器。"; return; }
        var target = ResolveOpenTarget(explicitPlayer);
        try
        {
            var result = await Players.Launch(target);
            if (_disposed) return;
            Status = result.Message;
            if (result.Success && target is not null) RememberPlayer(target);
        }
        catch (Exception e) { Status = "打开播放器失败：" + e.Message; }
    }
    private void RememberPlayer(AppEntry player)
    {
        if (Settings.LastPlayerId == player.Id) return;
        Settings.LastPlayerId = player.Id;
        _save.Stop(); _save.Start();
    }
    public bool LaunchSteamVr { get => Settings.LaunchSteamVr; set { if (Settings.LaunchSteamVr == value) return; Settings.LaunchSteamVr = value; Changed(); } }
    public bool ExperimentalFeaturesEnabled { get => Settings.ExperimentalFeaturesEnabled; set { if (Settings.ExperimentalFeaturesEnabled == value) return; Settings.ExperimentalFeaturesEnabled = value; UpdateListeningTime(); Changed(); Notify(nameof(ListeningTimeVisible)); } }
    public bool ShowListeningTime { get => Settings.ShowListeningTime; set { if (Settings.ShowListeningTime == value) return; Settings.ShowListeningTime = value; UpdateListeningTime(); Changed(); Notify(nameof(ListeningTimeVisible)); } }
    public bool ListeningTimeVisible => ExperimentalFeaturesEnabled && ShowListeningTime;
    public string ListeningTimeLabel => "累计听歌 " + ListeningTimeTracker.Format(Settings.TotalListeningSeconds);
    private bool _vrSettingsOpen;
    public bool VrSettingsOpen { get => _vrSettingsOpen; set { if (Set(ref _vrSettingsOpen, value)) { Notify(nameof(VrPlayerVisible)); Notify(nameof(VrPanelHeight)); } } }
    public bool VrPlayerVisible => !VrSettingsOpen;
    public bool VrHasProgress => UseDemo || _state is { TimelineKnown: true } s && s.Max > s.Min;
    public double VrPanelHeight => VrSettingsOpen || VrHasProgress ? 500 : 405;
    private string _vrSettingsTab = "wrist";
    public string VrSettingsTab { get => _vrSettingsTab; set => Set(ref _vrSettingsTab, value); }
    public bool FollowSteamVr { get => Settings.FollowSteamVr; set { if (value != Settings.FollowSteamVr && CanChangeSteamVrStartup) _ = SetFollowSteamVr(value); } }
    private bool _steamVrStartupBusy;
    public bool CanChangeSteamVrStartup => !_steamVrStartupBusy;
    private bool _needsSteamVrStartupRepair;
    public bool NeedsSteamVrStartupRepair { get => _needsSteamVrStartupRepair; private set => Set(ref _needsSteamVrStartupRepair, value); }
    private string _steamVrStartupStatus = "默认关闭。开启后，腕间音乐将在 SteamVR 启动时自动运行。";
    public string SteamVrStartupStatus { get => _steamVrStartupStatus; private set => Set(ref _steamVrStartupStatus, value); }

    public async Task RefreshSteamVrStartup(bool notifyStatus = true)
    {
        if (_disposed || _steamVrStartupBusy) return;
        SetSteamVrStartupBusy(true); SteamVrStartupStatus = "正在读取 SteamVR 自动启动状态…";
        try { ApplySteamVrStartupResult(await _autoStart.Execute(AutoStartOperation.Status), notifyStatus); }
        catch (Exception e) { if (!_disposed) SteamVrStartupStatus = "无法读取 SteamVR 设置：" + e.Message + "（当前状态未确认）"; }
        finally { SetSteamVrStartupBusy(false); }
    }
    public async Task SetFollowSteamVr(bool enabled)
    {
        if (_disposed || _steamVrStartupBusy) return;
        SetSteamVrStartupBusy(true); SteamVrStartupStatus = enabled ? "正在注册 SteamVR 自动启动…" : "正在关闭 SteamVR 自动启动…";
        try { ApplySteamVrStartupResult(await _autoStart.Execute(enabled ? AutoStartOperation.Enable : AutoStartOperation.Disable), true); }
        catch (Exception e) { if (!_disposed) { SteamVrStartupStatus = "设置失败：" + e.Message + "（当前状态未确认）"; Status = SteamVrStartupStatus; } }
        finally { SetSteamVrStartupBusy(false); Notify(nameof(FollowSteamVr)); }
    }
    private void ApplySteamVrStartupResult(AutoStartResult result, bool notifyStatus)
    {
        if (_disposed) return;
        SteamVrStartupStatus = result.Success ? result.Message : result.Message + "（当前状态未确认）";
        if (result.Success && result.Enabled is bool actual)
        {
            Settings.FollowSteamVr = actual; Notify(nameof(FollowSteamVr));
            NeedsSteamVrStartupRepair = actual && !result.RegistrationCurrent;
            Overlay?.IdentifyApplication(); Save();
        }
        if (notifyStatus) Status = SteamVrStartupStatus;
    }
    private void SetSteamVrStartupBusy(bool busy)
    {
        _steamVrStartupBusy = busy; Notify(nameof(CanChangeSteamVrStartup)); RefreshCommands();
    }
    private bool _bridgeSetupBusy;
    private string _bridgeSetupStatus = "可选安装 BetterNCM + InfLink-rs，为网易云补齐后台播放时间和进度跳转。请先退出网易云；安装完成后重新打开并播放歌曲。";
    public string BridgeSetupStatus { get => _bridgeSetupStatus; private set => Set(ref _bridgeSetupStatus, value); }
    public string NeteaseProgressStatus => _state is not null && MediaSessionSelection.IsNetease(_state.Source)
        ? _state.TimelineKnown ? _state.Seek ? "已收到网易云真实进度 · 支持跳转" : "已读取进度 · 当前会话未开放跳转" : "网易云已连接，但尚未提供有效播放时间"
        : "等待网易云播放歌曲后检测进度支持";
    private async Task InstallNeteaseBridge()
    {
        var player = Players.Apps.First(a => a.Id == "netease");
        if (!File.Exists(player.ExePath)) { BridgeSetupStatus = "请先到应用管理配置网易云程序路径。"; return; }
        if (MessageBox.Show("将从作者 GitHub 下载并校验 BetterNCM 1.3.4 和 InfLink-rs 3.4.0，安装到网易云目录及 C:\\betterncm。\n\n需要先退出网易云。此操作会为网易云加载第三方插件；不会覆盖已有其他版本组件。继续安装？", "网易云进度插件", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        _bridgeSetupBusy = true; RefreshCommands();
        BridgeSetupStatus = "正在下载、校验并安装网易云进度插件…";
        try { BridgeSetupStatus = await NeteaseBridgeSetup.Install(player.ExePath); }
        catch (UnauthorizedAccessException)
        {
            BridgeSetupStatus = "网易云目录需要管理员权限，正在请求 Windows 授权…";
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(MainViewModel).Assembly.Location);
                start.ArgumentList.Add("--netease-bridge-setup"); start.ArgumentList.Add(player.ExePath);
                using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动插件安装进程。");
                await helper.WaitForExitAsync();
                BridgeSetupStatus = helper.ExitCode == 0 ? "进度插件已安装。重新打开网易云并播放歌曲即可使用。" : "插件安装未完成。请确认网易云已完全退出，或查看插件说明。";
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { BridgeSetupStatus = "已取消管理员授权，插件未安装。"; }
            catch (Exception e) { BridgeSetupStatus = "安装未完成：" + e.Message; }
        }
        catch (Exception e) { BridgeSetupStatus = "安装未完成：" + e.Message; }
        finally { _bridgeSetupBusy = false; RefreshCommands(); }
    }
    private AppEntry _selectedSource;
    public AppEntry SelectedSource { get => _selectedSource; set { if (value is not null && Set(ref _selectedSource, value)) { Settings.SelectedApp = value.Id; _state = null; if (UseDemo) { SetDemoTrack(); NotifyAllMedia(); } else ClearMedia(); Changed(); _ = Refresh(); } } }
    public AppEntry? TargetPlayer
    {
        get
        {
            if (SelectedSource.Id != "auto") return SelectedSource;
            if (_state is not null) return Players.Apps.FirstOrDefault(a => a.Matches(_state.Source));
            var running = Players.Apps.Where(a => a.Running && !MediaSessionSelection.IsBrowser(
                Path.GetFileNameWithoutExtension(a.ExePath) ?? "")).ToArray();
            return running.Length == 1 ? running[0] : running.FirstOrDefault(a => a.Id == Settings.LastPlayerId);
        }
    }
    private bool _demo;
    // Simulated playback is used only by the internal self-test; production has no demo entry point.
    internal bool UseDemo { get => _demo; set { if (!Set(ref _demo, value)) return; _state = null; _seeking = false; if (value) { SetDemoTrack(); Overlay?.Disconnect(); } else { ClearMedia(); Status = "就绪 · 等待媒体会话"; _ = Refresh(); } NotifyAllMedia(); Notify(nameof(ModeLabel)); Notify(nameof(CanVolume)); } }
    public string ModeLabel => UseDemo ? "演示模式 · 模拟数据" : HasSession ? "Windows 媒体会话" : TargetPlayer?.Running == true ? "播放器兼容控制" : "等待播放器连接";
    private string _title = "等待音乐响起";
    public string Title { get => _title; private set => Set(ref _title, value); }
    private string _artist = "打开音乐软件，播放一首喜欢的歌";
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    private ImageSource? _artwork;
    public ImageSource Artwork { get => _artwork ?? (ImageSource)Application.Current.FindResource(DarkMode ? "MoonArtwork" : "LightArtwork"); private set => Set(ref _artwork, value); }
    private bool _playing;
    public bool Playing { get => _playing; private set { if (Set(ref _playing, value)) { Notify(nameof(PlayGlyph)); Notify(nameof(PlayLabel)); } } }
    public string PlayGlyph => Playing ? "\uE769" : "\uE768";
    public string PlayLabel => Playing ? "暂停" : "播放";
    private double _position;
    public double Position { get => _position; set { if (Set(ref _position, value)) { Notify(nameof(PositionLabel)); Notify(nameof(SeekPosition)); } } }
    // Slider coercion must not overwrite actual time, especially when duration is unavailable.
    public double SeekPosition { get => Math.Clamp(Position, TimelineMin, Math.Max(TimelineMin, Duration)); set { if (_seeking) Position = value; } }
    private double _duration = 1;
    public double Duration { get => _duration; private set { if (Set(ref _duration, Math.Max(TimelineMin, Math.Max(1, value)))) { Notify(nameof(DurationLabel)); Notify(nameof(SeekPosition)); } } }
    public double TimelineMin => _state?.Min ?? 0;
    public bool HasTimeline => UseDemo || _state?.TimelineKnown == true;
    public string PositionLabel => HasTimeline ? MediaMath.Time(Position) : "--:--";
    public string DurationLabel => UseDemo || _state is { Max: > 0 } ? MediaMath.Time(_state?.Max ?? Duration) : "--:--";
    public string SourceLabel => UseDemo ? "桌面音乐 · 演示" : TargetPlayer?.Name ?? (HasSession ? _state!.Source : "尚未连接播放器");
    public bool HasSession => _state is not null;
    private bool FallbackAvailable => !HasSession && (TargetPlayer?.Running == true || GlobalFallback && Media.CanSendGlobalKey(TargetPlayer));
    public bool CanToggle => UseDemo || (_state?.Toggle ?? FallbackAvailable);
    public bool CanPrevious => UseDemo || (_state?.Previous ?? FallbackAvailable);
    public bool CanNext => UseDemo || (_state?.Next ?? FallbackAvailable);
    public bool CanSeek => UseDemo || (_state?.Seek ?? false);
    public string CapabilityLabel => UseDemo ? "仅用于预览界面 · 不会控制真实播放器" : HasSession ? CanSeek ? "已连接系统媒体会话 · 支持进度控制" : HasTimeline ? "已读取歌曲进度 · 客户端未开放进度跳转" : "客户端当前未提供播放时间" : "播放后自动识别；未接入媒体会话的客户端可尝试兼容模式。";
    public string PlaybackHint => HasSession && !HasTimeline ? CapabilityLabel : Status;
    private bool _volumeAvailable;
    public bool CanVolume => UseDemo || _volumeAvailable;
    private double _volume = 65;
    public double Volume { get => _volume; set { value = Math.Clamp(value, 0, 100); if (Math.Abs(value - _volume) < 0.5) return; if (!UseDemo) { try { _volumeService.Set(value); } catch (Exception e) { Status = "音量调整失败：" + e.Message; return; } } Set(ref _volume, value); Notify(nameof(VolumeLabel)); } }
    public string VolumeLabel => $"{Volume:0}%";
    private string _status = "就绪 · 等待媒体会话";
    public string Status { get => _status; set { if (Set(ref _status, value)) Notify(nameof(PlaybackHint)); } }
    private string _vrStatus = "SteamVR 未连接";
    public string VrStatus { get => _vrStatus; set => Set(ref _vrStatus, value); }
    private string _vrDetail = "启动 SteamVR 并连接头显后，面板会自动连接。";
    public string VrDetail { get => _vrDetail; set => Set(ref _vrDetail, value); }
    private bool _vrConnected;
    public bool VrConnected { get => _vrConnected; set { if (Set(ref _vrConnected, value)) Notify(nameof(VrDot)); } }
    public Brush VrDot => VrConnected ? Brushes.MediumAquamarine : new SolidColorBrush(Color.FromRgb(112, 112, 132));
    private bool _vrChat;
    public bool VrChat { get => _vrChat; private set { if (Set(ref _vrChat, value)) Notify(nameof(VrChatLabel)); } }
    public string VrChatLabel => VrChat ? "VRChat 运行中" : "VRChat 未运行";
    public bool RightWrist { get => Settings.RightWrist; set { if (Settings.RightWrist == value) return; Settings.RightWrist = value; Changed(); Notify(nameof(WristLabel)); } }
    public string WristLabel => RightWrist ? "右手腕" : "左手腕";
    public double Scale { get => Settings.Scale; set { Settings.Scale = Math.Clamp(value, 50, 160); Changed(); } }
    public double Opacity { get => Settings.Opacity; set { Settings.Opacity = Math.Clamp(value, 30, 100); Changed(); } }
    public double OffsetX { get => Settings.OffsetX; set { Settings.OffsetX = Math.Clamp(value, -30, 30); Changed(); } }
    public double OffsetY { get => Settings.OffsetY; set { Settings.OffsetY = Math.Clamp(value, -30, 30); Changed(); } }
    public double OffsetZ { get => Settings.OffsetZ; set { Settings.OffsetZ = Math.Clamp(value, -30, 30); Changed(); } }
    public double Tilt { get => Settings.Tilt; set { Settings.Tilt = Math.Clamp(value, -180, 180); Changed(); } }
    public bool RaiseToShow { get => Settings.RaiseToShow; set { Settings.RaiseToShow = value; Changed(); } }
    public bool DoubleBToOpen { get => Settings.DoubleBToOpen; set { if (Settings.DoubleBToOpen == value) return; Settings.DoubleBToOpen = value; Changed(); } }
    internal void OpenVrPanelFromController()
    {
        OverlayEnabled = true; VrSettingsOpen = false; VrOpenRequest++;
        Status = "已通过双击 B 打开手腕面板。";
    }
    internal void ToggleVrPanelFromController(bool panelVisible)
    {
        if (OverlayEnabled && panelVisible)
        {
            OverlayEnabled = false; VrSettingsOpen = false;
            Status = "已通过双击 B 隐藏手腕面板。";
        }
        else OpenVrPanelFromController();
    }
    public bool OverlayEnabled { get => Settings.OverlayEnabled; set { Settings.OverlayEnabled = value; Changed(); } }
    public bool GlobalFallback { get => Settings.GlobalFallback; set { Settings.GlobalFallback = value; Changed(); NotifyAllMedia(); } }
    public bool Hotkeys { get => Settings.Hotkeys; set { Settings.Hotkeys = value; Changed(); HotkeysChanged?.Invoke(); } }
    public event Action? HotkeysChanged;

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null) { Notify(name); _save.Stop(); _save.Start(); }
    private void SavePending() { _save.Stop(); Save(); }
    private void RefreshCommands()
    {
        // An offscreen panel receives no mouse/focus events to trigger WPF's
        // automatic command requery. Publish state changes directly instead.
        MediaCommand.NotifyCanExecuteChanged();
        RefreshSteamVrStartupCommand.NotifyCanExecuteChanged();
        RepairSteamVrStartupCommand.NotifyCanExecuteChanged();
        InstallNeteaseBridgeCommand.NotifyCanExecuteChanged();
        NeteaseBridgeGuideCommand.NotifyCanExecuteChanged();
    }
    private void NotifyAllMedia()
    {
        foreach (var name in new[] { nameof(HasSession), nameof(CanToggle), nameof(CanNext), nameof(CanPrevious), nameof(CanSeek), nameof(SourceLabel), nameof(CapabilityLabel), nameof(ModeLabel), nameof(DurationLabel), nameof(PositionLabel), nameof(HasTimeline), nameof(TimelineMin), nameof(SeekPosition) }) Notify(name);
        Notify(nameof(PlaybackHint));
        Notify(nameof(NeteaseProgressStatus));
        Notify(nameof(VrHasProgress)); Notify(nameof(VrPanelHeight));
        RefreshCommands();
    }
    private void ClearMedia() {
        var running = TargetPlayer is { Running: true } player ? player : null;
        Title = running is null ? "等待音乐响起" : running.Name + " 已运行";
        Artist = running is null ? "打开音乐软件，播放一首喜欢的歌" : "客户端未提供歌曲信息，可尝试播放或切歌。"; _artwork = null; Notify(nameof(Artwork)); Playing = false; Position = 0; Duration = 1; NotifyAllMedia(); }
    private void SetDemoTrack() { var track = DemoTracks[_demoIndex]; Title = track.Title; Artist = track.Artist; Duration = track.Duration; Position = _demoIndex == 0 ? 84 : 0; Playing = true; _artwork = null; Notify(nameof(Artwork)); _lastTick = DateTimeOffset.UtcNow; }
    public async Task Refresh()
    {
        if (_disposed || _polling) return;
        _polling = true;
        try
        {
            Players.Refresh();
            var processes = Process.GetProcessesByName("VRChat"); VrChat = processes.Length > 0; foreach (var process in processes) process.Dispose();
            if (UseDemo) return;
            var requestedSource = SelectedSource;
            var state = await Media.Refresh(requestedSource.Id == "auto" ? null : requestedSource);
            if (UseDemo || requestedSource != SelectedSource || _disposed) return;
            ApplyMediaState(state);
            try { var level = _volumeService.Get(); _volumeAvailable = true; Set(ref _volume, level, nameof(Volume)); Notify(nameof(VolumeLabel)); Notify(nameof(CanVolume)); }
            catch { _volumeAvailable = false; Notify(nameof(CanVolume)); }
        }
        catch (Exception e) { _state = null; ClearMedia(); Status = "读取媒体状态失败，将自动重试：" + e.Message; }
        finally { _polling = false; }
    }
    internal void ApplyMediaState(MediaState? state)
    {
        _mediaFreshness.Restart();
        var previous = _state;
        _state = state;
        if (!UseDemo && state is not null && (state.Playing || Settings.LastPlayerId is null) &&
            (previous is null || previous.Source != state.Source || previous.Playing != state.Playing) &&
            Players.Apps.FirstOrDefault(a => a.Matches(state.Source)) is { } player)
            RememberPlayer(player);
        if (state is null) { ClearMedia(); if (Media.Error is not null) Status = Media.Error; }
        else
        {
            Title = string.IsNullOrWhiteSpace(state.Title) ? "未提供歌曲标题" : state.Title;
            Artist = string.IsNullOrWhiteSpace(state.Artist) ? "客户端未提供歌手信息" : state.Artist;
            _artwork = state.Artwork; Notify(nameof(Artwork)); Playing = state.Playing;
            Duration = state.Max;
            UpdatePosition(); NotifyAllMedia();
        }
        UpdateListeningTime();
    }
    private void UpdatePosition()
    {
        if (_seeking || _state is null) return;
        Position = _state.Timeline.At(DateTimeOffset.UtcNow, _state.Playing);
    }
    private void TickClock()
    {
        var now = DateTimeOffset.UtcNow;
        if (UseDemo && Playing && !_seeking) { Position = Math.Min(Duration, Position + (now - _lastTick).TotalSeconds); if (Position >= Duration) { _demoIndex = (_demoIndex + 1) % DemoTracks.Length; SetDemoTrack(); } }
        else if (!UseDemo) UpdatePosition();
        _lastTick = now;
        UpdateListeningTime();
        if (Settings.TotalListeningSeconds - _lastListeningSave >= 30) Save();
    }
    private void UpdateListeningTime()
    {
        _listening.Update(!_disposed && !UseDemo && HasSession && Playing && _mediaFreshness.Elapsed.TotalSeconds < 5 && ListeningTimeVisible);
        var changed = (long)Settings.TotalListeningSeconds != (long)_listening.TotalSeconds;
        Settings.TotalListeningSeconds = _listening.TotalSeconds;
        if (changed) Notify(nameof(ListeningTimeLabel));
    }

    public async Task RunMedia(string action)
    {
        if (_busy) return;
        if (!(action switch { "previous" => CanPrevious, "next" => CanNext, "toggle" => CanToggle, _ => false })) { Status = "当前播放器未开放该控制功能。"; return; }
        _busy = true; RefreshCommands();
        try
        {
            if (UseDemo)
            {
                switch (action)
                {
                    case "toggle": Playing = !Playing; break;
                    case "next": _demoIndex = (_demoIndex + 1) % DemoTracks.Length; SetDemoTrack(); break;
                    case "previous": _demoIndex = (_demoIndex + DemoTracks.Length - 1) % DemoTracks.Length; SetDemoTrack(); break;
                }
                Status = "演示操作已更新 · 未控制实际播放器";
            }
            else if (HasSession)
                Status = await Media.Control(action) ? "控制指令已执行。" : "播放器拒绝了该操作，或当前曲目不支持。";
            else
            {
                var code = action switch { "next" => 11, "previous" => 12, _ => 14 };
                if (Players.SendMediaCommand(TargetPlayer, code)) Status = "已向播放器发送兼容指令；此模式无法确认执行结果。";
                else if (GlobalFallback && Media.CanSendGlobalKey(TargetPlayer)) { PlayerService.SendGlobalMediaKey(action switch { "next" => 0xB0, "previous" => 0xB1, _ => 0xB3 }); Status = "已发送全局媒体按键；由 Windows 选择接收的应用。"; }
                else Status = "无法向播放器发送指令。可在设置中启用全局媒体按键兼容。";
            }
            if (!UseDemo) await Refresh();
        }
        catch (Exception e) { Status = "操作失败：" + e.Message; }
        finally { _busy = false; RefreshCommands(); }
    }
    private double _seekStart;
    public void BeginSeek() { if (CanSeek && !_seeking) { _seekStart = Position; _seeking = true; } }
    public void CancelSeek() { if (!_seeking) return; _seeking = false; if (UseDemo) Position = _seekStart; else UpdatePosition(); }
    public async Task EndSeek(double seconds)
    {
        if (!CanSeek) { _seeking = false; return; }
        _seeking = true;
        try
        {
            var value = Math.Clamp(seconds, TimelineMin, Duration);
            if (UseDemo) { Position = value; Status = "演示进度已调整。"; }
            else { Status = await Media.Control("seek", value) ? "已调整播放进度。" : "客户端未接受进度调整。"; await Refresh(); }
        }
        catch (Exception e) { Status = "进度调整失败：" + e.Message; }
        finally { _seeking = false; }
    }
    private void Configure(AppEntry? app)
    {
        if (app is null) return;
        var dialog = new OpenFileDialog { Filter = "播放器程序 (*.exe)|*.exe", Title = "选择 " + app.Name + " 的主程序", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        app.ExePath = dialog.FileName; Settings.AppPaths[app.Id] = dialog.FileName; SelectedSource = app; Changed(nameof(SelectedSource)); Status = "已配置 " + app.Name;
    }
    private void RunSafe(Func<string> operation) { try { Status = operation(); } catch (Exception e) { Status = "操作失败：" + e.Message; } }
    public void Save()
    {
        UpdateListeningTime();
        foreach (var app in Players.Apps) if (!string.IsNullOrEmpty(app.ExePath)) Settings.AppPaths[app.Id] = app.ExePath;
        try { _store.Save(Settings); _lastListeningSave = Settings.TotalListeningSeconds; } catch (Exception e) { Status = "设置保存失败：" + e.Message; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _updateLoop?.Cancel(); _poll.Dispose(); _clock.Dispose(); _save.Dispose(); Overlay?.Dispose(); Media.Dispose(); Save(); }
}
