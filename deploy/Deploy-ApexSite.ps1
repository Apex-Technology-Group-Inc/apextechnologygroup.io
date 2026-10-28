<#
.SYNOPSIS
    Stages apextechnologygroup.io (static site + /api), promotes it to IIS, and sets up the site.

.DESCRIPTION
    Run on the server from an ELEVATED prompt in the repo clone
    (default C:\temp\src\apextechnologygroup.io):

      .\deploy\Deploy-ApexSite.ps1                          stage: copy the site, publish the API
      .\deploy\Deploy-ApexSite.ps1 -Promote -CreateSite     first time: create pools, site, /api app, permissions
      .\deploy\Deploy-ApexSite.ps1 -Promote                 every later deploy
      .\deploy\Deploy-ApexSite.ps1 -Certificate             once DNS points here: win-acme cert + https binding

    Layout follows the other sites on this box:
      C:\temp\deploy\apextechnologygroup.io\{www,api}   stage
      C:\public\apextechnologygroup.io\{www,api}        live; www is the site root, api the nested /api app
      C:\ProgramData\ApexSite                           contact + analytics JSONL, outside the web root

    Mongo (local, loopback) and SES are configured in api\appsettings.json. A server-only
    api\appsettings.Production.json overrides it and is never overwritten by -Promote.

.EXAMPLE
    cd C:\temp\src\apextechnologygroup.io; git pull
    .\deploy\Deploy-ApexSite.ps1
    .\deploy\Deploy-ApexSite.ps1 -Promote
#>
[CmdletBinding()]
param(
    [string]   $Stage          = 'C:\temp\deploy\apextechnologygroup.io',
    [string]   $Root           = 'C:\public\apextechnologygroup.io',
    [switch]   $Promote,
    [switch]   $CreateSite,
    [switch]   $Certificate,
    [string]   $SiteName       = 'apextechnologygroup.io',
    [string]   $PoolName       = 'apextechnologygroup-api',
    [string[]] $HostNames      = @('apextechnologygroup.io', 'www.apextechnologygroup.io'),
    [string]   $VarDir         = 'C:\ProgramData\ApexSite',
    [string]   $SesCredentials = 'C:\credentials\apexposter-aws-ses.json',
    # Let's Encrypt account address: expiry warnings go here.
    [string]   $Email          = 'support@apextechnologygroup.io',
    [string]   $WacsPath       = ''
)

$ErrorActionPreference = 'Stop'
$Repo   = Split-Path -Parent $PSScriptRoot
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"

# What the public site is made of. Everything else in the repo (server\, deploy\, .claude\)
# stays out of the web root.
$SiteFiles = @('*.html', 'app-ads.txt', 'robots.txt', 'sitemap.xml', 'llms.txt', 'web.config')
$SiteDirs  = @('css', 'js', 'img', 'policies', 'new-orleans', 'nuclear')

function Assert-Admin([string]$What) {
    $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "$What needs an elevated PowerShell prompt."
    }
}

# Windows PowerShell 5.1 turns a native program's stderr into ErrorRecords, which 'Stop' makes
# fatal. Native calls are judged by exit code instead.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "  $_" } } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

function Invoke-AppCmd {
    param([string[]]$Arguments, [switch]$IgnoreExisting, [switch]$WarnOnFailure)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $appcmd @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }

    if ($LASTEXITCODE -ne 0) {
        if ($IgnoreExisting -and ($output -match 'already exists|duplicate collection')) {
            Write-Host '  exists, skipping' -ForegroundColor DarkGray
            return
        }
        if ($WarnOnFailure) { Write-Warning ($output -join "`n  "); return }
        throw ($output -join "`n")
    }
    $output | Where-Object { $_ } | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}

function Invoke-Robocopy([string[]]$Arguments) {
    & robocopy @Arguments /NFL /NDL /NJH /NP /R:2 /W:2 | Out-Null
    # robocopy: 0-7 are success, 8+ are real failures.
    if ($LASTEXITCODE -ge 8) { throw "robocopy $($Arguments[0]) -> $($Arguments[1]) failed with exit code $LASTEXITCODE" }
}

# A stage must be a whole site with config IIS can parse; IIS answers 500.19 for the whole
# site (and /api under it) when web.config is malformed.
function Test-Stage {
    foreach ($f in @('www\index.html', 'www\app-ads.txt', 'www\policies\Rentool.html', 'api\Apex.Site.Api.dll')) {
        if (-not (Test-Path (Join-Path $Stage $f))) { throw "Stage $Stage is missing $f. Run without -Promote first." }
    }
    foreach ($f in @('www\web.config', 'api\web.config')) {
        try { [xml](Get-Content (Join-Path $Stage $f) -Raw) | Out-Null }
        catch { throw "$Stage\$f is not valid XML; IIS would refuse the site. $($_.Exception.Message)" }
    }
}

