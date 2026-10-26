#requires -Version 7.0
# Regenerate the embedded Windows icon from the same pixels used by the tray.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Add-Type -Path (Join-Path $root 'src/PowerTrove.Platform.Windows/Shell/AppIcon.cs')
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $pixels = [PowerTrove.Platform.Windows.Shell.AppIcon]::Pixels($size)
    $maskSize = [int][Math]::Ceiling($size / 32.0) * 4 * $size
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([int]0); $writer.Write([int]($pixels.Length + $maskSize))
    foreach ($unused in 1..4) { $writer.Write([int]0) }
    # DIB rows are bottom-up; AppIcon.Pixels returns top-down premultiplied BGRA.
    for ($row = $size - 1; $row -ge 0; $row--) {
        $writer.Write($pixels, $row * $size * 4, $size * 4)
    }
    $writer.Write([byte[]]::new($maskSize))
    ,$stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
}
$output = Join-Path $root 'src/PowerTrove.Desktop/Assets/PowerTrove.ico'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$stream = [IO.File]::Create($output)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = [byte]($sizes[$i] % 256)
        $writer.Write($dimension); $writer.Write($dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
}
finally { $writer.Dispose(); $stream.Dispose() }
