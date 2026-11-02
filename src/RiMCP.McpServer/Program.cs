using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RiMCP.Core.Paths;
using RiMCP.McpServer.Services;

// ============================================================================
// RiMCP.McpServer —— RimWorld 代码检索 MCP stdio 服务器
//
// 启动流程：
//   1. 解析根目录（RIMCP_ROOT 覆盖，默认 F:\KXMOD\RiMCP_v2\）并经 PathUtil.LocateRoot 校验
//   2. 构造运行配置（index 目录、models 目录）
//   3. DI 注册三个核心单例：IndexService（symbols+mmap vectors+Lucene+ONNX）、
//      QueryEmbedding（查询侧嵌入）、GraphService（CSR/CSC 图查询）
//   4. 注册 MCP stdio transport + 从程序集自动发现 [McpServerToolType] 工具
//   5. AppRuntime 捕获 service provider，供静态工具类从 DI 解析单例
//   6. 运行 MCP stdio 服务器（stdin/stdout 走 JSON-RPC，日志一律走 stderr）
// ============================================================================

// ---- 1. 路径解析 ------------------------------------------------------------
string rootArg = Environment.GetEnvironmentVariable("RIMCP_ROOT") ?? @"F:\KXMOD\RiMCP_v2";
string v2Root = PathUtil.LocateRoot(rootArg);   // 校验同时含 RimWorldData 与 models
string indexDir = Path.Combine(v2Root, "index");
string modelDir = Environment.GetEnvironmentVariable("RIMCP_MODEL_PATH")
    ?? Path.Combine(v2Root, "models", "e5-base-v2-onnx");

// ---- 2. Host + 日志（stdout 是 MCP 协议通道，日志只能走 stderr）-------------
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Information);

// ---- 3. 核心服务 ------------------------------------------------------------
// IndexService：启动时一次性加载 build.json 校验 → SymbolIndex(symbols.tsv) →
//              mmap vectors.bin → 打开 Lucene 目录 → 加载 ONNX 模型 + tokenizer。
//              用 Lazy/延迟初始化，让 MCP 握手在后台完成、首个工具调用前就绪。
builder.Services.AddSingleton(sp => new IndexService(v2Root, indexDir, modelDir));
builder.Services.AddSingleton<QueryEmbedding>();  // 查询侧 "query: " 前缀 + WordPiece + 池化
builder.Services.AddSingleton<GraphService>();    // 加载 graph.csr.bin / csc.bin / nodes / pagerank

// ---- 4. MCP stdio ----------------------------------------------------------
// 工具返回 DTO 时 SDK 会自动产 JSON 文本块 + structuredContent。
// 不传自定义 JsonSerializerOptions（那会要求手动绑定 TypeInfoResolver），
// 直接用 SDK 默认选项（默认 camelCase，字段名即 count/results）。
builder.Services.AddMcpServer()
    .WithStdioServerTransport()                                     // stdin/stdout JSON-RPC
    .WithToolsFromAssembly(typeof(Program).Assembly);               // 自动发现 Tools/* 下工具

// ---- 5/6. 运行 --------------------------------------------------------------
using var app = builder.Build();
AppRuntime.Init(app.Services);
await app.RunAsync();

/// <summary>
/// 仅供静态工具类访问 DI 单例的静态门面。工具本身为 <c>[McpServerToolType]</c> 静态类，
/// 由 ModelContextProtocol 经 <c>WithToolsFromAssembly</c> 发现；它们从 <see cref="Instance"/>
/// 惰性解析 IndexService / GraphService，避免在静态字段初始化阶段触碰仍未就绪的服务。
/// </summary>
internal static class AppRuntime
{
    private static IServiceProvider? _services;

    /// <summary>在 Host 构建完成后注入 provider，此后保持只读。</summary>
    public static void Init(IServiceProvider services) => _services = services;

    /// <summary>从 DI 取服务；未初始化时抛异常以快速暴露配置错误。</summary>
    public static T Get<T>() where T : notnull
        => _services is { } services
           ? services.GetRequiredService<T>()
           : throw new InvalidOperationException("AppRuntime 尚未初始化，服务器未就绪。");
}