# --- Certificate ---------------------------------------------------------------------
# Lifted from ToxFinder's deploy\iis-setup.ps1. Needs DNS already pointing at this server:
# HTTP-01 validation is served from www\.well-known\acme-challenge over plain http, which the
# skip-acme rule in web.config keeps out of the https redirect.
if ($Certificate) {
    Assert-Admin '-Certificate'

    if (-not $WacsPath) {
        $WacsPath = @(
            'C:\Program Files\win-acme\wacs.exe',
            'C:\ProgramData\win-acme\wacs.exe',
            'C:\tools\win-acme\wacs.exe',
            'C:\win-acme\wacs.exe'
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $WacsPath) { throw 'wacs.exe not found. Pass -WacsPath, or run ToxFinder''s deploy\iis-setup.ps1 once (it installs win-acme to C:\tools\win-acme).' }

    foreach ($h in $HostNames) {
        try { $ips = [Net.Dns]::GetHostAddresses($h) | ForEach-Object { $_.IPAddressToString } }
        catch { throw "$h does not resolve. Point DNS at this server before requesting a certificate." }
        Write-Host "  $h -> $($ips -join ', ')"
    }

    $siteId = (& $appcmd list site /name:$SiteName /text:id | Select-Object -First 1)
    if (-not $siteId) { throw "Could not read the site id for $SiteName. Run -Promote -CreateSite first." }

    $wacsArgs = @(
        '--target', 'iis',
        '--siteid', $siteId,
        '--host', ($HostNames -join ','),
        '--validation', 'filesystem',
        '--webroot', "$Root\www",
        '--store', 'certificatestore',
        '--certificatestore', 'WebHosting',
        '--installation', 'iis',
        '--installationsiteid', $siteId,
        '--accepttos',
        '--emailaddress', $Email
    )
    Write-Host "  $WacsPath $($wacsArgs -join ' ')" -ForegroundColor DarkGray
    & $WacsPath @wacsArgs
    if ($LASTEXITCODE -ne 0) { throw "win-acme exited $LASTEXITCODE." }

    $bindings = & $appcmd list site /name:$SiteName /text:bindings
    if ($bindings -match 'https') { Write-Host "  https bindings: $bindings" -ForegroundColor Green }
    else { Write-Warning "  win-acme reported success but the site has no https binding: $bindings" }
    return
}

# --- Stage ---------------------------------------------------------------------------
if (-not $Promote) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK is not installed; it is needed to publish the API.' }

    Write-Host "Repo  : $Repo"
    Write-Host "Stage : $Stage"
    foreach ($d in @("$Stage\www", "$Stage\api")) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
        New-Item -ItemType Directory -Force $d | Out-Null
    }

    foreach ($pattern in $SiteFiles) {
        Get-ChildItem $Repo -Filter $pattern -File | Copy-Item -Destination "$Stage\www"
    }
    foreach ($dir in $SiteDirs) {
        Invoke-Robocopy @("$Repo\$dir", "$Stage\www\$dir", '/MIR')
    }

    Invoke-Native 'dotnet publish' {
        dotnet publish "$Repo\server\Apex.Site.Api\Apex.Site.Api.csproj" -c Release -o "$Stage\api" -nologo
    }

    Test-Stage
    $commit = (& git -C $Repo rev-parse --short HEAD 2>$null)
    Write-Host ''
    Write-Host "Staged commit $commit at $Stage." -ForegroundColor Green
    Write-Host 'When you are happy:  .\deploy\Deploy-ApexSite.ps1 -Promote   (add -CreateSite the first time)'
    return
}

# --- Promote -------------------------------------------------------------------------
Assert-Admin '-Promote'
Test-Stage

# web.config's <rewrite> block needs URL Rewrite; without it the whole site answers 500.19.
if (-not (Test-Path "$env:SystemRoot\System32\inetsrv\rewrite.dll")) {
    throw 'IIS URL Rewrite is not installed; web.config needs it. Install URL Rewrite 2.1 and rerun.'
}
if (-not ((& dotnet --list-runtimes 2>$null) -match 'Microsoft\.AspNetCore\.App 10\.')) {
    throw 'ASP.NET Core 10 runtime not found. Install the .NET 10 Hosting Bundle or /api answers 500.30.'
}

foreach ($d in @("$Root\www", "$Root\api", "$Root\api\logs", $VarDir)) {
    New-Item -ItemType Directory -Force $d | Out-Null
}

