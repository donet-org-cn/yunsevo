using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MyPan.Contracts;

namespace MyPan.Desktop;
public sealed class DesktopApp : Application
{
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Light; Styles.Add(new FluentTheme()); DesktopTheme.Apply(this); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow();
            if (Program.Smoke) DispatcherTimer.RunOnce(() =>
            {
                var target = Environment.GetEnvironmentVariable("MYPAN_DESKTOP_HOME");
                if (target != null && desktop.MainWindow is { } window)
                {
                    Directory.CreateDirectory(target);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    bitmap.Render(window); bitmap.Save(Path.Combine(target, "window.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                desktop.Shutdown(0);
            }, TimeSpan.FromSeconds(2));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
