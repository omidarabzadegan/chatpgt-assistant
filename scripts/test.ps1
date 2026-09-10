[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}
$base = [IO.Path]::GetFullPath($ProjectRoot)
& (Join-Path $base 'scripts/verify-release.ps1')
$native = Join-Path $base 'native'
$source = Join-Path $native 'ProjectBridge.cs'
$fixture = [IO.Path]::GetFullPath((Join-Path $base 'tests\fixture'))
$testData = [IO.Path]::GetFullPath((Join-Path $base 'tests\test-data'))
$exe = Join-Path $testData 'ProjectBridge.test.exe'
$configPath = Join-Path $base 'tests\test-config.generated.json'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not $fixture.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture path escaped the Project Bridge test root.'
}
if (-not $testData.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test data path escaped the Project Bridge test root.'
}

if (Test-Path -LiteralPath $testData) {
    Remove-Item -LiteralPath $testData -Recurse -Force
}
New-Item -ItemType Directory -Path $testData -Force | Out-Null
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $fixture 'source.txt'),
    "alpha`nbeta`n",
    (New-Object Text.UTF8Encoding($false))
)
[IO.File]::WriteAllText(
    (Join-Path $fixture 'echo-test.ps1'),
    "Write-Output 'bridge-script-ok'`n",
    (New-Object Text.UTF8Encoding($false))
)

& $compiler /nologo /optimize+ /target:exe /out:$exe /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }

$configuration = [ordered]@{
    projectRoot = $fixture
    dataDirectory = $testData
    maxReadBytes = 262144
    maxSearchResults = 40
    maxContextChars = 30000
    processTimeoutSeconds = 20
    executionEnabled = $true
    allowedLocalCommands = @('where.exe', 'ping.exe', 'ssh.exe')
    allowedLocalScripts = @('echo-test.ps1')
    servers = @(
        [ordered]@{
            id = 'test-server'
            host = '127.0.0.1'
            user = 'bridge-test'
            port = 22
            workingDirectory = '/tmp'
            allowedCommands = @('pwd')
        }
    )
    projectServers = @(
        [ordered]@{
            projectPath = $fixture
            projectName = 'fixture'
            server = 'test-server'
        }
    )
    tests = @(
        [ordered]@{
            id = 'fixture_verify'
            fileName = 'git.exe'
            arguments = 'diff --check'
            workingDirectory = '.'
            timeoutSeconds = 30
        }
    )
}
$configuration | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding UTF8

& git.exe -C $fixture init -q
if ($LASTEXITCODE -ne 0) { throw 'Fixture git init failed.' }
& git.exe -C $fixture add -- source.txt
if ($LASTEXITCODE -ne 0) { throw 'Fixture git add failed.' }

function Read-Exact {
    param([IO.Stream]$Stream, [int]$Length)
    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) { throw 'Unexpected end of Native Messaging stream.' }
        $offset += $read
    }
    return $buffer
}

