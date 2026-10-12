using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using ColekUI.Controls;
using ColekUI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using PKGMerge.ViewModels.Merge;

namespace PKGMerge.Views.Shell;

public partial class MainWindow : ColekWindow
{
    private readonly MergeViewModel _merge;

    private bool _canClose;

    private bool _isAsking;

    public MainWindow()
    {
        InitializeComponent();

        IServiceProvider services = App.GetServices();

        _merge = services.GetRequiredService<MergeViewModel>();
        Content = _merge;
        PlacementStore = services.GetRequiredService<IWindowPlacementStore>();
        PlacementKey = "MainWindow";

        PositionChanged += (_, _) => KeepOnScreen();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        KeepOnScreen();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ClientSizeProperty)
            KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        if (!IsVisible || Screens.ScreenFromWindow(this) is not { } screen)
            return;

        PixelRect area = screen.WorkingArea;
        double scaling = screen.Scaling;
        Size frame = FrameSize ?? ClientSize;

        MaxHeight = Math.Max(0, area.Height / scaling - (frame.Height - ClientSize.Height));

        int height = (int)Math.Ceiling(frame.Height * scaling);
        if (Position.Y + height > area.Bottom)
            Position = new PixelPoint(Position.X, Math.Max(area.Y, area.Bottom - height));
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_canClose || !_merge.IsRunning)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);

        if (e.CloseReason == WindowCloseReason.OSShutdown
            || (e.CloseReason == WindowCloseReason.ApplicationShutdown && OperatingSystem.IsWindows()))
        {
            await StopAndCloseAsync();
            return;
        }

        if (_isAsking)
            return;

        _isAsking = true;
        try
        {
            if (!await _merge.ConfirmStopAsync())
                return;
        }
        finally
        {
            _isAsking = false;
        }

        await StopAndCloseAsync();
    }

    private async Task StopAndCloseAsync()
    {
        await _merge.StopAsync();

        if (_canClose)
            return;

        _canClose = true;
        Close();
    }
}
