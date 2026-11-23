using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WristMusic;
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private HwndSource? _source;
    private IntPtr _handle;
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent(); _vm = vm; DataContext = vm;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        var workArea = SystemParameters.WorkArea;
        var fit = Math.Min(1, Math.Min((workArea.Width - 24) / Width, (workArea.Height - 24) / Height));
        if (fit < 0.98 && Content is FrameworkElement design)
        {
            design.Width = Width; design.Height = Height; Content = null;
            Content = new System.Windows.Controls.Viewbox { Child = design, Stretch = System.Windows.Media.Stretch.Uniform };
            MinWidth = 900; MinHeight = 575; Width *= fit; Height *= fit;
        }
        SourceInitialized += (_, _) => { _handle = new WindowInteropHelper(this).Handle; _source = HwndSource.FromHwnd(_handle); _source?.AddHook(WindowProc); UpdateHotkeys(); };
        _vm.HotkeysChanged += UpdateHotkeys;
        Closed += (_, _) => { _vm.HotkeysChanged -= UpdateHotkeys; UnregisterAll(); _source?.RemoveHook(WindowProc); _vm.Dispose(); };
    }
    private void TitleDrag(object sender, MouseButtonEventArgs e) { if (e.ChangedButton != MouseButton.Left) return; if (e.ClickCount == 2) MaximizeClick(sender, e); else DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ShowSmtcGuide(object sender, RoutedEventArgs e) => Views.ReleaseNotesWindow.Open(this, _vm, guide: true);
    private void UpdateHotkeys()
    {
        if (_handle == IntPtr.Zero) return;
        UnregisterAll(); if (!_vm.Hotkeys) return;
        var keys = new[] { 0x20u, 0x25u, 0x27u, 0x4Fu };
        var failed = false;
        for (var i = 0; i < keys.Length; i++) failed |= !RegisterHotKey(_handle, 701 + i, 0x4003, keys[i]);
        if (failed) _vm.Status = "部分全局快捷键已被占用，未能注册；可继续使用桌面或 VR 面板。";
    }
    private void UnregisterAll() { if (_handle != IntPtr.Zero) for (var i = 701; i <= 704; i++) UnregisterHotKey(_handle, i); }
    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        var action = wParam.ToInt32(); handled = true;
        if (action == 704) _vm.OverlayEnabled = !_vm.OverlayEnabled;
        else if (action is >= 701 and <= 703) _ = _vm.RunMedia(action switch { 702 => "previous", 703 => "next", _ => "toggle" });
        else handled = false;
        return IntPtr.Zero;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