function Invoke-Bridge {
    param([string]$Action, [hashtable]$Params = @{})
    $request = @{ action = $Action; params = $Params; requestId = [guid]::NewGuid().ToString('N') } | ConvertTo-Json -Depth 8 -Compress
    $payload = [Text.Encoding]::UTF8.GetBytes($request)
    $length = [BitConverter]::GetBytes([int]$payload.Length)

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = "--config=`"$configPath`""
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $null = $process.Start()
    $process.StandardInput.BaseStream.Write($length, 0, $length.Length)
    $process.StandardInput.BaseStream.Write($payload, 0, $payload.Length)
    $process.StandardInput.BaseStream.Flush()
    $process.StandardInput.Close()

    $responseLengthBytes = Read-Exact -Stream $process.StandardOutput.BaseStream -Length 4
    $responseLength = [BitConverter]::ToInt32($responseLengthBytes, 0)
    if ($responseLength -le 0 -or $responseLength -gt 921600) { throw "Invalid response length: $responseLength" }
    $responseBytes = Read-Exact -Stream $process.StandardOutput.BaseStream -Length $responseLength
    $process.WaitForExit(10000) | Out-Null
    $stderr = $process.StandardError.ReadToEnd()
    if ($process.ExitCode -ne 0) { throw "Bridge process failed: $stderr" }
    return ([Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json)
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

$results = New-Object Collections.Generic.List[string]

$ping = Invoke-Bridge -Action 'ping'
Assert-True $ping.ok 'ping should succeed'
Assert-True ($ping.projectRoot -eq $fixture) 'ping should report fixture root'
Assert-True ($ping.capabilities -contains 'choose_project' -and $ping.capabilities -contains 'write_files' -and $ping.capabilities -contains 'batch' -and $ping.capabilities -contains 'exec_local' -and $ping.capabilities -contains 'exec_server') 'ping should advertise new capabilities'
Assert-True ($ping.defaultServer -eq 'test-server') 'ping should report project-linked default server'
Assert-True (-not $ping.executionGranted) 'execution permission should default to denied for each native request'
$grantedPing = Invoke-Bridge -Action 'ping' -Params @{ __executionGranted = $true }
Assert-True ($grantedPing.executionEnabled -and $grantedPing.executionGranted) 'ping should report config and per-request execution gates'
$results.Add('PASS ping and capability discovery')

$alternateProject = Join-Path $testData 'alternate-project'
New-Item -ItemType Directory -Path $alternateProject -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $alternateProject 'alternate.txt'), "selected-project`n", (New-Object Text.UTF8Encoding($false)))
$overridePing = Invoke-Bridge -Action 'ping' -Params @{ __projectRoot = $alternateProject }
Assert-True ($overridePing.ok -and $overridePing.projectRoot -eq $alternateProject) 'selected project should override configured root'
$overrideRead = Invoke-Bridge -Action 'read_file' -Params @{ __projectRoot = $alternateProject; path = 'alternate.txt'; startLine = 1; endLine = 5 }
Assert-True ($overrideRead.ok -and $overrideRead.content -match 'selected-project') 'actions should use selected project root'
$results.Add('PASS selected project routing')

$batch = Invoke-Bridge -Action 'batch' -Params @{
    actions = @(
        @{ action = 'read_file'; args = @{ path = 'source.txt'; startLine = 1; endLine = 2 } },
        @{ action = 'project_tree'; args = @{ limit = 10 } }
    )
}
Assert-True ($batch.ok -and $batch.count -eq 2 -and $batch.failed -eq 0) 'batch should execute multiple actions'
$nested = Invoke-Bridge -Action 'batch' -Params @{ actions = @(@{ action = 'batch'; args = @{ actions = @() } }) }
Assert-True ($nested.ok -and $nested.failed -eq 1) 'nested batch should be rejected as a child result'
$tooMany = Invoke-Bridge -Action 'batch' -Params @{ actions = @(1..9 | ForEach-Object { @{ action = 'project_tree'; args = @{ limit = 1 } } }) }
Assert-True (-not $tooMany.ok) 'batch should reject more than eight actions'
$writeInBatch = Invoke-Bridge -Action 'batch' -Params @{ actions = @(@{ action = 'write_files'; args = @{ files = @(@{ path = 'blocked.txt'; content = 'no' }) } }) }
Assert-True ($writeInBatch.ok -and $writeInBatch.failed -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $fixture 'blocked.txt'))) 'write_files should not run inside batch'
$results.Add('PASS batch execution and limits')

