using System.IO;
using System.Security.Cryptography;
using WristMusic.Services;

namespace WristMusic;

internal static class BridgeTests
{
    internal static void Run(string folder, Action<bool, string> check)
    {
        check(MediaSessionSelection.SamePlayer("cloudmusic.exe", "InfLink-rs") &&
            !MediaSessionSelection.SamePlayer("cloudmusic.exe", "QQMusic.exe") &&
            !MediaSessionSelection.SamePlayer("chrome.exe", "InfLink-rs"),
            "Bridge session grouping keeps NetEase together without switching from another music player or browser");
        check(MediaSessionSelection.Score(true, true, false) > MediaSessionSelection.Score(false, false, true) &&
            MediaSessionSelection.Score(true, true, true) > MediaSessionSelection.Score(true, false, true),
            "A seekable bridge timeline outranks the playing metadata-only native session");
        var existing = Path.Combine(folder, "existing-third-party-component.dll");
        File.WriteAllText(existing, "An existing unrelated component must not be replaced.");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(existing)));
        var rejected = false;
        try { NeteaseBridgeSetup.EnsureCompatibleFile(existing, NeteaseBridgeSetup.LoaderHash); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(existing))) == hash,
            "Optional bridge setup refuses to overwrite an existing different loader");
        NeteaseBridgeSetup.EnsureCompatibleFile(existing, hash);
        check(File.Exists(existing), "An identical existing component is accepted without rewriting it");
    }
}
