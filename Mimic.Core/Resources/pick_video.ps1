$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes, WindowsBase
# Per monitor, or on mixed-scale screens the click reads back at the wrong place.
Add-Type -Namespace Win -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);'
[void][Win.Dpi]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)

# A nearly invisible window over every screen, so the cursor is a crosshair anywhere and the click goes nowhere.
$form = New-Object Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = [Windows.Forms.SystemInformation]::VirtualScreen
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.Opacity = 0.01
$form.BackColor = [Drawing.Color]::Black
$form.Cursor = [Windows.Forms.Cursors]::Cross
$form.KeyPreview = $true
$script:point = $null
$form.add_MouseDown({
    param($sender, $e)
    if ($e.Button -eq 'Left') { $script:point = [Windows.Forms.Cursor]::Position }
    $form.Close()
})
$form.add_KeyDown({ param($sender, $e) if ($e.KeyCode -eq 'Escape') { $form.Close() } })
$form.add_Shown({ $form.Activate() })
[void]$form.ShowDialog()
$form.Dispose()
if (-not $script:point) { '{}'; exit 0 }
Start-Sleep -Milliseconds 100

$A = [Windows.Automation.AutomationElement]
$walker = [Windows.Automation.TreeWalker]::RawViewWalker
$at = New-Object System.Windows.Point($script:point.X, $script:point.Y)
function Value-Of($element) {
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    return ''
}

# Browsers switch on their page tree when first asked, so the first answer can come back empty.
$frames = @()
$title = ''
for ($try = 0; $try -lt 4 -and $frames.Count -eq 0; $try++) {
    if ($try) { Start-Sleep -Milliseconds 400 }
    $element = $A::FromPoint($at)
    $node = $element
    $root = $element
    while ($node -ne $null -and $node -ne $A::RootElement) {
        if ($node.Current.ControlType -eq [Windows.Automation.ControlType]::Document) {
            $url = Value-Of $node
            if ($url -match '^https?://') {
                $frames += $url
                if (-not $title) { $title = $node.Current.Name }
            }
        }
        $root = $node
        $node = $walker.GetParent($node)
    }
}
if (-not $title) { $title = $root.Current.Name }

$address = ''
$edit = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
if ($edit) {
    $text = Value-Of $edit
    if ($text -match '^https?://\S+$') { $address = $text }
    elseif ($text -match '^[^\s/]+\.[^\s/]+(/\S*)?$') { $address = "https://$text" }
}

@{ frames = @($frames); address = $address; title = $title; process_id = $element.Current.ProcessId } | ConvertTo-Json -Compress
