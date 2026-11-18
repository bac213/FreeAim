# Run after a Release build with PowerShell 7 (SPT managed assemblies).
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$managed = Join-Path (Split-Path $repo) 'EscapeFromTarkov_Data/Managed'
[void][Reflection.Assembly]::LoadFrom((Join-Path $managed 'UnityEngine.CoreModule.dll'))
$dll = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'FreeAim.Client/bin/Release/FreeAim.Client.dll'))
$type = $dll.GetType('FreeAim.Client.MotionLimits', $true)
$round = $type.GetMethod('RoundReach', [Reflection.BindingFlags]'NonPublic, Static')
$travel = $type.GetMethod('ReachTravel', [Reflection.BindingFlags]'NonPublic, Static')
function Check($ok, $label) { if (!$ok) { throw "Failed: $label" } }
$reach = [UnityEngine.Vector2]::new(30,15)
foreach ($x in -60,-30,-22,-10,0,10,22,30,60) {
    foreach ($y in -30,-15,-12,-5,0,5,12,15,30) {
        $a = @([UnityEngine.Vector2]::new($x,$y), $reach, [UnityEngine.Vector2]::zero)
        $v = $round.Invoke($null,$a)
        $u = [Math]::Max(0, [Math]::Abs($v.x/30)-0.65); $w = [Math]::Max(0, [Math]::Abs($v.y/15)-0.65)
        Check ($u*$u + $w*$w -le 0.122501) "outside rounded reach ($x,$y)"
        if ($y -eq 0) { Check ([Math]::Abs($v.x-[Math]::Max(-30,[Math]::Min(30,$x))) -lt 0.0001) 'yaw range' }
        if ($x -eq 0) { Check ([Math]::Abs($v.y-[Math]::Max(-15,[Math]::Min(15,$y))) -lt 0.0001) 'pitch range' }
        if ([Math]::Abs($x) -lt 19 -and [Math]::Abs($y) -lt 9) { Check ($v.x -eq $x -and $v.y -eq $y) 'neutral unchanged' }
        if ($a[2].sqrMagnitude -gt 0) { Check ([Math]::Abs($a[2].sqrMagnitude-1) -lt 0.00001) 'contact normal' }
    }
}
foreach ($x in 0,0.3,0.96,1.1) {
    foreach ($dx in -1,-0.5,0,0.5,1) {
        $p = [UnityEngine.Vector3]::new($x,0,0)
        $dir = [UnityEngine.Vector3]::new($dx,[Math]::Sqrt(1-$dx*$dx),0)
        $m = $travel.Invoke($null,@($p,$dir,[single]1))
        $end = $p + $dir * [single]$m
        Check ($m -ge 0 -and [Math]::Abs($end.magnitude - [Math]::Max($p.magnitude,0.96)) -lt 0.0001) 'reach sphere exit'
    }
}
$cut = $type.GetMethod('CameraCut', [Reflection.BindingFlags]'NonPublic, Static')
foreach ($frames in 1,2,4,8) {
    $accepted = [UnityEngine.Vector2]::new(56/$frames, 32/$frames)
    $a = @($accepted,$accepted)
    Check (!$cut.Invoke($null,$a)) "diagonal flick over $frames frames"
}
$a = @([UnityEngine.Vector2]::new(185,0),[UnityEngine.Vector2]::new(-175,0))
Check ((!$cut.Invoke($null,$a)) -and $a[1].x -eq 185) 'turn wraps across 180'
$a = @([UnityEngine.Vector2]::zero,[UnityEngine.Vector2]::new(60,0))
Check ($cut.Invoke($null,$a)) 'unexplained camera cut'
'Passed: 81 angular positions, 20 grip travel limits and 6 camera samples (compiled Release DLL).'
