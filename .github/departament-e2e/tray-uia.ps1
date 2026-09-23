# Значок departament в области уведомлений глазами UI Automation (Windows PowerShell 5.1).
# Смотрит панель задач (Shell_TrayWnd) и оба вида окна переполнения: классическое
# NotifyIconOverflowWindow (Windows 10 / Server 2019–2022) и XAML-остров Windows 11
# (TopLevelWindowForOverflowXamlIsland). Новые значки Windows по умолчанию прячет в переполнение,
# поэтому с -OpenOverflow скрипт открывает его кнопкой «Показать скрытые значки», снимает экран
# и закрывает. Итог — JSON для стенда (DpE2E): нашёлся ли значок и где, плюс перечень того, что
# UIA вообще видит в трее, — чтобы по первому прогону было понятно, как устроена оболочка раннера.
param(
    [string]$Name = 'departament',
    [string]$Out = 'tray-uia.json',
    [string]$Shot = '',
    [switch]$OpenOverflow
)

$ErrorActionPreference = 'Stop'
$result = [ordered]@{
    found    = $false
    where    = $null
    scanned  = 0
    matches  = @()
    roots    = @()
    chevron  = $null
    elements = @()
    error    = $null
}

function Save-Screen([string]$path) {
    if (-not $path) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } catch { }
}

try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $AE = [System.Windows.Automation.AutomationElement]
    $TS = [System.Windows.Automation.TreeScope]
    $All = [System.Windows.Automation.Condition]::TrueCondition
    $root = $AE::RootElement

    function Scan($element, [string]$label) {
        foreach ($e in $element.FindAll($TS::Descendants, $All)) {
            $script:result.scanned++
            try {
                $c = $e.Current
                $n = $c.Name
                if ($script:result.elements.Count -lt 300 -and ($n -or $c.AutomationId)) {
                    $script:result.elements += "$label | $($c.ClassName) | $($c.AutomationId) | $n"
                }
                if ($n -and $n -like "*$Name*") {
                    $r = $c.BoundingRectangle
                    $script:result.matches += [ordered]@{
                        where        = $label
                        name         = $n
                        class        = $c.ClassName
                        automationId = $c.AutomationId
                        rect         = "$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)"
                        offscreen    = $c.IsOffscreen
                    }
                }
            } catch { }
        }
    }

    function Scan-Class([string]$cls, [string]$label) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, $cls)
        foreach ($w in $root.FindAll($TS::Children, $cond)) {
            $script:result.roots += $label
            Scan $w $label
        }
    }

    foreach ($cls in 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'NotifyIconOverflowWindow', 'TopLevelWindowForOverflowXamlIsland') {
        Scan-Class $cls $cls
    }

    if ($OpenOverflow -and $result.matches.Count -eq 0) {
        $trayCond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Shell_TrayWnd')
        $tray = $root.FindFirst($TS::Children, $trayCond)
        $chevron = $null
        if ($tray) {
            foreach ($e in $tray.FindAll($TS::Descendants, $All)) {
                $c = $e.Current
                # Имя кнопки зависит от версии и языка оболочки; AutomationId у Windows 11 — SystemTrayIcon.
                if ($c.AutomationId -eq 'SystemTrayIcon' -or $c.Name -match 'hidden icons|Hidden Icons|скрытые значки|Notification Chevron') {
                    $chevron = $e
                    break
                }
            }
        }
        if ($chevron) {
            $result.chevron = "$($chevron.Current.Name) | $($chevron.Current.AutomationId) | $($chevron.Current.ClassName)"
            $opened = $false
            try {
                $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                $opened = $true
            } catch {
                try {
                    $chevron.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
                    $opened = $true
                } catch {
                    $result.chevron += " (не открылась: $($_.Exception.Message))"
                }
            }
            if ($opened) {
                Start-Sleep -Milliseconds 900
                foreach ($cls in 'NotifyIconOverflowWindow', 'TopLevelWindowForOverflowXamlIsland') {
                    Scan-Class $cls "$cls (открыто)"
                }
                Save-Screen $Shot
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            }
        } else {
            $result.chevron = 'кнопка «Показать скрытые значки» не найдена'
        }
    }

    if ($result.matches.Count -gt 0) {
        $m = $result.matches[0]
        $result.found = $true
        $result.where = "$($m.where): «$($m.name)» $($m.rect)$(if ($m.offscreen) { ' (вне экрана)' })"
    }
} catch {
    $result.error = $_.Exception.Message
}

$result | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding UTF8
