[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$themePath = Join-Path $repoRoot 'src\TaskbarLyrics.App\Themes\ScrollBars.xaml'

[xml]$theme = Get-Content -LiteralPath $themePath -Raw
$namespaces = [System.Xml.XmlNamespaceManager]::new($theme.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')

function Require-Node {
    param([Parameter(Mandatory)] [string]$XPath)

    $node = $theme.SelectSingleNode($XPath, $namespaces)
    if ($null -eq $node) {
        throw "Required scrollbar theme node was not found: $XPath"
    }

    return $node
}

function Read-Number {
    param(
        [Parameter(Mandatory)] [System.Xml.XmlElement]$Node,
        [Parameter(Mandatory)] [string]$Attribute
    )

    $text = $Node.GetAttribute($Attribute)
    $value = 0.0
    if (-not [double]::TryParse(
        $text,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$value)) {
        throw "Attribute $Attribute must be an invariant number, got '$text'."
    }

    return $value
}

$thumbVisual = Require-Node "//p:Style[@x:Key='ScrollBarThumbVertical']//p:ControlTemplate//p:Border[@Width and @CornerRadius]"
$track = Require-Node "//p:ControlTemplate[@x:Key='VerticalScrollBarTemplate']//p:Track[@x:Name='PART_Track']"
$thumb = Require-Node "//p:ControlTemplate[@x:Key='VerticalScrollBarTemplate']//p:Track.Thumb/p:Thumb"
$viewerScrollBar = Require-Node "//p:Style[@x:Key='AppScrollViewer']//p:ScrollBar[@x:Name='PART_VerticalScrollBar']"

if ($track.GetAttribute('Orientation') -ne 'Vertical') {
    throw 'PART_Track must explicitly use vertical orientation.'
}

$diameter = Read-Number $thumbVisual 'Width'
$cornerRadius = Read-Number $thumbVisual 'CornerRadius'
$minimumLength = Read-Number $thumb 'MinHeight'

if ($diameter -le 0 -or $cornerRadius -lt ($diameter / 2)) {
    throw "Vertical thumb is not a full capsule: width=$diameter, radius=$cornerRadius."
}

# Track computes a proportional slot for virtualized content. A larger minimum height
# can overflow that slot at either end, preserving the leading radius while clipping
# the trailing radius into a flat edge.
if ($minimumLength -gt $diameter) {
    throw "Vertical thumb MinHeight ($minimumLength) exceeds its diameter ($diameter) and can clip the trailing cap."
}

$marginParts = $viewerScrollBar.GetAttribute('Margin').Split(',')
if ($marginParts.Count -ne 4 -or $marginParts[1] -ne $marginParts[3]) {
    throw "Vertical scrollbar must keep symmetric top/bottom insets, got '$($viewerScrollBar.GetAttribute('Margin'))'."
}

Write-Host "Scrollbar theme smoke passed (vertical capsule diameter=$diameter, MinHeight=$minimumLength)."
