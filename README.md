# 沄序文件云 · Yunsevo

基于 .NET 10：用户与用户组、私有/公用/指定授权目录、目录配额、版本历史、回收站、审计、网页预览，以及登录式桌面/无界面同步客户端。

多文件同步支持受控并发与批次处理；大文件支持分块续传、范围下载和校验，服务端及客户端使用可恢复的增量状态日志。配置、恢复文件及当前边界见[同步性能说明](docs/sync-performance.md)。

Android 手机端 1.2 支持全屏工程相机、拍摄时切换工程、照片/视频水印、EXIF 参数保留，以及仅云端或同时保存本机。云端相册使用服务端小缩略图和滚动续载，支持左右滑动看图、缩放、保存与分享。地址和天气服务在服务端配置。安装包生成于 artifacts/mobile/android-arm64/yunsevo-android-arm64.apk，需配合新版服务端；使用流程、构建、后台限制和签名说明见[手机端说明](docs/mobile.md)。当前为内部测试签名，尚未纳入 v2.6.2 发布包；iPhone 端后续开发。

商业品牌使用“沄序 · Yunsevo”，桌面客户端为“沄序 Drive”。新版界面、分页、密码重置和回收站来源记录见 [界面与管理说明](docs/design-and-workspace.md)。现有程序文件名、配置和同步根标识保留兼容。

## 下载与界面

