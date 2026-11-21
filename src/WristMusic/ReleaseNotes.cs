namespace WristMusic;

public static class ReleaseNotes
{
    // Use the distributed build version for read receipts, including preview builds.
    internal const string BuildVersion = "4.2.0";
    public const string Heading = "v4.2.0更新内容";
    public const string Body = "1.修复了在极小概率下出现无法正常更新问题\n" +
        "2.加入了更新进度显示";

    internal static bool IsUnread(string? lastSeen) =>
        !Version.TryParse(lastSeen, out var seen) || seen < Version.Parse(BuildVersion);
}
