using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using WristMusic.Services;

namespace WristMusic.Views;

public partial class OverlayView : UserControl
{
    private Slider? _activeSlider;
    private MainViewModel? _sliderOwner;
    private double _sliderValue;
    public OverlayView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel previous) { PropertyChangedEventManager.RemoveHandler(previous, OnThemeChanged, nameof(MainViewModel.DarkMode)); PropertyChangedEventManager.RemoveHandler(previous, OnLayoutChanged, nameof(MainViewModel.VrPanelHeight)); }
            if (e.NewValue is MainViewModel next) { PropertyChangedEventManager.AddHandler(next, OnThemeChanged, nameof(MainViewModel.DarkMode)); PropertyChangedEventManager.AddHandler(next, OnLayoutChanged, nameof(MainViewModel.VrPanelHeight)); }
            ApplyPalette();
        };
        ApplyPalette();
    }
    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e) => ApplyPalette();
    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e) { if (_activeSlider is not null && !IsShown(_activeSlider)) CancelInteraction(); }
    private void ApplyPalette()
    {
        // Offscreen VR visuals have no Window ancestor to receive application resource changes.
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(ThemeService.IsDark ? "Dark" : "Light")}.xaml") });
    }
    public void SetPointer(Point? point)
    {
        Pointer.Visibility = point is null ? Visibility.Collapsed : Visibility.Visible;
        if (point is not null) { Canvas.SetLeft(Pointer, point.Value.X - 7.5); Canvas.SetTop(Pointer, point.Value.Y - 7.5); }
    }
    public void Interact(Point point, bool click, bool held)
    {
        if (_activeSlider is not null)
        {
            if (!IsShown(_activeSlider) || !_activeSlider.IsEnabled) { CancelInteraction(); return; }
            if (!held) { ReleaseInteraction(); return; }
            ApplySlider(point); return;
        }
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(this,
            visual => visual is UIElement element && (!element.IsHitTestVisible || element.Visibility != Visibility.Visible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        while (hit is not null && hit != this)
        {
            if (hit is ToggleButton toggle)
            {
                if (click && toggle.IsEnabled)
                {
                    toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, toggle.IsChecked != true);
                    toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
                }
                return;
            }
            if (hit is Button button)
            {
                if (click && button.IsEnabled && button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
                return;
            }
            if (hit is Slider slider)
            {
                if (click && held && slider.IsEnabled && DataContext is MainViewModel vm)
                {
                    _activeSlider = slider; _sliderOwner = vm;
                    if (slider.Tag?.ToString() == "seek") vm.BeginSeek();
                    ApplySlider(point);
                }
                return;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
    }
    private void ApplySlider(Point point)
    {
        if (_activeSlider is not { } slider || _sliderOwner is not { } vm) return;
        _sliderValue = SeekSliderInput.ValueAt(slider, TranslatePoint(point, slider));
        switch (slider.Tag?.ToString())
        {
            case "seek": vm.SeekPosition = _sliderValue; break;
            case "volume": vm.Volume = _sliderValue; break;
            case "scale": vm.Scale = _sliderValue; break;
            case "opacity": vm.Opacity = _sliderValue; break;
            case "offsetX": vm.OffsetX = _sliderValue; break;
            case "offsetY": vm.OffsetY = _sliderValue; break;
            case "offsetZ": vm.OffsetZ = _sliderValue; break;
            case "tilt": vm.Tilt = _sliderValue; break;
        }
    }
    private static bool IsShown(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }
    public void ReleaseInteraction()
    {
        var seek = _activeSlider?.Tag?.ToString() == "seek"; var owner = _sliderOwner; var value = _sliderValue;
        _activeSlider = null; _sliderOwner = null;
        if (seek && owner is not null) _ = owner.EndSeek(value);
    }
    public void CancelInteraction()
    {
        if (_activeSlider?.Tag?.ToString() == "seek") _sliderOwner?.CancelSeek();
        _activeSlider = null; _sliderOwner = null;
    }
}
