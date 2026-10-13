[CmdletBinding()]
param(
    [string]$OutputRoot = 'D:\workdata\DiaryOfMine',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$PackageVersion = '1.1.1',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $workspaceRoot 'src\DiaryOfMine.Desktop\DiaryOfMine.Desktop.csproj'
$stageRoot = Join-Path $workspaceRoot "artifacts\demo-package-$PackageVersion"
$publishDirectory = Join-Path $stageRoot 'publish'
$payloadDirectory = Join-Path $stageRoot 'installer-payload'
$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
$installerDirectory = Join-Path $outputRootFull 'Installer'
$completeDirectory = Join-Path $outputRootFull "Complete-$PackageVersion"
$installerName = "DiaryOfMine-Setup-$PackageVersion-$RuntimeIdentifier.exe"
$installerPath = Join-Path $installerDirectory $installerName
$cscPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if ($stageRoot -notlike "$workspaceRoot\artifacts\*") { throw '暂存目录校验失败。' }
if ([System.IO.Path]::GetPathRoot($outputRootFull) -eq $outputRootFull) {
    throw '输出目录不能是磁盘根目录。'
}
if (!(Test-Path -LiteralPath $cscPath)) { throw "找不到安装器编译器：$cscPath" }

if (!$SkipPublish -and (Test-Path -LiteralPath $stageRoot)) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
foreach ($path in @($payloadDirectory, $completeDirectory)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if (Test-Path -LiteralPath $installerPath) { Remove-Item -LiteralPath $installerPath -Force }
New-Item -ItemType Directory -Path $publishDirectory, $payloadDirectory, $installerDirectory, $completeDirectory -Force | Out-Null

if (!$SkipPublish) {
    dotnet publish $projectPath -c Release -r $RuntimeIdentifier --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:GenerateDocumentationFile=false `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }
}

$publishedExecutable = Join-Path $publishDirectory 'DiaryOfMine.exe'
if (!(Test-Path -LiteralPath $publishedExecutable)) { throw '发布结果中没有 DiaryOfMine.exe。' }
$publishedVersion = (Get-Item -LiteralPath $publishedExecutable).VersionInfo.ProductVersion.Split('+')[0]
if ($publishedVersion -ne $PackageVersion) {
    throw "发布程序版本 $publishedVersion 与安装包版本 $PackageVersion 不一致。"
}
$installerSource = Get-Content -LiteralPath (Join-Path $workspaceRoot 'packaging\InstallerHost.cs') -Raw
if ($installerSource -notmatch ('private const string Version = "' + [regex]::Escape($PackageVersion) + '";')) {
    throw "安装引导程序版本与安装包版本 $PackageVersion 不一致。"
}

$installerHost = Join-Path $payloadDirectory 'DiaryOfMine-Installer.exe'
$uninstallerHost = Join-Path $payloadDirectory 'Uninstall-DiaryOfMine.exe'
$commonReferences = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
)
& $cscPath /nologo /target:winexe /optimize+ /platform:anycpu `
    "/win32icon:$workspaceRoot\src\DiaryOfMine.Desktop\Assets\DiaryOfMine.ico" `
    "/out:$installerHost" @commonReferences `
    (Join-Path $workspaceRoot 'packaging\InstallerHost.cs')
if ($LASTEXITCODE -ne 0) { throw "安装引导程序编译失败：$LASTEXITCODE" }
& $cscPath /nologo /target:winexe /optimize+ /platform:anycpu `
    "/win32icon:$workspaceRoot\src\DiaryOfMine.Desktop\Assets\DiaryOfMine.ico" `
    "/out:$uninstallerHost" @commonReferences `
    (Join-Path $workspaceRoot 'packaging\UninstallerHost.cs')
if ($LASTEXITCODE -ne 0) { throw "卸载程序编译失败：$LASTEXITCODE" }

Copy-Item -LiteralPath $publishedExecutable -Destination (Join-Path $payloadDirectory 'DiaryOfMine.exe')
Copy-Item -LiteralPath $publishedExecutable -Destination (Join-Path $completeDirectory 'DiaryOfMine.exe')
Copy-Item -LiteralPath $uninstallerHost -Destination (Join-Path $completeDirectory 'Uninstall-DiaryOfMine.exe')

$sedPath = Join-Path $stageRoot 'DiaryOfMine-demo.sed'
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$installerPath
FriendlyName=Diary of Mine v$PackageVersion 安装程序
AppLaunched=DiaryOfMine-Installer.exe
PostInstallCmd=<None>
AdminQuietInstCmd=DiaryOfMine-Installer.exe
UserQuietInstCmd=DiaryOfMine-Installer.exe
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$payloadDirectory\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
[Strings]
FILE0="DiaryOfMine.exe"
FILE1="DiaryOfMine-Installer.exe"
FILE2="Uninstall-DiaryOfMine.exe"
"@
[System.IO.File]::WriteAllText($sedPath, $sed, [System.Text.Encoding]::Unicode)

$iexpressProcess = Start-Process -FilePath "$env:SystemRoot\System32\iexpress.exe" `
    -ArgumentList @('/N', $sedPath) -PassThru -Wait -WindowStyle Hidden
if ($iexpressProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $installerPath)) {
    throw "IExpress 安装包生成失败：$($iexpressProcess.ExitCode)"
}

$privacyCheckRoots = @($publishDirectory, $payloadDirectory, $completeDirectory)
$forbiddenFiles = Get-ChildItem -LiteralPath $privacyCheckRoots -Recurse -File | Where-Object {
    $_.Name -match '(?i)(appsettings|calendar\.dat|diary\.json|work\.json|reminder-acknowledgements)' -or
    $_.Extension -match '(?i)^\.(db|log|bak|tmp|pdb)$'
}
if ($forbiddenFiles) {
    throw "发布目录发现不应包含的数据文件：$($forbiddenFiles.FullName -join ', ')"
}

$readme = @"
Diary of Mine v$PackageVersion ($RuntimeIdentifier)

Installer\$installerName
  图形化安装包。检测到旧版时自动定位原安装路径并原地更新；首次安装可自定义位置。
  安装时可选择创建桌面/开始菜单快捷方式及添加到任务栏。
  安装目录包含 DiaryOfMine.exe 与独立卸载程序。

Complete\DiaryOfMine.exe
  与安装后相同的完整自包含程序，可直接双击运行。

数据与隐私
  本发布包不包含开发或测试期间的日程、日记、工作记录、设置、日志及缓存。
  用户个人数据保存在 %LocalAppData%\DiaryOfMine\userdata 或其设置的缓存目录。
  升级和卸载默认保留用户数据与设置，避免误删个人内容。

系统要求
  Windows 10/11 x64。程序已包含所需 .NET 运行时。

注意
  当前 Demo 未进行代码签名，Windows 可能显示未知发布者提示。
  Windows 对自动固定任务栏有版本和策略限制；安装器会同时调用系统固定命令并写入当前用户任务栏快捷方式。
"@
[System.IO.File]::WriteAllText((Join-Path $outputRootFull "README-$PackageVersion.txt"), $readme,
    [System.Text.UTF8Encoding]::new($true))

$hashFiles = @(
    Get-Item -LiteralPath $installerPath
    Get-ChildItem -LiteralPath $completeDirectory -Recurse -File
    Get-Item -LiteralPath (Join-Path $outputRootFull "README-$PackageVersion.txt")
) | Sort-Object FullName
$relativePathOffset = $outputRootFull.TrimEnd('\').Length + 1
$hashLines = foreach ($file in $hashFiles) {
    $relative = $file.FullName.Substring($relativePathOffset)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[System.IO.File]::WriteAllLines((Join-Path $outputRootFull "SHA256SUMS-$PackageVersion.txt"), $hashLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Demo package created at: $outputRootFull"
