using System.IO;

namespace WristMusic.Services;

internal static class VrDiagnostics
{
    private static readonly object Gate = new();
    internal static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WristMusic", "Logs", "steamvr.log");
    internal static void Write(string message)
    {
        lock (Gate) try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.Move(LogPath, LogPath + ".previous", true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
