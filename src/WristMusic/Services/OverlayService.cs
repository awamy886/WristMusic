using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Valve.VR;
using WristMusic.Views;

namespace WristMusic.Services;

public sealed class OverlayService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly OverlayView _view;
    private readonly BackgroundUiTimer _timer;
    private volatile string _stage = "waiting";
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private byte[] _pixels = [];
    private int _surfaceHeight;
    private readonly Stopwatch _renderClock = Stopwatch.StartNew();
    private DateTime _nextConnect = DateTime.MinValue;
    private CVRSystem? _system;
    private CVROverlay? _overlay;
    private ulong _handle;
    private bool _initialized, _visible, _triggerWasDown;
    private bool _requireTriggerRelease = true;
    private bool _launchRequested;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly OverlayVisibilityState _visibility = new();
    private VrControllerInput? _controllerInput;
    private OverlayGpuTexture? _gpu;
    private readonly CursorFrameComposer _frame = new();
    private bool _pointerVisible;
    private double _lastInteraction = double.NegativeInfinity;
    private bool _previewLayout, _previewHasPixels;
    private long _previewRevision;
    private bool PreviewWanted => _vm.VrSettingsOpen && !_vm.VrNotesOpen && _vm.VrSettingsTab == "toolbox" &&
        _view.WristCameraTool.Form.Visibility == Visibility.Visible;
    private bool _manualOpen, _forceOpenPending;
    private int _observedOpenRequest;
    private bool _lastRaiseSetting;
    private (uint Device, double X, double Y, double Z, double Tilt)? _placement;
    private double _appliedScale = double.NaN, _appliedOpacity = double.NaN;
    private RenderTargetBitmap _bitmap = null!;
    public OverlayService(MainViewModel vm)
    {
        _vm = vm; _launchRequested = vm.LaunchSteamVr; _lastRaiseSetting = vm.RaiseToShow; _view = new() { DataContext = vm };
        EnsureSurface();
        _timer = new(TimeSpan.FromMilliseconds(1000d / 90), Tick, "VR loop", () => _stage);
    }
    public void Start() { _timer.Start(); Tick(); }
    public void Reconnect() { Disconnect(); _launchRequested = _vm.LaunchSteamVr; _nextConnect = DateTime.MinValue; Tick(); }
    private void Connect()
    {
        _nextConnect = DateTime.UtcNow.AddSeconds(8);
        try
        {
            var running = SteamVrConnectionPolicy.IsRunning();
            var launch = _launchRequested && _vm.LaunchSteamVr;
            _launchRequested = false;
            if (!SteamVrConnectionPolicy.ShouldInitialize(running, launch))
            { SetStatus(false, "SteamVR 未运行", "等待你启动 SteamVR，程序不会主动启动它。桌面音乐控制可独立使用。"); return; }
            if (!OpenVR.IsRuntimeInstalled()) { SetStatus(false, "SteamVR 未安装", "请先在 Steam 中安装并启动 SteamVR。桌面音乐控制可独立使用。"); return; }
            var error = EVRInitError.None;
            _system = OpenVR.Init(ref error, SteamVrConnectionPolicy.ApplicationType(running, launch));
            if (error != EVRInitError.None || _system is null) { SetStatus(false, "SteamVR 未连接", "连接失败：" + error + "。启动 SteamVR 后点击重新连接。"); return; }
            _initialized = true;
            _overlay = OpenVR.Overlay ?? throw new InvalidOperationException("SteamVR Overlay 接口不可用。");
            Check(_overlay.CreateOverlay("cn.wristmusic.controller." + Environment.ProcessId, "腕间音乐", ref _handle));
            Check(_overlay.SetOverlayInputMethod(_handle, VROverlayInputMethod.None));
            Check(_overlay.SetOverlayFlag(_handle, VROverlayFlags.NoDashboardTab, true));
            var adapter = -1; _system.GetDXGIOutputInfo(ref adapter);
            _gpu = new OverlayGpuTexture(adapter);

            VrDiagnostics.Write($"Connected. Renderer=D3D11 persistent texture, cursor=composited into panel with partial GPU updates and 90Hz target, adapter={adapter}, raise={_vm.RaiseToShow}, doubleB={_vm.DoubleBToOpen}.");
            _controllerInput = new VrControllerInput();
            if (!_controllerInput.Initialize()) _vm.Status = _controllerInput.Error ?? "手柄绑定未加载，可在 SteamVR 中检查腕间音乐的按键绑定。";
            SetStatus(true, "SteamVR 已连接", "用另一只手柄瞄准面板并按扳机；双击 B 可显示或隐藏面板。");
        }
        catch (DllNotFoundException) { Disconnect(); SetStatus(false, "OpenVR 组件缺失", "请保留程序同目录中的 openvr_api.dll，然后重新连接。"); }
        catch (Exception e) { VrDiagnostics.Write("Connect failure: " + e); Disconnect(); SetStatus(false, "SteamVR 连接失败", e.Message); }
    }
    private void Tick()
    {
        _stage = "input/events";
        if (_vm.UseDemo) { if (_initialized) Disconnect(); SetStatus(false, "SteamVR · 演示模式", "演示模式仅预览面板，不会向 SteamVR 提交画面。"); return; }
        if (!_initialized)
        {
            if (!_vm.OverlayEnabled && !_vm.DoubleBToOpen) { _vm.VrDetail = "手腕面板已关闭，可在桌面重新启用。"; return; }
            if (DateTime.UtcNow >= _nextConnect) Connect(); return;
        }
        try
        {
            var vrEvent = new VREvent_t();
            while (_system!.PollNextEvent(ref vrEvent, (uint)Marshal.SizeOf<VREvent_t>()))
            {
                if (vrEvent.eventType == (uint)EVREventType.VREvent_Quit) { _system.AcknowledgeQuit_Exiting(); Disconnect(); _nextConnect = DateTime.UtcNow.AddSeconds(8); return; }
                if (((EVREventType)vrEvent.eventType).ToString().StartsWith("VREvent_Input_", StringComparison.Ordinal))
                    VrDiagnostics.Write("SteamVR input event: " + (EVREventType)vrEvent.eventType);
            }
            var now = _uptime.Elapsed.TotalSeconds;
            if (_lastRaiseSetting != _vm.RaiseToShow) { _manualOpen = _forceOpenPending = false; _lastRaiseSetting = _vm.RaiseToShow; }
            if (_controllerInput?.Poll(_vm.DoubleBToOpen, now) == true) _vm.ToggleVrPanelFromController(_visible);
            if (!_vm.OverlayEnabled)
            {
                _manualOpen = _forceOpenPending = false; _visibility.Reset(); SetVisibility(false);
                _vm.VrDetail = _vm.DoubleBToOpen ? "面板已隐藏 · 双击手柄 B 键可打开。" : "手腕面板已关闭。"; return;
            }
            if (_observedOpenRequest != _vm.VrOpenRequest)
            {
                _observedOpenRequest = _vm.VrOpenRequest; _manualOpen = _forceOpenPending = true;

                _view.CancelInteraction(); SetPointer(null); _triggerWasDown = false; _requireTriggerRelease = true;
            }
            _system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _poses);
            var wrist = _system.GetTrackedDeviceIndexForControllerRole(_vm.RightWrist ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand);
            var pointer = _system.GetTrackedDeviceIndexForControllerRole(_vm.RightWrist ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
            if (wrist >= _poses.Length || !_poses[wrist].bDeviceIsConnected)
            {
                if (!_visibility.Update(true, false, false, now)) SetVisibility(false);

                _view.CancelInteraction(); SetPointer(null); _triggerWasDown = false; _requireTriggerRelease = true;
                _vm.VrDetail = "等待" + _vm.WristLabel + "对应的手柄。请检查 SteamVR 手柄角色与追踪状态。"; return;
            }
            var transform = MakeTransform(_vm.Settings);
            var head = _poses[0];
            var tracked = _poses[wrist].bPoseIsValid && (!_vm.RaiseToShow || _manualOpen || head.bPoseIsValid);
            var desired = !_vm.RaiseToShow || _manualOpen || tracked && Raised(_poses[wrist].mDeviceToAbsoluteTracking, head.mDeviceToAbsoluteTracking, transform, _visibility.Visible);
            var show = _visibility.Update(true, tracked, desired, now, _forceOpenPending && tracked);
            if (tracked) _forceOpenPending = false;
            if (!tracked)
            {
                if (!show) SetVisibility(false);

                _view.CancelInteraction(); SetPointer(null); _triggerWasDown = false; _requireTriggerRelease = true;
                _vm.VrDetail = "手柄追踪暂不可用，等待恢复。"; return;
            }
            if (!show) { SetVisibility(false); _vm.VrDetail = "面板已隐藏 · 抬腕或双击 B 可打开。"; return; }
            var placement = (wrist, _vm.OffsetX, _vm.OffsetY, _vm.OffsetZ, _vm.Tilt);
            if (_placement != placement) { Check(_overlay!.SetOverlayTransformTrackedDeviceRelative(_handle, wrist, ref transform)); _placement = placement; }
            if (_appliedScale != _vm.Scale) { Check(_overlay!.SetOverlayWidthInMeters(_handle, (float)(0.36 * _vm.Scale / 100))); _appliedScale = _vm.Scale; }
            if (_appliedOpacity != _vm.Opacity) { Check(_overlay!.SetOverlayAlpha(_handle, (float)(_vm.Opacity / 100))); _appliedOpacity = _vm.Opacity; }
            _stage = "panel layout";
            if (!_visible) _vm.ShowUnreadVrNotes();
            var resized = EnsureSurface();
            // Submit a complete frame before showing; never expose an empty new texture.
            if (!_visible || resized) { Render(); _renderClock.Restart(); }
            SetVisibility(true);
            _vm.VrDetail = "面板显示中 · " + _controllerInput?.Status + "。用另一只手柄瞄准，光点出现后按扳机点击。";
            _stage = "pointer";
            if (ProcessPointer(pointer, _poses[wrist].mDeviceToAbsoluteTracking, transform)) _lastInteraction = now;
            // Process the current input before painting its result. Cursor movement has no panel-frame gate.
            resized = EnsureSurface();
            var interval = now - _lastInteraction < .5 ? 1000d / 30 : 125;
            var preview = _vm.Recorder.Preview;
            var wanted = PreviewWanted;
            preview.Watch(this, wanted);
            if (resized || wanted != _previewLayout || wanted && (preview.Pixels != null) != _previewHasPixels ||
                _renderClock.Elapsed.TotalMilliseconds >= interval) { Render(); _renderClock.Restart(); }
            else if (wanted && preview.Pixels != null && preview.Revision != _previewRevision)
            {
                var area = ComposePreview();
                if (area.Width > 0) { _gpu!.UploadRegion(_frame.Pixels, _frame.Width, _frame.Height, area); SubmitTexture(); }
            }
        }
        catch (Exception e) { VrDiagnostics.Write("Tick failure: " + e); Disconnect(); _nextConnect = DateTime.UtcNow.AddSeconds(8); SetStatus(false, "SteamVR 连接中断", e.Message + "；稍后自动重试。"); }
    }
    private void SetVisibility(bool visible)
    {
        if (!visible) { _vm.Recorder.Preview.Watch(this, false); _view.CancelInteraction(); SetPointer(null); _triggerWasDown = false; _requireTriggerRelease = true; }
        if (_visible == visible || _overlay is null || _handle == 0) return;
        Check(visible ? _overlay.ShowOverlay(_handle) : _overlay.HideOverlay(_handle)); _visible = visible;
        if (visible) _vm.MarkVrGuideShown();
        VrDiagnostics.Write("Panel visibility=" + visible);
    }

    public static HmdMatrix34_t MakeTransform(UserSettings settings)
    {
        var angle = settings.Tilt * Math.PI / 180;
        return new() { m0 = 1, m3 = (float)(settings.OffsetX / 100), m5 = (float)Math.Cos(angle), m6 = (float)-Math.Sin(angle), m7 = (float)(settings.OffsetY / 100), m9 = (float)Math.Sin(angle), m10 = (float)Math.Cos(angle), m11 = (float)(settings.OffsetZ / 100) };
    }
    private static Vector3 Transform(HmdMatrix34_t m, Vector3 p) => new(m.m0 * p.X + m.m1 * p.Y + m.m2 * p.Z + m.m3, m.m4 * p.X + m.m5 * p.Y + m.m6 * p.Z + m.m7, m.m8 * p.X + m.m9 * p.Y + m.m10 * p.Z + m.m11);
    public static bool Raised(HmdMatrix34_t wrist, HmdMatrix34_t head, HmdMatrix34_t relative, bool visible)
    {
        var origin = Transform(wrist, new(relative.m3, relative.m7, relative.m11));
        var normal = Vector3.Normalize(Transform(wrist, new(relative.m2, relative.m6, relative.m10)) - Transform(wrist, Vector3.Zero));
        var headPosition = new Vector3(head.m3, head.m7, head.m11);
        var towardHead = headPosition - origin;
        var distance = towardHead.Length();
        return distance > (visible ? .08f : .1f) && distance < (visible ? 1.4f : 1.25f) && origin.Y > headPosition.Y - (visible ? 0.65f : 0.55f) && Vector3.Dot(normal, Vector3.Normalize(towardHead)) > (visible ? 0.20f : 0.35f);
    }
    public static bool TryRayHit(HmdMatrix34_t wrist, HmdMatrix34_t relative, HmdMatrix34_t pointer, double width, out Point point, double panelHeight = 500)
    {
        point = default;
        var center = Transform(wrist, new(relative.m3, relative.m7, relative.m11));
        var right = Vector3.Normalize(Transform(wrist, new(relative.m0, relative.m4, relative.m8)) - Transform(wrist, Vector3.Zero));
        var up = Vector3.Normalize(Transform(wrist, new(relative.m1, relative.m5, relative.m9)) - Transform(wrist, Vector3.Zero));
        var normal = Vector3.Cross(right, up);
        var rayOrigin = new Vector3(pointer.m3, pointer.m7, pointer.m11);
        var direction = Vector3.Normalize(new(-pointer.m2, -pointer.m6, -pointer.m10));
        var denominator = Vector3.Dot(direction, normal);
        if (denominator >= -0.0001 || width <= 0 || !double.IsFinite(panelHeight) || panelHeight <= 0) return false;
        var distance = Vector3.Dot(center - rayOrigin, normal) / denominator;
        if (distance is < 0 or > 3) return false;
        var offset = rayOrigin + direction * distance - center;
        var x = 0.5 + Vector3.Dot(offset, right) / width;
        var y = 0.5 - Vector3.Dot(offset, up) / (width * panelHeight / 820);
        if (x is < 0 or > 1 || y is < 0 or > 1) return false;
        point = new(x * 820, y * panelHeight); return true;
    }
    private bool ProcessPointer(uint pointer, HmdMatrix34_t wrist, HmdMatrix34_t relative)
    {
        if (pointer >= _poses.Length || !_poses[pointer].bPoseIsValid || !_poses[pointer].bDeviceIsConnected) { _view.CancelInteraction(); SetPointer(null); _triggerWasDown = false; _requireTriggerRelease = true; return false; }
        var pose = _poses[pointer].mDeviceToAbsoluteTracking;
        var hit = TryRayHit(wrist, relative, pose, 0.36 * _vm.Scale / 100, out var point, _surfaceHeight);
        SetPointer(hit ? point : null);
        var down = false;
        var valid = _controllerInput?.TryTrigger(!_vm.RightWrist, out down) == true;
        if (!valid)
        {
            var state = new VRControllerState_t();
            valid = _system!.GetControllerState(pointer, ref state, (uint)Marshal.SizeOf<VRControllerState_t>());
            down = (state.ulButtonPressed & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger)) != 0 || state.rAxis1.x > 0.8;
        }
        if (!valid) { _view.CancelInteraction(); _triggerWasDown = false; _requireTriggerRelease = true; return false; }
        if (valid && !down) _requireTriggerRelease = false;
        if (hit)
        {
            if (!_requireTriggerRelease) _view.Interact(point, down && !_triggerWasDown, down);
            if (!_requireTriggerRelease && down && !_triggerWasDown) _system!.TriggerHapticPulse(pointer, 0, 650);
        }
        else { SetPointer(null); if (!down) _view.ReleaseInteraction(); }
        var interacting = !_requireTriggerRelease && (hit && down || !down && _triggerWasDown);
        _triggerWasDown = down;
        return interacting;
    }
    private void SetPointer(Point? point)
    {
        // Keep WPF's pointer out of the background cache; the final shared texture
        // contains the cursor, so it cannot be lost behind another native overlay.
        _view.SetPointer(null);
        var dirty = _frame.SetCursor(point, _vm.DarkMode);
        if (_pointerVisible != (point is not null))
        { _pointerVisible = point is not null; VrDiagnostics.Write("Composited cursor hit=" + _pointerVisible); }
        if (!_visible || _gpu is null || dirty.Width <= 0 || dirty.Height <= 0) return;
        _gpu.UploadRegion(_frame.Pixels, _frame.Width, _frame.Height, dirty);
        SubmitTexture();
    }
    private void SubmitTexture()
    {
        _stage = "texture submission";
        var texture = new Texture_t { handle = _gpu!.Handle, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
        Check(_overlay!.SetOverlayTexture(_handle, ref texture));
    }
    private void Render()
    {
        _stage = "WPF render";
        _view.UpdateLayout();
        _bitmap.Clear(); _bitmap.Render(_view);
        var converted = new FormatConvertedBitmap(_bitmap, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(_pixels, 820 * 4, 0);
        for (var i = 0; i < _pixels.Length; i += 4) (_pixels[i], _pixels[i + 2]) = (_pixels[i + 2], _pixels[i]);
        _frame.SetPanel(_pixels, 820, _surfaceHeight);
        _previewLayout = PreviewWanted;
        _previewHasPixels = _previewLayout && _vm.Recorder.Preview.Pixels != null;
        if (_previewHasPixels) ComposePreview();
        _stage = "GPU upload";
        _gpu!.Upload(_frame.Pixels, 820, _surfaceHeight);
        SubmitTexture();
    }
    private Int32Rect ComposePreview()
    {
        var preview = _vm.Recorder.Preview;
        var origin = _view.WristCameraTool.PreviewBox.TranslatePoint(new Point(), _view);
        var area = new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y), CameraPreview.Width, CameraPreview.Height);
        if (area.X < 0 || area.Y < 0 || area.X + area.Width > _frame.Width || area.Y + area.Height > _frame.Height) return default;
        _frame.SetPreview(preview.Pixels!, area);
        _previewRevision = preview.Revision;
        return area;
    }
    internal Size TextureSize => new(_bitmap.PixelWidth, _bitmap.PixelHeight);
    internal bool EnsureSurface()
    {
        var height = (int)_vm.VrPanelHeight;
        if (_surfaceHeight == height) return false;

        _view.CancelInteraction(); SetPointer(null);
        _surfaceHeight = height;
        _view.Measure(new(820, height)); _view.Arrange(new(0, 0, 820, height)); _view.UpdateLayout();
        _bitmap = new(820, height, 96, 96, PixelFormats.Pbgra32);
        _pixels = new byte[820 * height * 4];
        return true;
    }
    private static void Check(EVROverlayError error) { if (error != EVROverlayError.None) throw new InvalidOperationException("OpenVR：" + error); }
    private void SetStatus(bool connected, string status, string detail) { _vm.VrConnected = connected; _vm.VrStatus = status; _vm.VrDetail = detail; }
    public void IdentifyApplication()
    {
        if (!_initialized || _controllerInput is not null) return;
        try
        {
            var applications = OpenVR.Applications;
            if (applications?.IsApplicationInstalled(SteamVrAutoStartService.ApplicationKey) == true)
            {
                var error = applications.IdentifyApplication(0, SteamVrAutoStartService.ApplicationKey);
                if (error != EVRApplicationError.None) _vm.Status = "SteamVR 应用身份关联失败：" + error;
            }
        }
        catch (Exception e) { _vm.Status = "无法关联 SteamVR 应用身份：" + e.Message; }
    }
    public void Disconnect()
    {
        _vm.Recorder.Preview.Watch(this, false);
        _view.CancelInteraction();
        _frame.SetCursor(null, _vm.DarkMode); _pointerVisible = false;
        try { if (_handle != 0) _overlay?.DestroyOverlay(_handle); }
        finally { _handle = 0; _overlay = null; _gpu?.Dispose(); _gpu = null; _system = null; _controllerInput?.Dispose(); _controllerInput = null; if (_initialized) OpenVR.Shutdown(); _initialized = false; _visible = false; _triggerWasDown = false; _requireTriggerRelease = true; _vm.VrConnected = false; _visibility.Reset(); _manualOpen = _forceOpenPending = false; _placement = null; _appliedScale = _appliedOpacity = double.NaN; }
    }
    public void Dispose() { _timer.Dispose(); Disconnect(); }
}
