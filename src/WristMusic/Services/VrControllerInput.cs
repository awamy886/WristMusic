using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace WristMusic.Services;

internal sealed class VrControllerInput : IDisposable
{
    private CVRInput? _input;
    private CVRSystem? _system;
    private CVRRenderModels? _models;
    private VrInputRegistration? _registration;
    private bool _actionsReady;
    private ulong _rightSource, _leftSource;
    private readonly Dictionary<uint, (string Model, string? Component, ulong Mask, double RetryAt)> _components = [];
    private ulong _set, _rightB, _leftB, _rightTrigger, _leftTrigger;
    private bool _frameValid;
    private (bool RightAction, bool LeftAction, bool RightPhysical, bool LeftPhysical)? _lastButtons;
    private readonly VRActiveActionSet_t[] _active = new VRActiveActionSet_t[1];
    private readonly VrDoubleBShortcut _shortcut = new();
    public string? Error { get; private set; }
    internal string Status { get; private set; } = "等待手柄输入";
    public bool Initialize()
    {
        try
        {
            _input = OpenVR.Input ?? throw new InvalidOperationException("SteamVR Input 不可用");
            _system = OpenVR.System; _models = OpenVR.RenderModels;
            Check(_input.GetInputSourceHandle("/user/hand/right", ref _rightSource));
            Check(_input.GetInputSourceHandle("/user/hand/left", ref _leftSource));
            _registration = new VrInputRegistration();
            _registration.DiscoverControllerBindings(_system);
            _registration.Register();
            Check(_input.SetActionManifestPath(_registration.ActionManifest));
            Check(_input.GetActionSetHandle("/actions/wrist", ref _set));
            Check(_input.GetActionHandle("/actions/wrist/in/open_right", ref _rightB));
            Check(_input.GetActionHandle("/actions/wrist/in/open_left", ref _leftB));
            Check(_input.GetActionHandle("/actions/wrist/in/select_right", ref _rightTrigger));
            Check(_input.GetActionHandle("/actions/wrist/in/select_left", ref _leftTrigger));
            _active[0].ulActionSet = _set; _actionsReady = true; Error = null;
            VrDiagnostics.Write("Input action manifest loaded: " + _registration.ActionManifest); return true;
        }
        catch (Exception e) { _actionsReady = false; Error = e.Message; VrDiagnostics.Write("Input initialization: " + e); return false; }
    }
    public bool Poll(bool enabled, double now)
    {
        var update = _actionsReady && _input is not null ? _input.UpdateActionState(_active, (uint)Marshal.SizeOf<VRActiveActionSet_t>()) : EVRInputError.InvalidHandle;
        _frameValid = update == EVRInputError.None;
        var right = false; var left = false;
        var rightValid = _frameValid && Read(_rightB, out right);
        var leftValid = _frameValid && Read(_leftB, out left);
        var physicalRightValid = ReadPhysical(true, now, out var physicalRight);
        var physicalLeftValid = ReadPhysical(false, now, out var physicalLeft);
        var buttons = (right, left, physicalRight, physicalLeft);
        if (_lastButtons != buttons) { _lastButtons = buttons; VrDiagnostics.Write($"B states: action R={right}, L={left}; physical R={physicalRight}, L={physicalLeft}"); }
        var status = $"右手 B：{Channel(rightValid, physicalRightValid)}；左手 B：{Channel(leftValid, physicalLeftValid)}";
        if (status != Status) { Status = status; VrDiagnostics.Write(status + $"; actionState={update}"); }
        var opened = _shortcut.Update(enabled, now, new(rightValid, right), new(leftValid, left),
            new(physicalRightValid, physicalRight), new(physicalLeftValid, physicalLeft));
        if (opened) VrDiagnostics.Write("Double B recognized; toggle panel requested.");
        return opened;
    }
    public bool TryTrigger(bool rightHand, out bool down)
    { down = false; return _frameValid && Read(rightHand ? _rightTrigger : _leftTrigger, out down); }
    private bool Read(ulong handle, out bool down)
    {
        down = false;
        if (_input is null || handle == 0) return false;
        var data = new InputDigitalActionData_t();
        var error = _input.GetDigitalActionData(handle, ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), 0);
        if (error != EVRInputError.None || !data.bActive) return false;
        down = data.bState; return true;
    }
    private static void Check(EVRInputError error) { if (error != EVRInputError.None) throw new InvalidOperationException("SteamVR 按键绑定失败：" + error); }
    private static string Channel(bool action, bool physical) => action ? "动作已绑定" : physical ? "实体组件可用" : "未检测到绑定或实体组件";
    private bool ReadPhysical(bool rightHand, double now, out bool down)
    {
        down = false;
        if (_system is null || _models is null || _input is null) return false;
        var device = _system.GetTrackedDeviceIndexForControllerRole(rightHand ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand);
        if (device == OpenVR.k_unTrackedDeviceIndexInvalid || !_system.IsTrackedDeviceConnected(device)) return false;
        var model = Property(device, ETrackedDeviceProperty.Prop_RenderModelName_String);
        if (string.IsNullOrEmpty(model)) return false;
        if (!_components.TryGetValue(device, out var entry) || entry.Model != model || entry.Component is null && now >= entry.RetryAt)
        {
            string? component = null; ulong mask = 0;
            var count = Math.Min(1024, _models.GetComponentCount(model));
            var available = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                var name = new StringBuilder(256); _models.GetComponentName(model, i, name, (uint)name.Capacity);
                var text = name.ToString(); available.Add(text);
                if (!IsBComponent(text)) continue;
                component = text; mask = _models.GetComponentButtonMask(model, component); break;
            }
            entry = (model, component, mask, now + 3); _components[device] = entry;
            VrDiagnostics.Write($"Controller {device}, type={Property(device, ETrackedDeviceProperty.Prop_ControllerType_String)}, model={model}, B component={component ?? "none"}, mask={mask}, components={string.Join(",", available)}");
        }
        if (entry.Component is null) return false;
        var state = new RenderModel_ComponentState_t(); var mode = new RenderModel_ControllerMode_State_t();
        var source = rightHand ? _rightSource : _leftSource;
        var componentValid = source != 0 && _models.GetComponentStateForDevicePath(model, entry.Component, source, ref mode, ref state);
        // This mask comes from the identified B component; never guess a menu/system alias.
        // A component transform can succeed with an inactive pressed_path in compatibility mode.
        var legacy = new VRControllerState_t();
        var legacyValid = entry.Mask != 0 && _system.GetControllerState(device, ref legacy, (uint)Marshal.SizeOf<VRControllerState_t>());
        var combined = CombinePhysicalB(new(componentValid, (state.uProperties & (uint)EVRComponentProperty.IsPressed) != 0),
            new(legacyValid, (legacy.ulButtonPressed & entry.Mask) != 0));
        down = combined.Down; return combined.Valid;
    }
    internal static VrButtonState CombinePhysicalB(VrButtonState component, VrButtonState legacy) =>
        new(component.Valid || legacy.Valid, component.Valid && component.Down || legacy.Valid && legacy.Down);
    internal static bool IsBComponent(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant() is "b" or "buttonb" or "bbutton";
    private string Property(uint device, ETrackedDeviceProperty property)
    {
        var error = ETrackedPropertyError.TrackedProp_Success; var text = new StringBuilder(1024);
        _system!.GetStringTrackedDeviceProperty(device, property, text, (uint)text.Capacity, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? text.ToString() : "";
    }
    public void Reset() { _frameValid = false; _lastButtons = null; _shortcut.Reset(); }
    public void Dispose() { Reset(); _registration?.Dispose(); _registration = null; }
}
