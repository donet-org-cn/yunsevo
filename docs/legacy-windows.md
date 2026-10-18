> 此文档保留 V1 的 Windows 配置和操作说明。V2 功能与最新启动方式请见 [项目说明](../README.md)。

# MyPan 云盘

使用 .NET 10 开发的单账户自托管云盘第一版，包含 ASP.NET Core 服务端和 Windows 桌面同步客户端。两端均已实际发布为 Native AOT EXE，客户端使用原生 Win32 设置窗口和 Windows Cloud Files API。

截图中的文件列表就是 Windows 资源管理器。本项目向 Windows 注册真实的同步目录和占位文件，由资源管理器显示云朵、可用性状态、文件大小与修改日期；双击文件时才下载内容。不是另画一个仿资源管理器的文件列表。Windows 主题和版本决定图标的具体外观。

## 已实现

- 资源管理器可见的文件、文件夹占位符；中文路径、零字节文件。
- 按需下载、HTTP Range 流式下载、1 MiB 分块传给 Cloud Files、下载取消和进度上报。
- 客户端“按需下载”设置；关闭后下载并固定全部文件。
- 客户端可指定“云端目录”（如 `工作资料` 或 `部门/资料`），不同客户端和不同本地路径连接同名目录，复用原文件 ID；留空兼容原来的根目录。
- 对文件或整个文件夹执行“保留在此设备”“释放空间”；也监听 Explorer 的固定状态变化。
- 每 5 秒同步目录，本地新建、修改、重命名、删除与云端变化双向同步。
- 只读文件保留原属性上传；被占用或无法访问的项目单独待重试，其他文件继续同步。
- 向 Explorer 上报等待、传输中、完成及错误状态和进度，并发送 Shell 刷新通知；用户目录之外的同步根自动接入 Windows Search 状态缓存。
- 条件上传检查版本；双方同时修改时保留本地冲突副本，原文件更新为云端内容。
- 回收站 API、按删除批次还原目录；云端删除遇到未上传的本地修改时保留并重新上传这些修改。
- 原子持久化目录与客户端状态；客户端重启恢复；服务器独占数据目录。
- Bearer API 密钥；客户端通过 Windows DPAPI 加密保存密钥；远程连接要求 HTTPS。
- 托盘驻留、暂停/恢复、立即同步、登录后启动、解除 Explorer 注册。
- 内嵌网页管理：浏览目录、搜索、多文件/拖放上传、上传进度和取消、新建文件夹、下载、重命名、删除到回收站及恢复；列表每 5 秒自动刷新，目录名称可复制给客户端。
- 首次启动自动生成 256 位随机 API 密钥，保存到程序目录的 `api-key.txt`，重启继续使用。

## 运行

要求 Windows 10 2004 或更新版本 / Windows 11、x64、本机 NTFS 固定磁盘。同步客户端必须保持运行，才能打开尚未下载的占位文件。发布后的 EXE 无需安装 .NET 运行时。

1. 在项目目录运行 `powershell -ExecutionPolicy Bypass -File scripts/start-server.ps1`。默认仅监听 `http://127.0.0.1:5080`，数据保存在项目下 `data`，无需手动设置密钥。
2. 浏览器打开 `http://127.0.0.1:5080`，从 `artifacts/server/api-key.txt` 复制密钥登录，即可上传文件和管理云盘。密钥只保存在当前页面内存中，刷新后重新输入。源码直接运行时，密钥在实际运行的 `bin/Release/net10.0` 目录中；启动日志会显示准确路径。
3. 打开 `artifacts/client/MyPan.Client.exe`，填写服务器地址 `http://127.0.0.1:5080`、相同的 API 密钥、“云端目录”和一个**空的、专用的本地 NTFS 文件夹**。例如云端目录填 `工作资料`，本地目录填 `F:\MyPanFiles`；也可以在网页打开文件夹后点击“复制目录名”。云端目录不存在时首次连接会创建，存在则连接原文件；留空同步全部根目录文件。
4. 勾选“按需下载”，点击“保存并启动”，然后“打开云盘目录”。在网页上传一个文件，客户端下次同步会创建仅云端的占位文件；打开它时才下载内容。不要删除本地文件来测试按需下载，双向同步会把删除同步到云端；已有文件用“释放空间”。
5. 在资源管理器中使用“详细信息”视图，启用“状态”或“可用性状态”列。可用性状态由 Windows Shell 提供；可能需要重新打开资源管理器窗口才能刷新注册信息。

如果列表没有图标，在列标题（名称/修改日期）上右击 → 更多，勾选云盘的“状态”列（`System.StorageProviderUIStatus`，可能有多个同名的“状态”列）。已存在本地的文件上传后仍保留内容，显示本地可用；执行“释放空间”后才变为仅云端占位文件。只读文件无需手动取消只读属性。若启动日志提示 Windows Search 未就绪，检查该服务和目录索引设置；注册仅添加本同步根的规则，解除注册会移除客户端自己添加的规则。

客户端配置默认位于 `%LOCALAPPDATA%\MyPan\client.json`。新配置的同步状态保存在相邻的 `client.json.profiles` 文件夹内，按服务器、云端目录和本地路径分别保存；旧版本匹配的 `client.json.state.json` 会直接复用。不要删除这些状态文件。切换云端目录时选择新的空本地目录，旧的本地文件和状态保留；切换回来可继续原来的同步。

换电脑或换一个本地路径时，填写**相同的服务器、密钥和云端目录**，选择新的空本地目录即可。比如电脑 A 本地 `F:\工作`、电脑 B 本地 `D:\资料`，云端都填 `工作资料`，两端就是同一份文件；本地目录名称不会成为新的云端目录。网页打开“工作资料”后可以查看、上传和删除这些文件。已绑定的云端目录若被网页删除、重命名或替换，原客户端暂停同步并保留本地文件；从回收站恢复原目录后可恢复连接。

