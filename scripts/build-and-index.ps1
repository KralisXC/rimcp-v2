#Requires -Version 5.1
# ============================================================
#  RiMCP_v2 构建索引
#  用法：.\build-and-index.ps1 [-BatchSize 32]
# ============================================================
param(
    [int]$BatchSize = 32,
    [switch]$ForceClean,
    [ValidateSet("cuda","directml","cpu","auto")] [string]$OnnxProvider = "auto"
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

# 项目根 = scripts 的上一级（$PSScriptRoot 即 scripts 目录）
$ProjectRoot = Split-Path -Parent $PSScriptRoot
Write-Host "项目根目录：$ProjectRoot" -ForegroundColor DarkGray

function Write-Step($msg) { Write-Host "`n== $msg ==" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Err($msg)   { Write-Host "  [!!] $msg" -ForegroundColor Red }

Write-Step "检测运行环境"
$hasGpu = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($hasGpu) { Write-Ok "检测到 NVIDIA GPU，将使用 CUDA 加速" }
else { Write-Host "未检测到 nvidia-smi，将回退到 CPU 嵌入（较慢）。" -ForegroundColor Yellow }

$modelFile = Join-Path $ProjectRoot "models\e5-base-v2-onnx\model.onnx"
if (-not (Test-Path $modelFile)) {
    Write-Err "找不到模型：$modelFile"
    Write-Host "请先运行 scripts\install.ps1 第 2 步，或手动把模型放到该目录。" -ForegroundColor Yellow
    exit 1
}
Write-Ok "模型就绪：$modelFile"

$rwRoot = Join-Path $ProjectRoot "RimWorldData"
if (-not (Test-Path (Join-Path $rwRoot "Data"))) {
    Write-Err "找不到 RimWorldData\Data（XML Def）。请先用 install.ps1 准备。"; exit 1
}
if (-not (Test-Path (Join-Path $rwRoot "RimWorld_Decompiled\Assembly-CSharp"))) {
    Write-Err "找不到反编译 C# 源码。请先用 install.ps1 准备。"; exit 1
}
Write-Ok "RimWorldData 就绪"

Write-Step "索引输出目录"
$indexDir = Join-Path $ProjectRoot "index"
if ($ForceClean -and (Test-Path $indexDir)) {
    Write-Host "正在清空 $indexDir ..."
    Remove-Item $indexDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "已清空旧索引"
}
elseif (Test-Path $indexDir) {
    $ans = Read-Host "发现已存在的 index\ 目录。清空后重建？[Y/N]"
    if ($ans -match '^(Y|y|是|是|确实)$') {
        Write-Host "正在清空 $indexDir ..."
        Remove-Item $indexDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Ok "已清空旧索引"
    } else {
        Write-Host "保留旧索引，本次会覆盖同名的构建产物。" -ForegroundColor Yellow
    }
}

Write-Step "构建索引（预计约 40 分钟）"
# provider 优先级：显式参数 > 自动检测（GPU 存在用 cuda，否则 cpu）
$provider = $OnnxProvider.ToLowerInvariant()
if ($provider -eq "auto") {
    $provider = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { "cuda" } else { "cpu" }
}
Write-Ok "ONNX provider = $provider"

Push-Location (Join-Path $ProjectRoot "src")
try {
    & dotnet run --project RiMCP.Indexer\RiMCP.Indexer.csproj -- `
        --root "$rwRoot" `
        --batch-size $BatchSize `
        --onnx-provider $provider
    if ($LASTEXITCODE -ne 0) {
        Write-Err "索引构建失败（退出码 $LASTEXITCODE）。"
        Write-Host "显存不足？请降低 batch size（建议减半）后重试。" -ForegroundColor Yellow
        exit 1
    }
} finally { Pop-Location }

Write-Step "构建完成，产物统计"
if (Test-Path (Join-Path $indexDir "symbols.tsv")) {
    $lines = (Get-Content (Join-Path $indexDir "symbols.tsv") | Measure-Object -Line).Lines
    Write-Ok "symbols.tsv 约 $lines 行"
}
$vecSize = (Get-Item (Join-Path $indexDir "vectors.bin") -ErrorAction SilentlyContinue).Length
if ($vecSize) { Write-Ok ("vectors.bin {0:N1} MB" -f ($vecSize / 1MB)) }
$meta = Join-Path $indexDir "build.json"
if (Test-Path $meta) { Write-Host "详情见：$meta" -ForegroundColor Yellow }
Write-Host "索引已就绪，可配置 MCP 客户端开始使用。" -ForegroundColor Green