服务端、桌面客户端和无界面客户端的分平台发布包见 [GitHub Releases](https://github.com/hnlyf168/yunsevo/releases)。选择对应系统与 x64/ARM64 架构：server 为服务端，drive 为桌面客户端，headless 为无界面客户端。每个发布包附带运行说明，发布页提供 SHA-256 校验与各平台检查记录。

**客户端 · 多服务器、多同步目录**

![沄序 Drive 客户端](docs/screenshots/client-multiple-servers.png)

**服务端 · 网页文件管理**

![沄序文件云服务端](docs/screenshots/server-files.png)

更多登录、任务向导、托盘、日志、用量、权限、回收站和审计界面见 [完整截图画廊](docs/screenshots.md)。截图使用隔离演示数据，桌面示例来自 Windows。

**[v2.6.2 全平台下载](docs/releases/v2.6.2.md)** 已提供 Windows、Linux、macOS 各自的 x64/ARM64 服务端及客户端，全部六个平台通过最终压缩包解压后的原生运行、同步与窗口渲染检查，共 72 项。[截图画廊](docs/screenshots.md) 也包含 Linux 和 macOS 的实际原生窗口。

## 启动新版

先退出旧服务端，再在项目目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/start-server.ps1
```

脚本使用 `artifacts/v2/server/MyPan.Server.exe`，首次安装建议项目的 `data`，已安装则沿用 `server-settings.json` 中的存储路径。旧版 `artifacts/server/api-key.txt` 存在且新位置还没有密钥时，会复制原密钥。不会清空原数据。升级首次读取旧目录时自动备份旧 `catalog.json`；降级前须恢复成套数据备份，不能让旧程序直接写 V2 数据。

打开 `http://127.0.0.1:5080`。全新部署按[安装向导](docs/first-install.md)设置管理员账号、密码和存储位置；已有部署沿用原账号，旧目录首次迁移的兼容管理员仍为 `admin`，初始密码为 API 密钥。管理员可在用户管理中重置用户密码；个人账户可点击右上角账户入口修改自己的密码。API 密钥登录保留为旧客户端的管理员兼容入口；普通用户应使用个人账号。迁移完旧客户端后，可设置 `MyPan__AllowLegacyApiKey=false` 并重启，关闭共享密钥入口。

新版桌面客户端：`artifacts/v2/desktop/MyPan.Desktop.exe`，也可通过 `powershell -ExecutionPolicy Bypass -File scripts/start-desktop.ps1` 打开。先登录，再在“同步任务”点击“创建”，选择已有服务器或添加服务器，然后选择云端文件夹、本地目录和下载方式。支持同时连接多个服务器、每台服务器添加多个独立任务；登录状态加密保存，下次启动自动恢复。云端列表标注“私有 / 公共 / 分配给我”和读写权限，个人根目录显示为 `/个人`。旧版 Win32 客户端另在 `artifacts/v2/windows-client`，用于兼容原 API 密钥配置。

若新版发布到了 `artifacts/v2-update`，先从托盘退出旧客户端并停止旧服务端，再运行 `powershell -ExecutionPolicy Bypass -File scripts/install-update.ps1` 安装。安装脚本备份被替换的程序文件，保留服务端数据、API 密钥及客户端配置，随后按上述命令重新启动服务端和客户端。

分发新版桌面端时请完整复制 `desktop` 目录，保留随包原生图形库；无需安装 .NET 运行时。

## 管理网页

左侧按功能区分：我的文件、回收站、用户管理、用户组管理、服务设置、审计日志。普通用户看不到管理入口；后端也独立执行权限验证。

会话有效期内刷新网页会自动恢复登录，并保留当前菜单、文件夹、搜索、排序和分页位置。各标签页通过自己的地址保存导航状态，密码及访问令牌不会写入地址或网页存储；登录由 HttpOnly Cookie 恢复。退出登录、密码变更或会话失效后会返回登录页，不能通过恢复旧页面绕过权限。

- 创建用户后生成私有的 `个人/用户名`，客户端显示为 `/个人`，以及所有登录用户可读写的 `公共` 目录。个人目录仅本人和网页管理员可访问；管理员使用新版客户端也不能访问他人的私有目录。指定授权可以按用户、用户组分别设只读、读写、管理。
- 文件夹的“设置”可调整授权、容量和保留版本。配额由管理员设置，例如 10 GiB；统计当前文件、历史版本和回收站。子目录配额受父级总配额共同约束；超额写入返回 507，现有版本保持原样。
- 版本数包含当前版本。例如保留 3 个版本，就是当前版本加最多 2 个历史版本；默认 10。可下载或恢复旧版本；恢复也会受配额检查。
- 删除进入回收站；回收站文件可直接下载和预览，无需先恢复。恢复和永久删除均写入审计。永久删除才释放对应回收站文件及其历史版本占用。
- 双击文件夹进入、双击文件预览；不支持预览的格式直接下载。图片、浏览器支持编码的视频/音频可预览，视频支持 Range 拖动；文本安全显示前 1 MiB。HTML/SVG 不作为活动内容执行；媒体不做转码。
- 审计记录用户、时间、来源、操作、对象和 HTTP 结果。网页可筛选用户、用户组、设置、回收站操作；完整文件在 `data/audit`，20 MiB 轮转，保留当前文件和 5 个历史文件。密码和访问令牌不会写入审计。

管理员可在“服务设置 → 迁移与备份”一键下载整站 ZIP。备份包含文件、版本、回收站、账户权限、协议配置、审计和所需密钥；恢复脚本随服务端包发布，校验通过后恢复到新目录。默认个人目录会为初始管理员和旧账户自动补齐，并保持私有。操作步骤及边界见 [备份与迁移](docs/backup-and-migration.md)。

## WebDAV、FTP、SMB

在“服务设置”中勾选并保存，立即生效，重启也保留。默认全部关闭。端口启动失败会显示错误并回退原配置。

| 协议 | 默认入口 | 认证 |
| --- | --- | --- |
| WebDAV | `https://你的域名/dav/`，本机可用 `http://127.0.0.1:5080/dav/` | 云盘用户名 / 密码，Basic over HTTPS |
| FTP | `127.0.0.1:2121`，UTF-8、被动模式 | 云盘用户名 / 密码 |
| SMB 2/3 | TCP 1445，共享 `mypan`，关闭 SMB1 | 用户管理中“SMB 密码”生成的专用连接密码 |

协议访问共用云盘权限、条件写入、配额、版本和回收站，不直接共享 blob 存储目录。SMB 的密码为随机专用凭据，经 AES-GCM 加密保存在数据目录；网页登录密码始终只存 PBKDF2 哈希。停用用户立即阻止其新操作。SMB 专用密码重置会影响后续登录，已打开的句柄遵循现有操作的授权语义。

FTP 是明文 FTP，适合本机或可信内网，未实现 FTPS/SFTP、主动模式和断点续传。防火墙需要允许控制连接和被动数据连接；公网使用 HTTPS 同步/WebDAV。WebDAV 支持 OPTIONS、PROPFIND（Depth 0/1）、GET/HEAD、PUT、MKCOL、DELETE、MOVE、文件 COPY；不提供 DAV 锁定及递归 COPY，不能当作完整 Office 协作锁服务。

SMB 默认 1445 以避开系统共享端口；Linux 可通过 CIFS 的 `port=1445` 挂载，macOS 连接地址可指定端口。Windows 资源管理器通常需要 445；请在端口空闲的专用主机/IP 上配置，程序不会停止系统 SMB 服务。SMB 依赖独立的 `server/smb` 网关，必须完整复制服务端发布目录。网关为自包含托管 .NET，主服务端和客户端为 Native AOT。第三方许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 客户端、托盘和删除确认

桌面 UI 使用 Avalonia，Windows、Linux、macOS 共享“登录 → 同步任务 → 添加任务”流程。左侧分别为同步任务、同步日志、通知和全局设置。单击托盘图标展开 OneDrive 式状态与最近活动面板，右键菜单保留全部暂停、继续和退出。界面与设置说明见 [桌面界面](docs/desktop-interface.md)。每个任务可单独暂停、继续、打开本地目录或移除；托盘/菜单栏可暂停、继续全部任务和退出。未登录、已连接、连接断开、需要处理和暂停使用不同状态图标。关闭窗口会继续同步；从托盘退出才结束进程。Linux 的 GNOME 通常需安装 AppIndicator 扩展以显示托盘，KDE 可直接显示。

每个任务独立选择下载方式、云端目录和本地目录，配置按服务器及账户保存，登录后仅自动启动该账户已启用的任务。禁止重复或相互包含的本地目录，同一账户的云端目录也不能重叠。原单任务桌面配置会在同一服务器、同一用户名成功登录后导入，并沿用原同步状态文件。改变下载方式需移除后重新添加任务；新的下载方式应使用空的本地目录。

任务创建流程参考 [Synology Drive Client 官方说明](https://kb.synology.com/DSM/help/SynologyDriveClient/synologydriveclient)。

日志按天保存在客户端设置目录的 `activity`，保留最近 30 个日志文件。桌面不保存账户密码；自动登录的设备凭据加密保存在 `credentials/accounts.bin`，不写入偏好文件。设备会话有效期 90 天并自动续期，网页会话仍为 12 小时；密码重置、停用账户及退出登录会撤销对应凭据。多个服务器独立恢复，连接失败不影响其他服务器。不同账户、云端目录、本地路径和同步模式使用独立状态文件。不要删除同步状态文件或把新账号直接绑定到有旧文件的目录。跨平台凭据保护的细节见[桌面界面说明](docs/desktop-interface.md)。

任务已建立连接并在线运行时，本地删除会直接同步到云端回收站及其他设备，不弹出确认。退出、暂停或断线期间的本地删除，在启动任务或重连后仍需选择；“稍后处理”的离线删除不会因后续连接成功而自动删除云端：

1. **也删除云端**：其他设备同步删除，可从云端回收站恢复。
2. **仅本地删除**：云端保留，本设备暂不重新下载；选择持久保存。重新在该路径创建文件可恢复该项目的同步。
3. **稍后处理**：暂不删除云端；其他正常文件继续同步。

Windows 使用 Cloud Files API，在本地 NTFS 上提供资源管理器占位文件和按需下载。Linux/macOS 当前提供完整本地副本同步及托盘；尚未实现 macOS File Provider 或 Linux 虚拟文件系统占位功能。各平台的本地改动、远程更新冲突会保留副本；权限、配额或锁定错误按文件隔离。

## Linux 无界面客户端

`headless/MyPan.Headless` 无需桌面环境，支持常驻和 `--once`。配置参考 [packaging/linux/client.example.json](packaging/linux/client.example.json)，密码从 `MYPAN_PASSWORD` 或 `MYPAN_PASSWORD_FILE` 读取。服务用密码文件应限制为当前账户可读（0600）。

```bash
export MYPAN_PASSWORD_FILE="$HOME/.config/mypan/password"
/opt/mypan/headless/MyPan.Headless --config "$HOME/.config/mypan/client.json"
# 查看等待确认的本地删除
/opt/mypan/headless/MyPan.Headless --config "$HOME/.config/mypan/client.json" --list-deletions
# 明确选择：cloud 删除云端；local 仅本地删除；defer 继续等待
/opt/mypan/headless/MyPan.Headless --config "$HOME/.config/mypan/client.json" --resolve-delete "资料/a.txt" --decision local
```

待确认文件在配置旁的 `.deletions.json`；未确认不会删除云端，决定只使用一次。提供 [systemd 用户服务模板](packaging/linux/mypan-sync.service) 和 [桌面入口](packaging/linux/mypan.desktop)，按实际安装路径修改后安装。

## 连接协议和 TLS

全新部署先进入[首次安装向导](docs/first-install.md)，设置管理员用户名、密码和存储绝对路径。安装完成后自动进入登录页，重启沿用 `server-settings.json`；已有数据的升级不重置管理员。配置文件随升级保留，不会被启动脚本的默认路径覆盖。

服务端默认 HTTP 监听 `0.0.0.0:5080`，启动脚本也使用全部 IPv4 网卡。其他设备通过 `http://服务器IP:5080/` 打开登录页；仅限本机访问时可用 `scripts/start-server.ps1 -ListenAddress 127.0.0.1`。直接启动程序仍可通过 `ASPNETCORE_URLS` 覆盖监听地址。配置 HTTPS 证书后，默认 HTTPS 监听所有 IPv4/IPv6 网卡，`MyPan__ListenAddress` 可指定单个地址。端口仍需由部署环境的防火墙、路由或反向代理放行。

登录页提供无需登录的客户端下载入口，只显示 `server/downloads` 中实际存在的已知发布包。运行 `scripts/publish.ps1` 或 `scripts/publish.sh` 会自动生成对应平台的桌面版、无界面版下载包；已有发布目录可用 `scripts/package-clients.ps1 -Runtime win-x64 -Output artifacts/v2` 重新打包。Windows 为 ZIP，Linux/macOS 为保留可执行权限的 tar.gz。部署服务端时保留 `downloads` 子目录，也可用 `MyPan__ClientDownloadsPath` 指向集中存放包的目录。将其他平台构建完成的包复制到该目录后，对应入口自动出现；不会显示尚未构建的平台。下载支持断点续传，打包仅包含运行文件和第三方许可。

Windows 桌面版下载后完整解压，运行 `MyPan.Desktop.exe`。跨设备客户端登录需要受信任证书的 HTTPS 地址；HTTP 仅允许客户端连接本机回环地址。网页使用 HTTP 测试可访问，但公开部署应配置 HTTPS。

客户端复用 HTTP 连接池，本地文件监听 + 服务端 SSE 持续推送变更，200 ms 合并事件，5 秒补查丢失事件和失败重试。网络断开自动重连，成功提交后才推送。文件内容仍通过流式 HTTP 上传/下载，使用 ETag 条件写入保护并发更新。

HTTPS 时优先 HTTP/3（QUIC），不可用则协商 HTTP/2/1.1；HTTP/1.1 本身也支持连接复用。TLS 服务端设置：

```powershell
$env:MyPan__CertificatePath='F:\certs\server.pfx'
$env:MyPan__CertificatePassword='通过部署环境安全注入'
$env:MyPan__ListenAddress='0.0.0.0'
$env:MyPan__HttpsPort='5443'
$env:MyPan__EnableHttp3='true'
```

证书配置后使用上述显式 HTTPS 端口，取代 `ASPNETCORE_URLS` 监听。客户端必须信任真实有效证书，不跳过校验。HTTP/3 需要相应系统 QUIC 支持及 UDP 端口畅通；反向代理终止 TLS 时，HTTP/3 由代理决定，还需禁用 SSE 缓冲。当前机器实际 HTTP/2 已通过验证，HTTP/3 强制连接因主机 UDP 路径不可达未验证成功，已验证可降级连接。SMB 网关通过 HTTPS 访问主服务端时也执行正常证书校验；可用 `MyPan__GatewayServerUrl` 指定其可达且可信的服务 URL。

## 编译与发布

需要 .NET SDK 10.0.402 及当前平台 Native AOT 工具链；Windows 需要 VS C++ 和 Windows SDK，Linux 需要 clang/zlib 开发包，macOS 需要 Xcode 命令行工具。在对应系统执行：

```powershell
./scripts/publish.ps1 -Runtime win-x64
./scripts/publish.ps1 -Runtime linux-x64 -Output artifacts/v2/linux-x64
./scripts/publish.ps1 -Runtime osx-arm64 -Output artifacts/v2/osx-arm64
```

Native AOT 需要在对应操作系统构建。提供 [手动构建工作流](.github/workflows/native-aot.yml) 及 [完整发布工作流](.github/workflows/release.yml)，覆盖 Windows、Linux、macOS 的 x64/ARM64，并生成 macOS `.app`。推送版本标签后，发布工作流在各系统分别构建，检查原生架构、服务启动、账号登录、文件上传下载、客户端同步和真实桌面窗口渲染；全部平台通过后才发布 GitHub Release。每个平台的实际结果以 Release 的 verification 附件与 Actions 记录为准，未通过的 CI 不视为已验证。macOS 包尚未进行 Apple Developer ID 签名或公证。

Linux/macOS 无需安装 PowerShell，也可直接运行 `bash scripts/publish.sh linux-x64` 或 `bash scripts/publish.sh osx-arm64`。CI 的 Unix 包使用 tar.gz 保留可执行权限。

## 数据和边界

未安装时直接运行服务端建议 EXE 旁 `data`，启动脚本建议项目 `data`，`MyPan__DataPath` 可设置初始建议值。安装后以 `server-settings.json` 中的路径为准。文件内容在 `blobs`，原始目录名在 `catalog.json` 和已提交的 `catalog.journal`。备份/迁移必须包含整个数据目录、`api-key.txt`，尤其是 SMB 加密密钥；不要手工把文件塞入 blobs，应通过网页或协议上传。同一个数据目录仅允许一个主服务端写入。

首次打开旧版目录时，会在数据目录生成 `catalog.json.v1-backup-*` 原始目录备份，再补充账户、权限、版本和会话字段，保留文件 ID、路径、版本和回收站记录。初始 `admin` 密码使用现有 API 密钥。已包含新版字段但字段缺失、为 null 或用户列表为空的目录会停止加载并报告原因，避免覆盖已有账户或权限。

当前采用内存目录快照及持久化增量提交日志，推送后按同步范围拉取授权目录，ETag 未变化时返回 304；不是数据库集群或块级差分同步。已实现大文件分块续传。尚未实现去重、分布式部署、分享外链、自动升级、跨平台虚拟占位文件或已签名安装包。大规模目录仍需专门容量测试及事务数据库方案。崩溃或存储删除失败可能残留不再引用的 blob/SMB 暂存文件，尚无后台垃圾回收；配额以已提交目录记录统计。

旧版 Windows 的命令行和 CFAPI 说明见 [旧版文档](docs/legacy-windows.md)。V2 API 补充见 [docs/api-v2.md](docs/api-v2.md)。

## 本次验证

本次通过首次安装 34 项、Windows Explorer / CFAPI 136 项、发布程序 V2 集成 180 项、HTTPS 54 项、真实 Edge 浏览器 73 项及目录迁移恢复 61 项检查。包括安装中断与重启、自定义管理员和存储路径、分块续传、进度槽损坏恢复、并发配额、已提交回执恢复、130 文件批次及原生 Windows 大文件上传。Native AOT 桌面保留 26 项流程及 6 项进程重启恢复检查。此前 Linux x64、macOS ARM64 桌面代码编译通过；对应系统上的原生运行仍待验证。本机 AOT 对照、吞吐开销与容量边界见[同步性能说明](docs/sync-performance.md)。证据路径、二进制 SHA-256、各平台验证范围见已安装包的 [verification.json](artifacts/v2/verification.json) 或待安装包的 [verification.json](artifacts/v2-update/verification.json)。

Windows 上可用 `powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Native` 重跑；加 `-Browser` 执行网页检查，需要 Node 24、Edge 和 PATH 中的 ffmpeg。测试均创建独立 `.test-data` 目录，不使用正式云盘数据。

旧目录升级回归可单独运行 `dotnet run --project tests/MyPan.StoreTests -c Release -- --native`，覆盖旧文件和回收站恢复、原始目录备份、重启登录、损坏目录保护、增量日志恢复及启动脚本实际启动 AOT 服务端。本次安装的 AOT 包通过 61 项隔离升级与启动脚本检查；此前现有数据隔离副本的 59 项回归证据也保留在验证报告的历史字段中。
