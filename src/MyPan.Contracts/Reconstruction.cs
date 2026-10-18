namespace MyPan.Contracts;

public sealed record ReconstructionCapabilities(bool Cpu, bool Vulkan, string? Device = null, bool Experimental = true, string? Error = null);
public sealed record ReconstructionPhoto(Guid Id, string Version);
public sealed record StartReconstruction(Guid FolderId, string Name, List<ReconstructionPhoto> Photos, string Backend = "auto", int Quality = 0, string Subject = "object", Guid? ClientRequestId = null);
public sealed record ReconstructionJob(Guid Id, Guid UserId, string Username, string Name, string State, int Progress,
    string Message, string Backend, string Subject, int PhotoCount, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, CloudEntry? Model = null);

public static class ReconstructionMessages
{
    public static string Error(string text)
    {
        if (text.StartsWith("重建未完成：", StringComparison.Ordinal)) text = text[6..];
        if (text.StartsWith("Only ", StringComparison.Ordinal) && text.Contains(" cameras registered", StringComparison.Ordinal))
            return "相机定位不足：" + text[5..text.IndexOf(" cameras registered", StringComparison.Ordinal)] + " 张照片完成定位。请保持物体居中、对焦清晰，绕行时补充相邻视角；避免人员移动、变焦和快速转动。原片已保留，可补拍后重试。";
        if (text.Contains("Insufficient camera movement / parallax", StringComparison.Ordinal)) return "缺少足够的相机位移。请带着手机围绕静止物体走动，原地转动手机无法恢复三维深度。原片已保留。";
        return text;
    }
    public static string Progress(string text) => text.StartsWith("bundle adjustment", StringComparison.Ordinal) ? "联合优化相机位置与空间点" :
        text.StartsWith("registration candidate", StringComparison.Ordinal) ? "验证相机定位覆盖率" :
        text.StartsWith("recovering view", StringComparison.Ordinal) || text.StartsWith("recovered view", StringComparison.Ordinal) ? "重新匹配未定位的照片" :
        text.StartsWith("focused object", StringComparison.Ordinal) ? "定位中心物体，排除远处背景" : text.StartsWith("features ", StringComparison.Ordinal) ? "提取照片特征" :
        text.StartsWith("matching", StringComparison.Ordinal) ? "匹配重叠视角" :
        text.StartsWith("initial", StringComparison.Ordinal) ? "初始化三维定位" :
        text.StartsWith("camera", StringComparison.Ordinal) || text.StartsWith("pnp", StringComparison.Ordinal) ? "恢复相机位置" :
        text.StartsWith("refining", StringComparison.Ordinal) ? "优化空间几何" :
        text.StartsWith("dense", StringComparison.Ordinal) ? "计算多视角深度 · " + (text.Contains("vulkan", StringComparison.Ordinal) ? "Vulkan GPU" : "CPU") :
        text.StartsWith("fusing", StringComparison.Ordinal) ? "融合三维表面" :
        text.StartsWith("texturing", StringComparison.Ordinal) ? "从原照片生成清晰贴图" :
        text.StartsWith("writing", StringComparison.Ordinal) ? "保存 GLB 模型" :
        text == "completed" ? "重建已完成" : text.StartsWith("EXIF", StringComparison.Ordinal) ? "读取相机焦距标定" :
        text.StartsWith("No EXIF", StringComparison.Ordinal) ? "原片没有焦距标定，使用估计值；如失败请使用引导相机补拍。" : text;
}
