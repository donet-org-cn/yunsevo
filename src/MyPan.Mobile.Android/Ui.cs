using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using MyPan.Contracts;

namespace MyPan.Mobile.Android;

internal static class Ui
{
    public static readonly Color Teal = Color.Rgb(8, 143, 151), Ink = Color.Rgb(24, 53, 69), Muted = Color.Rgb(105, 126, 141), Background = Color.Rgb(245, 248, 250);
    public static int Dp(Context context, int value) => (int)(value * context.Resources!.DisplayMetrics!.Density + .5f);
    public static TextView Text(Context context, string value, float size = 15, bool bold = false)
    {
        var text = new TextView(context) { Text = value, TextSize = size };
        text.SetTextColor(Ink); if (bold) text.SetTypeface(null, TypefaceStyle.Bold);
        text.SetPadding(0, Dp(context, 4), 0, Dp(context, 4)); return text;
    }
    public static GradientDrawable Shape(Color color, int radius = 12)
    {
        var shape = new GradientDrawable(); shape.SetColor(color); shape.SetCornerRadius(radius); return shape;
    }
    public static LinearLayout Column(Context context, int padding = 20)
    {
        var layout = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var px = Dp(context, padding); layout.SetPadding(px, px, px, px); return layout;
    }
    public static Button Button(Context context, string title, Action action, bool primary = true)
    {
        var button = new Button(context) { Text = title }; button.SetAllCaps(false);
        button.SetTextColor(primary ? Color.White : Teal);
        button.Background = Shape(primary ? Teal : Color.Rgb(230, 245, 246));
        button.SetPadding(Dp(context, 12), Dp(context, 8), Dp(context, 12), Dp(context, 8));
        button.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(context, 48)) { TopMargin = Dp(context, 10), BottomMargin = Dp(context, 4) };
        button.Click += (_, _) => action(); return button;
    }
    public static EditText Input(Context context, string hint, string? value = null, bool password = false)
    {
        var edit = new EditText(context) { Hint = hint, Text = value ?? "" }; edit.SetSingleLine(true);
        edit.SetTextColor(Ink); edit.TextSize = 15;
        edit.InputType = password ? global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword : global::Android.Text.InputTypes.ClassText;
        edit.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(context, 52)) { BottomMargin = Dp(context, 10) };
        return edit;
    }
    public static void Alert(Activity activity, string title, string message)
        => new AlertDialog.Builder(activity)!.SetTitle(title)!.SetMessage(message)!.SetPositiveButton("知道了", (_, _) => { })!.Show();
    public static string PathLabel(string path)
    {
        var user = AppServices.Client.Account?.Login.User.Username;
        var prefix = "个人/" + user;
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ? "/个人" :
            path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? "/个人" + path[prefix.Length..] : "/" + path;
    }
    public static string Size(long size) => size < 1024 ? size + " B" : size < 1048576 ? (size / 1024d).ToString("0.#") + " KB" :
        size < 1073741824 ? (size / 1048576d).ToString("0.#") + " MB" : (size / 1073741824d).ToString("0.#") + " GB";
    public static string Category(string category) => category switch { "private" => "私有", "public" => "公共", "container" => "仅可浏览", _ => "分配给我" };
    public static string Description(CloudEntry file)
    {
        var origin = file.Origin;
        if (origin == null) return "历史文件，未记录拍摄或上传来源";
        return string.Join("\n", new[] {
            origin.Photographer == null ? null : "拍摄人：" + origin.Photographer.DisplayName + "（" + origin.Photographer.Username + "）",
            origin.CapturedUtc?.ToLocalTime() is { } captured ? "拍摄时间：" + captured.ToString("yyyy-MM-dd HH:mm:ss zzz") + "（设备上报）" : null,
            origin.Project == null ? null : "工程：" + origin.Project,
            origin.Place == null ? null : "部位：" + origin.Place,
            origin.Note == null ? null : "备注：" + origin.Note,
            origin.Location == null ? null : "位置：" + origin.Location,
            "上传人：" + origin.UploadedBy.DisplayName + "（" + origin.UploadedBy.Username + "）",
            "上传时间：" + origin.UploadedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            origin.DeviceName == null ? null : "设备：" + origin.DeviceName,
            "来源：" + (origin.Kind switch { "camera" => "工程水印拍照", "album" => "相册备份", "shared" => "其他应用分享", _ => "文件上传" })
        }.Where(x => x != null));
    }
}
