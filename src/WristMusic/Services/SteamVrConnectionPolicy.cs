using System.Diagnostics;
using Valve.VR;

namespace WristMusic.Services;

internal static class SteamVrConnectionPolicy
{
    internal static bool ShouldInitialize(bool running, bool launchRequested) => running || launchRequested;
    // Background is enforced even if SteamVR exits between the process check and VR_Init.
    internal static EVRApplicationType ApplicationType(bool running, bool launchRequested) => !running && launchRequested
        ? EVRApplicationType.VRApplication_Overlay : EVRApplicationType.VRApplication_Background;
    internal static bool IsRunning()
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("vrserver");
        try { return processes.Any(p => { try { return p.SessionId == current.SessionId; } catch { return false; } }); }
        finally { foreach (var p in processes) p.Dispose(); }
    }
}
