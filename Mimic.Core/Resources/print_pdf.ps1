$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$job = [IO.File]::ReadAllText($env:MIMIC_PDF_JOB, [Text.Encoding]::UTF8) | ConvertFrom-Json
$output = [IO.Path]::GetFullPath([string]$job.output)
$outputFolder = [IO.Path]::GetDirectoryName($output)
if (-not (Test-Path -LiteralPath $outputFolder -PathType Container)) {
    [void][IO.Directory]::CreateDirectory($outputFolder)
}

$fontTitle = [Drawing.Font]::new('Segoe UI', 18, [Drawing.FontStyle]::Bold)
$fontMeta = [Drawing.Font]::new('Segoe UI', 9)
$fontHead = [Drawing.Font]::new('Segoe UI', 11, [Drawing.FontStyle]::Bold)
$fontBody = [Drawing.Font]::new('Segoe UI', 10.5)
$black = [Drawing.Brushes]::Black
$grey = [Drawing.Brushes]::DimGray

$script:blocks = [Collections.ArrayList]::new()
function Add-Block($text, $font, $brush, $gap) {
    if ($text) { [void]$script:blocks.Add(@{ text = [string]$text; font = $font; brush = $brush; gap = $gap }) }
}
Add-Block $job.title $fontTitle $black 4
Add-Block $job.meta $fontMeta $grey 14
$script:image = $null
if ($job.image) {
    if (-not (Test-Path -LiteralPath $job.image -PathType Leaf)) {
        throw "The image for this PDF is unavailable: $($job.image)"
    }
    $script:image = [Drawing.Image]::FromFile($job.image)
}
foreach ($section in $job.sections) {
    Add-Block $section.heading $fontHead $black 2
    Add-Block $section.text $fontBody $black 12
}

$format = [Drawing.StringFormat]::new()
$format.Trimming = [Drawing.StringTrimming]::Word
$format.FormatFlags = [Drawing.StringFormatFlags]::LineLimit
$script:index = 0
$script:imageDone = $false

$doc = [Drawing.Printing.PrintDocument]::new()
$doc.PrinterSettings.PrinterName = 'Microsoft Print to PDF'
if (-not $doc.PrinterSettings.IsValid) {
    throw "Microsoft Print to PDF is not available on this PC. In Windows Settings, enable the 'Microsoft Print to PDF' optional feature, then try again."
}
$doc.PrinterSettings.PrintToFile = $true
$doc.PrinterSettings.PrintFileName = $output
$doc.DocumentName = [string]$job.title
$doc.PrintController = [Drawing.Printing.StandardPrintController]::new()
$doc.add_PrintPage({
    param($sender, $e)
    $area = $e.MarginBounds
    $y = [single]$area.Top
    $bottom = [single]$area.Bottom
    $width = [single]$area.Width
    while ($script:index -lt $script:blocks.Count) {
        $block = $script:blocks[$script:index]
        $room = [Drawing.SizeF]::new($width, [single]($bottom - $y))
        [int]$chars = 0
        [int]$lines = 0
        $size = $e.Graphics.MeasureString($block.text, $block.font, $room, $format, [ref]$chars, [ref]$lines)
        if ($chars -eq 0) { break }
        $bounds = [Drawing.RectangleF]::new([single]$area.Left, $y, $width, [single]$size.Height)
        $e.Graphics.DrawString($block.text, $block.font, $block.brush, $bounds, $format)
        $y += $size.Height
        if ($chars -lt $block.text.Length) {
            $block.text = $block.text.Substring($chars).TrimStart()
            break
        }
        $y += $block.gap
        $script:index++
        if ($script:image -and -not $script:imageDone) {
            $scale = [Math]::Min($width / $script:image.Width, ($area.Height * 0.45) / $script:image.Height)
            $w = $script:image.Width * $scale
            $h = $script:image.Height * $scale
            $e.Graphics.DrawImage($script:image, [single]$area.Left, $y, [single]$w, [single]$h)
            $y += $h + 14
            $script:imageDone = $true
        }
    }
    $e.HasMorePages = $script:index -lt $script:blocks.Count
})

try {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    $doc.Print()
} finally {
    $doc.Dispose()
    $format.Dispose()
    $fontTitle.Dispose()
    $fontMeta.Dispose()
    $fontHead.Dispose()
    $fontBody.Dispose()
    if ($script:image) { $script:image.Dispose() }
}

for ($i = 0; $i -lt 240; $i++) {
    if ((Test-Path -LiteralPath $output) -and (Get-Item -LiteralPath $output).Length -gt 0) {
        try { [IO.File]::Open($output, 'Open', 'Read', 'None').Close(); exit 0 } catch { }
    }
    Start-Sleep -Milliseconds 250
}
throw "Microsoft Print to PDF did not write the selected output file: $output"
