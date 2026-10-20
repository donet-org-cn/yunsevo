using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using System.Globalization;

namespace Yunsevo.Mobile.Android;

internal static class CameraExif
{
    // Copy photographic values, not offset-dependent thumbnails or MakerNote binary blocks.
    private static readonly string[] Preserved = [
        "Make", "Model", "DateTime", "DateTimeOriginal", "DateTimeDigitized", "SubSecTime", "SubSecTimeOriginal", "SubSecTimeDigitized",
        "FNumber", "ExposureTime", "ISOSpeedRatings", "PhotographicSensitivity", "ExposureBiasValue", "FocalLength", "FocalLengthIn35mmFilm",
        "MaxApertureValue", "MeteringMode", "SubjectDistance", "Flash", "WhiteBalance", "ExposureProgram", "ExposureMode", "SceneCaptureType",
        "ShutterSpeedValue", "ApertureValue", "BrightnessValue", "ExposureIndex", "SensingMethod", "SubjectArea", "SubjectLocation",
        "OffsetTime", "OffsetTimeOriginal", "OffsetTimeDigitized",
        "DigitalZoomRatio", "ColorSpace", "Artist", "Copyright", "LensMake", "LensModel", "LensSpecification", "LightSource",
        "XResolution", "YResolution", "ResolutionUnit"
    ];
    public static Dictionary<string, string> FromResult(TotalCaptureResult result, float zoom, double exposureStep)
    {
        var values = new Dictionary<string, string>();
        void Number(CaptureResult.Key key, string tag, Func<Java.Lang.Object, string?> convert)
        {
            using var value = result.Get(key); if (value != null && convert(value) is { } text) values[tag] = text;
        }
        Number(CaptureResult.SensorExposureTime!, "ExposureTime", value => value is Java.Lang.Long n && n.LongValue() > 0 ? n.LongValue().ToString(CultureInfo.InvariantCulture) + "/1000000000" : null);
        Number(CaptureResult.SensorSensitivity!, "ISOSpeedRatings", value => value is Java.Lang.Integer n ? n.IntValue().ToString(CultureInfo.InvariantCulture) : null);
        Number(CaptureResult.LensAperture!, "FNumber", value => value is Java.Lang.Float n ? n.FloatValue().ToString("0.####", CultureInfo.InvariantCulture) : null);
        Number(CaptureResult.LensFocalLength!, "FocalLength", value => value is Java.Lang.Float n ? n.FloatValue().ToString("0.####", CultureInfo.InvariantCulture) : null);
        Number(CaptureResult.ControlAeExposureCompensation!, "ExposureBiasValue", value => value is Java.Lang.Integer n ? (n.IntValue() * exposureStep).ToString("0.####", CultureInfo.InvariantCulture) : null);
        Number(CaptureResult.LensFocusDistance!, "SubjectDistance", value => value is Java.Lang.Float n && n.FloatValue() > 0 ? (1 / n.FloatValue()).ToString("0.####", CultureInfo.InvariantCulture) : null);
        Number(CaptureResult.ControlAwbMode!, "WhiteBalance", value => value is Java.Lang.Integer n ? n.IntValue() == 1 /* CONTROL_AWB_MODE_AUTO */ ? "0" : "1" : null);
        values["DigitalZoomRatio"] = zoom.ToString("0.####", CultureInfo.InvariantCulture);
        return values;
    }
    public static void Write(ExifInterface original, string output, int width, int height, DateTimeOffset captured, string photographer,
        string description, Guid id, IReadOnlyDictionary<string, string>? camera, double? latitude, double? longitude, string? attribution)
    {
        using var target = new ExifInterface(output);
        foreach (var tag in Preserved)
        {
            var value = original.GetAttribute(tag);
            if (!string.IsNullOrEmpty(value)) target.SetAttribute(tag, value);
        }
        if (camera != null) foreach (var pair in camera)
            if (string.IsNullOrEmpty(target.GetAttribute(pair.Key))) target.SetAttribute(pair.Key, pair.Value);
        void Fallback(string tag, string? value) { if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(target.GetAttribute(tag))) target.SetAttribute(tag, value); }
        Fallback("Make", Build.Manufacturer); Fallback("Model", Build.Model);
        var local = captured.ToLocalTime(); var time = local.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
        Fallback("DateTimeOriginal", time); Fallback("DateTimeDigitized", time);
        target.SetAttribute("DateTime", DateTimeOffset.Now.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        Fallback("SubSecTimeOriginal", local.ToString("fff", CultureInfo.InvariantCulture));
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Fallback("OffsetTimeOriginal", local.ToString("zzz", CultureInfo.InvariantCulture)); Fallback("OffsetTimeDigitized", local.ToString("zzz", CultureInfo.InvariantCulture));
        }
        target.SetAttribute("Orientation", "1");
        target.SetAttribute("ImageWidth", width.ToString(CultureInfo.InvariantCulture)); target.SetAttribute("ImageLength", height.ToString(CultureInfo.InvariantCulture));
        target.SetAttribute("PixelXDimension", width.ToString(CultureInfo.InvariantCulture)); target.SetAttribute("PixelYDimension", height.ToString(CultureInfo.InvariantCulture));
        target.SetAttribute("Software", "Yunsevo Android");
        target.SetAttribute("Artist", photographer); target.SetAttribute("ImageDescription", description);
        target.SetAttribute("ImageUniqueID", id.ToString("N")); target.SetAttribute("UserComment", description + (attribution == null ? "" : "\n" + attribution));
        Fallback("ColorSpace", "1"); Fallback("ResolutionUnit", "2"); Fallback("XResolution", "72/1"); Fallback("YResolution", "72/1");
        if (latitude.HasValue && longitude.HasValue)
        {
            static string Coordinate(double value)
            {
                value = Math.Abs(value); var degree = (int)value; var minutes = (value - degree) * 60; var minute = (int)minutes;
                return FormattableString.Invariant($"{degree}/1,{minute}/1,{(long)Math.Round((minutes - minute) * 60 * 10000000)}/10000000");
            }
            target.SetAttribute("GPSLatitude", Coordinate(latitude.Value)); target.SetAttribute("GPSLatitudeRef", latitude < 0 ? "S" : "N");
            target.SetAttribute("GPSLongitude", Coordinate(longitude.Value)); target.SetAttribute("GPSLongitudeRef", longitude < 0 ? "W" : "E");
        }
        target.SaveAttributes();
    }
}
