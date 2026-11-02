#Requires -Version 5.1
# ============================================================
#  RiMCP_v2 一键安装（主逻辑）
#  步骤：检测环境 → 模型 → RimWorldData → 编译 → 建索引 → 配置
# ============================================================
param(
    [switch]$DryRun      # 调试：只打印计划，不实际执行
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

if ($DryRun) {
    Write-Host "[DRYRUN] RiMCP_v2 安装计划（不会实际执行）：" -ForegroundColor DarkGray
    Write-Host "  1. 检测环境（dotnet / nvidia-smi / nvcc）"
    Write-Host "  2. 准备 ONNX 模型：缺失时 [A] 自动转换（venv + pip + optimum）/ [B] 手动"
    Write-Host "  3. 准备 RimWorldData（Data + 反编译源码）"
    Write-Host "  4. dotnet build（RiMCP.slnx，4 个项目）"
    Write-Host "  5. 构建索引（约 40 分钟）"
    Write-Host "  6. 打印 Trae mcp.json"
    Write-Host "[DRYRUN] 退出，未做任何修改。" -ForegroundColor DarkGray
    exit 0
}

# 项目根 = scripts 的上一级（$PSScriptRoot 即 scripts 目录）
$ProjectRoot = Split-Path -Parent $PSScriptRoot
Write-Host "项目根目录：$ProjectRoot" -ForegroundColor DarkGray

function Write-Step($msg) { Write-Host "`n== $msg ==" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Err($msg)   { Write-Host "  [!!] $msg" -ForegroundColor Red }

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  RiMCP_v2 安装器" -ForegroundColor Cyan
Write-Host "  RimWorld 代码索引 + MCP 服务器" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

# ============ 第 1 步：检测环境 ============
Write-Step "第 1 步 / 6：检测环境"

$envOk = $true

# --- dotnet ---
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Err "未检测到 .NET SDK。请安装 .NET 8 SDK："
    Write-Host "        https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    $envOk = $false
} else {
    $dotnetVer = & dotnet --version
    Write-Ok ".NET $dotnetVer"
}

# --- 显卡 / 后端检测：决定编译的 OnnxBackend 与索引的 provider ---
# 规则：NVIDIA → CUDA；AMD/Intel 独显 → DirectML；都没有 → CPU
$onnxBackend = "CUDA"
$onnxProvider = "cuda"
$hasGpu = Get-Command nvidia-smi -ErrorAction SilentlyContinue
$nvcc = Get-Command nvcc -ErrorAction SilentlyContinue

if ($hasGpu) {
    Write-Ok "检测到 NVIDIA 显卡"
    if (-not $nvcc) {
        Write-Err "未检测到 CUDA 工具链(nvcc)。请安装 CUDA 13.0 + cuDNN 9 以获得 GPU 加速："
        Write-Host "        https://developer.nvidia.com/cuda-13-0-0-download-archive" -ForegroundColor Yellow
        Write-Host "        未安装将回退 CPU，仅影响索引速度，不影响使用。" -ForegroundColor Yellow
    } else {
        Write-Ok "CUDA 编译工具链已就绪"
    }
} else {
    Write-Err "未检测到 NVIDIA 显卡(nvidia-smi)。探测 AMD / Intel 独显（DirectML）..."
    $aml = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -match "AMD|Radeon|Intel|Arc" }
    if ($aml) {
        Write-Ok "检测到可用的 DirectML 显卡：$($aml.Name -join '、')"
        Write-Host "        将使用 DirectML 后端（AMD/Intel 独显加速，约 1-2 小时）。实验性功能，未经完整测试。" -ForegroundColor Yellow
        $onnxBackend = "DirectML"
        $onnxProvider = "directml"
    } else {
        Write-Host "未检测到 AMD/Intel 独显，将使用纯 CPU 后端（约 2-5 小时），日常查询无感，无需 CUDA/cuDNN。" -ForegroundColor Yellow
        $onnxBackend = "CPU"
        $onnxProvider = "cpu"
    }
}
Write-Ok "后端 = $onnxBackend （ONNX provider = $onnxProvider）"

if (-not $envOk) {
    Write-Host "`n环境不完整，请按上面提示安装后重新运行。" -ForegroundColor Red
    exit 1
}

# ============ 第 2 步：准备 ONNX 模型 ============
Write-Step "第 2 步 / 6：准备 ONNX 模型"

$modelDir = Join-Path $ProjectRoot "models\e5-base-v2-onnx"
$modelFile = Join-Path $modelDir "model.onnx"
$venvDir   = Join-Path $ProjectRoot ".onnx-convert-venv"
$convertScript = Join-Path $ProjectRoot "convert_onnx.py"
$hfCache   = Join-Path $ProjectRoot ".hf-download-cache"