$permissionDenied = Invoke-Bridge -Action 'exec_local' -Params @{ command = 'where.exe'; arguments = 'git.exe'; timeoutSeconds = 10 }
Assert-True (-not $permissionDenied.ok -and $permissionDenied.error -match 'permission') 'local execution should require the extension permission grant'
$local = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; command = 'where.exe'; arguments = 'git.exe'; timeoutSeconds = 10 }
Assert-True ($local.ok -and $local.passed -and $local.output -match 'git') 'allowlisted local command should execute'
$blockedLocal = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; command = 'cmd.exe'; arguments = '/c echo blocked' }
Assert-True (-not $blockedLocal.ok) 'non-allowlisted local command should be blocked'
$directSsh = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; command = 'ssh.exe'; arguments = 'someone@example.com pwd' }
Assert-True (-not $directSsh.ok -and $directSsh.error -match 'exec_server') 'SSH should not bypass configured server profiles through exec_local'
$badWorking = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; command = 'where.exe'; arguments = 'git.exe'; workingDirectory = '..\outside' }
Assert-True (-not $badWorking.ok) 'local working directory traversal should be blocked'
$timed = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; command = 'ping.exe'; arguments = '127.0.0.1 -n 6'; timeoutSeconds = 1 }
Assert-True ($timed.ok -and $timed.timedOut) 'local command timeout should be enforced'
$scriptRun = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; script = 'echo-test.ps1'; timeoutSeconds = 10 }
Assert-True ($scriptRun.ok -and $scriptRun.passed -and $scriptRun.mode -eq 'powershell_script' -and $scriptRun.output -match 'bridge-script-ok') 'allowlisted PowerShell script should execute'
$blockedScript = Invoke-Bridge -Action 'exec_local' -Params @{ __executionGranted = $true; script = 'missing.ps1' }
Assert-True (-not $blockedScript.ok) 'non-allowlisted PowerShell script should be blocked'
$results.Add('PASS controlled local execution')

$serverPermissionDenied = Invoke-Bridge -Action 'exec_server' -Params @{ server = 'test-server'; command = 'pwd' }
Assert-True (-not $serverPermissionDenied.ok -and $serverPermissionDenied.error -match 'permission') 'SSH execution should require the extension permission grant'
$unknownServer = Invoke-Bridge -Action 'exec_server' -Params @{ __serverExecutionGranted = $true; server = 'missing'; command = 'pwd' }
Assert-True (-not $unknownServer.ok) 'unknown server profile should be rejected'
$blockedServer = Invoke-Bridge -Action 'exec_server' -Params @{ __serverExecutionGranted = $true; server = 'test-server'; command = 'whoami' }
Assert-True (-not $blockedServer.ok) 'non-allowlisted remote command should be rejected'
$unsafeServer = Invoke-Bridge -Action 'exec_server' -Params @{ __serverExecutionGranted = $true; server = 'test-server'; command = 'pwd; whoami' }
Assert-True (-not $unsafeServer.ok) 'unsafe remote shell syntax should be rejected'
foreach ($action in @('remote_tree', 'remote_search', 'remote_read_file', 'remote_apply_patch', 'test_server')) {
    $denied = Invoke-Bridge -Action $action -Params @{ server = 'test-server'; path = 'source.txt'; query = 'alpha' }
    Assert-True (-not $denied.ok -and $denied.error -match 'permission') "$action must require SSH permission"
}
$unbound = Invoke-Bridge -Action 'ping' -Params @{ __projectRoot = $testData }
Assert-True ($unbound.ok -and -not $unbound.defaultServer) 'unrelated projects must not inherit the only configured server'
$bound = Invoke-Bridge -Action 'bind_project_server' -Params @{ __projectRoot = $testData; server = 'test-server' }
Assert-True $bound.ok 'project server selection should save without connecting'
$boundPing = Invoke-Bridge -Action 'ping' -Params @{ __projectRoot = $testData }
Assert-True ($boundPing.defaultServer -eq 'test-server') 'project server selection should persist'
$invalidBinding = Invoke-Bridge -Action 'bind_project_server' -Params @{ server = 'missing' }
Assert-True (-not $invalidBinding.ok) 'unknown server binding should fail'
$results.Add('PASS controlled server validation')

$read = Invoke-Bridge -Action 'read_file' -Params @{ path = 'source.txt'; startLine = 1; endLine = 10 }
Assert-True ($read.ok -and $read.content -match 'alpha' -and $read.content -match 'beta') 'read_file should return fixture content'
$results.Add('PASS read_file with line boundaries')

$search = Invoke-Bridge -Action 'search_code' -Params @{ query = 'alpha'; fixed = $true }
Assert-True ($search.ok -and $search.results -match 'source.txt') 'search_code should locate source.txt'
$results.Add('PASS search_code through ripgrep')

