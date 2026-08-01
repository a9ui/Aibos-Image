[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$appXamlPath = Join-Path $RepoRoot 'local-native\PhotoViewer.Wpf\App.xaml'
$mainWindowXamlPath = Join-Path $RepoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$virtualPanelPath = Join-Path $RepoRoot 'local-native\PhotoViewer.Wpf\VirtualizingWrapPanel.cs'

$appXaml = Get-Content -LiteralPath $appXamlPath -Raw
$mainWindowXaml = Get-Content -LiteralPath $mainWindowXamlPath -Raw
$virtualPanel = Get-Content -LiteralPath $virtualPanelPath -Raw

function ConvertFrom-XamlColor {
    param([Parameter(Mandatory)][string]$Hex)

    $normalized = $Hex.TrimStart('#')
    if ($normalized.Length -eq 6) {
        $normalized = 'FF' + $normalized
    }
    if ($normalized.Length -ne 8) {
        throw "Unsupported XAML color: #$normalized"
    }

    [pscustomobject]@{
        A = [Convert]::ToInt32($normalized.Substring(0, 2), 16) / 255.0
        R = [double][Convert]::ToInt32($normalized.Substring(2, 2), 16)
        G = [double][Convert]::ToInt32($normalized.Substring(4, 2), 16)
        B = [double][Convert]::ToInt32($normalized.Substring(6, 2), 16)
    }
}

function Merge-Color {
    param(
        [Parameter(Mandatory)]$Foreground,
        [Parameter(Mandatory)]$Background
    )

    $alpha = $Foreground.A
    [pscustomobject]@{
        A = 1.0
        R = ($Foreground.R * $alpha) + ($Background.R * (1.0 - $alpha))
        G = ($Foreground.G * $alpha) + ($Background.G * (1.0 - $alpha))
        B = ($Foreground.B * $alpha) + ($Background.B * (1.0 - $alpha))
    }
}

function Get-LinearChannel {
    param([double]$Value)

    $scaled = $Value / 255.0
    if ($scaled -le 0.04045) {
        return $scaled / 12.92
    }
    return [Math]::Pow(($scaled + 0.055) / 1.055, 2.4)
}

function Get-RelativeLuminance {
    param([Parameter(Mandatory)]$Color)

    return (0.2126 * (Get-LinearChannel $Color.R)) +
        (0.7152 * (Get-LinearChannel $Color.G)) +
        (0.0722 * (Get-LinearChannel $Color.B))
}

function Get-ContrastRatio {
    param(
        [Parameter(Mandatory)]$Foreground,
        [Parameter(Mandatory)]$Background
    )

    $foregroundLuminance = Get-RelativeLuminance $Foreground
    $backgroundLuminance = Get-RelativeLuminance $Background
    $lighter = [Math]::Max($foregroundLuminance, $backgroundLuminance)
    $darker = [Math]::Min($foregroundLuminance, $backgroundLuminance)
    return ($lighter + 0.05) / ($darker + 0.05)
}

$colors = @{}
foreach ($match in [regex]::Matches(
    $appXaml,
    '<Color\s+x:Key="([^"]+)">#([0-9A-Fa-f]{6,8})</Color>')) {
    $colors[$match.Groups[1].Value] = ConvertFrom-XamlColor $match.Groups[2].Value
}

$requiredTokens = @(
    'BgPrimaryColor',
    'BgSecondaryColor',
    'BgTertiaryColor',
    'BgElevatedColor',
    'HeaderBgColor',
    'TextPrimaryColor',
    'TextSecondaryColor',
    'TextTertiaryColor',
    'TextDisabledColor',
    'SelectionTextColor',
    'AccentLightColor',
    'AccentFillColor',
    'AccentFillHoverColor',
    'FavoriteSoftColor',
    'SuccessColor',
    'SuccessSoftColor',
    'DangerTextColor',
    'DangerFillColor',
    'DangerFillHoverColor',
    'AiBadgeBackgroundColor',
    'CompactFavoriteBackgroundColor'
)
foreach ($token in $requiredTokens) {
    if (-not $colors.ContainsKey($token)) {
        throw "Missing required color token: $token"
    }
}

$colors['DirectOpenAlbumsColor'] = ConvertFrom-XamlColor 'FF6EE7B7'
$colors['DirectNegativeColor'] = ConvertFrom-XamlColor 'FFF5A5A5'
$colors['DatePickerSurfaceColor'] = ConvertFrom-XamlColor 'FF111521'
$colors['DatePickerGrayTextColor'] = ConvertFrom-XamlColor 'FF8F97AC'
$colors['DatePickerHighlightColor'] = ConvertFrom-XamlColor 'FF625BDA'
$colors['DirectAiBadgeTextColor'] = ConvertFrom-XamlColor 'FFBAE6FD'
$colors['PreviewInactiveTextColor'] = ConvertFrom-XamlColor 'FFD3D3D3'

$base = Merge-Color $colors.BgPrimaryColor (ConvertFrom-XamlColor 'FF000000')
$checks = @()
function Add-ContrastCheck {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ForegroundToken,
        [Parameter(Mandatory)][string]$BackgroundToken,
        [double]$Minimum = 4.5
    )

    $background = Merge-Color $colors[$BackgroundToken] $base
    $foreground = Merge-Color $colors[$ForegroundToken] $background
    $ratio = Get-ContrastRatio $foreground $background
    $script:checks += [pscustomobject]@{
        Name = $Name
        Foreground = $ForegroundToken
        Background = $BackgroundToken
        Ratio = [Math]::Round($ratio, 2)
        Minimum = $Minimum
        Ok = $ratio -ge $Minimum
    }
}

