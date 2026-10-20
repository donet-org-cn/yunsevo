using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Yunsevo.Desktop;

internal static class DesktopScreenshots
{
    public static async Task Save(Window window, string path)
    {
        await Task.Delay(200);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
