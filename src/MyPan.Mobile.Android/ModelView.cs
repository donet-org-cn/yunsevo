using Android.Webkit;
using Android.OS;
using Android.App;
using Android.Content;
using Android.Widget;

namespace MyPan.Mobile.Android;

internal sealed class ModelView : WebView
{
    public ModelView(Context context, string file) : base(context)
    {
        Settings.JavaScriptEnabled = true; Settings.AllowFileAccess = false; Settings.AllowContentAccess = false;
        Settings.MixedContentMode = MixedContentHandling.NeverAllow; Settings.SetSupportMultipleWindows(false);
        Settings.DomStorageEnabled = false; SetWebViewClient(new LocalClient(context, file));
        LoadUrl("https://yunsevo-model.local/index.html");
    }
    private sealed class LocalClient(Context context, string file) : WebViewClient
    {
        private static WebResourceResponse Denied() => new("text/plain", "UTF-8", 403, "Forbidden", new Dictionary<string, string>(), new MemoryStream());
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => true;
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var uri = request?.Url; if (uri?.Scheme != "https" || uri.Host != "yunsevo-model.local" || request?.Method != "GET") return Denied();
            var path = uri.Path?.TrimStart('/');
            var headers = new Dictionary<string, string> { ["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; connect-src 'self' blob:; img-src blob: data:; worker-src blob:; base-uri 'none'", ["X-Content-Type-Options"] = "nosniff", ["Cache-Control"] = "no-store" };
            try
            {
                if (path == "model.glb") return new("model/gltf-binary", null, 200, "OK", headers, File.OpenRead(file));
                if (path is "index.html" or "viewer.js" or "viewer.css" or "model-viewer.min.js") return new(path.EndsWith(".js") ? "text/javascript" : path.EndsWith(".css") ? "text/css" : "text/html", "UTF-8", 200, "OK", headers, context.Assets!.Open("model-viewer/" + path));
            }
            catch (IOException) { }
            return Denied();
        }
    }
    public void Release() { StopLoading(); LoadUrl("about:blank"); RemoveAllViews(); Destroy(); }
}
[Activity(Label = "三维模型", Exported = false)]
public sealed class LocalModelActivity : Activity
{
    private ModelView? viewer;
    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state); if (!Guid.TryParse(Intent?.GetStringExtra("scan"), out var id)) { Finish(); return; }
        try
        {
            var session = ScanStore.Get(id); var body = new LinearLayout(this) { Orientation = Orientation.Vertical }; body.SetFitsSystemWindows(true);
            viewer = new(this, ScanStore.Model(id)); body.AddView(viewer, new LinearLayout.LayoutParams(-1, 0, 1));
            var actions = Ui.Column(this, 12); actions.AddView(Ui.Text(this, session.Name, 16, true)); actions.AddView(Ui.Text(this, "模型为相对尺寸，不能直接用于工程测量。", 12));
            actions.AddView(Ui.Button(this, "分享 GLB 模型", () =>
            {
                var folder = Path.Combine(FilesDir!.AbsolutePath, "previews"); Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".glb"); File.Copy(ScanStore.Model(id), copy);
                CloudMedia.Share(this, copy, session.Name + ".glb", "model/gltf-binary");
            }, false)); body.AddView(actions); SetContentView(body);
        }
        catch (Exception ex) { Ui.Alert(this, "模型无法打开", ex.Message); }
    }
    protected override void OnDestroy() { viewer?.Release(); base.OnDestroy(); }
}
