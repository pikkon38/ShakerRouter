using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ShakerRouter.ViewModels;

namespace ShakerRouter;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        // Title shows the build stamp so you can confirm an update landed.
        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(version)) Title = $"Shaker Router  v{version}";

        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
    }

    /// <summary>Scroll over a fader: 5% per notch, 1% with Shift. Keeps the page from scrolling.</summary>
    private void Fader_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        int steps = e.Delta > 0 ? 1 : -1;
        bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch ((sender as FrameworkElement)?.DataContext)
        {
            case MatrixCellViewModel cell:
                cell.Nudge(steps, fine);
                break;
            case InputRowViewModel row:
                row.Percent += steps * (fine ? 1 : 5);
                break;
            case MainViewModel vm:
                vm.MasterVolume += steps * (fine ? 1 : 5);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // Press and drag left/right to change a fader relative to where it was.
    // A plain click changes nothing. Hold Shift while dragging for 4x finer control.
    private const double DragThreshold = 3;   // px of movement before a press counts as a drag
    private double _dragStartX;
    private double _dragStartValue;
    private bool _dragging;

    private void Fader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        _dragStartX = e.GetPosition(slider).X;
        _dragStartValue = slider.Value;
        _dragging = false;
        slider.CaptureMouse();
        e.Handled = true; // stop the stock slider from jumping to the click point
    }

    private void Fader_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;

        double dx = e.GetPosition(slider).X - _dragStartX;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold) return;
            _dragging = true;
            _dragStartX += Math.Sign(dx) * DragThreshold; // no jump when the drag kicks in
            dx = e.GetPosition(slider).X - _dragStartX;
        }

        // Dragging the full width of the control = the full range. Shift = 4x finer.
        double width = Math.Max(slider.ActualWidth, 40);
        double perPixel = (slider.Maximum - slider.Minimum) / width;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) perPixel /= 4;

        double value = Math.Clamp(Math.Round(_dragStartValue + dx * perPixel), slider.Minimum, slider.Maximum);
        slider.SetCurrentValue(RangeBase.ValueProperty, value);
    }

    private void Fader_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && slider.IsMouseCaptured)
        {
            slider.ReleaseMouseCapture();
            _dragging = false;
            e.Handled = true;
        }
    }

    /// <summary>Right click a cell: off, or back to its last level.</summary>
    private void Cell_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MatrixCellViewModel cell)
        {
            cell.Toggle();
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Dispose();
        base.OnClosing(e);
    }
}
