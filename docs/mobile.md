# 沄序 · Android 手机端

Android 优先实现，采用 .NET 10 原生 Android 界面；登录、队列、上传、相册去重等逻辑位于 MyPan.Mobile.Core，供后续 iPhone 端复用。当前交付不包含 iPhone 安装包。

## 使用流程

1. 先升级到包含手机拍摄来源接口的服务端，使用受信任 HTTPS 地址登录。登录凭据经 Android Keystore 的 AES-GCM 密钥加密保存，下次自动恢复。
2. 在“工程拍照”选择或创建工程目录，设置施工部位、备注、定位与保存系统相册选项，再开始拍摄。
3. 原生 Camera2 实时取景显示水印内容；照片生成时将水印烘焙进 JPEG，按工程目录加入上传队列。拍摄人由服务端根据登录账号填写，项目名称与保存位置由服务端目录解析。
4. 在“相册”选择有写入权限的目录并开启自动备份，授权照片/视频访问；支持仅 Wi-Fi、包含视频和仅充电时上传。
5. 其他应用分享菜单选择“沄序”，或从文件页面选择手机文件；接收后选择云端保存目录。文件先保存到应用私有收件箱，再进入持久上传队列。
6. 文件页面支持目录浏览、搜索与分页；点文件打开图片、视频、文本预览，详情显示拍摄人、工程、拍摄时间、上传人及设备。其他类型下载到私有预览缓存后可授权给外部应用打开。

相册使用保留云端副本的备份方式，手机清理照片不会自动删除已备份云端内容。工程拍摄保存的系统相册副本已由工程队列上传，自动相册扫描会跳过 Pictures/Yunsevo，避免重复备份。

## 上传与恢复

队列按服务地址和用户 ID 隔离；切换账号后不会把原账号的待上传文件发到新账号。权限撤销、配额不足、网络中断和会话失效都会保留待上传内容，修复后从“传输”重试。

接收和备份使用流式私有暂存，上传复用已有分块续传与 SHA-256 校验。收到云端提交后才清除私有缓存；提交响应丢失时用拍摄编号、长度及内容哈希确认既有文件，避免覆盖或重复创建。

拍摄草稿与最终 JPEG 先落盘；新进程可将完成的照片恢复到队列，未完成处理的当前照片不会被后台任务抢先搬走。

Android 使用系统 JobScheduler 周期任务、应用运行时的媒体变更监听和前台触发。后台执行时机由系统根据网络、充电和省电策略调度；强制停止应用后需重新打开才能恢复系统任务。Android 14 仅授权选中照片时，自动备份范围相应受限。定位仅在拍照页按授权获取，不能定位时不生成虚假坐标。

当前视频预览先下载到应用私有缓存再播放，尚非边下边播；图片缩采样、文本最多显示前 1 MiB。HEIC 等格式取决于系统解码器，其他格式可由外部应用打开。

## 构建

需要 SDK 10.0.402、.NET Android 工作负载、Android SDK 36 和 JDK 21。主解决方案不依赖 Android 工作负载；手机端另用 MyPan.Mobile.slnx。

~~~powershell
dotnet workload install android
./scripts/publish-android.ps1 -Runtime android-arm64 -AndroidSdk '你的SDK目录' -JavaSdk '你的JDK目录'
~~~

产物在 artifacts/mobile/android-arm64/yunsevo-android-arm64.apk，适用于 Android 8.0 及以上 ARM64 设备。android-x64 为模拟器/对应架构测试包，不是 ARM 手机包。

Release 启用 Mono AOT，并关闭仅针对配置文件的 AOT；这与服务端/桌面的 Native AOT 不同。Android NativeAOT 在 .NET 10 中仍为实验能力，因此本客户端采用已支持的 Mono AOT。

默认构建使用 .NET Android 开发签名，只适合内部测试。正式分发请提供 SigningKeyStore、SigningKeyAlias，并通过 YUNSEVO_ANDROID_STORE_PASSWORD 和 YUNSEVO_ANDROID_KEY_PASSWORD 环境变量传入密码；不要上传密钥库，后续升级需沿用同一签名。

## 服务端数据

CloudEntry 新增可选 origin，历史数据可继续读取；当前内容、历史版本和回收站均保留相应来源。恢复历史版本时也恢复其拍摄信息。

小文件通过 X-MyPan-Photo 发送 Base64 UTF-8 JSON，大文件在 StartUpload.Metadata 中携带相同结构。服务端验证项目目录和权限，并从认证身份生成 photographer/uploadedBy；客户端上报的同名身份字段不被采信。时间、位置、设备名称属于设备上报信息，上传时间及用户身份由服务端记录。

原生 AOT 集成检查使用 tests/MyPan.MobileTests；Android 实际流程脚本 tests/android-smoke.mjs 仅允许 emulator-* 设备，会清空测试模拟器中的该 App 数据，不可用于实际手机。实际验证范围、截图及报告在 artifacts/mobile 下汇总。

## 界面与验证范围

下面是 Android 15 x64 模拟器中实际运行的 Release AOT 界面，使用隔离演示账号与虚拟相机画面。工程拍照生成的水印已经写入 JPEG，不依赖预览时叠加。

| 登录 | 工程目录 |
| --- | --- |
| ![手机登录](screenshots/mobile-login.png) | ![选择工程](screenshots/mobile-project.png) |

| 工程水印相机 | 云端照片与拍摄人 |
| --- | --- |
| ![工程水印相机](screenshots/mobile-camera.png) | ![照片预览与拍摄人](screenshots/mobile-preview.png) |

原生服务端与共享手机核心通过 35 项集成检查，包含分块续传、身份来源、版本恢复、相册去重、拍摄中断恢复和账号切换隔离；网页通过 73 项回归检查。Android 模拟器完成登录、实际 Camera2 拍摄上传、相册备份、接收分享、预览和重启自动登录的 11 项流程检查。

ARM64 包已在 Android 16 真机安装并确认登录界面正常启动。真机的相机兼容性、相册权限和厂商后台省电策略仍需结合实际使用验证；当前安装包为开发签名，未作为正式商用签名发布。iPhone 端尚未提供。

参考：[.NET Android 构建属性](https://learn.microsoft.com/dotnet/android/building-apps/build-properties)、[.NET 移动运行时与编译](https://learn.microsoft.com/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0)、[Android 照片访问权限](https://developer.android.com/about/versions/14/changes/partial-photo-video-access)、[Android JobScheduler](https://developer.android.com/reference/android/app/job/JobInfo.Builder)。
