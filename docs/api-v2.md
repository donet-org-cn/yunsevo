# V2 API

JSON 使用 camelCase 和源生成序列化。`POST /api/auth/login` 接受 `{ "username": "alice", "password": "..." }`，返回 `accessToken`、`expiresUtc`、`user`。客户端使用 `Authorization: Bearer <accessToken>`；网页登录同时设置 HttpOnly、SameSite=Strict Cookie，用于认证媒体和 SSE。Cookie 写入请求须带 `X-MyPan-Client: web`，浏览器页面不会把令牌放入 URL、localStorage 或 sessionStorage。

新版网页分页、密码重置、个人修改密码及回收站删除来源的请求与返回约定，见 [界面与管理更新](design-and-workspace.md)。旧的全量目录和最多 300 条审计接口继续兼容，新网页使用 `/api/browser/*` 和 `/api/admin/*/page` 分页接口。

| 接口 | 用途 |
| --- | --- |
| `GET /api/me` | 当前账户与角色 |
| `GET /api/sync-folders` | 此账户可见的全部文件夹，包含继承后的目录类型与访问权限，供客户端选择同步目录 |
| `POST /api/auth/logout` | 注销当前令牌并清除 Cookie |
| `GET/POST /api/admin/users` | 管理员列出 / 创建用户 |
| `PUT /api/admin/users/{id}` | 显示名称、密码、管理员角色、停用；用户名不可更改 |
| `GET/POST /api/admin/groups` | 列出 / 新建用户组 |
| `PUT/DELETE /api/admin/groups/{id}` | 更新名称与成员 / 删除组及相关授权 |
| `GET /api/principals/users`、`/groups` | 授权编辑器所需的已启用账户与组名称 |
| `GET/PUT/DELETE /api/folders/{id}/policy` | 查询 / 设置 / 恢复继承文件夹策略 |
| `GET /api/entries/{id}/versions` | 当前及保留版本 |
| `GET /api/entries/{id}/versions/{version}` | 下载指定版本，支持 Range |
| `POST /api/entries/{id}/versions/{version}/restore` | 恢复为新版本，必须带当前 `If-Match` |
| `DELETE /api/trash/{id}` | 永久删除所选删除批次内的项目 / 子树及历史版本 |
| `GET /api/preview/{id}?version=...` | 受权限约束的安全类型预览，支持 Range |
| `GET /api/events?after=revision` | SSE `event: change`，数据为 `{ "revision": 123 }`；15 秒心跳，断线可重连 |
| `GET/PUT /api/admin/settings` | 查询运行状态 / 设置协议开关和监听 IP、端口 |
| `POST /api/admin/users/{id}/smb-password` | 管理员重置并返回此用户随机 SMB 专用密码 |
| `GET /api/admin/audit?user=alice&kind=trash&action=DELETE` | 最近最多 300 条匹配审计；kind 支持 all/trash/accounts/settings |

原文件与目录接口继续可用，详见 [基础接口](api.md)。V2 的 Catalog 是**授权后的视图**，`revision` 仍为全局值；`CloudEntry.access` 为位掩码：读=1、写=2、管理=4、结构遍历=8。只为到达共享后代而返回的父目录为 8，不表示可以读取其他子项或写入。管理员和目录所有者通常为 7。权限基于最近一个显式目录策略；未授权内容的直接 ID 下载也会拒绝。

同步目录接口返回 `[{ "id": "GUID", "path": "个人/alice", "category": "private", "access": 7 }]`。category 为 private（私有）、public（公共）、assigned（指定授权）、container（仅结构路径分组）。子目录按最近的显式策略分类；目录列表不返回授权用户、组成员或其他不可见目录。container 的 access 为 8，不能作为同步根目录。客户端绑定所选目录的不可变 ID，目录被删除、重命名或替换后停止该任务并保留本地文件。

策略请求示例：

```json
{
  "folderId": "目录 GUID",
  "ownerId": "所有者 GUID",
  "visibility": "restricted",
  "publicAccess": 0,
  "grants": [{ "kind": "group", "id": "组 GUID", "access": 3 }],
  "quotaBytes": 10737418240,
  "keepVersions": 5
}
```

visibility 为 private/public/restricted；private 仅所有者与管理员，public 另外允许所有登录用户按 publicAccess 访问，restricted 使用 grants。授权 access 只能为 1、3、7。配额和版本策略由管理员控制；目录所有者调整授权不能提高、移除管理员设置的存储上限。keepVersions 包括当前版本，支持 1–1000，留空继承，默认 10。

主要错误码：400 参数错误、401 未登录 / 会话失效、403 无权限、404 不存在、409 路径 / 配置冲突、412 条件写入冲突、413 超过单文件限制、415 不支持预览、507 配额不足。上传和版本恢复会在提交门内再次校验权限与配额；配额拒绝不会替换当前版本。正在下载的不可变 blob 已打开共享删除句柄，版本清理不影响已开始的传输。

WebDAV 使用 `/dav/`，FTP 使用服务设置中的控制端口，SMB 共享名 `mypan`。它们使用同一份目录数据；内部网关接口仅接受每次主服务端启动生成的独立随机服务凭据，普通用户和 API 密钥不能调用 `/api/internal/*`。

回收站下载、预览和整站迁移导出见 [备份与迁移](backup-and-migration.md)。
