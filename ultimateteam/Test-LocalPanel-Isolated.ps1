[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$server = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Aurora21-UltimateTeam-Server.exe')).Path
$dataRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'runtime-source\ultimateteam21')).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('Aurora21-panel-test-' + [guid]::NewGuid().ToString('N'))
$configRoot = Join-Path $root 'config'
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
$stderrPath = Join-Path $root 'server.stderr.log'
$priorRuntime = $env:ULTIMATETEAM_RUNTIME_ROOT
$priorConfig = $env:ULTIMATETEAM21_USER_CONFIG_ROOT
$priorConnection = $env:ULTIMATETEAM_POSTGRES_CONNECTION
$child = $null

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Request-Json([string]$Url, [string]$Method = 'GET', [object]$Body = $null) {
    $request = @{ Uri = $Url; Method = $Method; TimeoutSec = 3 }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
    }
    return Invoke-RestMethod @request
}

function Send-DevTools([Net.WebSockets.ClientWebSocket]$Socket, [int]$Id, [string]$Method, [object]$Parameters) {
    $json = ConvertTo-Json -InputObject @{ id = $Id; method = $Method; params = $Parameters } -Depth 20 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $segment = [ArraySegment[byte]]::new($bytes)
    $null = $Socket.SendAsync($segment, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    while ($true) {
        $buffer = New-Object byte[] 65536
        $stream = New-Object IO.MemoryStream
        do {
            $part = $Socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            if ($part.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'Chrome closed DevTools during the panel flow.' }
            $stream.Write($buffer, 0, $part.Count)
        } while (-not $part.EndOfMessage)
        $reply = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($stream.ToArray()))
        if ($reply.id -eq $Id) {
            if ($reply.error) { throw "Chrome DevTools $Method failed: $($reply.error.message)" }
            return $reply.result
        }
    }
}

