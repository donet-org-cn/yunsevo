using Android.Content;
using Android.Views;
using Android.Widget;

namespace Yunsevo.Mobile.Android;

// Handles horizontal paging over video/loading surfaces. ZoomImageView owns image gestures.
internal sealed class GalleryFrame(Context context, Func<bool> canPage, Action<int> page) : FrameLayout(context)
{
    private float startX, startY;
    private bool paging;
    public override bool OnInterceptTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked == MotionEventActions.Down) { startX = e.GetX(); startY = e.GetY(); paging = false; }
        else if (e.ActionMasked == MotionEventActions.Move && canPage())
        {
            var dx = Math.Abs(e.GetX() - startX); var dy = Math.Abs(e.GetY() - startY);
            if (dx > Ui.Dp(Context!, 28) && dx > dy * 1.4f) paging = true;
        }
        return paging;
    }
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked == MotionEventActions.Up && paging)
        {
            var dx = e.GetX() - startX; paging = false;
            if (Math.Abs(dx) >= Ui.Dp(Context!, 70) && canPage()) page(dx < 0 ? 1 : -1);
            PerformClick();
        }
        if (e.ActionMasked == MotionEventActions.Cancel) paging = false;
        return true;
    }
    public override bool PerformClick() { base.PerformClick(); return true; }
}
