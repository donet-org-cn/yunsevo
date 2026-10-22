# 沄序 Yunsevo 云盘入口清理

双击同目录的 **清理云盘入口.cmd**，核对列表后点击“清理勾选入口”。无需安装 .NET SDK，也不需要 Python。适用于支持云文件的 Windows 10 / 11。

默认勾选测试目录 `.test-data`、失效目录及空注册记录。自动识别当前用户的 Yunsevo、旧版 MyPan、PreviousFixture 测试入口，不会扫描或清理 OneDrive 等其他云盘，也不会处理其他 Windows 用户的注册。

工具读取本机 Yunsevo / MyPan 桌面和旧版客户端配置，默认保护已配置的目录及其父子目录，包括暂停的任务。仍存在的未知目录默认不勾选，可手动选择。若确实要移除已配置目录的侧栏入口，可开启“允许勾选已配置目录的入口”再勾选。同步任务再次启动可能重新注册入口；本工具不删除任务配置。

## 文件与备份

仅清理 Windows 侧栏的 SyncRootManager、NameSpace、CLSID 注册记录，不访问云端、不遍历或删除同步文件、不停止客户端、不重启资源管理器。**不调用** `StorageProviderSyncRootManager.Unregister` 或 `CfUnregisterSyncRoot`：注销系统云文件根可能处理本地占位文件，不适合仅清理侧栏入口。

操作前导出每个入口的注册表记录到同目录 `backups/时间-编号/`，包含 `.reg` 文件、`manifest.json` 和执行后的 `result.json`。配置中的密码和令牌不会导出。备份必须成功才会开始清理。

需要恢复入口时，可在退出客户端后双击相应 `.reg` 文件导入，涉及 HKLM 的记录需要管理员权限；重新启动对应同步任务也会注册入口。没有注册信息的空记录无法恢复出一个有效任务。恢复文件应来自这台电脑、同一 Windows 用户；备份不包含云端文件内容。

清理后通常自动刷新。若某个已打开的窗口仍显示旧入口，关闭后重新打开资源管理器。若提示无权移除空的 HKLM 注册记录，可用同一账户“以管理员身份运行”命令行；不要改用其他账户。

## 命令行

以下命令在工具目录中使用系统 Windows PowerShell 5.1 执行。未加 `-Apply` 时只读预览。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\cleanup-explorer.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\cleanup-explorer.ps1 -Apply -WhatIf
powershell -NoProfile -ExecutionPolicy Bypass -File .\cleanup-explorer.ps1 -Apply
```

自定义配置或额外需要保留的目录：

```powershell
.\cleanup-explorer.ps1 -Apply -ConfigPath 'D:\配置\client.json' -KeepPath 'D:\我的云盘'
```

`-Json` 输出结构化预览或执行报告；`-BackupDirectory` 可指定备份位置。若发现配置损坏、目录映射冲突或其他用户映射，会停止或保护对应入口。

明确清理某个已配置目录的入口：

```powershell
.\cleanup-explorer.ps1 -Apply -RemovePath 'F:\2121','F:\21213'
```

接口说明：[Microsoft：注销侧栏同步根](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager.unregister)、[Microsoft：CfUnregisterSyncRoot 对占位文件的处理](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot)。
