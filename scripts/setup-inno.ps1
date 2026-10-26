#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ToolDirectory = (Join-Path $PSScriptRoot '../artifacts/toolchain/inno-6.7.3')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Inno Setup requires Windows.' }
$destination = [IO.Path]::GetFullPath($ToolDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Compiler directory already exists. Use a fresh directory.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$download = Join-Path $destination 'innosetup-6.7.3.exe'
# Pinned official release asset; verify both bytes and publisher before execution.
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -cne '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732') {
    throw 'Inno Setup download checksum mismatch.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Pyrsys B\.V\.(,|$)') {
    throw 'Inno Setup publisher signature verification failed.'
}
$compilerDirectory = Join-Path $destination 'compiler'
$arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /NOICONS /TASKS="" /DIR="' + $compilerDirectory + '"'
$process = Start-Process -FilePath $download -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Inno Setup toolchain installation failed.' }
$iscc = Join-Path $compilerDirectory 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'Compiler was not installed.' }
Write-Output $iscc
