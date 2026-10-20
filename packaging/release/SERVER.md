# 沄序文件云 · 服务端

解压完整目录，在可写目录运行 `Yunsevo.Server.exe`（Windows）或 `./Yunsevo.Server`（Linux/macOS）。无需安装 .NET。

默认监听全部 IPv4 地址的 5080 端口。浏览器打开 `http://服务器地址:5080`，首次启动后使用同目录 `setup-code.txt` 中的一次性安装码，设置管理员用户名、密码和空的文件存储目录。配置完成后自动进入正式服务。保存 `server-settings.json`、`api-key.txt` 和整个数据目录，升级时不要覆盖这些文件。

公网部署请先配置受信任的 HTTPS，客户端跨设备登录要求 HTTPS。可通过 `ASPNETCORE_URLS` 自定义 HTTP 监听，或使用 `Yunsevo__CertificatePath`、`Yunsevo__CertificatePassword`、`Yunsevo__HttpsPort` 配置 TLS。请在部署环境安全注入证书密码。

`downloads/` 内含本平台的桌面和无界面客户端，登录网页自动显示下载入口。其他平台包可从同一 GitHub Release 下载，保持原文件名放进 `downloads/` 即可显示。

`smb/` 为自包含的托管 .NET SMB 网关，主服务端为 Native AOT。需完整保留目录，默认文件协议均关闭，可在网页服务设置中启用。第三方许可见 `THIRD-PARTY-NOTICES.md` 和 `smb/Notices/`。

迁移前从网页“服务设置”导出备份，参考 `MIGRATION.md`。停止旧服务后再替换程序，避免同时访问同一存储目录。

Linux 包使用 glibc，建议 Ubuntu 24.04 或兼容发行版；不适用于 Alpine/musl。macOS 包未进行 Apple Developer ID 签名或公证。构建与测试范围见 Release 附件 `verification-*.json`。
