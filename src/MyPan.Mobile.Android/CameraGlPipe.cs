using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Java.Nio;
using MyPan.Mobile.Core;

namespace MyPan.Mobile.Android;

// One camera texture feeds the preview and the encoder; both receive the same burnt watermark.
internal sealed class CameraGlPipe : IDisposable
{
    private readonly HandlerThread thread = new("YunsevoCameraGL");
    private readonly Handler handler;
    private readonly Action<string> error;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => closed.Task;
    private EGLDisplay display = null!;
    private EGLContext context = null!;
    private EGLConfig config = null!;
    private EGLSurface previewWindow = null!;
    private EGLSurface idleWindow = null!;
    private EGLSurface? encoderWindow;
    private SurfaceTexture? inputTexture;
    private Surface? inputSurface;
    private FrameListener? listener;
    private FloatBuffer vertices = null!;
    private readonly float[] textureMatrix = new float[16];
    private int cameraTexture, watermarkTexture, cameraProgram, overlayProgram;
    private int previewWidth, previewHeight, frameWidth, frameHeight, rotation, encodeWidth, encodeHeight, disposed;
    private bool mirror, failed;
    private int previewBottomInset, previewRightInset;
    private readonly VideoFrameClock videoClock = new();
    private string watermark = "";
    private MobileOptions options = new();
    private string? renderedKey;
    public CameraGlPipe(Action<string> error)
    {
        this.error = error; thread.Start(); handler = new(thread.Looper!);
    }
    private Task<T> Invoke<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref disposed) != 0) return Task.FromException<T>(new IOException("相机图像处理已关闭。"));
        if (!handler.Post(() => { try { completion.TrySetResult(action()); } catch (Exception ex) { completion.TrySetException(ex); } }))
            completion.TrySetException(new IOException("相机图像线程已结束。"));
        return completion.Task;
    }
    public Task<Surface> Initialize(SurfaceTexture target, int width, int height, int cameraWidth, int cameraHeight, int angle, bool front)
        => Invoke(() =>
        {
            previewWidth = width; previewHeight = height; frameWidth = cameraWidth; frameHeight = cameraHeight; rotation = angle; mirror = front;
            display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay)!;
            var versions = new int[2];
            if (!EGL14.EglInitialize(display, versions, 0, versions, 1)) throw new IOException("设备无法初始化 OpenGL 图像处理。");
            int[] attributes = [EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, 8,
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit, EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit, 0x3142, 1, EGL14.EglNone]; // EGL_RECORDABLE_ANDROID
            var configurations = new EGLConfig[1]; var count = new int[1];
            if (!EGL14.EglChooseConfig(display, attributes, 0, configurations, 0, 1, count, 0) || count[0] == 0)
                throw new IOException("设备不支持带水印的视频编码表面。");
            config = configurations[0];
            context = EGL14.EglCreateContext(display, config, EGL14.EglNoContext, [EGL14.EglContextClientVersion, 2, EGL14.EglNone], 0)!;
            idleWindow = EGL14.EglCreatePbufferSurface(display, config, [EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone], 0)!;
            previewWindow = EGL14.EglCreateWindowSurface(display, config, target, [EGL14.EglNone], 0)!;
            MakeCurrent(previewWindow);
            var buffer = ByteBuffer.AllocateDirect(16 * sizeof(float))!.Order(ByteOrder.NativeOrder()!)!;
            vertices = buffer.AsFloatBuffer()!;
            vertices.Put(new float[] { -1, -1, 0, 0, 1, -1, 1, 0, -1, 1, 0, 1, 1, 1, 1, 1 }); vertices.Position(0);
            cameraProgram = Program(CameraVertex, CameraFragment); overlayProgram = Program(OverlayVertex, OverlayFragment);
            cameraTexture = Texture(GLES11Ext.GlTextureExternalOes); watermarkTexture = Texture(GLES20.GlTexture2d);
            inputTexture = new(cameraTexture); inputTexture.SetDefaultBufferSize(cameraWidth, cameraHeight);
            listener = new(this); inputTexture.SetOnFrameAvailableListener(listener, handler); inputSurface = new(inputTexture);
            return inputSurface;
        });
    public void Resize(int width, int height, int angle, bool front)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        handler.Post(() => { previewWidth = width; previewHeight = height; rotation = angle; mirror = front; renderedKey = null; });
    }
    public void PreviewInset(int bottom, int right = 0) => handler.Post(() => { previewBottomInset = Math.Max(0, bottom); previewRightInset = Math.Max(0, right); });
    public void Watermark(string text, MobileOptions value)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        handler.Post(() => { watermark = text; options = value; renderedKey = null; });
    }
    public Task AttachEncoder(Surface target, int width, int height) => Invoke(() =>
    {
        if (encoderWindow != null) throw new InvalidOperationException("录像已开始。");
        encodeWidth = width; encodeHeight = height; videoClock.Reset();
        encoderWindow = EGL14.EglCreateWindowSurface(display, config, target, [EGL14.EglNone], 0)!;
        if (encoderWindow.Equals(EGL14.EglNoSurface)) { encoderWindow = null; throw new IOException("无法建立视频编码输出。"); }
        renderedKey = null; return true;
    });
    public Task DetachEncoder() => Invoke(() =>
    {
        if (encoderWindow != null) { MakeCurrent(idleWindow); EGL14.EglDestroySurface(display, encoderWindow); encoderWindow.Dispose(); encoderWindow = null; renderedKey = null; }
        return true;
    });
    private void MakeCurrent(EGLSurface surface)
    {
        if (!EGL14.EglMakeCurrent(display, surface, surface, context)) throw new IOException("相机图像上下文不可用。");
    }
    private void Frame()
    {
        if (Volatile.Read(ref disposed) != 0 || inputTexture == null || failed) return;
        try
        {
            MakeCurrent(previewWindow); inputTexture.UpdateTexImage(); inputTexture.GetTransformMatrix(textureMatrix);
            NormalizeProducerTransform(textureMatrix);
            Draw(previewWindow, previewWidth, previewHeight);
            if (encoderWindow != null)
            {
                Draw(encoderWindow, encodeWidth, encodeHeight);
                EGLExt.EglPresentationTimeANDROID(display, encoderWindow, videoClock.Timestamp(inputTexture.Timestamp, Java.Lang.JavaSystem.NanoTime()));
                if (!EGL14.EglSwapBuffers(display, encoderWindow)) throw new IOException("视频编码输出中断。");
            }
            MakeCurrent(previewWindow); EGL14.EglSwapBuffers(display, previewWindow);
        }
        catch (Exception ex) { failed = true; error(ex.Message); }
    }
    private void Draw(EGLSurface window, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        MakeCurrent(window); GLES20.GlViewport(0, 0, width, height); GLES20.GlClearColor(0, 0, 0, 1); GLES20.GlClear(GLES20.GlColorBufferBit);
        GLES20.GlDisable(GLES20.GlBlend); GLES20.GlUseProgram(cameraProgram); Vertices(cameraProgram);
        GLES20.GlActiveTexture(GLES20.GlTexture0); GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, cameraTexture);
        GLES20.GlUniform1i(GLES20.GlGetUniformLocation(cameraProgram, "uImage"), 0);
        GLES20.GlUniformMatrix4fv(GLES20.GlGetUniformLocation(cameraProgram, "uTexture"), 1, false, textureMatrix, 0);
        var radians = rotation * MathF.PI / 180;
        GLES20.GlUniform2f(GLES20.GlGetUniformLocation(cameraProgram, "uRotation"), MathF.Cos(radians), MathF.Sin(radians));
        GLES20.GlUniform1f(GLES20.GlGetUniformLocation(cameraProgram, "uMirror"), mirror ? 1 : 0);
        var sourceAspect = rotation % 180 == 0 ? frameWidth / (float)frameHeight : frameHeight / (float)frameWidth;
        var viewAspect = width / (float)height;
        GLES20.GlUniform2f(GLES20.GlGetUniformLocation(cameraProgram, "uCrop"), sourceAspect > viewAspect ? viewAspect / sourceAspect : 1, sourceAspect < viewAspect ? sourceAspect / viewAspect : 1);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
        var landscape = width > height;
        var overlayWidth = landscape ? 1280 : 720; var overlayHeight = landscape ? 720 : 1280;
        var key = watermark + options.WatermarkStyle + options.WatermarkTextColor + options.WatermarkBackground + options.WatermarkTextScale + landscape;
        GLES20.GlBindTexture(GLES20.GlTexture2d, watermarkTexture);
        if (renderedKey != key)
        {
            using var bitmap = WatermarkPainter.Overlay(overlayWidth, overlayHeight, watermark, options);
            GLUtils.TexImage2D(GLES20.GlTexture2d, 0, bitmap, 0); renderedKey = key;
        }
        GLES20.GlEnable(GLES20.GlBlend); GLES20.GlBlendFunc(GLES20.GlOne, GLES20.GlOneMinusSrcAlpha);
        GLES20.GlUseProgram(overlayProgram); Vertices(overlayProgram); GLES20.GlUniform1i(GLES20.GlGetUniformLocation(overlayProgram, "uImage"), 0);
        if (window == previewWindow && previewRightInset > 0) GLES20.GlViewport(0, 0, Math.Max(1, width - previewRightInset), height);
        GLES20.GlUniform1f(GLES20.GlGetUniformLocation(overlayProgram, "uOffsetY"), window == previewWindow ? 2f * previewBottomInset / height : 0);
        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4); GLES20.GlDisable(GLES20.GlBlend);
    }
    private static void NormalizeProducerTransform(float[] matrix)
    {
        // Camera producers may include their own rotation/mirroring. Apply sensor/display rotation once,
        // preserving the producer's valid sampling rectangle and the OpenGL-to-buffer vertical flip.
        var xs = new[] { matrix[12], matrix[0] + matrix[12], matrix[4] + matrix[12], matrix[0] + matrix[4] + matrix[12] };
        var ys = new[] { matrix[13], matrix[1] + matrix[13], matrix[5] + matrix[13], matrix[1] + matrix[5] + matrix[13] };
        var left = xs.Min(); var right = xs.Max(); var bottom = ys.Min(); var top = ys.Max();
        Array.Clear(matrix); matrix[0] = right - left; matrix[5] = bottom - top; matrix[10] = matrix[15] = 1; matrix[12] = left; matrix[13] = top;
    }
    private void Vertices(int program)
    {
        var position = GLES20.GlGetAttribLocation(program, "aPosition"); var uv = GLES20.GlGetAttribLocation(program, "aUv");
        vertices.Position(0); GLES20.GlVertexAttribPointer(position, 2, GLES20.GlFloat, false, 16, vertices); GLES20.GlEnableVertexAttribArray(position);
        vertices.Position(2); GLES20.GlVertexAttribPointer(uv, 2, GLES20.GlFloat, false, 16, vertices); GLES20.GlEnableVertexAttribArray(uv);
    }
    private static int Texture(int target)
    {
        var names = new int[1]; GLES20.GlGenTextures(1, names, 0); GLES20.GlBindTexture(target, names[0]);
        GLES20.GlTexParameteri(target, GLES20.GlTextureMinFilter, GLES20.GlLinear); GLES20.GlTexParameteri(target, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(target, GLES20.GlTextureWrapS, GLES20.GlClampToEdge); GLES20.GlTexParameteri(target, GLES20.GlTextureWrapT, GLES20.GlClampToEdge); return names[0];
    }
    private static int Program(string vertex, string fragment)
    {
        int Compile(int type, string source)
        {
            var shader = GLES20.GlCreateShader(type); GLES20.GlShaderSource(shader, source); GLES20.GlCompileShader(shader);
            var ok = new int[1]; GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, ok, 0);
            if (ok[0] == 0) { var message = GLES20.GlGetShaderInfoLog(shader); GLES20.GlDeleteShader(shader); throw new IOException("相机着色器初始化失败：" + message); }
            return shader;
        }
        var vs = Compile(GLES20.GlVertexShader, vertex); var fs = Compile(GLES20.GlFragmentShader, fragment);
        var program = GLES20.GlCreateProgram();
        try
        {
            GLES20.GlAttachShader(program, vs); GLES20.GlAttachShader(program, fs); GLES20.GlLinkProgram(program);
            var ok = new int[1]; GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, ok, 0);
            if (ok[0] == 0) throw new IOException("相机图像程序连接失败：" + GLES20.GlGetProgramInfoLog(program));
            return program;
        }
        finally { GLES20.GlDeleteShader(vs); GLES20.GlDeleteShader(fs); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        handler.Post(() =>
        {
            try
            {
                if (display == null) return;
                if (context != null && idleWindow != null) MakeCurrent(idleWindow);
                inputTexture?.SetOnFrameAvailableListener(null, handler); inputSurface?.Dispose(); inputTexture?.Release(); inputTexture?.Dispose(); listener?.Dispose();
                if (cameraProgram != 0) GLES20.GlDeleteProgram(cameraProgram); if (overlayProgram != 0) GLES20.GlDeleteProgram(overlayProgram);
                GLES20.GlDeleteTextures(2, new[] { cameraTexture, watermarkTexture }, 0); vertices?.Dispose();
                EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                if (encoderWindow != null) EGL14.EglDestroySurface(display, encoderWindow);
                if (previewWindow != null) EGL14.EglDestroySurface(display, previewWindow);
                if (idleWindow != null) EGL14.EglDestroySurface(display, idleWindow);
                if (context != null) EGL14.EglDestroyContext(display, context);
                EGL14.EglReleaseThread(); EGL14.EglTerminate(display);
            }
            catch (Exception) { }
            finally { closed.TrySetResult(); }
        });
        thread.QuitSafely();
    }
    private sealed class FrameListener(CameraGlPipe owner) : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
    { public void OnFrameAvailable(SurfaceTexture? surfaceTexture) => owner.Frame(); }
    private const string CameraVertex = """
        attribute vec4 aPosition; attribute vec2 aUv;
        uniform mat4 uTexture; uniform vec2 uRotation; uniform vec2 uCrop; uniform float uMirror;
        varying vec2 vUv;
        void main() {
            gl_Position = aPosition;
            vec2 p = (aUv - vec2(0.5)) * uCrop;
            if (uMirror > 0.5) p.x = -p.x;
            p = vec2(uRotation.x*p.x-uRotation.y*p.y, uRotation.y*p.x+uRotation.x*p.y);
            vUv = (uTexture * vec4(p + vec2(0.5), 0.0, 1.0)).xy;
        }
        """;
    private const string CameraFragment = """
        #extension GL_OES_EGL_image_external : require
        precision mediump float; uniform samplerExternalOES uImage; varying vec2 vUv;
        void main() { gl_FragColor = texture2D(uImage, vUv); }
        """;
    private const string OverlayVertex = """
        attribute vec4 aPosition; attribute vec2 aUv; varying vec2 vUv; uniform float uOffsetY;
        void main() { gl_Position = aPosition + vec4(0.0, uOffsetY, 0.0, 0.0); vUv = vec2(aUv.x, 1.0-aUv.y); }
        """;
    private const string OverlayFragment = """
        precision mediump float; uniform sampler2D uImage; varying vec2 vUv;
        void main() { gl_FragColor = texture2D(uImage, vUv); }
        """;
}
