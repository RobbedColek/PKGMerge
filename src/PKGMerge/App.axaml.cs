using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ColekUI.Dialogs;
using ColekUI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using PKGMerge.Repositories.Settings;
using PKGMerge.Services.Diagnostics;
using PKGMerge.Services.Files;
using PKGMerge.ViewModels.Merge;
using PKGMerge.Views.Shell;

namespace PKGMerge;

public class App : Application
{
    private IServiceProvider? Services { get; set; }

    private bool _isShowingError;

    public static IServiceProvider GetServices() => ((App)Current!).Services!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ServiceCollection services = new();

        // Register Services
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IWindowPlacementStore, WindowPlacementStore>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();

        // Register ViewModels
        services.AddSingleton<MergeViewModel>();

        Services = services.BuildServiceProvider();

        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                ErrorLog.Write(exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write(e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        bool logged = ErrorLog.Write(e.Exception);
        if (_isShowingError)
            return;

        _isShowingError = true;
        try
        {
            string details = logged ? $"\n\nThe details were written to '{ErrorLog.FilePath}'." : string.Empty;
            await GetServices().GetRequiredService<IDialogService>().ShowErrorAsync("Something went wrong", e.Exception.Message + details);
        }
        catch (Exception)
        {
            //
        }
        finally
        {
            _isShowingError = false;
        }
    }
}
