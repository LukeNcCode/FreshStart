Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-Button($root, [string]$name) {
    $n = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $t = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition($n, $t)))
}

function Invoke-Button($root, [string]$name) {
    $b = Find-Button $root $name
    if ($null -eq $b) { Write-Output "NOT FOUND: $name"; return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "invoked: $name"
    return $true
}

function Get-NewWindows($existing) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $result = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        $w = $all.Item($i)
        $handle = $w.Current.NativeWindowHandle
        if ($handle -ne 0 -and $existing -notcontains $handle) {
            $result += [pscustomobject]@{ Handle = $handle; Name = $w.Current.Name; Class = $w.Current.ClassName }
        }
    }
    return $result
}

function Get-WindowHandles {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $handles = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        $h = $all.Item($i).Current.NativeWindowHandle
        if ($h -ne 0) { $handles += $h }
    }
    return $handles
}

$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\FreshStart.App\bin\Debug\net8.0-windows\win-x64\FreshStart.Host.exe"
Get-Process FreshStart.Host -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$p = Start-Process -FilePath $exe -ArgumentList "--page=home" -PassThru
Start-Sleep -Milliseconds 2500

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidc = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidc)
if ($null -eq $win) { Write-Error "app window missing"; exit 1 }

$before = Get-WindowHandles

# 电源选项
$ok1 = Invoke-Button $win "电源选项"
Start-Sleep -Milliseconds 2000
$new1 = Get-NewWindows $before
$new1 | Format-Table -AutoSize
"control.exe: $((Get-Process control -ErrorAction SilentlyContinue | Measure-Object).Count)"
"powercfg window: $(( $new1 | Where-Object { $_.Name -match '电源|Power' } | Measure-Object).Count)"

# 环境变量
$before2 = Get-WindowHandles
$ok2 = Invoke-Button $win "环境变量"
Start-Sleep -Milliseconds 2000
$new2 = Get-NewWindows $before2
$new2 | Format-Table -AutoSize
"rundll32: $((Get-Process rundll32 -ErrorAction SilentlyContinue | Measure-Object).Count)"
"env window: $(( $new2 | Where-Object { $_.Name -match '环境变量|Environment' } | Measure-Object).Count)"

# 关闭新打开的窗口（按标题匹配）
foreach ($w in @($new1 + $new2)) {
    if ($w.Name -match '电源|Power|环境变量|Environment') {
        $el = [System.Windows.Automation.AutomationElement]::FromHandle($w.Handle)
        try {
            $pattern = $el.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $pattern.Close()
            Write-Output "closed: $($w.Name)"
        } catch { Write-Output "close failed: $($w.Name)" }
    }
}
Start-Sleep -Milliseconds 500
Stop-Process -Id $p.Id -Force