if ($CreateSite) {
    if (-not (Test-Path $appcmd)) { throw 'appcmd not found - is IIS installed?' }

    # Two pools. The site pool serves static files and needs no CLR.
    Write-Host "`n== app pool $SiteName (static) ==" -ForegroundColor Cyan
    Invoke-AppCmd @('add', 'apppool', "/name:$SiteName", '/managedRuntimeVersion:', '/managedPipelineMode:Integrated') -IgnoreExisting

    # maxProcesses:1 is NOT tuning: the API appends through a single background writer, and two
    # worker processes means two appenders on one JSONL file. Idle timeout and periodic recycle off
    # so queued rows are not dropped mid-batch.
    Write-Host "`n== app pool $PoolName (api) ==" -ForegroundColor Cyan
    Invoke-AppCmd @('add', 'apppool', "/name:$PoolName", '/managedRuntimeVersion:', '/managedPipelineMode:Integrated') -IgnoreExisting
    Invoke-AppCmd @('set', 'apppool', "/apppool.name:$PoolName",
                    '/processModel.idleTimeout:00:00:00',
                    '/recycling.periodicRestart.time:00:00:00',
                    '/processModel.maxProcesses:1')

    Write-Host "`n== site $SiteName ==" -ForegroundColor Cyan
    Invoke-AppCmd @('add', 'site', "/name:$SiteName", "/physicalPath:$Root\www", "/bindings:http/*:80:$($HostNames[0])") -IgnoreExisting
    foreach ($h in $HostNames) {
        Invoke-AppCmd @('set', 'site', "/site.name:$SiteName", "/+bindings.[protocol='http',bindingInformation='*:80:$h']") -IgnoreExisting
    }
    # Asserted every run: a site pointed at $Root instead of www puts api\ inside the web root.
    Invoke-AppCmd @('set', 'site', "/site.name:$SiteName", "/[path='/'].[path='/'].physicalPath:$Root\www")
    Invoke-AppCmd @('set', 'app', "/app.name:$SiteName/", "/applicationPool:$SiteName")

    Write-Host "`n== application /api ==" -ForegroundColor Cyan
    Invoke-AppCmd @('add', 'app', "/site.name:$SiteName", '/path:/api', "/physicalPath:$Root\api", "/applicationPool:$PoolName") -IgnoreExisting
    Invoke-AppCmd @('set', 'app', "/app.name:$SiteName/api", "/applicationPool:$PoolName")

    # In the app's own web.config this setting turns every response into a 500.19 on this
    # server; /commit:apphost is the way that works. Noise control only, so non-fatal.
    Invoke-AppCmd @('set', 'config', "$SiteName/api", '-section:system.webServer/httpLogging', '/dontLog:True', '/commit:apphost') -WarnOnFailure

    Write-Host "`n== permissions ==" -ForegroundColor Cyan
    & icacls "$Root\www" /grant 'IIS_IUSRS:(OI)(CI)(RX)' /T /C /Q | Out-Null
    foreach ($d in @($VarDir, "$Root\api\logs")) {
        & icacls $d /grant "IIS AppPool\${PoolName}:(OI)(CI)M" /T /C /Q | Out-Null
        Write-Host "  $d -> IIS AppPool\$PoolName modify"
    }
    # The SES credentials file is ACL'd to the pools that use it. Without this grant the API
    # boots, stores every contact submission, and silently never emails one.
    if (Test-Path $SesCredentials) {
        & icacls $SesCredentials /grant "IIS AppPool\${PoolName}:(R)" /C /Q | Out-Null
        Write-Host "  $SesCredentials -> IIS AppPool\$PoolName read"
    } else {
        Write-Warning "  $SesCredentials not found; contact email stays off until it exists and $PoolName can read it."
    }
}

# acme-challenge is win-acme's; google*.html and BingSiteAuth.xml are search-console
# verification files. None come from the repo, and excluding them keeps /MIR from deleting them.
Write-Host "`n== www ==" -ForegroundColor Cyan
Invoke-Robocopy @("$Stage\www", "$Root\www", '/MIR', '/XD', 'acme-challenge', '/XF', 'google*.html', 'BingSiteAuth.xml')

# appsettings.Production.json is the server's own; logs are the app's. Neither is in the build.
Write-Host "`n== api ==" -ForegroundColor Cyan
$offline = Join-Path $Root 'api\app_offline.htm'
Set-Content $offline 'deploying' -Encoding utf8
try {
    Invoke-Robocopy @("$Stage\api", "$Root\api", '/MIR', '/XF', 'app_offline.htm', 'appsettings.Production.json', '/XD', 'logs')
} finally {
    Remove-Item $offline -Force -ErrorAction SilentlyContinue
}

# Before DNS and the certificate, the site only answers http with a Host header. /api is
# outside the https redirect, so this works on the first deploy too.
Write-Host "`n== verify ==" -ForegroundColor Cyan
Start-Sleep -Seconds 2
$health = & curl.exe -s -m 20 -H "Host: $($HostNames[0])" 'http://127.0.0.1/api/health'
Write-Host "  /api/health: $health"
if ($health -notmatch '"mongo":"configured"') { Write-Warning '  Mongo is not configured or not reachable; analytics and contacts are JSONL-only.' }
if ($health -notmatch '"email":"configured"') { Write-Warning "  Email is off; check that $PoolName can read $SesCredentials." }

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
