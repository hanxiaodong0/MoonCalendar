param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$facade = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard\v4.0_2.0.0.0__cc7b13ffcd2ddd51\netstandard.dll'
if (!(Test-Path -LiteralPath $compiler) -or !(Test-Path -LiteralPath $facade)) {
    throw '需要 Windows .NET Framework 4.8。Windows 10/11 通常已预装。'
}
$outputDirectory = if ([String]::IsNullOrEmpty($OutputDirectory)) { Join-Path $projectRoot 'dist' } else { [System.IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$binary = Join-Path $outputDirectory 'MoonCalendar.exe'
$compilerArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$binary", "/win32manifest:$(Join-Path $projectRoot 'app.manifest')", "/win32icon:$(Join-Path $projectRoot 'assets\app.ico')")
foreach ($library in @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Net.Http.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.ServiceProcess.dll', 'System.Xml.dll', 'Microsoft.CSharp.dll')) {
    $compilerArgs += "/reference:$(Join-Path $framework $library)"
}
foreach ($library in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll')) { $compilerArgs += "/reference:$(Join-Path $framework "WPF\$library")" }
$compilerArgs += "/reference:$facade"
$compilerArgs += "/reference:$(Join-Path $projectRoot 'lib\lunar.dll')"
$compilerArgs += "/resource:$(Join-Path $projectRoot 'lib\lunar.dll'),lunar.dll"
$compilerArgs += "/resource:$(Join-Path $projectRoot 'src\MainWindow.xaml'),MainWindow.xaml"
$compilerArgs += "/resource:$(Join-Path $projectRoot 'assets\app.ico'),app.ico"
foreach ($year in @(2025, 2026, 2027)) { $compilerArgs += "/resource:$(Join-Path $projectRoot "data\$year.json"),Holidays.$year.json" }
$compilerArgs += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败：$LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'App.config') -Destination "$binary.config" -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'lib\lunar-csharp.LICENSE') -Destination (Join-Path $outputDirectory 'lunar-csharp.LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'lib\holiday-cn.LICENSE') -Destination (Join-Path $outputDirectory 'holiday-cn.LICENSE') -Force
if (Test-Path -LiteralPath (Join-Path $projectRoot 'README.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $outputDirectory '使用说明.md') -Force }
Write-Output "构建完成：$binary"
