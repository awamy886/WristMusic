namespace WristMusic;

public static class ReleaseNotes
{
    // Use the distributed build version for read receipts, including preview builds.
    internal const string BuildVersion = "4.0.0";
    public const string Heading = "v4.0.0更新内容";
    public const string Body = "1.加入了CPU、内存、显卡使用率的显示\n" +
        "2.加入了手动检测更新功能\n" +
        "3.vr面板上也显示更新内容弹窗\n" +
        "4.在设置关于旁边加入本次更新内容\n" +
        "5.修复新版本检测问题\n" +
        "6.删除了测试功能的总开关";

    internal static bool IsUnread(string? lastSeen) =>
        !Version.TryParse(lastSeen, out var seen) || seen < Version.Parse(BuildVersion);
}
