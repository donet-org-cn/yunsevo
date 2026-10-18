using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MyPan.Desktop;

internal static class CloudIcons
{
    private static readonly Dictionary<string, WindowIcon> cache = [];
    public static WindowIcon Create(string state)
    {
        if (cache.TryGetValue(state, out var existing)) return existing;
        using var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var pixels = new byte[32 * 32 * 4];
        bool Circle(int x, int y, int cx, int cy, int radius) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
        {
            var diamond = x >= 3 && x <= 27 && Math.Abs(y - 10) <= 6 && Math.Abs(x - 15) + 2 * Math.Abs(y - 10) <= 13;
            var layers = x >= 3 && x <= 27 && (Math.Abs(y - (17 - Math.Abs(x - 15) / 2)) <= 1 || Math.Abs(y - (23 - Math.Abs(x - 15) / 2)) <= 1);
            var cloud = diamond || layers;
            var badge = Circle(x, y, 25, 25, 7); if (!cloud && !badge) continue;
            byte r = state == "signed-out" ? (byte)143 : (byte)11, g = state == "signed-out" ? (byte)156 : (byte)139, b = state == "signed-out" ? (byte)170 : (byte)144;
            if (badge) { (r, g, b) = state switch { "connected" => ((byte)22, (byte)151, (byte)90), "signed-out" => ((byte)100, (byte)112, (byte)128), "error" => ((byte)212, (byte)45, (byte)55), _ => ((byte)223, (byte)152, (byte)20) }; }
            var mark = badge && (state == "connected" ? (x is >= 21 and <= 24 && y - x is >= 3 and <= 4 || x is >= 24 and <= 29 && x + y is >= 50 and <= 51) : state == "signed-out" ? Circle(x, y, 25, 23, 2) || x is >= 22 and <= 28 && y is >= 27 and <= 29 : x is >= 24 and <= 25 && y is >= 21 and <= 26 || x is >= 24 and <= 25 && y == 29);
            if (mark) r = g = b = 255;
            var offset = (y * 32 + x) * 4; pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
        }
        using (var buffer = bitmap.Lock()) Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        using var memory = new MemoryStream(); bitmap.Save(memory, PngBitmapEncoderOptions.Default); memory.Position = 0; var icon = new WindowIcon(memory); cache[state] = icon; return icon;
    }
}
