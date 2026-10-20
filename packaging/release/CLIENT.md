# 沄序 Drive · 桌面客户端

完整解压后运行 `Yunsevo.Desktop.exe`（Windows）、`./Yunsevo.Desktop`（Linux），或将 `Yunsevo.app` 放入“应用程序”（macOS）。无需安装 .NET；请保留原生图形库、许可证和其他附属文件。

先登录，再点击“创建”，可添加多个服务器和多个同步任务，选择授权的云端目录和本地目录。登录状态加密保存在本机，下次启动自动恢复。跨设备登录要求受信任证书的 HTTPS；HTTP 仅允许本机回环地址。

关闭主窗口后继续在托盘运行，单击托盘显示最近更改，右键菜单可退出。Linux GNOME 可能需要 AppIndicator 扩展；KDE 支持系统托盘。

Windows 使用 Cloud Files API 提供资源管理器占位文件和按需下载，需受支持 Windows 系统及本地 NTFS 同步目录。Linux/macOS 当前为完整下载同步，未实现对应系统的虚拟占位文件。

运行在线任务时，本地删除直接同步到云端回收站；退出、暂停、断网期间的本地删除会在重连后请求确认。

Linux 包使用 glibc，建议 Ubuntu 24.04 或兼容发行版，并安装桌面系统所需的 X11、fontconfig 和图形运行库。macOS 包未进行 Apple Developer ID 签名或公证，不能作为已签名安装包分发。构建、启动和同步检查范围见 Release 的 `verification-*.json`，不等同所有桌面环境实机验收。
