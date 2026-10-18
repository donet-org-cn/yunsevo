# HTTP API

以下记录基础文件接口。V2 已增加个人账户、授权目录和管理功能，认证与新接口以 [api-v2.md](api-v2.md) 为准。`/health`、登录接口与固定内嵌网页资源公开访问，其他接口需要有效会话；旧 API 密钥仅作为可关闭的管理员兼容入口。JSON 属性为 camelCase。

首次启动在 EXE 旁创建 `api-key.txt`，内容是 32 字节随机数的 64 位十六进制字符串。后续启动读取并复用；可用 `MyPan__ApiKeyFile` 改变保存路径。显式 `MyPan__ApiKey` 优先并写入文件，兼容原来的部署。

| 方法与路径 | 用途 |
| --- | --- |
| `GET /health` | 存活检查，返回纯文本 |
| `GET /api/entries` | 当前完整目录和全局 revision |
| `GET /api/capabilities` | 上传限制 `{"maxUploadBytes":21474836480}`，网页用来提前检查文件大小 |
| `POST /api/folders` | 新建目录，JSON `{"path":"资料"}` |
| `PUT /api/content?path=资料/a.txt` | 原始二进制请求体，流式上传 |
| `GET /api/content/{id}?version={version}` | 下载，支持标准 HTTP Range |
| `PATCH /api/entries/{id}` | 移动/重命名，JSON `{"path":"新路径","version":"当前版本"}` |
| `DELETE /api/entries/{id}` | 移入回收站；目录包含整个子树 |
| `GET /api/trash` | 回收站完整目录 |
| `POST /api/trash/{id}/restore` | 按删除批次还原项目或目录子树 |

`Catalog` 包含 `revision` 和 `entries`。每个项目包含 `id`、`path`、`isDirectory`、`size`、`modifiedUtc`、`version`。路径为相对路径，使用 `/` 分隔，不允许设备名、路径穿越、绝对路径和 Windows 不支持的文件名。

新建上传必须带 `If-None-Match: *`，更新上传和删除必须带 `If-Match: "当前版本"`。目录删除还必须带 `X-Catalog-Revision: 当前完整目录的revision`，防止误删其他客户端刚加入的子文件。还原遇到同名项目返回 409。

状态码：400 参数或路径错误，401 未鉴权，404 项目不存在，409 路径/下载版本冲突，412 条件版本不匹配，413 超出上传上限。修改元数据失败不会替换内存中的已提交目录。内容版本在重命名时保持不变，文件 ID 始终稳定。

PowerShell 上传示例：

```powershell
$apiKey = (Get-Content '.\artifacts\server\api-key.txt' -Raw).Trim()
$headers = @{ Authorization = "Bearer $apiKey"; 'If-None-Match' = '*' }
Invoke-RestMethod -Method Put -Uri 'http://127.0.0.1:5080/api/content?path=hello.txt' -Headers $headers -InFile '.\hello.txt' -ContentType 'application/octet-stream'
```

文件 ID 与版本用于定位不可变内容对象。服务器数据目录不按用户文件名映射真实磁盘路径。回收站保留原始目录和内容，还原只包含同一删除批次内的后代。

客户端可以用 `RemoteDirectory` 选择云盘内的逻辑目录，例如 `工作资料`。这不改变 HTTP 协议：上传路径仍为 `工作资料/a.txt`，网页显示完整云盘目录；客户端只显示所选目录的相对路径 `a.txt`。目录/文件 ID 和 Catalog 的全局 revision 在客户端映射中保持不变，同名云端目录可被多台设备复用。