$tree = Invoke-Bridge -Action 'project_tree' -Params @{ limit = 20 }
Assert-True ($tree.ok -and $tree.files -match 'source.txt') 'project_tree should include source.txt'
$results.Add('PASS project_tree with exclusions')

$context = Invoke-Bridge -Action 'prepare_context' -Params @{ prompt = 'Please inspect alpha in source.txt' }
Assert-True ($context.ok -and $context.context -match 'source.txt') 'prepare_context should produce relevant excerpts'
$results.Add('PASS automatic context preparation')

$blocked = Invoke-Bridge -Action 'read_file' -Params @{ path = '..\outside.txt' }
Assert-True (-not $blocked.ok) 'path traversal must be blocked'
$secret = Invoke-Bridge -Action 'read_file' -Params @{ path = '.env' }
Assert-True (-not $secret.ok) 'sensitive .env path must be blocked'
$results.Add('PASS path traversal and secret blocking')

$generatedRoot = Join-Path $fixture 'generated-page'
if (Test-Path -LiteralPath $generatedRoot) {
    Remove-Item -LiteralPath $generatedRoot -Recurse -Force
}
$written = Invoke-Bridge -Action 'write_files' -Params @{
    directories = @('generated-page/assets/icons')
    files = @(
        @{ path = 'generated-page/index.html'; content = "<!doctype html>`n<title>Bridge page</title>`n" },
        @{ path = 'generated-page/assets/app.js'; content = "console.log('bridge');`n" },
        @{ path = 'generated-page/empty.txt'; content = '' }
    )
}
Assert-True ($written.ok -and $written.checkpointId -and $written.fileCount -eq 3 -and $written.verified) 'write_files should create and verify a multi-file scaffold with a checkpoint'
Assert-True (@($written.artifacts | Where-Object { $_.path -eq 'generated-page/index.html' -and $_.exists -and $_.bytes -gt 0 -and $_.sha256 }).Count -eq 1) 'write_files should return verified file metadata and SHA-256'
Assert-True ((Test-Path -LiteralPath (Join-Path $generatedRoot 'index.html')) -and (Test-Path -LiteralPath (Join-Path $generatedRoot 'assets\icons'))) 'write_files should create parent and explicit directories'
Assert-True ((Get-Content -Raw -Encoding utf8 (Join-Path $generatedRoot 'index.html')) -match 'Bridge page') 'write_files should preserve file content'
Assert-True ((Get-Item -LiteralPath (Join-Path $generatedRoot 'empty.txt')).Length -eq 0) 'write_files should allow empty files'

$refusedOverwrite = Invoke-Bridge -Action 'write_files' -Params @{ files = @(@{ path = 'generated-page/index.html'; content = 'replaced' }) }
Assert-True (-not $refusedOverwrite.ok) 'write_files should refuse overwrite by default'
$allowedOverwrite = Invoke-Bridge -Action 'write_files' -Params @{ overwrite = $true; files = @(@{ path = 'generated-page/index.html'; content = 'replaced' }) }
Assert-True ($allowedOverwrite.ok -and (Get-Content -Raw -Encoding utf8 (Join-Path $generatedRoot 'index.html')) -eq 'replaced') 'write_files should overwrite only when explicitly requested'
$undoOverwrite = Invoke-Bridge -Action 'undo' -Params @{ checkpointId = $allowedOverwrite.checkpointId }
Assert-True ($undoOverwrite.ok -and (Get-Content -Raw -Encoding utf8 (Join-Path $generatedRoot 'index.html')) -match 'Bridge page') 'undo should restore a file replaced by write_files'

$blockedWrite = Invoke-Bridge -Action 'write_files' -Params @{ files = @(@{ path = '..\escaped.txt'; content = 'no' }) }
$secretWrite = Invoke-Bridge -Action 'write_files' -Params @{ files = @(@{ path = '.env'; content = 'no' }) }
Assert-True (-not $blockedWrite.ok -and -not $secretWrite.ok) 'write_files should block traversal and sensitive paths'
$undoWritten = Invoke-Bridge -Action 'undo' -Params @{ checkpointId = $written.checkpointId }
Assert-True ($undoWritten.ok -and -not (Test-Path -LiteralPath $generatedRoot)) 'undo should remove newly created files and empty directories'
$results.Add('PASS secure multi-file and directory creation with undo')

