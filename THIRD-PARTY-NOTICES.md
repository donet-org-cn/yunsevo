# Third-party dependencies

Desktop: [Avalonia](https://github.com/AvaloniaUI/Avalonia), MIT, with its bundled native and graphics dependencies. Preserve the accompanying notices when redistributing published packages.

Server image thumbnails: [SkiaSharp](https://github.com/mono/SkiaSharp), MIT, with Skia and its bundled third-party codecs. Keep the SkiaSharp native library and the upstream license and notices when distributing the server package. Thumbnail caches are disposable and are regenerated after migration.

SMB gateway: [SMBLibrary and SMBLibrary.Adapters](https://github.com/TalAloni/SMBLibrary), LGPL-3.0-or-later; [DiskAccessLibrary](https://github.com/TalAloni/DiskAccessLibrary), LGPL-3.0-or-later. The gateway uses the separately distributed, replaceable managed assemblies from official NuGet packages. It does not statically incorporate them into the Native AOT main server. The upstream repositories provide source, license texts and build instructions. A commercial SMBLibrary license is also available from its author.

The .NET self-contained SMB gateway also includes Microsoft .NET runtime components with their shipped license and third-party notices. Retain those files from the publish directory. No SMB library source has been copied into application source; files under `.test-data/smb-reference` are development references only and are not shipped.


## 三维扫描与离线模型查看

- OpenCV 4.13.0：Apache-2.0；用于特征、相机位姿及图像解码。运行包附 OpenCV 与第三方组件的许可证。原生移动库使用 NDK libc++，附 NDK NOTICE。
- Khronos Vulkan-Headers 1.3.280：Apache-2.0 / MIT，构建时使用；glslang 16.6.0 仅用于构建着色器，不作为运行时依赖。手机和 Windows 通过系统 Vulkan loader 连接显卡驱动，不要求 CUDA。
- @google/model-viewer 4.1.0：Apache-2.0，离线资源在 `src/MyPan.ModelViewer`，完整许可证见 `LICENSE.model-viewer`。打包 JS 保留依赖的许可注释（包括 Three.js MIT）。
- 本项目的原生重建原型为试验实现；不包含 COLMAP、OpenMVS、ODM 或 CUDA，不把仅使用 Vulkan 渲染视作 Vulkan 重建。

## 可选的服务端 CPU 高精度工作进程

`tools/reconstruction` 通过管理员独立安装的 Python 环境使用 COLMAP/pycolmap（BSD-3-Clause）、MobileSAM（Apache-2.0）、PyTorch（含 BSD、Apache、MIT 等组件）、Trimesh（MIT）、Embree（Apache-2.0）及 embreex（BSD-2-Clause）等依赖，版本与安装方式见该目录 README。它不包含在现有原生重建运行包中，照片不会因此上传第三方服务。

深度重建调用独立安装的 [OpenMVS 2.4.0](https://github.com/cdcseacave/openMVS/tree/v2.4.0) CLI，许可证为 AGPL-3.0；不随商业安装包默认分发。使用、修改、分发或托管时须遵守对应许可证，独立进程本身不免除义务。视频选帧使用管理员配置的 FFmpeg/FFprobe，其许可取决于实际安装的构建。各依赖及其传递依赖的完整许可和声明应保留在部署环境中。
