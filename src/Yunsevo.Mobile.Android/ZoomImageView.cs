using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace Yunsevo.Mobile.Android;

internal sealed class ZoomImageView : ImageView
{
    public Action<int>? Navigate { get; init; }
    public bool IsAtBaseScale => factor <= 1.05f;
    private readonly Matrix transform = new();
    private readonly ScaleGestureDetector scale;
    private readonly GestureDetector gestures;
    private Bitmap? bitmap;
    private float fitted = 1, factor = 1, lastX, lastY;
    private bool multiTouch;
    public ZoomImageView(Context context, Bitmap bitmap) : base(context)
    {
        this.bitmap = bitmap; SetImageBitmap(bitmap); SetScaleType(ScaleType.Matrix);
        SetBackgroundColor(Color.Rgb(12, 20, 27));
        scale = new(context, new ScaleListener(this));
        gestures = new(context, new Gestures(this));
        ContentDescription = "图片预览，缩放 100%，支持双指缩放、双击放大和拖动";
    }
    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    { base.OnSizeChanged(w, h, oldw, oldh); Fit(); }
    private void Fit()
    {
        if (bitmap == null || Width <= 0 || Height <= 0) return;
        fitted = Math.Min(Width / (float)bitmap.Width, Height / (float)bitmap.Height); factor = 1;
        transform.Reset(); transform.PostScale(fitted, fitted);
        transform.PostTranslate((Width - bitmap.Width * fitted) / 2, (Height - bitmap.Height * fitted) / 2);
        Apply();
    }
    private void Zoom(float change, float x, float y)
    {
        var next = Math.Clamp(factor * change, 1, 8); change = next / factor; factor = next;
        transform.PostScale(change, change, x, y); Clamp(); Apply();
    }
    private void Clamp()
    {
        if (bitmap == null) return;
        using var bounds = new RectF(0, 0, bitmap.Width, bitmap.Height); transform.MapRect(bounds);
        var x = bounds.Width() < Width ? Width / 2f - bounds.CenterX() : bounds.Left > 0 ? -bounds.Left : bounds.Right < Width ? Width - bounds.Right : 0;
        var y = bounds.Height() < Height ? Height / 2f - bounds.CenterY() : bounds.Top > 0 ? -bounds.Top : bounds.Bottom < Height ? Height - bounds.Bottom : 0;
        transform.PostTranslate(x, y);
    }
    private void Apply()
    {
        ImageMatrix = transform;
        ContentDescription = "图片预览，缩放 " + (int)Math.Round(factor * 100) + "%，支持双指缩放、双击放大和拖动";
        Invalidate();
    }
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked == MotionEventActions.Down) multiTouch = false;
        if (e.PointerCount > 1) multiTouch = true;
        Parent?.RequestDisallowInterceptTouchEvent(true);
        scale.OnTouchEvent(e); gestures.OnTouchEvent(e);
        if (e.ActionMasked == MotionEventActions.Down) { lastX = e.GetX(); lastY = e.GetY(); }
        else if (e.ActionMasked == MotionEventActions.Move)
        {
            if (!scale.IsInProgress && e.PointerCount == 1 && factor > 1)
            { transform.PostTranslate(e.GetX() - lastX, e.GetY() - lastY); Clamp(); Apply(); }
            lastX = e.GetX(); lastY = e.GetY();
        }
        else if (e.ActionMasked == MotionEventActions.PointerUp)
        {
            var remaining = e.ActionIndex == 0 ? 1 : 0;
            if (remaining < e.PointerCount) { lastX = e.GetX(remaining); lastY = e.GetY(remaining); }
        }
        else if (e.ActionMasked == MotionEventActions.Up) PerformClick();
        return true;
    }
    public override bool PerformClick() { base.PerformClick(); return true; }
    public void ReleaseImage() { SetImageDrawable(null); bitmap?.Dispose(); bitmap = null; }
    private sealed class ScaleListener(ZoomImageView owner) : ScaleGestureDetector.SimpleOnScaleGestureListener
    { public override bool OnScale(ScaleGestureDetector detector) { owner.Zoom(detector.ScaleFactor, detector.FocusX, detector.FocusY); return true; } }
    private sealed class Gestures(ZoomImageView owner) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;
        public override bool OnDoubleTap(MotionEvent e) { if (owner.factor > 1.1f) owner.Fit(); else owner.Zoom(3, e.GetX(), e.GetY()); return true; }
        public override bool OnFling(MotionEvent? first, MotionEvent second, float velocityX, float velocityY)
        {
            if (first == null || owner.multiTouch || owner.factor > 1.05f || owner.Navigate == null) return false;
            var dx = second.GetX() - first.GetX(); var dy = second.GetY() - first.GetY();
            if (Math.Abs(dx) < Ui.Dp(owner.Context!, 70) || Math.Abs(dx) < Math.Abs(dy) * 1.3f || Math.Abs(velocityX) < 250) return false;
            owner.Navigate(dx < 0 ? 1 : -1); return true;
        }
    }
}
