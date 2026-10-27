<#
.SYNOPSIS
    Scale-out demo: hard-kills one server instance, like a crash.
.DESCRIPTION
    Finds the process listening on the instance's port and kills it. server-a is port 5201 and server-b is
    port 5202 (the AppHost "scaleout" profile and the server's launch profiles both use these). Bring it back
    with Start on its row in the Aspire dashboard, or by re-running its launch profile.
.EXAMPLE
    ./scripts/kill-instance.ps1 server-a
.EXAMPLE
    ./scripts/kill-instance.ps1 -Port 5202
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('server-a', 'server-b')]
    [string] $Instance = 'server-a',

    [int] $Port = 0
)

if ($Port -eq 0) {
    $Port = @{ 'server-a' = 5201; 'server-b' = 5202 }[$Instance]
}

# A listening socket has no remote end. (Filter on that rather than -State Listen: some Windows builds report
# listeners with a wrong or empty State.)
$owners = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
    Where-Object { $_.RemotePort -eq 0 -and $_.OwningProcess -ne 0 } |
    Select-Object -ExpandProperty OwningProcess -Unique

if (-not $owners) {
    Write-Host "Nothing is listening on port $Port." -ForegroundColor Yellow
    exit 1
}

$killed = 0
foreach ($owner in $owners) {
    $process = Get-Process -Id $owner -ErrorAction SilentlyContinue
    # Only this sample's own processes: a wrong port must not take down something else (Docker, WSL, ...).
    if ($process.ProcessName -notmatch '^(Mcp2Dialog\.|dotnet$)') {
        Write-Host "Not killing $($process.ProcessName) (pid $owner) on port ${Port}: it isn't part of this sample." -ForegroundColor Yellow
        continue
    }

    Stop-Process -Id $owner -Force
    Write-Host "Killed $($process.ProcessName) (pid $owner), which was listening on port $Port." -ForegroundColor Red
    $killed++
}

if ($killed -eq 0) {
    exit 1
}
