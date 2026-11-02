namespace RiMCP.Core.Support;

/// <summary>
/// ONNX 运行后端选择（纯字符串部分，不依赖 OnnxRuntime）。
/// 模块化：本类只负责"解析出"用户要用的 provider；真正挂载执行提供方在调用项目本地实现。
/// 取值约定：cuda / directml / cpu。
/// </summary>
public static class OnnxProviderResolver
{
    /// <summary>
    /// 解析最终 provider。优先级：显式参数 &gt; 环境变量 <c>RIMCP_ONNX_PROVIDER</c> &gt; 编译时默认 cuda。
    /// </summary>
    /// <param name="explicitProvider">显式传入的 provider（例如 CLI --onnx-provider）。空则忽略。</param>
    public static string Resolve(string? explicitProvider = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitProvider))
            return explicitProvider.Trim().ToLowerInvariant();

        var env = Environment.GetEnvironmentVariable("RIMCP_ONNX_PROVIDER");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim().ToLowerInvariant();

        return "cuda";
    }
}