# 3D 模型查看

Yunsevo 网页和 Android 使用随程序打包的离线查看器显示 GLB 文件。支持拖动旋转、双指或滚轮缩放、自动环绕与复位。

网页：上传 `.glb` 后在文件列表中双击。回收站里的模型也可预览和下载；访问仍受账号、文件夹权限与版本校验保护。

Android：在“我的文件”点击云端 `.glb`，加载后可旋转、缩放、保存到手机或分享。点击“本机已有 3D 模型”可打开当前账号之前保存在应用中的模型；只读取现有记录，不修改原片、视频或模型。

已有 `名称.360.jpg`／`.360.png` 全景图片仍可在网页环视。

离线资源位于 `src/Yunsevo.ModelViewer`，由网页服务端内嵌、Android 作为应用资源打包。查看 GLB 不需要额外的生成引擎或计算环境。

验证：`node tests/model-viewer-smoke.mjs --server=发布目录/Yunsevo.Server.exe` 检查真实 WebGL 渲染、旋转、缩放、移动布局、登录和回收站预览。`dotnet run --project tests/Yunsevo.ModelTests -c Release` 检查本机旧模型的账号隔离与原文件保留。