function Test-BrowserControls([string]$Chrome, [string]$Root, [int]$PanelPort, [int]$CurrentValue) {
    $debugPort = Get-FreePort
    $browserRoot = Join-Path $Root 'chrome-controls'
    $chromeErrors = Join-Path $Root 'chrome-controls.stderr.log'
    $arguments = '--headless=new --disable-gpu --no-first-run --disable-background-networking --remote-debugging-port={0} --user-data-dir="{1}" "http://127.0.0.1:{2}/panel/#/rules/market"' -f $debugPort, $browserRoot, $PanelPort
    $browser = Start-Process -FilePath $Chrome -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardError $chromeErrors
    $socket = $null
    try {
        $target = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($browser.HasExited) { throw "Chrome exited before DevTools was ready; see $chromeErrors" }
            try {
                $targets = Request-Json "http://127.0.0.1:$debugPort/json"
                $target = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
                if ($target) { break }
            }
            catch { Start-Sleep -Milliseconds 100 }
        }
        if (-not $target) { throw "Chrome did not expose its page target; see $chromeErrors" }
        $socket = [Net.WebSockets.ClientWebSocket]::new()
        $webSocketUrl = [string](@($target.webSocketDebuggerUrl)[0])
        $null = $socket.ConnectAsync([uri]$webSocketUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $script = @'
(async () => {
  const wait = async (find, label) => {
    for (let i = 0; i < 300; i++) {
      const value = find();
      if (value) return value;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error(`Timed out waiting for ${label}: ${document.querySelector('#main')?.textContent?.slice(0, 700)}`);
  };
  const button = (root, label) => [...root.querySelectorAll('button')].find(b => b.textContent.trim() === label);
  const current = __CURRENT__;
  const row = await wait(() => [...document.querySelectorAll('#main tr')].find(tr => tr.textContent.includes('maintenanceIntervalSeconds')), 'market rule row');
  button(row, 'Edit').click();
  const editor = await wait(() => document.querySelector('#modal-root [role="dialog"]'), 'rule editor');
  editor.querySelector('textarea').value = JSON.stringify(current + 1);
  button(editor, 'Stage change').click();
  await wait(() => document.querySelector('#toasts')?.textContent.includes('Staged: publish'), 'staged change');
  location.hash = '#/changes';
  const main = document.querySelector('#main');
  await wait(() => main.textContent.includes('Changeset #') && button(main, 'Validate'), 'review page');
  button(main, 'Validate').click();
  await wait(() => main.textContent.includes('Ready to publish'), 'validation result');
  main.querySelector('input[placeholder^="What this changes"]').value = 'Browser rules edit';
  main.querySelector('input[placeholder="Your name"]').value = 'browser-test';
  button(main, 'Publish 1 change').click();
  await wait(() => button(document.querySelector('#modal-root'), 'Publish'), 'publish confirmation');
  button(document.querySelector('#modal-root'), 'Publish').click();
  await wait(() => main.textContent.includes('Published changeset #'), 'publish result');
  location.hash = '#/history';
  await wait(() => main.textContent.includes('Change history') && button(main, 'Revert'), 'history page');
  button(main, 'Revert').click();
  await wait(() => button(document.querySelector('#modal-root'), 'Stage the revert'), 'revert confirmation');
  button(document.querySelector('#modal-root'), 'Stage the revert').click();
  await wait(() => location.hash === '#/changes' && main.textContent.includes('reverts #'), 'staged revert');
  button(main, 'Validate').click();
  await wait(() => main.textContent.includes('Ready to publish'), 'revert validation');
  button(main, 'Publish 1 change').click();
  await wait(() => button(document.querySelector('#modal-root'), 'Publish'), 'revert publish confirmation');
  button(document.querySelector('#modal-root'), 'Publish').click();
  await wait(() => main.textContent.includes('Published changeset #'), 'revert publish');
  location.hash = '#/rules/market';
  const restored = await wait(() => [...main.querySelectorAll('tr')].find(tr => tr.textContent.includes('maintenanceIntervalSeconds')), 'restored market rule');
  if (!restored.textContent.includes(String(current))) throw new Error('The Rules page did not show the restored value.');
  location.hash = '#/packcatalog';
  await wait(() => button(main, 'Create pack'), 'pack catalogue');
  button(main, 'Create pack').click();
  const form = await wait(() => main.querySelector('form.pack-editor'), 'pack editor');
  const name = [...form.querySelectorAll('label.field')].find(label => label.querySelector('span')?.textContent === 'Name')?.querySelector('input');
  if (!name) throw new Error('The FIFA 21 pack editor has no Name field.');
  name.value = 'Browser staged pack';
  form.querySelector('button[type="submit"]').click();
  await wait(() => location.hash === '#/changes' && main.textContent.includes('1 change'), 'staged pack definition');
  location.hash = '#/store';
  const sell = await wait(() => main.querySelector('input[aria-label="Sell Browser staged pack"]'), 'pending pack in Store');
  sell.checked = true;
  sell.dispatchEvent(new Event('change', { bubbles: true }));
  await wait(() => button(main, 'Stage lineup') && !button(main, 'Stage lineup').disabled, 'changed store lineup');
  button(main, 'Stage lineup').click();
  await wait(() => location.hash === '#/changes' && main.textContent.includes('2 changes'), 'pack and lineup review');
  button(main, 'Validate').click();
  await wait(() => main.textContent.includes('Ready to publish'), 'pack validation');
  button(main, 'Publish 2 changes').click();
  await wait(() => button(document.querySelector('#modal-root'), 'Publish'), 'pack publish confirmation');
  button(document.querySelector('#modal-root'), 'Publish').click();
  await wait(() => main.textContent.includes('Published changeset #'), 'pack publication');
  location.hash = '#/packcatalog';
  await wait(() => [...main.querySelectorAll('tr')].some(tr => tr.textContent.includes('Browser staged pack') && tr.textContent.includes('In store')), 'published pack in store');
  return { ok: true, restored: current, pack: 'Browser staged pack' };
})()
'@.Replace('__CURRENT__', [string]$CurrentValue)
        $result = Send-DevTools $socket 1 'Runtime.evaluate' @{ expression = $script; awaitPromise = $true; returnByValue = $true }
        if ($result.exceptionDetails) { throw "Browser flow failed: $($result.exceptionDetails.text) $($result.exceptionDetails.exception.description)" }
        if (-not $result.result.value.ok) { throw 'Browser flow did not complete.' }
        Send-DevTools $socket 2 'Browser.close' @{} | Out-Null
        return $true
    }
    finally {
        if ($socket) { $socket.Dispose() }
        if (-not $browser.HasExited) {
            $owned = Get-CimInstance Win32_Process -Filter "ProcessId = $($browser.Id)" -ErrorAction SilentlyContinue
            if ($owned -and $owned.ExecutablePath -eq $Chrome) { Stop-Process -Id $browser.Id -ErrorAction SilentlyContinue }
        }
    }
}

try {
    $env:ULTIMATETEAM_RUNTIME_ROOT = $root
    $env:ULTIMATETEAM21_USER_CONFIG_ROOT = $configRoot
    Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
    $testPort = Get-FreePort
    do { $panelPort = Get-FreePort } while ($panelPort -eq $testPort)
    $arguments = 'serve --http-only-test-port {0} --panel-api-port {1} --lsx-mode off --data-root "{2}"' -f $testPort, $panelPort, $dataRoot
    $child = Start-Process -FilePath $server -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardError $stderrPath

    $status = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($child.HasExited) { throw "The isolated server exited with code $($child.ExitCode)." }
        try { $status = Request-Json "http://127.0.0.1:$testPort/ultimateteam/test/status"; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $status -or -not $status.testMode) { throw 'The isolated test listener did not become ready.' }
    if ($status.runtimeRoot -ne $root -or $status.database.mode -ne 'managed') {
        throw 'The test listener is not using its isolated runtime and managed database.'
    }
    $expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $server).Hash.ToLowerInvariant()
    $bootstrap = Request-Json "http://127.0.0.1:$testPort/ultimateteam/auth/bootstrap"
    if ($bootstrap.serverBuildSha256 -ne $expectedHash) { throw 'The running server build does not match the rebuilt executable.' }

    $page = Invoke-WebRequest -Uri "http://127.0.0.1:$panelPort/panel/" -TimeoutSec 5 -UseBasicParsing
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'js/pages/rules.js') { throw 'The Rules page is missing from the served panel.' }
    $rules = Request-Json "http://127.0.0.1:$panelPort/panel/api/content/rules"
    if (-not (@($rules.files.file) -contains 'market')) { throw 'The panel did not list the market rules.' }
    $market = Request-Json "http://127.0.0.1:$panelPort/panel/api/content/rules/market?q=maintenanceIntervalSeconds"
    $row = @($market.rules | Where-Object { @($_.path).Count -eq 1 -and $_.path[0] -eq 'maintenanceIntervalSeconds' })
    if ($row.Count -ne 1) { throw 'The panel did not return the market maintenance rule.' }
    $before = [int]$row[0].value

    $base = "http://127.0.0.1:$panelPort/panel/api/content"
    $stage = Request-Json "$base/changeset/changes" 'PUT' @{ area = 'rules'; key = @{ file = 'market'; path = @('maintenanceIntervalSeconds') }; operation = 'update'; after = @{ value = $before + 1 } }
    if ($stage.pending -ne 1) { throw 'The panel did not stage one rules edit.' }
    $validation = Request-Json "$base/changeset/validate" 'POST' @{}
    if (-not $validation.ok) { throw "The panel refused the staged rules edit: $($validation.errors -join '; ')" }
    $publish = Request-Json "$base/changeset/publish" 'POST' @{ note = 'isolated panel smoke test'; by = 'test' }
    if ($publish.content.revision -ne 2) { throw 'The panel did not publish a second content revision.' }
    $after = Request-Json "$base/rules/market?q=maintenanceIntervalSeconds"
    $updated = @($after.rules | Where-Object { @($_.path).Count -eq 1 -and $_.path[0] -eq 'maintenanceIntervalSeconds' })
    if ($updated.Count -ne 1 -or [int]$updated[0].value -ne $before + 1) { throw 'The published rule did not reach the panel API.' }
    $history = Request-Json "$base/history"
    if (@($history.changesets).Count -ne 1) { throw 'The published change is missing from history.' }
    $browserChecked = $false
    $chrome = Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'
    if (Test-Path -LiteralPath $chrome -PathType Leaf) {
        $browserChecked = Test-BrowserControls $chrome $root $panelPort ($before + 1)
    }
    Write-Host "PASS: isolated panel API published a rule; browser completed rule edit/revert and pack definition/lineup publication=$browserChecked; build $expectedHash"
    Write-Host "Fixture: $root"
}
finally {
    if ($null -ne $child -and -not $child.HasExited) {
        $owned = Get-CimInstance Win32_Process -Filter "ProcessId = $($child.Id)" -ErrorAction SilentlyContinue
        if ($null -ne $owned -and $owned.ExecutablePath -eq $server) {
            Stop-Process -Id $child.Id -ErrorAction Stop
            $child.WaitForExit(10000) | Out-Null
        }
        else { Write-Warning "The child path could not be verified; it was not stopped. PID $($child.Id), fixture $root" }
    }
    & $server postgres stop --data-root $dataRoot | Out-Null
    $env:ULTIMATETEAM_RUNTIME_ROOT = $priorRuntime
    $env:ULTIMATETEAM21_USER_CONFIG_ROOT = $priorConfig
    $env:ULTIMATETEAM_POSTGRES_CONNECTION = $priorConnection
}
