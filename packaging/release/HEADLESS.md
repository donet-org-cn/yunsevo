# 沄序 · 无界面同步客户端

完整解压后运行 `MyPan.Headless.exe`（Windows）或 `./MyPan.Headless`（Linux/macOS），无需安装 .NET。

复制 `client.example.json` 为自己的配置文件，填写 HTTPS 服务地址、用户名、云端目录实际路径和本地目录。`个人/alice` 仅为示例，需替换为当前用户路径。跨设备登录要求受信任的 HTTPS；HTTP 仅允许本机回环地址。

设置环境变量 `MYPAN_PASSWORD_FILE` 指向仅本用户可读的密码文件，然后运行：

```sh
./MyPan.Headless --config /path/to/client.json
```

加 `--once` 仅同步一次。Linux 可参考 `mypan-sync.service` 和 `client.env.example` 安装用户级 systemd 服务；示例默认程序路径 `/opt/mypan/headless/MyPan.Headless`，需按安装位置调整。

离线删除默认保留待确认，可用 `--config client.json --list-deletions` 查看，然后运行 `--config client.json --resolve-delete relative/path --decision cloud|local|defer` 选择删除云端、仅本地删除或稍后处理。正常在线同步的本地删除直接进入云端回收站。

配置和密码文件权限建议设为 600。请保留状态、删除确认队列与 activity 日志；不应把密码放在命令行参数或仓库中。第三方许可见 `THIRD-PARTY-NOTICES.md`。
