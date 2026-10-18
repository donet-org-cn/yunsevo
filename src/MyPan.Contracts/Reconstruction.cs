namespace MyPan.Contracts;

public sealed record ReconstructionCapabilities(bool Cpu, bool Vulkan, string? Device = null, bool Experimental = true, string? Error = null);
public sealed record ReconstructionPhoto(Guid Id, string Version);
public sealed record StartReconstruction(Guid FolderId, string Name, List<ReconstructionPhoto> Photos, string Backend = "auto", int Quality = 0, string Subject = "object", Guid? ClientRequestId = null);
public sealed record ReconstructionJob(Guid Id, Guid UserId, string Username, string Name, string State, int Progress,
    string Message, string Backend, string Subject, int PhotoCount, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, CloudEntry? Model = null);

public static class ReconstructionMessages
{
    public static string Progress(string text) => text.StartsWith("features ", StringComparison.Ordinal) ? "提取照片特征" :
        text.StartsWith("matching", StringComparison.Ordinal) ? "匹配重叠视角" :
        text.StartsWith("initial", StringComparison.Ordinal) ? "初始化三维定位" :
        text.StartsWith("camera", StringComparison.Ordinal) || text.StartsWith("pnp", StringComparison.Ordinal) ? "恢复相机位置" :
        text.StartsWith("refining", StringComparison.Ordinal) ? "优化空间几何" :
        text.StartsWith("dense", StringComparison.Ordinal) ? "计算多视角深度 · " + (text.Contains("vulkan", StringComparison.Ordinal) ? "Vulkan GPU" : "CPU") :
        text.StartsWith("fusing", StringComparison.Ordinal) ? "融合三维表面" :
        text.StartsWith("writing", StringComparison.Ordinal) ? "保存 GLB 模型" :
        text == "completed" ? "重建已完成" : text.StartsWith("EXIF", StringComparison.Ordinal) ? "读取相机焦距标定" :
        text.StartsWith("No EXIF", StringComparison.Ordinal) ? "原片没有焦距标定，使用估计值；如失败请使用引导相机补拍。" : text;
}
