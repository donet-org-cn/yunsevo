using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using MyPan.Contracts;

namespace MyPan.Mobile.Android;

public sealed partial class MainActivity
{
    private CancellationTokenSource? albumStop;
    private GalleryAdapter? gallery;
    private GridView? galleryGrid;
    private TextView? galleryFooter;
    private string albumFolder = "", albumAccount = "", nextAlbumCursor = "";
    private int albumScope, albumTotal;
    private bool albumLoading, albumMore;
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (galleryGrid == null || gallery == null) return;
        var columns = newConfig.Orientation == global::Android.Content.Res.Orientation.Landscape ? 5 : 3;
        var first = galleryGrid.FirstVisiblePosition; galleryGrid.NumColumns = columns; gallery.ChangeColumns(columns); galleryGrid.SetSelection(first);
    }
    private void ClearAlbumImages()
    {
        albumStop?.Cancel(); albumStop?.Dispose(); albumStop = null;
        gallery?.Release(); gallery = null; galleryGrid = null; albumLoading = false;
    }
    private async Task RenderCloudAlbum()
    {
        ClearAlbumImages(); albumStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        albumAccount = Client.AccountKey; albumFolder = ""; nextAlbumCursor = ""; albumMore = true;
        var body = Ui.Column(this, 10); body.SetBackgroundColor(Ui.Background);
        body.AddView(Ui.Text(this, "云端相册", 23, true));
        var toolbar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var labels = new[] { "全部照片与视频", "相册备份目录", "当前工程目录" };
        void Action(string title, System.Action action, float weight)
        {
            var button = Ui.Button(this, title, action, false); button.TextSize = 12;
            button.LayoutParameters = new LinearLayout.LayoutParams(0, Ui.Dp(this, 40), weight) { RightMargin = Ui.Dp(this, 5), BottomMargin = Ui.Dp(this, 8) }; toolbar.AddView(button);
        }
        Action("备份设置", () => _ = Run(RenderBackup), 1);
        Action(labels[albumScope] + " ▾", () => new global::Android.App.AlertDialog.Builder(this)!.SetTitle("查看范围")!.SetItems(labels, (_, args) =>
        { albumScope = args.Which; _ = Run(RenderCloudAlbum); })!.Show(), 1.65f);
        Action("刷新相册", () => _ = Run(RenderCloudAlbum), 1); body.AddView(toolbar);
        var columns = Resources!.Configuration!.Orientation == global::Android.Content.Res.Orientation.Landscape ? 5 : 3;
        galleryGrid = new GridView(this) { NumColumns = columns, StretchMode = StretchMode.StretchColumnWidth };
        galleryGrid.SetVerticalSpacing(Ui.Dp(this, 3)); galleryGrid.SetHorizontalSpacing(Ui.Dp(this, 3));
        gallery = new(this, columns); galleryGrid.Adapter = gallery; galleryGrid.SetRecyclerListener(gallery);
        galleryGrid.SetOnScrollListener(new GalleryScroll(this));
        galleryGrid.ItemClick += (_, args) =>
        {
            if (gallery == null || args.Position >= gallery.Count) return;
            var entry = gallery[args.Position];
            StartActivity(new Intent(this, typeof(PreviewActivity)).PutExtra("id", entry.Id.ToString()).PutExtra("path", entry.Path).PutExtra("albumFolder", albumFolder));
        };
        body.AddView(galleryGrid, new LinearLayout.LayoutParams(-1, 0, 1));
        galleryFooter = Ui.Text(this, "正在加载照片…", 11); galleryFooter.Gravity = GravityFlags.Center;
        galleryFooter.Click += (_, _) => _ = LoadMoreAlbum(); body.AddView(galleryFooter);
        workspaceBody.RemoveAllViews(); workspaceBody.AddView(body);
        var selected = albumScope == 1 ? Client.Options.BackupFolder : albumScope == 2 ? Client.Options.Project : null;
        if (albumScope != 0)
        {
            if (selected == null) { albumMore = false; galleryFooter.Text = albumScope == 1 ? "请先在备份设置中选择目录。" : "请先选择工程目录。"; return; }
            albumFolder = (await Client.Folders(albumStop.Token)).FirstOrDefault(x => x.Id == selected.Id && (x.Access & 1) != 0)?.Path ?? throw new IOException("相册目录已移动或失去访问权限，请重新选择。");
        }
        await LoadMoreAlbum();
    }
    private async Task LoadMoreAlbum()
    {
        if (albumLoading || !albumMore || gallery == null || albumStop == null) return;
        var target = gallery; var ct = albumStop.Token; var footer = galleryFooter!;
        albumLoading = true; footer.Text = "正在加载…";
        try
        {
            var result = await Client.Album(albumFolder, 1, ct, nextAlbumCursor.Length == 0 ? null : nextAlbumCursor);
            if (ct.IsCancellationRequested || target != gallery || Client.AccountKey != albumAccount) return;
            target.Append(result.Items.Select(x => x.Entry)); albumTotal = result.Total;
            nextAlbumCursor = result.NextCursor ?? ""; albumMore = nextAlbumCursor.Length > 0;
            footer.Text = albumTotal == 0 ? "云端还没有照片或视频" : albumMore ? "已加载 " + target.Count + " / " + albumTotal + " 项 · 上滑继续浏览" : "共 " + target.Count + " 项";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (target == gallery) footer.Text = "加载未完成 · 点击重试：" + ex.Message; }
        finally { if (target == gallery) albumLoading = false; }
    }
    private sealed class GalleryScroll(MainActivity owner) : Java.Lang.Object, AbsListView.IOnScrollListener
    {
        public void OnScrollStateChanged(AbsListView? view, ScrollState state) { }
        public void OnScroll(AbsListView? view, int firstVisibleItem, int visibleItemCount, int totalItemCount)
        { if (totalItemCount > 0 && firstVisibleItem + visibleItemCount >= totalItemCount - 6) _ = owner.LoadMoreAlbum(); }
    }
    private sealed class GalleryTile : Java.Lang.Object
    {
        public ImageView Image = null!;
        public TextView Caption = null!, Placeholder = null!;
        public Guid Id;
        public Bitmap? Bitmap;
        public CancellationTokenSource? Request;
        public void Clear()
        { Request?.Cancel(); Request?.Dispose(); Request = null; Image.SetImageDrawable(null); Bitmap?.Dispose(); Bitmap = null; Id = Guid.Empty; }
    }
    private sealed class GalleryAdapter(MainActivity owner, int columns) : BaseAdapter<CloudEntry>, AbsListView.IRecyclerListener
    {
        private readonly List<CloudEntry> entries = [];
        private readonly HashSet<Guid> ids = [];
        private readonly List<GalleryTile> tiles = [];
        private bool released;
        private int columnCount = columns;
        public void ChangeColumns(int value) { columnCount = value; NotifyDataSetChanged(); }
        public override int Count => entries.Count;
        public override CloudEntry this[int position] => entries[position];
        public override long GetItemId(int position) => position;
        public void Append(IEnumerable<CloudEntry> added) { entries.AddRange(added.Where(x => ids.Add(x.Id))); NotifyDataSetChanged(); }
        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var cell = convertView as FrameLayout; GalleryTile tile;
            if (cell == null)
            {
                cell = new FrameLayout(owner) { ImportantForAccessibility = ImportantForAccessibility.Yes, Clickable = true }; cell.SetBackgroundColor(Color.Rgb(224, 232, 236)); tile = new(); cell.Tag = tile; tiles.Add(tile);
                cell.Click += (_, _) =>
                {
                    if (released || tile.Id == Guid.Empty) return;
                    var selected = entries.FirstOrDefault(x => x.Id == tile.Id);
                    if (selected != null) owner.StartActivity(new Intent(owner, typeof(PreviewActivity))
                        .PutExtra("id", selected.Id.ToString()).PutExtra("path", selected.Path).PutExtra("albumFolder", owner.albumFolder));
                };
                tile.Image = new ImageView(owner) { ImportantForAccessibility = ImportantForAccessibility.No }; tile.Image.SetScaleType(ImageView.ScaleType.CenterCrop); cell.AddView(tile.Image, new FrameLayout.LayoutParams(-1, -1));
                tile.Placeholder = Ui.Text(owner, "", 18); tile.Placeholder.ImportantForAccessibility = ImportantForAccessibility.No; tile.Placeholder.Gravity = GravityFlags.Center; cell.AddView(tile.Placeholder, new FrameLayout.LayoutParams(-1, -1));
                tile.Caption = Ui.Text(owner, "", 10); tile.Caption.ImportantForAccessibility = ImportantForAccessibility.No; tile.Caption.SetTextColor(Color.White); tile.Caption.SetSingleLine(true); tile.Caption.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
                tile.Caption.SetPadding(Ui.Dp(owner, 5), Ui.Dp(owner, 3), Ui.Dp(owner, 5), Ui.Dp(owner, 3)); tile.Caption.SetBackgroundColor(Color.Argb(140, 10, 24, 30));
                cell.AddView(tile.Caption, new FrameLayout.LayoutParams(-1, -2, GravityFlags.Bottom));
            }
            else tile = (GalleryTile)cell.Tag!;
            tile.Clear(); var entry = entries[position]; tile.Id = entry.Id;
            var size = Math.Max(Ui.Dp(owner, 60), ((parent?.Width ?? 0) > 0 ? parent!.Width : owner.Resources!.DisplayMetrics!.WidthPixels - Ui.Dp(owner, 20)) / columnCount);
            cell.LayoutParameters = new AbsListView.LayoutParams(-1, size);
            cell.ContentDescription = "查看云端媒体 " + Path.GetFileName(entry.Path);
            tile.Caption.Text = (CloudMedia.IsVideo(entry.Path) ? "▶ " : "") + (entry.Origin?.CapturedUtc ?? entry.ModifiedUtc).ToLocalTime().ToString("MM-dd") + (entry.Origin?.Photographer is { } who ? " · " + who.DisplayName : "");
            tile.Placeholder.Text = CloudMedia.IsVideo(entry.Path) ? "▷" : "▧"; tile.Placeholder.Visibility = ViewStates.Visible;
            tile.Request = new(); _ = Load(tile, entry, tile.Request.Token); return cell;
        }
        private async Task Load(GalleryTile tile, CloudEntry entry, CancellationToken ct)
        {
            try
            {
                var bitmap = await CloudMedia.Thumbnail(entry, ct); if (bitmap == null) return;
                if (released || ct.IsCancellationRequested || tile.Id != entry.Id) { bitmap.Dispose(); return; }
                tile.Bitmap = bitmap; tile.Image.SetImageBitmap(bitmap); tile.Placeholder.Visibility = ViewStates.Gone;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        public void OnMovedToScrapHeap(View? view) { if (view?.Tag is GalleryTile tile) tile.Clear(); }
        public void Release() { released = true; foreach (var tile in tiles) tile.Clear(); tiles.Clear(); }
    }
}
