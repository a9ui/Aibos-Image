[CmdletBinding()]
param(
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName PresentationFramework

    [xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="Aibos Image"
        Width="430"
        Height="230"
        ResizeMode="NoResize"
        ShowInTaskbar="True"
        Topmost="True"
        WindowStartupLocation="CenterScreen"
        Background="#15181D">
  <Grid Margin="24">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto" />
      <RowDefinition Height="Auto" />
      <RowDefinition Height="*" />
      <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
    <TextBlock Grid.Row="0"
               Text="Aibos Image"
               Foreground="#F4F7FA"
               FontSize="24"
               FontWeight="SemiBold" />
    <TextBlock Grid.Row="1"
               Margin="0,8,0,0"
        Text="起動する画面を選んでください。"
               Foreground="#BBC4CE"
               FontSize="14" />
    <StackPanel Grid.Row="3" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button Name="BrowserButton"
              Width="112"
              Height="38"
              Margin="0,0,10,0"
              Content="Browser"
              IsDefault="True" />
      <Button Name="WpfButton"
              Width="112"
              Height="38"
              Margin="0,0,10,0"
              Content="WPF" />
      <Button Name="CancelButton"
              Width="82"
              Height="38"
              Content="Cancel"
              IsCancel="True" />
    </StackPanel>
  </Grid>
</Window>
'@

    $reader = [System.Xml.XmlNodeReader]::new($xaml)
    try {
        $window = [Windows.Markup.XamlReader]::Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    $script:selection = 'cancel'
    $browserButton = $window.FindName('BrowserButton')
    $wpfButton = $window.FindName('WpfButton')
    $cancelButton = $window.FindName('CancelButton')

    if ($Verify) {
        if ($null -eq $browserButton -or $null -eq $wpfButton -or $null -eq $cancelButton) {
            throw 'The launch target dialog is missing one or more required buttons.'
        }
        [Console]::Out.WriteLine('verified')
        $window.Close()
        exit 0
    }

    $browserButton.Add_Click({
        $script:selection = 'browser'
        $window.Close()
    })
    $wpfButton.Add_Click({
        $script:selection = 'wpf'
        $window.Close()
    })
    $cancelButton.Add_Click({
        $script:selection = 'cancel'
        $window.Close()
    })
    $window.Add_Closed({
        if ([string]::IsNullOrWhiteSpace($script:selection)) {
            $script:selection = 'cancel'
        }
    })

    $window.ShowDialog() | Out-Null
    [Console]::Out.WriteLine($script:selection)
    exit 0
}
catch {
    [Console]::Error.WriteLine("[Aibos Image] Launch target dialog failed: $($_.Exception.Message)")
    exit 1
}
