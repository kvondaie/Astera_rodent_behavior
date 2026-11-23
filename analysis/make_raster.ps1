# Builds a per-trial lick raster (HTML) from a trials CSV and the matching licks CSV.
# Usage: powershell -File make_raster.ps1 -Trials ..\trials_A4439_2026-11-21_15-35-49.csv [-Licks ..\licks....csv] [-Out raster.html]
# Event times are reconstructed from the trial log (harp clock):
#   responseStart = ChoiceHarpSeconds - ResponseTimeSec
#   delayStart    = responseStart - DelaySec
#   sampleStart   = delayStart - SampleActualSec
param([Parameter(Mandatory)][string]$Trials, [string]$Licks, [string]$Out)
$ErrorActionPreference = 'Stop'
function Read-Shared($p) { $fs = [IO.File]::Open((Resolve-Path $p), 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs); $t = $sr.ReadToEnd(); $sr.Close(); $t }
$ci = [Globalization.CultureInfo]::InvariantCulture
function Num($s) { if ($s -eq $null -or $s -eq '' -or $s -eq 'NaN') { return $null }; [double]::Parse($s, $ci) }

$trialFile = Get-Item $Trials
if (-not $Licks) {
    # the licks file is the one written by the same Bonsai run: latest one started before this session
    $Licks = Get-ChildItem $trialFile.DirectoryName -Filter 'licks*.csv' | Where-Object { $_.CreationTime -le $trialFile.CreationTime -and $_.LastWriteTime -ge $trialFile.LastWriteTime.AddMinutes(-1) } | Sort-Object CreationTime | Select-Object -Last 1 | ForEach-Object FullName
    if (-not $Licks) { throw "No licks file found covering $($trialFile.Name); pass -Licks" }
}
if (-not $Out) { $name = ($trialFile.BaseName -replace '^trials_', 'raster_') + '.html'; $Out = Join-Path $PSScriptRoot $name }

$rows = (Read-Shared $trialFile.FullName) -split "`r?`n" | Where-Object { $_ -ne '' }
$hdr = $rows[0] -split ','
$TList = New-Object System.Collections.Generic.List[object]
foreach ($r in $rows[1..($rows.Count - 1)]) { $f = $r -split ','; if ($f.Count -lt $hdr.Count) { continue }; $o = @{}; for ($i = 0; $i -lt $hdr.Count; $i++) { $o[$hdr[$i]] = $f[$i] }; $TList.Add($o) }

# lick onsets (0 -> 1 transitions) per side
$onL = New-Object System.Collections.Generic.List[double]; $onR = New-Object System.Collections.Generic.List[double]
$pl = 0; $pr = 0
foreach ($r in ((Read-Shared $Licks) -split "`r?`n")) {
    if ($r -notmatch '^\d') { continue }; $f = $r -split ','; if ($f.Count -lt 3) { continue }
    $t = [double]::Parse($f[0], $ci); $a = [int]$f[1]; $b = [int]$f[2]
    if ($a -eq 1 -and $pl -eq 0) { $onL.Add($t) }; if ($b -eq 1 -and $pr -eq 0) { $onR.Add($t) }
    $pl = $a; $pr = $b
}
function Window($list, [double]$t0, [double]$from, [double]$to) {
    $res = New-Object System.Collections.Generic.List[double]
    foreach ($v in $list) { $d = $v - $t0; if ($d -ge $from -and $d -le $to) { $res.Add([Math]::Round($d, 3)) } }
    , $res.ToArray()
}

$rowsOut = @()
foreach ($o in $TList) {
    $c = Num $o.ChoiceHarpSeconds; $rt = Num $o.ResponseTimeSec; $dl = Num $o.DelaySec; $sa = Num $o.SampleActualSec
    $tr = [ordered]@{ n = [int]$o.Trial; side = $o.SampleSide; choice = $o.Choice; outcome = $o.Outcome; start = $o.TrialStart
        delay = $dl; sampleSet = (Num $o.SampleSec); timeout = (Num $o.TimeoutSec); consume = (Num $o.ConsumeSec); preTrial = (Num $o.PreTrialSec)
        seq = $o.SideSequence; small = $o.SmallDropMs; big = $o.BigDropMs; ypos = $o.YPos; firstSampleLick = (Num $o.FirstSampleLickSec); preSample = (Num $o.PreSampleSec) }
    if ($c -ne $null -and $rt -ne $null -and $dl -ne $null -and $sa -ne $null) {
        $s0 = $c - $rt - $dl - $sa   # sample onset = t 0
        $tr.sample = [Math]::Round($sa, 3); $tr.resp = [Math]::Round($sa + $dl, 3); $tr.choiceT = [Math]::Round($sa + $dl + $rt, 3)
        $after = if ($o.Outcome -eq 'Correct') { $tr.consume } else { $tr.timeout }
        $from = - [Math]::Max(1.5, $tr.preTrial + 0.2); $to = $tr.choiceT + [Math]::Max(2.5, $after + 0.5)
        $tr.L = Window $onL $s0 $from $to; $tr.R = Window $onR $s0 $from $to
    } else { $tr.L = @(); $tr.R = @() }
    $rowsOut += $tr
}
$meta = [ordered]@{ mouse = $TList[0].MouseID; file = $trialFile.Name; licks = (Split-Path $Licks -Leaf); trials = $rowsOut }
$json = $meta | ConvertTo-Json -Depth 6 -Compress
$html = (Get-Content (Join-Path $PSScriptRoot 'raster_template.html') -Raw -Encoding utf8).Replace('/*DATA*/null', $json)
[IO.File]::WriteAllText($Out, $html, (New-Object Text.UTF8Encoding($false)))
"{0}: {1} trials, licks from {2} -> {3}" -f $meta.mouse, $rowsOut.Count, $meta.licks, $Out