foreach ($backgroundToken in @(
    'BgPrimaryColor',
    'BgSecondaryColor',
    'BgTertiaryColor',
    'BgElevatedColor',
    'HeaderBgColor')) {
    Add-ContrastCheck "tertiary text on $backgroundToken" 'TextTertiaryColor' $backgroundToken
    Add-ContrastCheck "disabled text on $backgroundToken" 'TextDisabledColor' $backgroundToken
}

Add-ContrastCheck 'primary text on primary surface' 'TextPrimaryColor' 'BgPrimaryColor'
Add-ContrastCheck 'secondary text on elevated surface' 'TextSecondaryColor' 'BgElevatedColor'
Add-ContrastCheck 'selected text on accent fill' 'SelectionTextColor' 'AccentFillColor'
Add-ContrastCheck 'selected text on accent hover fill' 'SelectionTextColor' 'AccentFillHoverColor'
Add-ContrastCheck 'selected text on danger fill' 'SelectionTextColor' 'DangerFillColor'
Add-ContrastCheck 'selected text on danger hover fill' 'SelectionTextColor' 'DangerFillHoverColor'
Add-ContrastCheck 'compact favorite text on hover fill' 'SelectionTextColor' 'CompactFavoriteBackgroundColor'
Add-ContrastCheck 'accent text on accent-soft surface' 'AccentLightColor' 'AccentSoftColor'
Add-ContrastCheck 'danger text on favorite-soft surface' 'DangerTextColor' 'FavoriteSoftColor'
Add-ContrastCheck 'success text on success-soft surface' 'SuccessColor' 'SuccessSoftColor'
Add-ContrastCheck 'open Albums icon on header' 'DirectOpenAlbumsColor' 'HeaderBgColor'
Add-ContrastCheck 'negative prompt text on elevated surface' 'DirectNegativeColor' 'BgElevatedColor'
Add-ContrastCheck 'date picker gray text on date picker surface' 'DatePickerGrayTextColor' 'DatePickerSurfaceColor'
Add-ContrastCheck 'date picker selected text on highlight' 'SelectionTextColor' 'DatePickerHighlightColor'
Add-ContrastCheck 'AI badge text on badge surface' 'DirectAiBadgeTextColor' 'AiBadgeBackgroundColor'
Add-ContrastCheck 'inactive preview-tab text on primary surface' 'PreviewInactiveTextColor' 'BgPrimaryColor'

$failedChecks = @($checks | Where-Object { -not $_.Ok })
if ($failedChecks.Count -gt 0) {
    throw "Contrast checks failed: $($failedChecks | ConvertTo-Json -Compress)"
}

$disabledOpacityValues = @()
foreach ($match in [regex]::Matches(
    $appXaml,
    '(?s)<Trigger\s+Property="IsEnabled"\s+Value="False">.*?</Trigger>')) {
    foreach ($opacityMatch in [regex]::Matches(
        $match.Value,
        'Property="Opacity"\s+Value="([0-9.]+)"')) {
        $disabledOpacityValues += [double]$opacityMatch.Groups[1].Value
    }
}
if ($disabledOpacityValues.Count -eq 0 -or
    @($disabledOpacityValues | Where-Object { $_ -lt 0.65 }).Count -gt 0) {
    throw 'Disabled control opacity must remain at least 0.65.'
}

$photorealStart = $mainWindowXaml.IndexOf(
    '<Grid x:Name="ModalPhotorealSettingsPopup"',
    [StringComparison]::Ordinal)
$photorealEnd = $mainWindowXaml.IndexOf(
    '<!-- image area -->',
    [StringComparison]::Ordinal)
if ($photorealStart -lt 0 -or $photorealEnd -le $photorealStart) {
    throw 'Could not locate the photoreal settings overlay.'
}
$photorealXaml = $mainWindowXaml.Substring(
    $photorealStart,
    $photorealEnd - $photorealStart)
if ($photorealXaml -match '(Foreground|Background|TextElement\.Foreground)="#[0-9A-Fa-f]{6,8}"') {
    throw 'Photoreal settings contains a direct foreground/background color instead of shared theme resources.'
}
if ($photorealXaml.IndexOf(
    'Style="{StaticResource PhotorealSettingsComboBox}"',
    [StringComparison]::Ordinal) -lt 0) {
    throw 'Photoreal dropdowns must use the contrast-safe shared ComboBox style.'
}
foreach ($requiredFragment in @(
    'PhotorealSettingsComboBoxItem',
    'PhotorealSettingsComboBox',
    'TextElement.Foreground="{TemplateBinding Foreground}"',
    'Property="Background" Value="{StaticResource AccentFill}"',
    'Property="Background" Value="{StaticResource AccentFillHover}"',
    'Property="Foreground" Value="{StaticResource TextDisabled}"')) {
    if ($mainWindowXaml.IndexOf($requiredFragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Photoreal dropdown is missing contrast-safe template fragment: $requiredFragment"
    }
}

if ($virtualPanel.IndexOf(
    'Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)',
    [StringComparison]::Ordinal) -lt 0) {
    throw 'Virtualized date-group count text must use the readable 0xB0 alpha.'
}

[pscustomobject]@{
    Ok = $true
    ContrastChecks = $checks
    MinimumDisabledOpacity = ($disabledOpacityValues | Measure-Object -Minimum).Minimum
    PhotorealDropdownUsesThemeResources = $true
    VirtualizedGroupCountAlpha = '0xB0'
} | ConvertTo-Json -Depth 5