$patch = @'
diff --git a/source.txt b/source.txt
--- a/source.txt
+++ b/source.txt
@@ -1,2 +1,2 @@
 alpha
-beta
+bridge-test
'@
$applied = Invoke-Bridge -Action 'apply_patch' -Params @{ patch = $patch }
if (-not $applied.ok) {
    Write-Host ($applied | ConvertTo-Json -Depth 8) -ForegroundColor Red
}
Assert-True ($applied.ok -and $applied.checkpointId) 'apply_patch should return a checkpoint'
Assert-True ((Get-Content -Raw -Encoding utf8 (Join-Path $fixture 'source.txt')) -match 'bridge-test') 'patch should change fixture file'
$results.Add('PASS git apply check, checkpoint and patch application')

$diff = Invoke-Bridge -Action 'git_diff' -Params @{ path = 'source.txt' }
Assert-True ($diff.ok -and $diff.diff -match 'bridge-test') 'git_diff should show the applied change'
$test = Invoke-Bridge -Action 'run_test' -Params @{ id = 'fixture_verify' }
Assert-True ($test.ok -and $test.passed) 'allowlisted test should pass'
$results.Add('PASS git_diff and allowlisted test execution')

$undo = Invoke-Bridge -Action 'undo' -Params @{ checkpointId = $applied.checkpointId }
Assert-True $undo.ok 'undo should succeed'
$restored = Get-Content -Raw -Encoding utf8 (Join-Path $fixture 'source.txt')
Assert-True ($restored -match 'beta' -and $restored -notmatch 'bridge-test') 'undo should restore fixture file'
$results.Add('PASS checkpoint undo restoration')

# Independent history must work without a Git repository or Chrome storage state.
$historyRoot = Join-Path $testData 'history-project'
$otherRoot = Join-Path $testData 'other-project'
New-Item -ItemType Directory -Path $historyRoot, $otherRoot -Force | Out-Null
$steps = @()
for ($i = 0; $i -lt 10; $i++) {
    $step = Invoke-Bridge -Action 'write_files' -Params @{ __projectRoot = $historyRoot; files = @(@{ path = "step-$i.txt"; content = "value-$i" }) }
    Assert-True $step.ok 'history fixture write must succeed'
    $steps += $step.checkpointId
}
$history = Invoke-Bridge -Action 'history_list' -Params @{ __projectRoot = $historyRoot }
Assert-True ($history.ok -and $history.count -eq 10 -and $history.cleanupRecommended -and $history.bytes -gt 0) 'history must persist ten steps with cleanup recommendation and size'
$other = Invoke-Bridge -Action 'history_list' -Params @{ __projectRoot = $otherRoot }
Assert-True ($other.ok -and $other.count -eq 0) 'history must be isolated by project'
$wrong = Invoke-Bridge -Action 'history_restore' -Params @{ __projectRoot = $otherRoot; checkpointId = $steps[0] }
Assert-True (-not $wrong.ok) 'foreign checkpoint must not restore'
$restoreMany = Invoke-Bridge -Action 'history_restore' -Params @{ __projectRoot = $historyRoot; checkpointId = $steps[2] }
Assert-True ($restoreMany.ok -and $restoreMany.restoredSteps -eq 8) 'older checkpoint must undo all eight later steps'
Assert-True ((Test-Path -LiteralPath (Join-Path $historyRoot 'step-1.txt')) -and -not (Test-Path -LiteralPath (Join-Path $historyRoot 'step-2.txt')) -and -not (Test-Path -LiteralPath (Join-Path $historyRoot 'step-9.txt'))) 'multi-step restore must preserve earlier files and remove later ones'
$repeated = Invoke-Bridge -Action 'history_restore' -Params @{ __projectRoot = $historyRoot; checkpointId = $steps[2] }
Assert-True (-not $repeated.ok) 'consumed checkpoint must not be applied again'
$clear = Invoke-Bridge -Action 'history_clear' -Params @{ __projectRoot = $historyRoot }
Assert-True ($clear.ok -and $clear.count -eq 0 -and (Test-Path -LiteralPath (Join-Path $historyRoot 'step-1.txt'))) 'clear must preserve current project files'
$first = Invoke-Bridge -Action 'write_files' -Params @{ __projectRoot = $historyRoot; overwrite = $true; files = @(@{ path = 'step-1.txt'; content = 'second' }) }
$second = Invoke-Bridge -Action 'write_files' -Params @{ __projectRoot = $historyRoot; overwrite = $true; files = @(@{ path = 'step-1.txt'; content = 'third' }) }
$restoreSame = Invoke-Bridge -Action 'history_restore' -Params @{ __projectRoot = $historyRoot; checkpointId = $first.checkpointId }
Assert-True ($restoreSame.ok -and (Get-Content -Raw (Join-Path $historyRoot 'step-1.txt')) -eq 'value-1') 'repeated edits must restore original content in reverse order'
$results.Add('PASS persistent Git-free history, warning at ten, project isolation, multi-step restore, repeated edits and clear')

