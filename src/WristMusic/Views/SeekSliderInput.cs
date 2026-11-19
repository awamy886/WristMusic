using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace WristMusic.Views;

// Capture the slider, not its thin RepeatButton track, so clicks and drags use the same route.
internal sealed class SeekSliderInput
{
    private readonly Slider _slider;
    private MainViewModel? _owner;
    private bool _mouse, _keyboard;
    internal SeekSliderInput(Slider slider)
    {
        _slider = slider;
        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!BeginAt(e.GetPosition(slider))) return;
            slider.Focus(); slider.CaptureMouse(); e.Handled = true;
        };
        slider.PreviewMouseMove += (_, e) => { if (_mouse && e.LeftButton == MouseButtonState.Pressed) { MoveAt(e.GetPosition(slider)); e.Handled = true; } };
        slider.PreviewMouseLeftButtonUp += async (_, e) => { if (_mouse) { e.Handled = true; MoveAt(e.GetPosition(slider)); await Commit(); } };
        slider.LostMouseCapture += (_, _) => { if (_mouse) Cancel(); };
        slider.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown && slider.DataContext is MainViewModel vm && vm.CanSeek)
            { _keyboard = true; _owner = vm; vm.BeginSeek(); }
        };
        slider.PreviewKeyUp += async (_, _) => { if (_keyboard) await Commit(); };
        slider.LostKeyboardFocus += (_, _) => { if (_keyboard) Cancel(); };
        slider.Unloaded += (_, _) => Cancel();
    }
    public static void Attach(Slider slider) => _ = new SeekSliderInput(slider);
    internal bool BeginAt(Point point)
    {
        if (!_slider.IsEnabled || _slider.DataContext is not MainViewModel vm || !vm.CanSeek) return false;
        _owner = vm; _mouse = true; vm.BeginSeek(); MoveAt(point); return true;
    }
    internal void MoveAt(Point point)
    { if (_mouse && _owner is not null) { _slider.SetCurrentValue(Slider.ValueProperty, ValueAt(_slider, point)); _slider.GetBindingExpression(Slider.ValueProperty)?.UpdateSource(); } }
    internal async Task Commit()
    {
        if (!_mouse && !_keyboard) return;
        var owner = _owner; var value = _slider.Value; _mouse = _keyboard = false; _owner = null;
        if (_slider.IsMouseCaptured) _slider.ReleaseMouseCapture();
        if (owner is not null) await owner.EndSeek(value);
    }
    private void Cancel()
    { var owner = _owner; _mouse = _keyboard = false; _owner = null; owner?.CancelSeek(); }
    internal static double ValueAt(Slider slider, Point point)
    {
        slider.ApplyTemplate();
        var track = slider.Template?.FindName("PART_Track", slider) as Track;
        var thumbWidth = track?.Thumb?.ActualWidth ?? 14;
        var x = track is null ? point.X - 7 : slider.TranslatePoint(point, track).X;
        var width = track?.ActualWidth ?? Math.Max(1, slider.ActualWidth - 14);
        var fraction = Math.Clamp((x - thumbWidth / 2) / Math.Max(1, width - thumbWidth), 0, 1);
        if (slider.IsDirectionReversed) fraction = 1 - fraction;
        return slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
    }
}
