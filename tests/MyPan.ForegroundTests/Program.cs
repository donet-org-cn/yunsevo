using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MyPan.Mobile.Android;

if (args.Length != 4) throw new ArgumentException("photos models native-runtime output.glb");
NativeLibrary.SetDllImportResolver(typeof(LocalForeground).Assembly, (name, _, _) => name == "yunsevo_reconstruction" ? NativeLibrary.Load(Path.Combine(Path.GetFullPath(args[2]), "yunsevo_reconstruction.dll")) : 0);
var files = Directory.GetFiles(args[0]).Where(x => Path.GetExtension(x) is ".jpg" or ".png").Order(StringComparer.Ordinal).ToArray();
var before = files.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
LocalForeground.Generate(files, args[1], Path.Combine(args[0], ".foreground"), (p, m) => Console.WriteLine($"{p}% {m}"), () => false);
Console.WriteLine("MASKS COMPLETE");
Native.Progress progress = (_, p, text) => { Console.WriteLine($"{p}% {Marshal.PtrToStringUTF8(text)}"); return 0; };
int code = Native.yv_reconstruct_v2(args[0], args[3], "cpu", 2, "object", progress, 0);
GC.KeepAlive(progress);
if (code != 0) throw new IOException(Marshal.PtrToStringUTF8(Native.yv_last_error()));
if (!files.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).SequenceEqual(before)) throw new IOException("Original photograph bytes changed");
Console.WriteLine("PASS local masks + native silhouette reconstruction; all original hashes unchanged");
internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Progress(nint user, int percent, nint message);
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] public static extern int yv_reconstruct_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string output, [MarshalAs(UnmanagedType.LPUTF8Str)] string backend, int quality, [MarshalAs(UnmanagedType.LPUTF8Str)] string subject, Progress progress, nint user);
    [DllImport("yunsevo_reconstruction", CallingConvention = CallingConvention.Cdecl)] public static extern nint yv_last_error();
}
