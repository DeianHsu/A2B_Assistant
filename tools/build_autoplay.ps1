param(
    [string]$GameDirectory = 'D:\Game\Steam\steamapps\common\A2B',
    [string]$Python = 'python',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $projectRoot 'build\autoplay'
$cache = Join-Path $buildDirectory 'vendor'
$package = Join-Path $buildDirectory 'package\A2B-Autoplay'
$distributionName = "A2B_Assistant_v$Version.exe"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$gameLibraries = Join-Path $GameDirectory 'A2B_Data\Managed'
if (!(Test-Path -LiteralPath $compiler)) { throw '未找到 .NET Framework C# 编译器。' }
if (!(Test-Path -LiteralPath (Join-Path $gameLibraries 'Assembly-CSharp.dll'))) { throw '游戏目录无效。' }
New-Item -ItemType Directory -Path $cache, $package, (Join-Path $projectRoot 'dist') -Force | Out-Null
& $Python -B (Join-Path $PSScriptRoot 'fetch_autoplay_dependencies.py') --cache-directory $cache
if ($LASTEXITCODE -ne 0) { throw '下载运行时依赖失败。' }
Expand-Archive -LiteralPath (Join-Path $cache 'BepInEx_win_x64_5.4.23.5.zip') -DestinationPath (Join-Path $cache 'bepinex') -Force
$loader = Join-Path $cache 'bepinex'
$payload = Join-Path $package 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$loaderTarget = Join-Path $payload 'loader'
New-Item -ItemType Directory -Path $loaderTarget -Force | Out-Null
Get-ChildItem -LiteralPath $loader -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($loader.Length + 1)
    $destination = Join-Path $loaderTarget $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}
$pluginReferences = @(
    (Join-Path $loader 'BepInEx\core\BepInEx.dll'),
    (Join-Path $gameLibraries 'Assembly-CSharp.dll'),
    (Join-Path $gameLibraries 'UnityEngine.dll'),
    (Join-Path $gameLibraries 'UnityEngine.CoreModule.dll'),
    (Join-Path $gameLibraries 'UnityEngine.UI.dll'),
    (Join-Path $gameLibraries 'Unity.TextMeshPro.dll'),
    (Join-Path $gameLibraries 'Newtonsoft.Json.dll'),
    (Join-Path $gameLibraries 'netstandard.dll')
) | ForEach-Object { '/reference:' + $_ }
$bridgeOutput = Join-Path $payload 'A2B.Autoplay.Bridge.dll'
& $compiler /nologo /target:library /optimize+ "/out:$bridgeOutput" @pluginReferences (Join-Path $projectRoot 'windows_autoplay\bridge\AutoplayBridge.cs')
if ($LASTEXITCODE -ne 0) { throw '桥接插件编译失败。' }
$core = Join-Path $projectRoot 'windows_autoplay\launcher\AutoplayCore.cs'
$launcher = Join-Path $projectRoot 'windows_autoplay\launcher\Launcher.cs'
$launcherOutput = Join-Path $package 'A2B-Autoplay.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$launcherOutput" $core $launcher
if ($LASTEXITCODE -ne 0) { throw '启动器编译失败。' }
$runtime = Join-Path $package 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Expand-Archive -LiteralPath (Join-Path $cache 'python-3.14.0-embed-amd64.zip') -DestinationPath $runtime -Force
$sitePackages = Join-Path $runtime 'Lib\site-packages'
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$wheel = Join-Path $cache 'PyYAML-6.0.3-win64.whl'
$wheelZip = [System.IO.Compression.ZipFile]::OpenRead($wheel)
try {
    foreach ($entry in $wheelZip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $destination = Join-Path $sitePackages $entry.FullName
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
    }
} finally { $wheelZip.Dispose() }
@('python314.zip', '.', 'Lib\site-packages', '..\solver', 'import site') | Set-Content -LiteralPath (Join-Path $runtime 'python314._pth') -Encoding ascii
$solver = Join-Path $package 'solver'
New-Item -ItemType Directory -Path (Join-Path $solver 'tools') -Force | Out-Null
foreach ($directory in @('src', 'test_data', 'test_output')) {
    $target = Join-Path $solver $directory
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' } | ForEach-Object {
        $relative = $_.FullName.Substring((Join-Path $projectRoot $directory).Length + 1)
        $destination = Join-Path $target $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'autoplay_candidate.py') -Destination (Join-Path $solver 'tools\autoplay_candidate.py') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $package 'PROJECT-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $cache 'BepInEx-LICENSE.txt') -Destination $package -Force
@"
A=B 自动验证程序

双击 $distributionName，程序自动准备内置资源，选择起点后点击“开始自动验证”。
从第一关、从当前关开始会重新验证已通过关卡；从首个未通过关卡开始只处理未通过关卡。
从当前关开始时，请先在游戏里打开该关卡。

程序自动查找游戏、部署桥接插件，优先使用已验证答案，缺少时调用随包提供的求解器。
支持暂停、恢复和停止。失败会记录并继续；游戏退出或插件断开时停止调度。
资源缓存位于 %LOCALAPPDATA%\A2B-Autoplay\runtime，设置、日志和存档备份位于其 data 目录。
可在界面点击“打开备份和日志”查看用户数据。

5-5 的现有乘法答案运行较慢，本机一次实测约 53 分钟。程序显示用例进度及已用时间。
游戏需通过 Steam 安装；首次部署时如游戏已打开，请先退出游戏。

本程序使用 BepInEx、CPython、PyYAML，许可证随包提供。游戏本体不包含在发布包中。
"@ | Set-Content -LiteralPath (Join-Path $package '使用说明.txt') -Encoding utf8
$archivePath = Join-Path $buildDirectory 'A2B-Autoplay.payload.zip'
$archiveStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::Create)
$archive = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('A2B-Autoplay.exe', 'payload', 'runtime', 'solver', 'PROJECT-LICENSE.txt', 'BepInEx-LICENSE.txt', '使用说明.txt')) {
        $source = Join-Path $package $name
        $files = if (Test-Path -LiteralPath $source -PathType Leaf) { @(Get-Item -LiteralPath $source) } else { @(Get-ChildItem -LiteralPath $source -File -Recurse) }
        foreach ($file in $files) {
            if ($file.FullName -match '[\\/]__pycache__[\\/]') { continue }
            $relative = $file.FullName.Substring($package.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, ('A2B-Autoplay/' + $relative), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $archive.Dispose(); $archiveStream.Dispose() }
$bundleIdentity = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant()
$bundleInfo = Join-Path $buildDirectory 'BundleInfo.cs'
@"
[assembly: System.Reflection.AssemblyTitle("A=B 自动验证")]
[assembly: System.Reflection.AssemblyVersion("$Version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$Version.0")]
namespace A2BAutoplay.Bootstrap {
    internal static class BundleInfo {
        public const string Identity = "$bundleIdentity";
        public const string Version = "$Version";
    }
}
"@ | Set-Content -LiteralPath $bundleInfo -Encoding utf8
$singleFileOutput = Join-Path (Join-Path $projectRoot 'dist') $distributionName
$bootstrap = Join-Path $projectRoot 'windows_autoplay\bootstrap'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll "/resource:$archivePath,A2BAutoplay.Bundle" "/out:$singleFileOutput" (Join-Path $bootstrap 'SingleFile.cs') (Join-Path $bootstrap 'BundleCache.cs') $bundleInfo
if ($LASTEXITCODE -ne 0) { throw '单文件程序编译失败。' }
Write-Output ('构建完成：' + $singleFileOutput)
