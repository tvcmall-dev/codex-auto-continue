param([string]$OutputPath = (Join-Path $PSScriptRoot '../assets/app.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(64, 64)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = [System.Drawing.ColorTranslator]::FromHtml('#142022')
$accent = [System.Drawing.ColorTranslator]::FromHtml('#B4D397')
$graphics.Clear($background)
$pen = New-Object System.Drawing.Pen($accent, 2.5)
$muted = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#415A45'), 1.2)
$brush = New-Object System.Drawing.SolidBrush($accent)
$graphics.DrawEllipse($muted, 5, 5, 54, 54)
$graphics.DrawEllipse($muted, 14, 14, 36, 36)
$graphics.DrawArc($pen, 10, 10, 44, 44, 210, 235)
$graphics.FillEllipse($brush, 25, 25, 14, 14)
$graphics.FillEllipse($brush, 47, 12, 6, 6)
$png = New-Object System.IO.MemoryStream
$bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$fullPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fullPath)) | Out-Null
$stream = [System.IO.File]::Create($fullPath)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$png.Length); $writer.Write([uint32]22); $writer.Write($png.ToArray())
}
finally { $writer.Dispose(); $stream.Dispose(); $png.Dispose(); $pen.Dispose(); $muted.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
