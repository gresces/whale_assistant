# 发布正式启动器：requireAdministrator + 覆盖根目录 EXE。
#
# 为什么不直接 dotnet publish：
#   测试构建（-p:WhaleTestBuild=true）和正式构建共用 launcher/obj。
#   dotnet clean 不会删掉 obj 里的 apphost.exe，下次发布会直接复用它，
#   正式 EXE 就悄悄变成免提权版，桥接随后读不到管理员运行的微信。
#   所以这里直接删掉 obj/bin 再发布，并在发布后读 PE 清单自证提权。

param(
    [string]$Dotnet = 'dotnet',
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'launcher\WhaleLauncher.csproj'
$out = Join-Path $root 'launcher\publish'
$exe = Join-Path $out 'WhaleLauncher.exe'

Remove-Item -Recurse -Force (Join-Path $root 'launcher\obj'), (Join-Path $root 'launcher\bin') -ErrorAction SilentlyContinue

& $Dotnet publish $project -c Release -o $out -p:PublishSingleFile=true -p:SelfContained=false --nologo
if ($LASTEXITCODE -ne 0) { throw '正式发布失败' }
if (-not (Test-Path -LiteralPath $exe)) { throw "没有生成：$exe" }

$text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($exe))
if ($text -notmatch 'requestedExecutionLevel level="requireAdministrator"') {
    throw '正式 EXE 缺 requireAdministrator：obj 可能残留测试清单，先重新运行本脚本'
}
if ($text -match 'requestedExecutionLevel level="asInvoker"') {
    throw '正式 EXE 是 asInvoker，拒绝发布'
}

if (-not $NoCopy) {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $root 'WhaleLauncher.exe') -Force
}

$target = if ($NoCopy) { $exe } else { Join-Path $root 'WhaleLauncher.exe' }
$info = Get-Item -LiteralPath $target
Write-Host ("正式启动器：{0}（{1:N0} 字节，requireAdministrator，{2}）" -f $target, $info.Length, $info.VersionInfo.ProductVersion)
