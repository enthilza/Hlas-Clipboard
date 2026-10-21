param([switch]$InitializeKey, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseDist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $releaseRoot 'dist' }
$keyDirectory = Join-Path $env:LOCALAPPDATA 'HlasClipboardPublisher'
$privatePath = Join-Path $keyDirectory 'signing-key.dpapi'
$publicPath = Join-Path $releaseRoot 'src\Hlas\Assets\update-public-key.xml'
Add-Type -AssemblyName System.Security
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
try {
    if (!(Test-Path -LiteralPath $privatePath)) {
        if (!$InitializeKey) { throw 'Publisher signing key is missing. Restore it; do not rotate the embedded public key for an existing release channel.' }
        if (Test-Path -LiteralPath $publicPath) { throw 'A public key already exists. Restore its matching private key instead of replacing it.' }
        New-Item -ItemType Directory -Force -Path $keyDirectory | Out-Null
        $secretBytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        try { [IO.File]::WriteAllBytes($privatePath, [Security.Cryptography.ProtectedData]::Protect($secretBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)) }
        finally { [Array]::Clear($secretBytes, 0, $secretBytes.Length) }
        [IO.File]::WriteAllText($publicPath, $rsa.ToXmlString($false), (New-Object Text.UTF8Encoding($false)))
    } else {
        $secretBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($privatePath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($secretBytes)) }
        finally { [Array]::Clear($secretBytes, 0, $secretBytes.Length) }
        if (!(Test-Path -LiteralPath $publicPath) -or [IO.File]::ReadAllText($publicPath) -ne $rsa.ToXmlString($false)) { throw 'The publisher key does not match the embedded public key.' }
    }
    if ($InitializeKey) { Write-Output 'Publisher key ready (DPAPI-protected outside the project); public key embedded in the application.'; return }
    $exe = Join-Path $releaseDist 'Hlas.exe'
    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0]
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    # Read PE machine type rather than assuming which runtime was built.
    $stream = [IO.File]::OpenRead($exe)
    try { $reader = New-Object IO.BinaryReader($stream); $stream.Position = 0x3c; $pe = $reader.ReadInt32(); $stream.Position = $pe + 4; $machine = $reader.ReadUInt16() } finally { $stream.Dispose() }
    $runtime = switch ($machine) { 0x8664 { 'win-x64' } 0xAA64 { 'win-arm64' } default { throw 'Unsupported EXE architecture.' } }
    $manifest = [ordered]@{ version=$version; tag="v$version"; runtime=$runtime; file='Hlas.exe'; size=(Get-Item -LiteralPath $exe).Length; sha256=$hash } | ConvertTo-Json
    $bytes = [Text.Encoding]::UTF8.GetBytes($manifest)
    [IO.File]::WriteAllBytes((Join-Path $releaseDist 'update.json'), $bytes)
    $signature = $rsa.SignData($bytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    [IO.File]::WriteAllText((Join-Path $releaseDist 'update.sig'), [Convert]::ToBase64String($signature))
    [IO.File]::WriteAllText((Join-Path $releaseDist 'SHA256SUMS.txt'), "$hash  Hlas.exe`n")
    Write-Output "Prepared signed release v$version ($runtime)."
} finally { $rsa.Dispose() }