更改云端目录后，如果原本地目录已有文件，请选择另一个空目录，例如云端 `myws` 配本地 `F:\2121-myws`，保留原来的 `F:\2121`。不要删除原文件来清空目录，它们可能仍属于原云盘。客户端会检查实际的 Windows 云盘注册：空目录保留着旧 MyPan 注册时可以安全更新、重新连接；有同步状态但注册已解除时会重新注册，不再仅凭状态文件决定注册方式。其他云盘或其他云盘内的子目录不会被接管。注册失败时保留原配置，并显示失败发生在注册还是连接步骤。

命令行配置（密钥不放在命令行参数中）：

```powershell
$secureKey = Read-Host 'API 密钥' -AsSecureString
$env:MYPAN_API_KEY = [Net.NetworkCredential]::new('', $secureKey).Password
.\artifacts\client\MyPan.Client.exe --init --config F:\MyPanConfig\client.json --server http://127.0.0.1:5080 --root F:\MyPanFiles --remote-dir 工作资料
Remove-Item Env:\MYPAN_API_KEY
.\artifacts\client\MyPan.Client.exe --config F:\MyPanConfig\client.json
```

`--headless` 运行控制台同步；Ctrl+C 退出。`--background` 自动开始并隐藏到托盘。关闭设置窗口继续同步；从托盘菜单或窗口“退出”按钮停止。先退出客户端，再执行 `--unregister --config <配置文件>` 解除资源管理器注册并关闭该配置的自动启动。此操作保留数据，但仅云端占位文件需要重新注册并连接后才能打开。

## 编译和 AOT 发布

需要 .NET SDK 10.0.402（`global.json` 固定到 .NET 10），以及 Visual Studio 的“使用 C++ 的桌面开发”和 Windows SDK。NuGet 源仅使用官方 nuget.org。JSON 使用源码生成，原生调用使用 `LibraryImport`，回调使用 `UnmanagedCallersOnly`。

```powershell
dotnet build MyPan.slnx -c Release
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Native
```

输出：`artifacts/server/MyPan.Server.exe` 和 `artifacts/client/MyPan.Client.exe`。PDB 仅用于调试，不是运行依赖。服务端源码可在 Linux 上编译，Linux AOT 应在相应 Linux 构建机发布；本工作区验证的是 Windows x64。

测试在 `.test-data` 的独立目录启动临时服务、注册测试同步根，覆盖协议鉴权、路径、版本冲突、回收站、实际占位文件读写、固定/释放、双向同步、重启、WinRT Shell 注册和原生 AOT 客户端回调。测试会停止自己创建的进程并解除注册，保留文件与日志便于检查。

网页测试使用 Node.js 24 和已安装的 Microsoft Edge，不需要 npm 包：运行 `node tests/browser-smoke.mjs`，会用独立服务端目录和浏览器配置验证登录、上传、覆盖、重命名、下载、回收站和手机布局，截图保存在 `.test-data/web-*`。可用 `MYPAN_TEST_EDGE` 指定 Edge EXE 路径。

2026-10-17 验证结果：Release 编译零警告、零错误；服务端和客户端均完成 Windows x64 Native AOT 发布；130 项联调检查和 25 项真实 Edge 浏览器检查全部通过，包含旧云盘注册恢复、空目录切换、外部云盘保护、同名云端目录跨客户端复用、并发建目录、目录删除保护、旧配置兼容、原生 EXE、随机密钥生成/复用、网页自动发现上传/删除、中文多文件上传/下载、覆盖确认、超限提示、上传取消、回收站，以及只读固件上传、Explorer 状态/进度与索引、文件异常隔离。

## 服务端部署

生产部署设置以下环境变量后运行服务端：

| 环境变量 | 用途 |
| --- | --- |
| `MyPan__ApiKey` | 可选，至少 32 个无空白字符；显式设置时优先使用，并写入密钥文件，兼容已有部署 |
| `MyPan__ApiKeyFile` | 可选，密钥文件路径，默认 EXE 旁的 `api-key.txt` |
| `MyPan__DataPath` | 持久数据目录，默认 EXE 旁的 `data` |
| `MyPan__MaxUploadBytes` | 单文件上传上限，默认 20 GiB |
| `ASPNETCORE_URLS` | 监听地址，默认 `http://127.0.0.1:5080` |

远程部署放在 HTTPS 反向代理后面，配置代理上传大小和超时，保留真实证书校验。不要直接把开发用 HTTP 端口暴露到公网。服务端目录中的 `catalog.json` 和 `blobs` 必须一起备份；同一个数据目录只允许一个服务端进程。

请妥善保管 `api-key.txt`：该文件是明文凭据，不会通过网页提供下载，也已加入 Git 忽略规则。不要删除它来“重启”，删除后下次启动会生成新密钥，需要同时更新网页和所有客户端。已有文件内容无效时服务端会报错，不会悄悄更换密钥。复制 EXE 升级时保留密钥文件和数据目录。

API 说明见 [docs/api.md](api.md)，同步设计和边界见 [docs/design.md](design.md)。当前版本适合验证和继续开发：尚未实现多用户、团队权限、分享链接、断点续传上传、配额、历史版本 UI、自动升级与安装包；旧文件版本和回收站内容暂不自动清理。大量文件时的全目录轮询需要进一步改成增量同步。

Windows 实现依据：[Cloud Files API](https://learn.microsoft.com/en-us/windows/win32/cfapi/cloud-files-api-portal)、[微软 CloudMirror 示例](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CloudMirror)、[Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。