# 完全成功后才允许清理
function Clear-ConvertTemp {
    $freed = 0L
    foreach ($t in @($venvDir, $hfCache)) {
        if (Test-Path $t) {
            $sz = (Get-ChildItem $t -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            if ($sz) { $freed += $sz }
            Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $convertScript) { Remove-Item $convertScript -Force -ErrorAction SilentlyContinue }
    if ($freed -gt 0) { Write-Ok "已释放磁盘空间：{0:N0} MB" -f ($freed / 1MB) }
}

if (Test-Path $modelFile) {
    Write-Ok "模型已存在：$modelFile"
} else {
    Write-Host "未找到模型：$modelFile" -ForegroundColor Yellow
    Write-Host "请选择获取方式："
    Write-Host "  [A] 自动转换（推荐）——全自动，约 15-30 分钟，临时占用约 1.2GB"
    Write-Host "  [B] 手动准备——跳过，自己放模型"
    Write-Host "  [C] 退出——稍后再来"
    $choice = Read-Host "输入 A / B / C"

    if ($choice -match '^(c|C|q|Q)$') {
        Write-Host "已退出。稍后重新运行 install.bat 即可继续。" -ForegroundColor Yellow
        exit 0
    }
    elseif (-not $choice -match '^(A|a)$') {
        Write-Host "请手动将 ONNX 模型放到 models\e5-base-v2-onnx\" -ForegroundColor Yellow
        Write-Host "  可从本项目 Releases 页下载 zip 包（约 420MB）："
        Write-Host "    https://github.com/KralisXC/rimcp-v2/releases"
        Write-Host "  或自行用 optimum 转换 intfloat/e5-base-v2"
    }
    else {
        # ===== 选 A：自动转换 =====
        Write-Host ""
        Write-Host "[1/7] 检测 Python"
        $py = Get-Command python -ErrorAction SilentlyContinue
        if (-not $py) {
            Write-Err "需要 Python 3.10+。下载：https://www.python.org/downloads/"
            Write-Host "  安装时务必勾选 'Add python.exe to PATH'" -ForegroundColor Yellow
            Write-Host "  装完重新运行 install.bat"
            exit 1
        }
        $pyVerRaw = (& python --version 2>&1).ToString()
        $m = [regex]::Match($pyVerRaw, 'Python\s+(\d+)\.(\d+)')
        if (-not ($m.Success -and ([int]$m.Groups[1].Value -gt 3 -or ([int]$m.Groups[1].Value -eq 3 -and [int]$m.Groups[2].Value -ge 10)))) {
            Write-Err "Python 版本过低（需 3.10+）。当前：$pyVerRaw"
            Write-Host "  升级：https://www.python.org/downloads/" -ForegroundColor Yellow
            exit 1
        }
        Write-Ok $pyVerRaw

        Write-Host ""
        Write-Host "[2/7] 创建临时虚拟环境"
        if (-not (Test-Path $venvDir)) {
            & python -m venv $venvDir
            if ($LASTEXITCODE -ne 0) {
                Write-Err "创建虚拟环境失败。请手动删除该目录后重试：$venvDir"
                exit 1
            }
        }
        Write-Ok "虚拟环境就绪：$venvDir"
        $activate = Join-Path $venvDir "Scripts\Activate.ps1"
        if (Test-Path $activate) { . $activate }

        Write-Host ""
        Write-Host "[3/7] 安装转换依赖（下载几百 MB）"
        & python -m pip install --disable-pip-version-check -i https://pypi.tuna.tsinghua.edu.cn/simple "optimum[onnxruntime]" onnx
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  清华镜像失败，改用官方源重试一次 ..." -ForegroundColor Yellow
            & python -m pip install --disable-pip-version-check "optimum[onnxruntime]" onnx
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Err "依赖安装失败。手动执行（保留 venv 以便排查）："
            Write-Host "  $venvDir\Scripts\python -m pip install optimum[onnxruntime] onnx" -ForegroundColor Yellow
            exit 1
        }
        Write-Ok "依赖安装完成"

        Write-Host ""
        Write-Host "[4/7] 准备转换脚本"
        $convertPy = @'
from optimum.onnxruntime import ORTModelForFeatureExtraction
from transformers import AutoTokenizer
import os, shutil

SOURCE = "intfloat/e5-base-v2"
DST = os.path.abspath("models/e5-base-v2-onnx")
HF_TMP = os.path.abspath(".hf-download-cache")

os.environ["HF_HOME"] = HF_TMP
os.environ.setdefault("HF_ENDPOINT", "https://hf-mirror.com")

print("[1/3] 下载 e5-base-v2 原始模型（约 400MB）...")
model = ORTModelForFeatureExtraction.from_pretrained(SOURCE, export=True)
tokenizer = AutoTokenizer.from_pretrained(SOURCE)

print("[2/3] 导出 ONNX...")
os.makedirs(DST, exist_ok=True)
model.save_pretrained(DST)
tokenizer.save_pretrained(DST)

print("[3/3] 清理临时缓存...")
if os.path.exists(HF_TMP):
    shutil.rmtree(HF_TMP, ignore_errors=True)

print(f"完成：{DST}")
'@
        Set-Content -Path $convertScript -Value $convertPy -Encoding UTF8
        Write-Ok "脚本已生成：$convertScript"

        Write-Host ""
        Write-Host "[5/7] 运行转换（实时显示进度）"
        Push-Location $ProjectRoot
        & python $convertScript
        $convOk = $LASTEXITCODE -eq 0
        Pop-Location
        if (-not $convOk) {
            Write-Err "模型转换失败。已保留中间产物便于手动排查："
            Write-Host "  venv:   $venvDir"
            Write-Host "  脚本:   $convertScript"
            Write-Host "  缓存:   $hfCache"
            exit 1
        }
        Write-Ok "转换完成"

        Write-Host ""
        Write-Host "[6/7] 校验模型"
        $sz = (Get-Item $modelFile -ErrorAction SilentlyContinue).Length
        if (-not (Test-Path $modelFile) -or -not $sz -or $sz -lt 300MB) {
            Write-Err "警告：模型校验失败（model.onnx 缺失或小于 300MB）"
            Write-Host ("  当前大小：{0:N0} MB" -f ((($sz) / 1MB))) -ForegroundColor Yellow
        } else {
            Write-Ok ("模型校验通过：{0:N0} MB" -f ($sz / 1MB))
        }

        Write-Host ""
        Write-Host "[7/7] 清理临时文件（仅本次完全成功后执行）"
        Clear-ConvertTemp
    }

    if (-not (Test-Path $modelFile)) {
        Write-Host "注意：模型尚未就绪。后续 '构建索引' 前请先补齐。" -ForegroundColor Yellow
    }
}

# ============ 第 3 步：准备 RimWorldData ============
Write-Step "第 3 步 / 6：准备 RimWorldData"

$rwRoot = Join-Path $ProjectRoot "RimWorldData"
$dataExist = Test-Path (Join-Path $rwRoot "Data")
$csExist   = Test-Path (Join-Path $rwRoot "RimWorld_Decompiled\Assembly-CSharp")

if ($dataExist -and $csExist) {
    Write-Ok "Data 与反编译源码都就绪"
} else {
    Write-Host "缺少 RimWorldData（游戏源码，版权原因不入库，需本地准备）。" -ForegroundColor Yellow
    if (-not $dataExist) {
        Write-Host "  1) 打开 Steam 游戏目录（默认）："
        Write-Host "       C:\Program Files (x86)\Steam\steamapps\common\RimWorld"
        Write-Host "  2) 把 Data 文件夹整个复制到："
        Write-Host "       $rwRoot\Data"
    }
    if (-not $csExist) {
        Write-Host "  3) 下载反编译工具 ILSpy："
        Write-Host "       https://github.com/icsharpcode/ILSpy/releases"
        Write-Host "  4) 用 ILSpy 打开游戏目录下："
        Write-Host "       RimWorldWin64_Data\Managed\Assembly-CSharp.dll"
        Write-Host "     在 Assembly-CSharp 上右键 → Save Code，输出到："
        Write-Host "       $rwRoot\RimWorld_Decompiled\Assembly-CSharp"
    }
    Write-Host "完成后按回车继续 ..." -ForegroundColor Yellow
    Read-Host "（回车）"
}

# ============ 第 4 步：编译 ============
Write-Step "第 4 步 / 6：编译项目"
Push-Location (Join-Path $ProjectRoot "src")
try {
    # 方案 B：src 下创建了解决方案（RiMCP.slnx 或 RiMCP.sln）。优先用解决方案一次编译全部 4 个项目。
    $slnCandidates = @()
    foreach ($n in @("RiMCP.slnx", "RiMCP.sln")) {
        if (Test-Path $n) { $slnCandidates += Get-Item $n }
    }
    $sln = $slnCandidates | Select-Object -First 1

    if ($sln) {
        Write-Ok "使用解决方案：$($sln.Name)（后端 $onnxBackend）"
        & dotnet build $sln.Name -p:OnnxBackend=$onnxBackend
    } else {
        Write-Host "未找到解决方案文件，改用逐个项目编译。" -ForegroundColor Yellow
        & dotnet build "RiMCP.Core\RiMCP.Core.csproj"
        & dotnet build "RiMCP.Indexer\RiMCP.Indexer.csproj" -p:OnnxBackend=$onnxBackend
        & dotnet build "RiMCP.McpServer\RiMCP.McpServer.csproj" -p:OnnxBackend=$onnxBackend
        & dotnet build "RiMCP.CoreTests\RiMCP.CoreTests.csproj"
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Err "编译失败，请查看上方错误信息后重试，或反馈给作者。"
        Pop-Location
        exit 1
    }
    Write-Ok "编译通过（RiMCP.Core / RiMCP.Indexer / RiMCP.McpServer / RiMCP.CoreTests）"
} finally { Pop-Location }

# ============ 第 5 步：构建索引 ============
Write-Step "第 5 步 / 6：构建索引（可选）"
if (-not (Test-Path $modelFile)) {
    Write-Host "模型未就绪，跳过索引构建。请先放入模型，再运行 scripts\build-and-index.ps1。" -ForegroundColor Yellow
} else {
    $ans = Read-Host "现在构建索引吗？期间可以正常用电脑(不建议玩游戏)  [Y/N]"
    if ($ans -match '^(Y|y|是|是|确实)$') {
        # ---- batch size 交互 ----
        $defaultBatch = if ($onnxBackend -eq "CPU") { "1" } else { "2" }
        Write-Host "请选择索引构建的 batch size（影响速度和显存占用）：" -ForegroundColor Cyan
        Write-Host "  [1] 保守  batch=16   （适合 4GB 显存，最安全）"
        Write-Host "  [2] 推荐  batch=32   （适合 6GB 显存）"
        Write-Host "  [3] 快速  batch=64   （适合 6-8GB 显存）"
        Write-Host "  [4] 极速  batch=128  （适合 12GB+ 显存）"
        Write-Host "  [5] 自定义（输入数字 1-256）"
        Write-Host "  [6] 默认  batch=32  "
        if ($onnxBackend -eq "DirectML") {
            Write-Host "DirectML 用户建议从 32 开始，如果报错就降到 16。" -ForegroundColor Yellow
        }
        if ($onnxBackend -eq "CPU") {
            Write-Host "CPU 模式建议 batch=16 或更小。" -ForegroundColor Yellow
        }
        $bsIn = Read-Host "输入选项 [$defaultBatch]"
        if ([string]::IsNullOrWhiteSpace($bsIn)) { $bsIn = $defaultBatch }
        $batch = 32
        switch ($bsIn) {
            "1" { $batch = 16 }
            "2" { $batch = 32 }
            "3" { $batch = 64 }
            "4" { $batch = 128 }
            "5" { $custom = [int](Read-Host "输入 batch 数字（1-256）"); $batch = [Math]::Max(1, [Math]::Min(256, $custom)) }
            "6" { $batch = 32 }
            default { $batch = 32 }
        }

        # ---- 预估时间 ----
        $est = switch ("$onnxBackend|$batch") {
            "CUDA|64"  { "约 30-50 分钟" }
            "CUDA|32"  { "约 40-60 分钟" }
            "CUDA|128" { "约 20-30 分钟" }
            "DirectML|*" { "约 1-2 小时" }
            "CPU|*"    { "约 2-5 小时" }
            default    { "不确定" }
        }
        Write-Host ""
        Write-Ok "后端=$onnxBackend  batch=$batch  预计构建 $est（"约"，不承诺精确）"

        & (Join-Path $PSScriptRoot "build-and-index.ps1") -BatchSize $batch -OnnxProvider $onnxProvider
    } else {
        Write-Host "跳过。之后可手动运行 scripts\build-and-index.ps1。" -ForegroundColor Yellow
    }
}

# ============ 第 6 步：配置 Trae ============
Write-Step "第 6 步 / 6：配置 Trae"
$match = ""; $mcp = $null
$rootFwd = $ProjectRoot -replace '\\','/'
$mcp = @{
    mcpServers = @{
        rimcp = @{
            command = "dotnet"
            args = @("run", "--project", "$rootFwd/src/RiMCP.McpServer")
            env = @{
                RIMCP_ROOT = "$rootFwd"
                RIMCP_MODEL_PATH = "$rootFwd/models/e5-base-v2-onnx"
            }
        }
    }
}
$json = $mcp | ConvertTo-Json -Depth 6
Write-Host "请把下面内容复制到 Trae 的 MCP 配置里：" -ForegroundColor Cyan
Write-Host "  设置路径：Settings → MCP → Edit mcp.json" -ForegroundColor Cyan
Write-Host ""
$json
Write-Host ""

Write-Host "============================================" -ForegroundColor Green
Write-Host "  安装结束。祝 Mod 开发愉快！" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green