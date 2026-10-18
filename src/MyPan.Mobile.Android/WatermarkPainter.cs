using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Widget;
using MyPan.Mobile.Core;
using System.Text.RegularExpressions;

namespace MyPan.Mobile.Android;

internal static class WatermarkPainter
{
    public static readonly string[] Styles = ["工程卡片", "简约文字", "横幅记录"];
    public static Color TextColor(MobileOptions options)
    {
        try { return Color.ParseColor(options.WatermarkTextColor); } catch (Java.Lang.IllegalArgumentException) { return Color.White; }
    }
    public static void Draw(Canvas canvas, int width, int height, string text, MobileOptions options)
    {
        var style = Styles.Contains(options.WatermarkStyle) ? options.WatermarkStyle : Styles[0];
        var scale = float.IsFinite(options.WatermarkTextScale) ? Math.Clamp(options.WatermarkTextScale, .65f, 1.8f) : 1;
        using var paint = new Paint(PaintFlags.AntiAlias) { Color = TextColor(options), TextSize = Math.Max(14, width * .026f * scale) };
        var margin = width * .035f; var columns = style == "横幅记录" ? 2 : 1;
        var available = (width - margin * 2.7f) / columns;
        List<string> Wrap()
        {
            var result = new List<string>();
            foreach (var paragraph in text.Split('\n'))
            {
                var remaining = paragraph;
                while (remaining.Length > 0)
                {
                    var take = Math.Max(1, paint.BreakText(remaining, true, available, null));
                    if (take < remaining.Length && char.IsHighSurrogate(remaining[take - 1])) take = take > 1 ? take - 1 : Math.Min(2, remaining.Length);
                    result.Add(remaining[..take]); remaining = remaining[take..];
                }
            }
            return result;
        }
        var lines = Wrap();
        while ((int)Math.Ceiling(lines.Count / (double)columns) * paint.TextSize * 1.5f + margin * 2 > height * .55f && paint.TextSize > 10)
        { paint.TextSize *= .9f; lines = Wrap(); }
        var spacing = paint.TextSize * 1.5f; var rows = (lines.Count + columns - 1) / columns;
        var top = Math.Max(margin, height - margin * 1.5f - spacing * rows);
        var left = style == "横幅记录" ? 0 : margin * .45f;
        var right = style == "工程卡片" ? Math.Min(width - margin * .45f, lines.Select(paint.MeasureText).DefaultIfEmpty(0).Max() + margin * 2) : width;
        if (options.WatermarkBackground)
        {
            using var panel = new Paint { Color = Color.Argb(style == "简约文字" ? 115 : 178, 10, 32, 42) };
            canvas.DrawRoundRect(left, top - margin * .3f, right, height - margin * .4f, style == "工程卡片" ? margin * .3f : 0, style == "工程卡片" ? margin * .3f : 0, panel);
        }
        else paint.SetShadowLayer(Math.Max(1, width * .002f), 1, 1, Color.Argb(220, 0, 0, 0));
        if (style != "简约文字")
        {
            using var accent = new Paint { Color = Ui.Teal };
            if (style == "横幅记录") canvas.DrawRect(0, top - margin * .3f, width, top - margin * .3f + Math.Max(3, margin * .12f), accent);
            else canvas.DrawRect(margin * .65f, top, margin * .82f, height - margin * .75f, accent);
        }
        for (var index = 0; index < lines.Count; index++)
        {
            var column = columns == 1 ? 0 : index % 2; var row = columns == 1 ? index : index / 2;
            paint.FakeBoldText = index == 0 && style != "简约文字";
            canvas.DrawText(lines[index], margin * 1.1f + column * (width / 2f), top + spacing * (row + .85f), paint);
        }
    }
    public static Bitmap Overlay(int width, int height, string text, MobileOptions options)
    {
        var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap); canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear!); Draw(canvas, width, height, text, options); return bitmap;
    }
    public static void Settings(Activity activity, MobileOptions current, Action<MobileOptions> save)
    {
        var body = Ui.Column(activity, 16); var draft = current;
        body.AddView(Ui.Text(activity, "水印样式", 17, true));
        var styles = new Spinner(activity); styles.Adapter = new ArrayAdapter<string>(activity, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, Styles);
        styles.SetSelection(Math.Max(0, Array.IndexOf(Styles, current.WatermarkStyle))); body.AddView(styles);
        var background = new CheckBox(activity) { Text = "显示水印底色", Checked = current.WatermarkBackground }; body.AddView(background);
        var colors = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        var color = Ui.Input(activity, "字体颜色，例如 #FFFFFF", current.WatermarkTextColor);
        foreach (var value in new[] { "#FFFFFF", "#FFD54F", "#101820", "#FF5252", "#00E5C8" })
        {
            var swatch = new Button(activity) { ContentDescription = "字体颜色 " + value }; swatch.SetBackgroundColor(Color.ParseColor(value));
            swatch.LayoutParameters = new LinearLayout.LayoutParams(0, Ui.Dp(activity, 38), 1) { RightMargin = Ui.Dp(activity, 5) };
            swatch.Click += (_, _) => color.Text = value; colors.AddView(swatch);
        }
        body.AddView(Ui.Text(activity, "字体颜色", 14)); body.AddView(colors); body.AddView(color);
        body.AddView(Ui.Text(activity, "字体大小", 14));
        var size = new SeekBar(activity) { Max = 115, Progress = (int)(Math.Clamp(current.WatermarkTextScale, .65f, 1.8f) * 100 - 65) }; body.AddView(size);
        var preview = new ImageView(activity); preview.SetBackgroundColor(Color.Rgb(70, 93, 108)); body.AddView(preview, new LinearLayout.LayoutParams(-1, Ui.Dp(activity, 200)));
        Bitmap? rendered = null;
        void Refresh()
        {
            draft = draft with { WatermarkStyle = Styles[styles.SelectedItemPosition < 0 ? 0 : styles.SelectedItemPosition], WatermarkBackground = background.Checked,
                WatermarkTextColor = Regex.IsMatch(color.Text ?? "", "^#[0-9a-fA-F]{6}$") ? color.Text! : "#FFFFFF", WatermarkTextScale = (65 + size.Progress) / 100f };
            var previous = rendered; rendered = Overlay(720, 480, "沄序 · 工程记录\n工程：示范工程\n拍摄人：" + (AppServices.Client.Account?.Login.User.DisplayName ?? "现场人员") + "\n时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"), draft);
            preview.SetImageBitmap(rendered); previous?.Dispose();
        }
        styles.ItemSelected += (_, _) => Refresh(); background.CheckedChange += (_, _) => Refresh(); color.TextChanged += (_, _) => Refresh(); size.ProgressChanged += (_, _) => Refresh(); Refresh();
        var scroll = new ScrollView(activity); scroll.AddView(body);
        var dialog = new AlertDialog.Builder(activity)!.SetTitle("水印设置")!.SetView(scroll)!.SetNegativeButton("取消", (_, _) => { })!.SetPositiveButton("保存", (_, _) => { })!.Create()!;
        dialog.Show(); dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            if (!Regex.IsMatch(color.Text ?? "", "^#[0-9a-fA-F]{6}$")) { color.Error = "请输入 #RRGGBB 格式的颜色"; return; }
            Refresh(); save(draft); dialog.Dismiss();
        };
        dialog.DismissEvent += (_, _) => { preview.SetImageDrawable(null); rendered?.Dispose(); };
    }
}