for ($i = 0; $i -lt 50; $i++) {
    $limited = Invoke-Bridge -Action 'write_files' -Params @{ __projectRoot = $historyRoot; files = @(@{ path = "limit-$i.txt"; content = 'limit' }) }
    Assert-True $limited.ok 'fifty steps should fit history count limit'
}
$full = Invoke-Bridge -Action 'write_files' -Params @{ __projectRoot = $historyRoot; files = @(@{ path = 'overflow.txt'; content = 'overflow' }) }
Assert-True (-not $full.ok -and -not (Test-Path -LiteralPath (Join-Path $historyRoot 'overflow.txt'))) 'full history must block mutation before creating an unprotected file'
$null = Invoke-Bridge -Action 'history_clear' -Params @{ __projectRoot = $historyRoot }
$results.Add('PASS history count limit prevents unprotected mutations')

if (Get-Command node -ErrorAction SilentlyContinue) {
    & node --test (Join-Path $base 'tests/background.test.cjs') (Join-Path $base 'tests/popup.test.cjs') (Join-Path $base 'tests/attribution.test.cjs')
    Assert-True ($LASTEXITCODE -eq 0) 'JavaScript bridge and popup tests must pass'
    $results.Add('PASS JavaScript bridge and popup tests')
}

$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $base 'extension\manifest.json') | ConvertFrom-Json
Assert-True ($manifest.manifest_version -eq 3) 'manifest should be MV3'
Assert-True ($manifest.permissions -contains 'nativeMessaging') 'manifest should request nativeMessaging'
$results.Add('PASS extension manifest validation')

$releaseExe = Join-Path $native 'ProjectBridge.agent.exe'
if (
    -not (Test-Path -LiteralPath $releaseExe) -or
    (Get-Item -LiteralPath $releaseExe).LastWriteTimeUtc -lt (Get-Item -LiteralPath $source).LastWriteTimeUtc
) {
    try {
        Copy-Item -LiteralPath $exe -Destination $releaseExe -Force -ErrorAction Stop
    } catch [System.IO.IOException] {
        $stagedExe = Join-Path $native 'ProjectBridge.agent.next.exe'
        Copy-Item -LiteralPath $exe -Destination $stagedExe -Force
        Write-Warning 'ProjectBridge.agent.exe is currently in use. The tested build was staged as ProjectBridge.agent.next.exe; replace/restart the Native Host before using the new build.'
    }
} else {
    Write-Host 'ProjectBridge.agent.exe is already current; publish copy skipped.' -ForegroundColor DarkGray
}
Assert-True (Test-Path -LiteralPath $releaseExe) 'tested Bridge executable should be published'

$results | ForEach-Object { Write-Host $_ -ForegroundColor Green }
Write-Host "ALL TESTS PASSED ($($results.Count))" -ForegroundColor Cyan
