param(
    [switch]$Quiet
)

# ============================================================
#  ALambda Midi - .NET 10 桌面运行时安装助手
#  用法：install-dotnet.cmd        双击/普通运行（结束后暂停）
#        install-dotnet.cmd /quiet 静默运行（不暂停，供安装程序调用）
# ============================================================

$ErrorActionPreference = 'Stop'

# 与控制台编码保持一致，避免中文乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$DownloadUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
$InstallerPath = Join-Path $env:TEMP 'windowsdesktop-runtime-10-win-x64.exe'
$ManualUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'

function Write-Title {
    Write-Host ''
    Write-Host '==========================================================' -ForegroundColor DarkCyan
    Write-Host '   ALambda Midi' -ForegroundColor Cyan
    Write-Host '   .NET 10 桌面运行时 安装助手' -ForegroundColor Cyan
    Write-Host '==========================================================' -ForegroundColor DarkCyan
    Write-Host ''
}

function Test-DotNet10Installed {
    # 安装程序可能以 32 位进程启动，$env:ProgramFiles 会指向 x86；
    # ProgramW6432 始终指向真正的 64 位 Program Files。
    $programFiles = $env:ProgramW6432
    if ([string]::IsNullOrWhiteSpace($programFiles)) { $programFiles = $env:ProgramFiles }

    $roots = @(
        (Join-Path $programFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'),
        (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App')
    )

    foreach ($root in $roots) {
        if (Test-Path $root) {
            $found = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '10.*' }
            if ($found) { return $true }
        }
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($runtimes -match 'Microsoft\.WindowsDesktop\.App 10\.') { return $true }
    }

    return $false
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-BeforeExit {
    if ($Quiet -or -not [Environment]::UserInteractive) {
        return
    }

    try {
        Write-Host ''
        Write-Host '按任意键关闭窗口...' -ForegroundColor DarkGray
        [void][Console]::ReadKey($true)
    }
    catch {
        # 无控制台（例如被其他程序调用）时直接返回
    }
}

try {
    Write-Title

    if (Test-DotNet10Installed) {
        Write-Host '[√] 已经安装 .NET 10 桌面运行时，无需重复安装。' -ForegroundColor Green
        Write-Host '    现在可以直接运行 ALambdaMidi.exe 了。'
        Wait-BeforeExit
        exit 0
    }

    Write-Host '未检测到 .NET 10 桌面运行时：ALambda Midi 需要它才能启动。' -ForegroundColor Yellow
    Write-Host '接下来会自动下载并安装，全程无需操作（需要联网和管理员权限）。'
    Write-Host ''

    # ---- 管理员权限 ----
    if (-not (Test-IsAdmin)) {
        Write-Host '正在请求管理员权限，请在弹出窗口中点击「是」...' -ForegroundColor Yellow
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
        if ($Quiet) { $arguments += '-Quiet' }

        try {
            # 等待提升权限后的子进程结束，避免安装程序提前继续
            $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -PassThru
            $proc.WaitForExit()
            exit $proc.ExitCode
        }
        catch {
            Write-Host '[!] 未能获取管理员权限。' -ForegroundColor Red
            Write-Host '    请右键点击 install-dotnet.cmd，选择「以管理员身份运行」。'
            Start-Process $ManualUrl
            Wait-BeforeExit
            exit 1
        }
    }

    # ---- 下载 ----
    Write-Host '正在从微软官方下载安装包（约 60 MB）...' -ForegroundColor Cyan
    Write-Host "  $DownloadUrl" -ForegroundColor DarkGray
    Write-Host ''

    if (Test-Path $InstallerPath) { Remove-Item $InstallerPath -Force -ErrorAction SilentlyContinue }

    $downloaded = $false
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (Test-Path $curl) {
        & $curl -L --fail --progress-bar -o $InstallerPath $DownloadUrl
        if ($LASTEXITCODE -eq 0 -and (Test-Path $InstallerPath)) { $downloaded = $true }
    }

    if (-not $downloaded) {
        try {
            $oldProgress = $ProgressPreference
            $ProgressPreference = 'Continue'
            Invoke-WebRequest -Uri $DownloadUrl -OutFile $InstallerPath -UseBasicParsing
            $ProgressPreference = $oldProgress
            $downloaded = (Test-Path $InstallerPath)
        }
        catch {
            $downloaded = $false
        }
    }

    if (-not $downloaded) {
        Write-Host ''
        Write-Host '[!] 下载失败，可能是网络问题。' -ForegroundColor Red
        Write-Host '    即将打开微软官方下载页面，请手动下载后双击安装。'
        Start-Process $ManualUrl
        Wait-BeforeExit
        exit 1
    }

    # ---- 安装 ----
    Write-Host ''
    Write-Host '正在安装（大约 1-3 分钟，请勿关闭窗口）...' -ForegroundColor Cyan
    Write-Host ''

    $process = Start-Process -FilePath $InstallerPath `
        -ArgumentList '/install', '/quiet', '/norestart' `
        -Wait -PassThru
    $code = $process.ExitCode

    Write-Host ''
    if (Test-DotNet10Installed) {
        Write-Host '[√] .NET 10 桌面运行时安装成功！' -ForegroundColor Green
        Write-Host '    现在可以直接运行 ALambdaMidi.exe 了。'
        Remove-Item $InstallerPath -Force -ErrorAction SilentlyContinue
        Wait-BeforeExit
        exit 0
    }

    Write-Host "[!] 安装未能完成（安装程序返回码 $code）。" -ForegroundColor Red
    Write-Host "    请尝试手动安装：$ManualUrl"
    Start-Process $ManualUrl
    Wait-BeforeExit
    exit 1
}
catch {
    Write-Host ''
    Write-Host "[!] 出现异常：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    请手动安装：$ManualUrl"
    Start-Process $ManualUrl
    Wait-BeforeExit
    exit 1
}
