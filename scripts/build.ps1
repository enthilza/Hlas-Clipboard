param([switch]$NoPause, [switch]$Verify, [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskBuild = [IO.Path]::GetFullPath((Join-Path $taskRoot '.build'))
$taskDist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'dist' }
$taskResult = 1
$taskOldCli = $env:DOTNET_CLI_HOME
$taskOldNuget = $env:NUGET_PACKAGES
try {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK to build. Target PCs do not need .NET.' }
    $taskSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or [int]($taskSdk.Split('.')[0]) -lt 10) { throw '.NET 10 SDK or newer is required.' }
    # Resolve the configured package cache before moving CLI scratch files into .build.
    # Otherwise DOTNET_CLI_HOME also puts NuGet packages in the synced staging tree.
    $taskPackageCache = & dotnet nuget locals global-packages --list --force-english-output
    if ($LASTEXITCODE -ne 0 -or $taskPackageCache -notmatch '^global-packages: ') { throw 'Unable to resolve the NuGet package cache.' }
    $env:NUGET_PACKAGES = ($taskPackageCache -replace '^global-packages:\s*', '').Trim()
    New-Item -ItemType Directory -Force -Path $taskBuild,$taskDist | Out-Null
    $env:DOTNET_CLI_HOME = Join-Path $taskBuild 'cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Write-Host 'Using application icon from src/Hlas/Assets.' -ForegroundColor Cyan
    $taskPublish = Join-Path $taskBuild 'publish'
    Write-Host "Building standalone Hlas.exe ($Runtime)..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $taskRoot 'src\Hlas\Hlas.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -p:NuGetAudit=false -o $taskPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
    $taskExe = Join-Path $taskPublish 'Hlas.exe'
    if (!(Test-Path -LiteralPath $taskExe)) { throw 'Published EXE is missing.' }
    Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskDist 'Hlas.exe') -Force
    # Portable user files are local-only; release publishing uses an explicit asset allowlist.
    $taskKey = Join-Path $taskRoot 'openrouter.api'
    $taskOutputKey = Join-Path $taskDist 'openrouter.api'
    if ((Test-Path -LiteralPath $taskKey) -and !(Test-Path -LiteralPath $taskOutputKey)) { Copy-Item -LiteralPath $taskKey -Destination $taskOutputKey }
    if ($Verify) {
        Write-Host 'Checking the bundled EXE...' -ForegroundColor Cyan
        $taskTestDir = Join-Path $taskRoot 'test-results'
        New-Item -ItemType Directory -Force -Path $taskTestDir | Out-Null
        $taskReport = Join-Path $taskTestDir 'self-test.json'
        $taskTest = Start-Process -FilePath (Join-Path $taskDist 'Hlas.exe') -ArgumentList @('--self-test', ('"' + $taskReport + '"')) -PassThru -Wait -WindowStyle Hidden
        if ($taskTest.ExitCode -ne 0) { throw "EXE verification failed. See $taskReport" }
        $taskChecks = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
        Write-Host ("Passed: " + $taskChecks.checks.Count + ' checks; bundled runtime: ' + $taskChecks.runtime) -ForegroundColor Green
        foreach($taskSkipped in $taskChecks.skipped) { Write-Host "Not verified in this environment: $taskSkipped" -ForegroundColor Yellow }
    }
    $taskSize = [Math]::Round((Get-Item -LiteralPath (Join-Path $taskDist 'Hlas.exe')).Length / 1MB,1)
    Write-Host "Ready: $taskDist\Hlas.exe ($taskSize MB)" -ForegroundColor Green
    $taskResult = 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
} finally {
    $env:DOTNET_CLI_HOME = $taskOldCli
    $env:NUGET_PACKAGES = $taskOldNuget
    # Only delete the exact project-owned staging directory. Never follow a junction.
    if ($taskBuild -eq ([IO.Path]::GetFullPath((Join-Path $taskRoot '.build'))) -and $taskBuild.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $taskBuild) {
            try {
                $taskJunctions = @(Get-Item -LiteralPath $taskBuild) + @(Get-ChildItem -LiteralPath $taskBuild -Recurse -Force -Attributes ReparsePoint)
                # Nextcloud marks regular files as cloud reparse points. Only name-surrogate
                # tags (junctions/symlinks) redirect a path and must block recursive cleanup.
                Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class HlasBuildPaths {
    [StructLayout(LayoutKind.Sequential)] struct TagInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out TagInfo info, uint size);
    public static bool Redirects(string path) {
        using (var file = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
            TagInfo info;
            if (file.IsInvalid || !GetFileInformationByHandleEx(file, 9, out info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (info.Tag & 0x20000000) != 0;
        }
    }
}
'@
                foreach($taskItem in $taskJunctions) {
                    if ((([int]$taskItem.Attributes) -band 1024) -ne 0 -and [HlasBuildPaths]::Redirects($taskItem.FullName)) { throw 'Build directory contains a path redirect; automatic cleanup stopped.' }
                }
                Remove-Item -LiteralPath $taskBuild -Recurse -Force
                Write-Host 'Temporary build files removed.'
            } catch { Write-Host "Cleanup: $($_.Exception.Message)" -ForegroundColor Yellow; $taskResult = 1 }
        }
    }
}
if (!$NoPause) { [void](Read-Host 'Press Enter to close') }
exit $taskResult


