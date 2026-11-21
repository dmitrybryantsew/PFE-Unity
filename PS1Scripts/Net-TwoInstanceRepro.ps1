# Net-TwoInstanceRepro.ps1 — headless two-instance net session against the dev player
#
# Drives both ends of a LAN session with the -pfe-autostart boot hook and the scenario harness,
# writing each instance's log where the flood traces and overlay readbacks can be read afterwards.
#
# Prerequisites: Builds/PFE_Dev rebuilt AFTER the current tree (Tools/PFE/Build Development Player
# in the Editor — the CLI build is broken on this machine, see HeadlessBuilder.cs).
#
# Usage:  powershell -File PS1Scripts/Net-TwoInstanceRepro.ps1
#         [-Port 7770] [-Frames 2400] [-Warmup 600]

param(
    [int]$Port = 7770,
    [int]$Frames = 2400,
    [int]$Warmup = 600
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'Builds/PFE_Dev/PFE_Unity.exe'
$logDir = Join-Path $root 'Logs/netrepro'
New-Item -ItemType Directory -Force $logDir | Out-Null

$common = @(
    '-batchmode', '-nographics',
    '-pfe-autostart',
    '-pfe-scenario', 'move+shots',
    "-pfe-scenario-frames", $Frames,
    "-pfe-scenario-warmup", $Warmup,
    '-pfe-scenario-move', 'right',
    '-pfe-scenario-shots', '8'
)

Write-Host "Starting HOST  on port $Port ..."
$hostLog = Join-Path $logDir 'host.log'
$hostProc = Start-Process -FilePath $exe -ArgumentList ($common + @(
    '-logFile', $hostLog,
    '-pfe-net-host', '-pfe-net-port', $Port,
    '-pfe-scenario-tag', 'netrepro-host'
)) -PassThru

Start-Sleep -Seconds 8

Write-Host "Starting CLIENT on 127.0.0.1:$Port ..."
$clientLog = Join-Path $logDir 'client.log'
$clientProc = Start-Process -FilePath $exe -ArgumentList ($common + @(
    '-logFile', $clientLog,
    '-pfe-net-client', '-pfe-net-address', '127.0.0.1', '-pfe-net-port', $Port,
    '-pfe-net-nonce', '4242',
    '-pfe-scenario-tag', 'netrepro-client'
)) -PassThru

Wait-Process -Id $hostProc.Id
Wait-Process -Id $clientProc.Id

Write-Host "Both instances exited."
Write-Host "Logs: $hostLog"
Write-Host "       $clientLog"
Write-Host "Read client.log first: HostWriteGuard stack traces name the flood site;"
Write-Host "the overlay rows (doors/doorStates/projView/roomTx) name the rest."
