using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace WristMusic.Services;

public sealed class CameraRecorder : Observable, IDisposable
{
    private readonly UserSettings _settings;
    private readonly Action _save;
    private Process? _process;
    private bool _stopping;
    private string _status = "在 VRChat 的串流相机中开启 Spout Stream 后开始录制。";
    private bool _disposed;

    public CameraRecorder(UserSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        OpenFolder = new(_ => _ = OpenSaveFolder());
        CycleFps = new(_ => { _settings.RecordingFps = _settings.RecordingFps switch { 30 => 60, 60 => 120, _ => 30 }; Notify(nameof(FpsLabel)); _save(); }, _ => CanConfigure);
        CycleSize = new(_ => { _settings.RecordingHeight = _settings.RecordingHeight switch { 720 => 1080, 1080 => 1440, _ => 720 }; Notify(nameof(SizeLabel)); _save(); }, _ => CanConfigure);
        Toggle = new(_ => { if (_process == null) _ = Run(); else Stop(); }, _ => !_stopping && !_disposed);
    }

    public string Folder => string.IsNullOrWhiteSpace(_settings.RecordingFolder)
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify)
        : _settings.RecordingFolder;
    public string FpsLabel => $"{_settings.RecordingFps} FPS  ›";
    public string SizeLabel => $"{_settings.RecordingHeight}p  ›";
    public bool CanConfigure => _process == null;
    public bool RecordDesktopAudio
    {
        get => _settings.RecordDesktopAudio;
        set { if (CanConfigure && value != RecordDesktopAudio) { _settings.RecordDesktopAudio = value; Notify(); _save(); } }
    }
    public bool RecordMicrophoneAudio
    {
        get => _settings.RecordMicrophoneAudio;
        set { if (CanConfigure && value != RecordMicrophoneAudio) { _settings.RecordMicrophoneAudio = value; Notify(); _save(); } }
    }
    public bool RecordVrChatAudio
    {
        get => _settings.RecordVrChatAudio;
        set { if (CanConfigure && value != RecordVrChatAudio) { _settings.RecordVrChatAudio = value; Notify(); _save(); } }
    }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ButtonLabel => _stopping ? "正在保存…" : _process == null ? "开始录制" : "停止录制";
    public ActionCommand CycleFps { get; }
    public ActionCommand CycleSize { get; }
    public ActionCommand Toggle { get; }
    public ActionCommand OpenFolder { get; }
    public CameraPreview Preview { get; } = new();

    private async Task OpenSaveFolder()
    {
        var folder = Folder;
        try
        {
            await Task.Run(() =>
            {
                var directory = Directory.CreateDirectory(folder);
                using var explorer = Process.Start(new ProcessStartInfo(directory.FullName) { UseShellExecute = true, Verb = "open" });
            });
        }
        catch (Exception ex) { Status = $"无法打开保存文件夹 · {ex.Message}"; }
    }

    public void ChooseFolder(string folder)
    {
        if (!CanConfigure || !Path.IsPathFullyQualified(folder)) return;
        _settings.RecordingFolder = folder;
        Notify(nameof(Folder));
        _save();
    }
    private void RefreshControls()
    {
        Notify(nameof(CanConfigure)); Notify(nameof(ButtonLabel));
        CycleFps.NotifyCanExecuteChanged(); CycleSize.NotifyCanExecuteChanged(); Toggle.NotifyCanExecuteChanged();
    }
    internal static string OutputPath(string folder, DateTime now)
    {
        var stem = "腕间音乐-vrchat录制-" + now.ToString("yyyy年MM月dd日HH时mm分ss秒", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".mp4");
        for (int suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(folder, $"{stem}-{suffix}.mp4");
        return path;
    }

    private async Task Run()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Folder)) throw new IOException("请先选择保存位置。");
            Directory.CreateDirectory(Folder);
            var path = OutputPath(Folder, DateTime.Now);
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WristCamera.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            info.ArgumentList.Add(path);
            info.ArgumentList.Add(_settings.RecordingHeight.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(_settings.RecordingFps.ToString(CultureInfo.InvariantCulture));
            int audioMask = (RecordDesktopAudio ? 1 : 0) | (RecordMicrophoneAudio ? 2 : 0) | (RecordVrChatAudio ? 4 : 0);
            info.ArgumentList.Add($"--audio={audioMask}");
            using var process = Process.Start(info) ?? throw new IOException("录制组件启动失败。");
            _process = process;
            Status = "正在等待 VRChat 相机的 Spout2 画面…";
            RefreshControls();
            var errors = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (line == "RECORDING" && !_stopping) Status = $"正在录制 · {Path.GetFileName(path)}";
            await process.WaitForExitAsync();
            var detail = (await errors).Trim();
            Status = process.ExitCode switch
            {
                0 => $"已保存到 {path}",
                2 => "已取消等待，相机尚未开始录制。",
                3 => $"串流已断开，视频已保存到 {path}",
                _ when File.Exists(path) => $"录制中断，已保存的片段在 {path}",
                _ => string.IsNullOrEmpty(detail) ? "录制组件无法运行，请检查 Windows 媒体功能。" : $"录制失败 · {detail}"
            };
        }
        catch (Exception ex) { Status = $"录制失败 · {ex.Message}"; }
        finally { _process = null; _stopping = false; RefreshControls(); }
    }

    private void Stop()
    {
        if (_process == null || _stopping) return;
        _stopping = true;
        Status = "正在结束录制…";
        RefreshControls();
        // EOF lets the independent worker finalize the MP4 even during desktop shutdown.
        try { _process.StandardInput.Close(); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }
    public void Dispose() { _disposed = true; Stop(); Preview.Dispose(); }
}
