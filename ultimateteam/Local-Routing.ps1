# Shared local-launch routing. Callers supply HostsPath, UserConfigRoot and ProjectRoot.
$RedirectHosts = @(
    'spring18.gosredirector.ea.com',
    'spring18.gosredirector.stest.ea.com',
    'spring18.gosredirector.scert.ea.com',
    'spring18.gosredirector.sdev.ea.com'
)
$RoutingStatePath = Join-Path $UserConfigRoot 'local-routing.json'

function Split-RedirectHostsLines([string[]]$Lines) {
    $keep = [Collections.Generic.List[string]]::new()
    $redirect = [Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        $parts = $line -split '#', 2
        $fields = @($parts[0].Trim() -split '\s+' | Where-Object { $_ })
        if ($fields.Count -lt 2) { $keep.Add($line); continue }
        $targets = @($fields[1..($fields.Count - 1)] | Where-Object { $RedirectHosts -contains $_ })
        if (-not $targets.Count) { $keep.Add($line); continue }
        $others = @($fields[1..($fields.Count - 1)] | Where-Object { $RedirectHosts -notcontains $_ })
        if (-not $others.Count) { $redirect.Add($line) }
        else {
            $redirect.Add($fields[0] + ' ' + ($targets -join ' '))
            $comment = if ($parts.Count -eq 2) { ' #' + $parts[1] } else { '' }
            $keep.Add($fields[0] + ' ' + ($others -join ' ') + $comment)
        }
    }
    return [pscustomobject]@{ Keep = $keep.ToArray(); Redirect = $redirect.ToArray() }
}

function Read-LocalRoutingState {
    $state = Get-Content -LiteralPath $RoutingStatePath -Raw -ErrorAction Stop | ConvertFrom-Json
    if ($state.version -ne 1 -or $null -eq $state.previous -or $null -eq $state.installed -or
        -not [string]::Equals([string]$state.owner, [IO.Path]::GetFullPath($ProjectRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Local routing backup belongs to another checkout or is invalid: $RoutingStatePath. Stop the original local launcher first."
    }
    return $state
}

function Resolve-LocalRedirectHost([string]$Hostname) {
    return @([Net.Dns]::GetHostAddresses($Hostname) | ForEach-Object { $_.IPAddressToString })
}

function Add-RedirectHostsEntry {
    $split = Split-RedirectHostsLines ([IO.File]::ReadAllLines($HostsPath))
    $installed = @($RedirectHosts | ForEach-Object { "127.0.0.1 $_" })
    if (Test-Path -LiteralPath $RoutingStatePath) {
        $state = Read-LocalRoutingState
        # Repeated starts must keep the original hosted entries, not back up loopback.
        $current = $split.Redirect -join "`n"
        if ($current -ne (@($state.installed) -join "`n") -and $current -ne (@($state.previous) -join "`n")) {
            throw 'FIFA routing changed since the local launch. Stop local testing before switching launchers; the saved routing was preserved.'
        }
    } else {
        New-Item -ItemType Directory -Force -Path $UserConfigRoot | Out-Null
        $state = [ordered]@{ version = 1; owner = [IO.Path]::GetFullPath($ProjectRoot); previous = @($split.Redirect); installed = $installed }
        # Persist recovery information before touching the real hosts file.
        $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RoutingStatePath -Encoding UTF8
    }
    [IO.File]::WriteAllLines($HostsPath, [string[]](@($split.Keep) + $installed), [Text.UTF8Encoding]::new($false))
    ipconfig /flushdns | Out-Null
    foreach ($hostname in $RedirectHosts) {
        $resolved = @(Resolve-LocalRedirectHost $hostname)
        if (-not $resolved.Count -or @($resolved | Where-Object { $_ -ne '127.0.0.1' }).Count) {
            throw "Local routing failed for ${hostname}: $($resolved -join ', '). Close FIFA and retry after stopping the local launcher."
        }
    }
}

function Remove-RedirectHostsEntry {
    if (-not (Test-Path -LiteralPath $RoutingStatePath)) {
        # Compatibility with launches before routing backups were introduced.
        $lines = [IO.File]::ReadAllLines($HostsPath)
        $pattern = '^\s*127\.0\.0\.1\s+spring18\.gosredirector\.(?:ea\.com|(?:stest|scert|sdev)\.ea\.com)\s*$'
        $keep = @($lines | Where-Object { $_ -notmatch $pattern })
        if ($keep.Count -ne $lines.Count) {
            [IO.File]::WriteAllLines($HostsPath, [string[]]$keep, [Text.UTF8Encoding]::new($false))
            ipconfig /flushdns | Out-Null
        }
        return
    }
    $state = Read-LocalRoutingState
    $split = Split-RedirectHostsLines ([IO.File]::ReadAllLines($HostsPath))
    $current = $split.Redirect -join "`n"
    if ($current -eq (@($state.installed) -join "`n")) {
        [IO.File]::WriteAllLines($HostsPath, [string[]](@($split.Keep) + @($state.previous)), [Text.UTF8Encoding]::new($false))
        ipconfig /flushdns | Out-Null
    } elseif ($current -ne (@($state.previous) -join "`n")) {
        throw "Another launcher changed FIFA routing; it was left unchanged. Recovery backup retained at $RoutingStatePath."
    }
    Remove-Item -LiteralPath $RoutingStatePath -Force
    Write-Host 'Restored the FIFA routing that preceded local testing.'
}
