# 服务端高精度重建工作进程（试验）

主服务端仍为 .NET 10 Native AOT。此可选工作进程使用 CPU，在独立进程中完成主体分割、相机标定、多视角深度与贴图，不需要 CUDA。原来的共享 C++ / Vulkan 草稿引擎继续保留。不要将 CPU 工作进程的结果标为 Vulkan 生成。

## 安装

需要 Python 3.13、约 8 GB 空闲临时磁盘、建议 16 GB 内存。依赖与权重应安装在服务账号可读、普通用户不可修改的目录。以下依赖是本次 Windows CPU 验证使用的版本；其他平台需分别验证本机二进制与模型效果。

```powershell
python -m venv .venv-quality
.venv-quality/Scripts/python -m pip install -r tools/reconstruction/requirements.txt
.venv-quality/Scripts/python -m pip install torch==2.14.1 torchvision==0.29.1 --index-url https://download.pytorch.org/whl/cpu
.venv-quality/Scripts/python -m pip install https://github.com/ChaoningZhang/MobileSAM/archive/f706ad9c4eb7f219c00d9050e46328518ffb65d2.zip
```

从 [MobileSAM 官方仓库](https://github.com/ChaoningZhang/MobileSAM)取得 `weights/mobile_sam.pt`，SHA256 必须是 `6dbb90523a35330fedd7f1d3dfc66f995213d81b29a5ca8108dbcdd4e37d6c2f`。从 [OpenMVS 官方 v2.4.0](https://github.com/cdcseacave/openMVS/releases/tag/v2.4.0)取得 CPU 工具，保留完整运行库目录。应用不会在用户提交照片时在线下载依赖或上传照片给第三方。

创建同目录 `worker.local.json`（已被 Git 忽略）：

```json
{
  "checkpoint": "D:/YunsevoQuality/mobile_sam.pt",
  "openMvsDirectory": "D:/YunsevoQuality/openmvs",
  "ffmpeg": "D:/YunsevoQuality/ffmpeg.exe",
  "ffprobe": "D:/YunsevoQuality/ffprobe.exe"
}
```

用 `python tools/reconstruction/worker.py --capabilities` 检查配置。也可用 `YUNSEVO_QUALITY_CONFIG` 指定配置文件的绝对路径。服务端启动环境增加：

```powershell
$env:MyPan__ReconstructionQualityPython = 'D:/YunsevoQuality/.venv/Scripts/python.exe'
$env:MyPan__ReconstructionQualityWorker = 'D:/YunsevoQuality/worker.py'
```

重启服务端后网页三维扫描会启用两个独立选项：

- **高精度，quality=2**：主体分割、镜头畸变标定、多视角深度和原片贴图。小范围平面缺口仅在边界共面、相邻表面方向一致且至少三个分离视角的照片颜色一致时插值，记录补面比例；盒口和未拍到的底面不自动封闭。插值仍是估计，不能作为实测精度证明。
- **轮廓外形，quality=3**：由多角度轮廓估计外形，主要辅助透明瓶身等弱纹理对象。若背景空间点可靠地恢复出邻近支撑平面，会约束底部外推，避免在桌面下生成尖锥；该平面下的瓶底细节仍未测量。它是明确选择的几何近似，无法恢复内凹、内部结构和未拍到的底面；不会在深度失败时自动冒充实测结果。

也可在服务端程序目录保存 `reconstruction-quality.local.json`，内容为 `{"MyPan":{"ReconstructionQualityPython":"完整 Python 路径","ReconstructionQualityWorker":"完整 worker.py 路径"}}`。环境变量和命令行优先于该文件。

配置 FFmpeg/FFprobe 后，网页同一目录中的视频可以选择“原视频自动选帧”。服务端从 3–180 秒、最多 512 MB 的连续视频提取最多 120 个视角，保留源分辨率（最长边上限 3840），不把 1080p 放大成 4K。对于转角处视角间隔较大的旧 48 帧素材，应优先用原视频重新取帧，而不是重复计算同一组不足的照片。FFmpeg 是独立安装的工具，许可证取决于所用构建，不随本项目商业包默认分发。

当前只接受 `subject=object`、`backend=cpu/auto`；显式 Vulkan 请求会拒绝。任务最多 90 分钟，与普通队列共享并发限制、取消和权限复核。原片保留在云端，临时工作目录由任务清理，模型旁的内部 `.quality.json` 供诊断；质量检查不是独立测量精度证明。

## 输入与质量边界

照片使用同一镜头和像素尺寸。保留原 JPEG 字节与焦距，标定、轮廓和贴图均使用原始栅格坐标；分割时为识别而旋转的画面，其轮廓会按 EXIF 逆变换回原栅格。最后只在模型展示时统一朝向，避免重复旋转和无意义的重压缩。对于同一原视频的连续帧，或 EXIF 证明属于连续短时采集的照片，按拍摄顺序匹配邻近视角和首尾重叠，减少相同包装两面被错误合并；缺少时间证据的照片不按文件名假定运动顺序。

主体分割同时比较完整物体候选与局部候选，视频使用前后帧轮廓连续性选择，最多剔除 10% 的明显失败帧，只删除临时副本。所有校准视角都参与最终轮廓检查，避免少量规则抽样放大个别错误轮廓。存在大范围未覆盖方向的素材会拒绝发布完整环绕模型。

首先利用完整场景辅助静止物体定位；未通过质量检查时，再排除背景重建，适应物体转动而背景不动的情况。流程检查定位覆盖、重投影误差、视角跳变、主体位置、表面连通性和与原片轮廓的一致性。贴图经过射线遮挡检查、相邻可见照片的有限曝光校正和连续区域选择，要求原片覆盖至少 90% 的外表面积；薄片背面单独选片，未观察到的背面用中性灰表示，不重复外侧印刷。所有贴图总解码像素限制为 128 Mi。

不能仅凭“所有相机定位成功”“模型可旋转”或“网格封闭”验收。清晰度、贴图接缝、薄片、凹处及底部仍须检查真实模型。抖动引起的姿态变化可以估计；已经失焦、遮挡、透明折射或没有拍到的内容不能保证恢复。

验证：`python tests/reconstruction-quality.py` 使用合成数据，禁止把私人素材加入仓库。真实对比模型与截图仅保存在本地诊断目录或原用户的私有云目录。

正式任务会保留 `.quality.json`；任务所有者可在网页下载，或访问 `GET /api/reconstruction/jobs/{id}/quality`。新报告包括实际模式、源码哈希、每张输入照片的 SHA-256、相机数量与轮廓覆盖。前景掩膜由固定版本的本地 MobileSAM 自动计算，贴图来自原始照片；不调用生成式图片服务。quality=1 的 C++ 深度结果与 quality=3 的轮廓结果不可当作同一算法进行比较。

`panorama.py` 是独立的 CPU 看房拼接工具，接受照片目录或带 `--ffmpeg` 的视频路径，输出 `名称.360.jpg` 和报告。SIFT 匹配、球面投影、曝光补偿、接缝选择及多频段融合均自动执行；弱纹理视角按特征数量筛除，不包含特定素材名的分支。它不生成三维房屋几何，手持视差及运动物体可能留下接缝，未拍到的上下区域保留深色。

## 第三方边界

COLMAP 使用 BSD 许可，MobileSAM 使用 Apache 2.0；具体声明以对应固定版本为准。**OpenMVS 使用 AGPL-3.0**，本工作进程通过独立安装的 CLI 调用它。本仓库不把 OpenMVS 二进制放进现有商业安装包，也不将其重新声明为本项目的专有代码。商用分发、修改或托管该组件时必须按其许可证处理相应义务；进程隔离本身不代表豁免。许可证与源码入口：[OpenMVS LICENSE](https://github.com/cdcseacave/openMVS/blob/v2.4.0/LICENSE)、[COLMAP](https://github.com/colmap/colmap)、[MobileSAM](https://github.com/ChaoningZhang/MobileSAM)。
