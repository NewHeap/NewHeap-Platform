# Run from PowerShell 7 after building SampleProjectManagement.Proxy in Release.
# Uses an isolated content root; never reads or edits the developer's demo database.
$ErrorActionPreference = 'Stop'
$assembly = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Back-end/Applications/SampleProjectManagement.Proxy/bin/Release/net10.0/SampleProjectManagement.Proxy.dll'))
if (!(Test-Path -LiteralPath $assembly)) {
    throw 'Build SampleProjectManagement.Proxy in Release before running this check.'
}

$temporaryRoot = [IO.Directory]::CreateTempSubdirectory('newheap-proxy-demo-check-').FullName
$process = $null
$client = $null
function Start-Demo([string] $environmentName = 'Development') {
    $arguments = @('"' + $assembly + '"', '--contentRoot', '"' + $temporaryRoot + '"', '--environment', $environmentName, '--urls', 'http://127.0.0.1:0', '--ProxyDemo=true')
    $script:process = Start-Process -FilePath dotnet -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $temporaryRoot 'stdout.log') -RedirectStandardError (Join-Path $temporaryRoot 'stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $output = Get-Content -LiteralPath (Join-Path $temporaryRoot 'stdout.log') -Raw -ErrorAction SilentlyContinue
        if ($output -match 'Proxy demo ready' -and $output -match 'Now listening on: (http://127\.0\.0\.1:\d+)') {
            return $Matches[1]
        }
        if ($process.HasExited) {
            throw (Get-Content -LiteralPath (Join-Path $temporaryRoot 'stderr.log') -Raw)
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The demo did not become ready within 30 seconds.'
}

function Stop-Demo {
    if ($script:process -and !$script:process.HasExited) {
        $script:process.Kill($true)
        $script:process.WaitForExit()
    }
    if ($script:process) {
        $script:process.Dispose()
        $script:process = $null
    }
}

function Form([string] $html, [hashtable] $values) {
    $match = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (!$match.Success) { throw 'Missing antiforgery token.' }
    $fields = [Collections.Generic.Dictionary[string,string]]::new()
    foreach ($key in $values.Keys) { $fields[$key] = $values[$key] }
    $fields['__RequestVerificationToken'] = [Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
    return [Net.Http.FormUrlEncodedContent]::new($fields)
}

try {
    $url = Start-Demo
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    foreach ($endpoint in @('/health', '/alive')) {
        $health = $client.GetAsync($url + $endpoint).GetAwaiter().GetResult()
        if ([int]$health.StatusCode -ne 200) { throw "Aspire health endpoint $endpoint is unavailable." }
    }
    $redirect = $client.GetAsync("$url/old-projects?campaign=check").GetAwaiter().GetResult()
    if ([int]$redirect.StatusCode -ne 302 -or $redirect.Headers.Location.OriginalString -ne '/projects?campaign=check&source=proxy') {
        throw 'The seeded literal redirect did not produce the expected 302.'
    }
    $login = $client.GetStringAsync("$url/newheap-proxy/Login").GetAwaiter().GetResult()
    $response = $client.PostAsync("$url/newheap-proxy/Login", (Form $login @{UserName='administrator';Password='NewHeap123!'})).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 302) { throw 'The documented test account could not sign in.' }
    $list = $client.GetStringAsync("$url/newheap-proxy").GetAwaiter().GetResult()
    $delete = [regex]::Match($list, 'href="([^"]*/Delete/[a-fA-F0-9-]+)"')
    if (!$delete.Success) { throw 'The example rule is missing from administration.' }
    $response = $client.PostAsync($url + $delete.Groups[1].Value, (Form $list @{revision='1'})).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 302) { throw 'The example rule could not be deleted.' }
    Stop-Demo
    $url = Start-Demo
    $response = $client.GetAsync("$url/old-projects").GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 404) { throw 'Restart recreated a deliberately deleted example rule.' }
    Stop-Demo
    $arguments = @('"' + $assembly + '"', '--contentRoot', '"' + $temporaryRoot + '"', '--environment', 'Production', '--ProxyDemo=true')
    $process = Start-Process -FilePath dotnet -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $temporaryRoot 'production.log') -RedirectStandardError (Join-Path $temporaryRoot 'production-error.log')
    if (!$process.WaitForExit(10000) -or $process.ExitCode -eq 0) { throw 'Production accepted the demo mode.' }
    if ((Get-Content -LiteralPath (Join-Path $temporaryRoot 'production-error.log') -Raw) -notmatch 'ProxyDemo is available only in Development') { throw 'Production failed for an unexpected reason.' }
    Write-Output 'PASS: Aspire health endpoints, fixed demo login, seeded 302, persisted deletion after restart, and Production guard (real SQLite).'
}
finally {
    Stop-Demo
    if ($client) { $client.Dispose() }
    $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
    if ($resolved -ne [IO.Path]::GetFullPath($temporaryRoot) -or [IO.Path]::GetFileName($resolved) -notlike 'newheap-proxy-demo-check-*') { throw 'Unexpected cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
