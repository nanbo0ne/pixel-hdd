#requires -Version 5
<#
    PIXEL HDD — build script
    Requires: Windows x64, .NET Framework 4.8 (csc.exe ships with the OS).
    Put the two openSeaChest executables in .\tools\ before building:
        openSeaChest_PowerControl.exe
        openSeaChest_SMART.exe
    They are embedded into the output EXE as resources at build time.
#>
$ErrorActionPreference = 'Stop'

$csc       = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root      = $PSScriptRoot
$tools     = Join-Path $root 'tools'
$outExe    = Join-Path $root 'PixelHdd.exe'
$outTest   = Join-Path $root 'TestRunner.exe'

if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 4.8 的 csc.exe：$csc" }

foreach ($name in 'openSeaChest_PowerControl.exe', 'openSeaChest_SMART.exe') {
    $p = Join-Path $tools $name
    if (-not (Test-Path -LiteralPath $p)) {
        throw "缺少 $p`n请从 Seagate 官方 openSeaChest 发行包取得该文件后放入 tools\\ 目录。"
    }
}

$refs = @(
    '-r:System.dll', '-r:System.Core.dll', '-r:System.Drawing.dll',
    '-r:System.Windows.Forms.dll', '-r:System.Management.dll', '-r:System.Web.Extensions.dll'
)
$res = @(
    "-resource:$(Join-Path $tools 'openSeaChest_PowerControl.exe'),PowerControl",
    "-resource:$(Join-Path $tools 'openSeaChest_SMART.exe'),Smart",
    "-resource:$(Join-Path $root 'Seagate-LICENSE.txt'),License"
)
$sources = @(
    (Join-Path $root 'src\Core.cs'),
    (Join-Path $root 'src\Ui.cs'),
    (Join-Path $root 'src\TestRunner.cs')
)

& $csc -nologo -target:winexe -main:PixelHdd.Program -platform:x64 -optimize+ -utf8output `
    "-out:$outExe" "-win32manifest:$(Join-Path $root 'src\app.manifest')" `
    @refs @res @sources
if ($LASTEXITCODE -ne 0) { throw '编译 PixelHdd.exe 失败' }

# console harness used by the self-test path
& $csc -nologo -target:exe -main:PixelHdd.TestRunner -platform:x64 -optimize+ -utf8output `
    "-out:$outTest" @refs @res @sources
if ($LASTEXITCODE -ne 0) { throw '编译 TestRunner.exe 失败' }

Get-Item -LiteralPath $outExe, $outTest | Select-Object Name, Length, LastWriteTime
