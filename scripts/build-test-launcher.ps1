# 构建「测试用启动器」：asInvoker，不弹 UAC。
#
# 正式版 WhaleLauncher.exe 是 requireAdministrator（桥接要读管理员运行的微信），
# 每次启动都会弹 UAC。UI 自动化、截图、界面回归都改从这里出产物，不要再
# 用 asInvoker 覆盖根目录正式 EXE —— 那会让用户下次拿到一个连不上桥接的版本。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/build-test-launcher.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/build-test-launcher.ps1 -Launch
#
# 产物：artifacts/test-launcher/WhaleLauncher.exe（已被 .gitignore 忽略）
# 收尾：正式发布必须用 scripts/publish-launcher.ps1，它会先删 obj/bin。

param(
    [string]$Dotnet = 'dotnet',
    [string]$Out = '',
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'artifacts\test-launcher' }

$project = Join-Path $root 'launcher\WhaleLauncher.csproj'
$official = Join-Path $root 'WhaleLauncher.exe'

if (-not (Test-Path -LiteralPath $project)) { throw "找不到项目：$project" }

# dotnet clean 不会删 obj 里的 apphost.exe；不删干净就会沿用上一次的清单。
Remove-Item -Recurse -Force (Join-Path $root 'launcher\obj'), (Join-Path $root 'launcher\bin') -ErrorAction SilentlyContinue

& $Dotnet publish $project -c Release -o $Out `
    -p:PublishSingleFile=true -p:SelfContained=false -p:WhaleTestBuild=true --nologo
if ($LASTEXITCODE -ne 0) { throw '测试构建失败' }

$exe = Join-Path $Out 'WhaleLauncher.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "没有生成可执行文件：$exe" }

# 防止手滑把产物指到仓库根目录，覆盖正式版。
$resolvedExe = (Resolve-Path -LiteralPath $exe).Path
if ($resolvedExe -eq (Resolve-Path -LiteralPath $official -ErrorAction SilentlyContinue).Path) {
    throw '拒绝覆盖根目录正式 EXE：-Out 必须指向 artifacts 下的独立目录'
}
if ($resolvedExe -notlike (Join-Path $root 'artifacts\*')) {
    Write-Warning "产物不在 artifacts 下：$resolvedExe"
}

# 直接读 PE 里嵌入的清单文本：requireAdministrator 会让每次启动弹 UAC，
# 必须确认这次构建真的是 asInvoker，而不是缓存或参数没生效。
$bytes = [IO.File]::ReadAllBytes($resolvedExe)
$text = [Text.Encoding]::UTF8.GetString($bytes)
if ($text -notmatch 'requestedExecutionLevel level="asInvoker"') {
    throw '构建产物不是 asInvoker：检查 -p:WhaleTestBuild=true 是否生效'
}
if ($text -match 'requestedExecutionLevel level="requireAdministrator"') {
    throw '构建产物仍带 requireAdministrator：删掉 launcher/obj 再重试'
}

$info = (Get-Item -LiteralPath $resolvedExe)
Write-Host ("测试启动器：{0}（{1:N0} 字节，asInvoker）" -f $resolvedExe, $info.Length)
Write-Host '正式发布请用 scripts/publish-launcher.ps1（会先删 obj/bin）。'

if ($Launch) {
    Start-Process -FilePath $resolvedExe -WorkingDirectory $root | Out-Null
    Write-Host '已启动（无 UAC 提示）。'
}
