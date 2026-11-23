namespace WristMusic;

public static class ReleaseNotes
{
    // Use the distributed build version for read receipts, including preview builds.
    internal const string BuildVersion = "5.0.0";
    public const string Heading = "v5.0.0更新内容";
    public const string Body = "1.增加了百宝箱页面(后面会更新一些小功能)\n" +
        "2.增加了vrchat相机录制功能(在VR视角下不用打开OBS也能录制 性能占用更低 不挡视野)\n" +
        "3.修复了音频录制没有声音的问题\n" +
        "4.增加了百宝箱vrchat相机录制预览框功能(预览框显示为30fps不代表真实输出)\n" +
        "5.增加了fps/分辨率录制设置\n" +
        "6.加入了文件保存位置管理\n" +
        "7.增加了文件保存位置打开\n" +
        "8.修复VR视角的排版Bug问题\n" +
        "9.加入了相机录制可以单独录制音频选择\n" +
        "预计未来更新内容:\n" +
        "内存优化(有时间就做 我要偷懒摸鱼喵)\n" +
        "估计没人会看到这里QWQ";

    internal static bool IsUnread(string? lastSeen) =>
        !Version.TryParse(lastSeen, out var seen) || seen < Version.Parse(BuildVersion);
}
