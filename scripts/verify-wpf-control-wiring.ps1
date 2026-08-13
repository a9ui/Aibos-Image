param(
    [string]$OutputPath = (Join-Path $env:TEMP ("aibos-wpf-control-wiring-" + [guid]::NewGuid().ToString('N') + ".json"))
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf\MainWindow.xaml'
$sourceRoot = Join-Path $repoRoot 'local-native\PhotoViewer.Wpf'
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $outputFullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must stay under TEMP: $outputFullPath"
}

[xml]$xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $xamlPath
$xamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
$automationNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
$interactiveTypes = @(
    'Button',
    'MenuItem',
    'ToggleButton',
    'CheckBox',
    'RadioButton',
    'RepeatButton'
)
$eventNames = @(
    'Click',
    'Checked',
    'Unchecked',
    'Indeterminate',
    'PreviewMouseLeftButtonDown',
    'MouseLeftButtonDown',
    'PreviewKeyDown',
    'KeyDown'
)

$source = [Text.StringBuilder]::new()
Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object {
    [void]$source.AppendLine((Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName))
}
$sourceText = $source.ToString()

$nodes = @($xaml.SelectNodes('//*')) | Where-Object {
    $interactiveTypes -contains $_.LocalName
}
$rows = [Collections.Generic.List[object]]::new()
$handlerReferences = [Collections.Generic.List[object]]::new()
foreach ($node in $nodes) {
    $name = $node.GetAttribute('Name', $xamlNamespace)
    $content = $node.GetAttribute('Content')
    $automationName = $node.GetAttribute('AutomationProperties.Name')
    $toolTip = $node.GetAttribute('ToolTip')
    $events = [ordered]@{}
    foreach ($eventName in $eventNames) {
        $handler = $node.GetAttribute($eventName)
        if ([string]::IsNullOrWhiteSpace($handler)) { continue }
        $events[$eventName] = $handler
        $escaped = [regex]::Escape($handler)
        $defined = [regex]::IsMatch(
            $sourceText,
            "(?m)\b$escaped\s*\(")
        $handlerReferences.Add([pscustomobject]@{
            controlType = $node.LocalName
            controlName = $name
            eventName = $eventName
            handler = $handler
            defined = $defined
        })
    }
    $hasDirectAccessibleLabel =
        -not [string]::IsNullOrWhiteSpace($automationName) `
        -or -not [string]::IsNullOrWhiteSpace($content) `
        -or -not [string]::IsNullOrWhiteSpace($toolTip) `
        -or $node.InnerXml -match '(?:Text|Content)="[^\"]+"'
    $rows.Add([pscustomobject]@{
        type = $node.LocalName
        name = $name
        content = $content
        automationName = $automationName
        toolTip = $toolTip
        hasDirectAccessibleLabel = $hasDirectAccessibleLabel
        events = $events
        command = $node.GetAttribute('Command')
    })
}

$named = @($rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.name) })
$duplicateNames = @($named | Group-Object name | Where-Object Count -gt 1 | ForEach-Object Name)
$missingHandlers = @($handlerReferences | Where-Object { -not $_.defined })
$unlabeledNamedControls = @($named | Where-Object { -not $_.hasDirectAccessibleLabel } | ForEach-Object name)
$counts = [ordered]@{}
foreach ($group in @($rows | Group-Object type | Sort-Object Name)) {
    $counts[$group.Name] = $group.Count
}
$result = [ordered]@{
    ok = $duplicateNames.Count -eq 0 -and $missingHandlers.Count -eq 0
    xaml = $xamlPath
    totalInteractiveControls = $rows.Count
    namedInteractiveControls = $named.Count
    unnamedInteractiveControls = $rows.Count - $named.Count
    countsByType = $counts
    eventReferenceCount = $handlerReferences.Count
    uniqueEventHandlerCount = @($handlerReferences.handler | Sort-Object -Unique).Count
    duplicateNames = $duplicateNames
    missingHandlers = $missingHandlers
    unlabeledNamedControlsForRuntimeReview = $unlabeledNamedControls
    controls = $rows
}

$json = $result | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($outputFullPath, $json, [Text.UTF8Encoding]::new($false))
if (-not $result.ok) {
    throw "WPF control wiring gate failed. Evidence: $outputFullPath"
}
[pscustomobject]@{
    ok = $result.ok
    totalInteractiveControls = $result.totalInteractiveControls
    namedInteractiveControls = $result.namedInteractiveControls
    eventReferenceCount = $result.eventReferenceCount
    uniqueEventHandlerCount = $result.uniqueEventHandlerCount
    duplicateNameCount = $result.duplicateNames.Count
    missingHandlerCount = $result.missingHandlers.Count
    unlabeledNamedControlCount = $result.unlabeledNamedControlsForRuntimeReview.Count
    evidence = $outputFullPath
}
