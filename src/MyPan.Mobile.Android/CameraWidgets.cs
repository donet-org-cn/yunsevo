using Android.Content;
using Android.Graphics;
using Android.Views;

namespace MyPan.Mobile.Android;

internal sealed class CameraGlyph(Context context, string glyph) : View(context)
{
    private bool active;
    public bool Active { get => active; set { active = value; Invalidate(); } }
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas); canvas.Save(); canvas.Translate(Width / 2f, Height / 2f); canvas.Scale(Width / 48f, Height / 48f);
        using var paint = new Paint(PaintFlags.AntiAlias) { Color = active ? Color.Rgb(255, 215, 74) : Color.White, StrokeWidth = 1.9f, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
        paint.SetStyle(Paint.Style.Stroke);
        if (glyph == "close") { canvas.DrawLine(-8, -8, 8, 8, paint); canvas.DrawLine(8, -8, -8, 8, paint); }
        else if (glyph == "more") { paint.SetStyle(Paint.Style.Fill); for (var x = -7; x <= 7; x += 7) canvas.DrawCircle(x, 0, 1.7f, paint); }
        else if (glyph == "album")
        {
            canvas.DrawRoundRect(-11, -11, 11, 11, 3, 3, paint); canvas.DrawCircle(5, -5, 2, paint);
            using var path = new global::Android.Graphics.Path(); path.MoveTo(-10, 6); path.LineTo(-4, -1); path.LineTo(2, 5); path.LineTo(6, 1); path.LineTo(11, 7); canvas.DrawPath(path, paint);
        }
        else if (glyph == "flash")
        {
            using var path = new global::Android.Graphics.Path(); path.MoveTo(2, -13); path.LineTo(-9, 2); path.LineTo(-1, 2); path.LineTo(-3, 13); path.LineTo(10, -4); path.LineTo(2, -4); path.Close(); canvas.DrawPath(path, paint);
            if (!active) canvas.DrawLine(-11, -11, 11, 11, paint);
        }
        else if (glyph == "flip")
        {
            canvas.DrawRoundRect(-9, -7, 9, 7, 2.5f, 2.5f, paint); canvas.DrawCircle(0, 0, 3.3f, paint);
            using var bounds = new RectF(-15, -15, 15, 15); canvas.DrawArc(bounds, 210, 110, false, paint); canvas.DrawArc(bounds, 30, 110, false, paint);
            canvas.DrawLine(12, -12, 12, -5, paint); canvas.DrawLine(12, -5, 6, -7, paint);
            canvas.DrawLine(-12, 12, -12, 5, paint); canvas.DrawLine(-12, 5, -6, 7, paint);
        }
        canvas.Restore();
    }
}

internal sealed class CameraShutter(Context context) : View(context)
{
    private bool recording;
    public bool Recording { get => recording; set { if (recording == value) return; recording = value; Invalidate(); } }
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas); var x = Width / 2f; var y = Height / 2f; var radius = Math.Min(Width, Height) * .405f;
        using var paint = new Paint(PaintFlags.AntiAlias) { Color = Color.White, StrokeWidth = Math.Max(2, radius * .045f) };
        paint.SetStyle(Paint.Style.Stroke); canvas.DrawCircle(x, y, radius, paint); paint.SetStyle(Paint.Style.Fill);
        paint.Color = recording ? Color.Rgb(250, 77, 77) : Color.White;
        if (recording) canvas.DrawRoundRect(x - radius * .4f, y - radius * .4f, x + radius * .4f, y + radius * .4f, radius * .13f, radius * .13f, paint);
        else canvas.DrawCircle(x, y, radius * (Pressed ? .75f : .84f), paint);
    }
    public override bool PerformClick() { base.PerformClick(); return true; }
}

// Composition guides are preview-only; they are never burnt into reconstruction input.
internal sealed class ScanGuide(Context context) : View(context)
{
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        using var paint = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(105, 255, 255, 255), StrokeWidth = Resources!.DisplayMetrics!.Density };
        var top = Height * .17f; var bottom = Height * .73f; var left = Width * .08f; var right = Width * .92f;
        for (var i = 1; i <= 2; i++)
        {
            var x = left + (right - left) * i / 3; var y = top + (bottom - top) * i / 3;
            canvas.DrawLine(x, top, x, bottom, paint); canvas.DrawLine(left, y, right, y, paint);
        }
        paint.Color = Color.Argb(200, 23, 194, 189);
        var cx = (left + right) / 2; var cy = (top + bottom) / 2;
        canvas.DrawLine(cx - 12, cy, cx + 12, cy, paint); canvas.DrawLine(cx, cy - 12, cx, cy + 12, paint);
    }
}
