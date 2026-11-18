<#
.SYNOPSIS
    Builds a Sonic release: a self-contained publish, then a Velopack installer (Setup.exe) and update packages.

.EXAMPLE
    ./scripts/release.ps1 -Version 1.3.0 -Publish
    Builds, then publishes the release on GitHub; installed copies update from there on their next check.

.EXAMPLE
    ./scripts/release.ps1 -Version 1.3.0
    Builds only (artifacts\releases). Nothing is uploaded.

.PARAMETER Repo
    The public GitHub repository whose Releases are the update feed.

.PARAMETER Feed
    Overrides the update feed baked into the build: a folder, a web address serving artifacts\releases, or a
    GitHub repository URL. Defaults to -Repo.

.PARAMETER Publish
    Upload the installer and update packages as GitHub release v<Version> (uses the GitHub CLI login: gh auth login).
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Repo = 'https://github.com/alexkonstich-byte/sonic',
    [string] $Feed,
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

<#
  The identity package gives the installed Sonic.exe a package identity (Windows Share menu). It is signed with a
  self-signed certificate kept in this user's certificate store (private key not exportable) and reused for every
  release, so computers that trusted it once keep trusting new versions.
#>
function New-IdentityPackage([string] $Version, [string] $OutputDir) {
    $subject = 'CN=Sonic Local Signing'
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        Write-Host "  Creating the signing certificate ($subject)…"
        $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'Sonic identity package signing' `
            -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(10)
    }

    $sdkBin = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter makeappx.exe |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $sdkBin) { throw "makeappx.exe not found (restore the Microsoft.Windows.SDK.BuildTools package)" }
    $makeappx = $sdkBin.FullName
    $signtool = Join-Path $sdkBin.DirectoryName 'signtool.exe'

    $stage = Join-Path ([IO.Path]::GetTempPath()) "sonic-identity-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force $stage | Out-Null
    try {
        Copy-Item (Join-Path $root 'src\Sonic\Identity\Assets') $stage -Recurse
        (Get-Content (Join-Path $root 'src\Sonic\Identity\AppxManifest.xml') -Raw).Replace('$VERSION$', "$Version.0") |
            Set-Content (Join-Path $stage 'AppxManifest.xml') -Encoding utf8
        New-Item -ItemType Directory -Force $OutputDir | Out-Null
        $msix = Join-Path $OutputDir 'Sonic.Identity.msix'
        & $makeappx pack /d $stage /p $msix /nv /o | Out-Null
        if ($LASTEXITCODE) { throw "makeappx failed" }
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /q $msix
        if ($LASTEXITCODE) { throw "signtool failed" }
        Export-Certificate -Cert $cert -FilePath (Join-Path $OutputDir 'Sonic.Identity.cer') | Out-Null
    }
    finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$publishDir = Join-Path $root 'artifacts\Sonic-win-x64'
$releaseDir = Join-Path $root 'artifacts\releases'
if (-not $Feed) { $Feed = $Repo }

function Get-GitHubToken {
    $token = (gh auth token 2>$null)
    if (-not $token) { throw "Not signed in to GitHub. Run: gh auth login" }
    return $token.Trim()
}

Push-Location $root
try {
    dotnet tool restore | Out-Null

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host "Publishing Sonic $Version (update feed: $Feed)…"
    dotnet publish src\Sonic\Sonic.csproj -c Release -r win-x64 -o $publishDir -nologo -v q `
        "-p:Version=$Version" "-p:SonicUpdateFeed=$Feed"
    if ($LASTEXITCODE) { throw "dotnet publish failed" }

    # The Share menu helper shares the self-contained runtime that Sonic already ships in the same folder.
    dotnet publish src\Sonic.ShareTarget\Sonic.ShareTarget.csproj -c Release -r win-x64 --self-contained -o $publishDir -nologo -v q `
        "-p:Version=$Version"
    if ($LASTEXITCODE) { throw "dotnet publish (SonicShare) failed" }

    Write-Host "Building the identity package (Share menu)…"
    New-IdentityPackage -Version $Version -OutputDir (Join-Path $publishDir 'identity')

    if ($Publish) {
        # The previous release must be next to the new one so vpk can build a small delta update.
        Write-Host "Fetching the latest published release for delta updates…"
        dotnet tool run vpk download github --repoUrl $Repo --token (Get-GitHubToken) --outputDir $releaseDir
        if ($LASTEXITCODE) { Write-Warning "No previous release downloaded; only a full package will be published." }
    }

    Write-Host "Packing installer and update packages…"
    # The package id decides the install folder (%LOCALAPPDATA%\SonicMusic). It must not be "Sonic":
    # %LOCALAPPDATA%\Sonic holds the library database and settings, and uninstalling removes the install folder.
    dotnet tool run vpk pack `
        --packId SonicMusic `
        --runtime win-x64 `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe Sonic.exe `
        --packTitle Sonic `
        --packAuthors Sonic `
        --icon src\Sonic\Assets\Sonic.ico `
        --shortcuts StartMenuRoot,Desktop `
        --outputDir $releaseDir
    if ($LASTEXITCODE) { throw "vpk pack failed" }

    if ($Publish) {
        Write-Host "Publishing GitHub release v$Version…"
        dotnet tool run vpk upload github --repoUrl $Repo --token (Get-GitHubToken) --outputDir $releaseDir `
            --publish --releaseName "Sonic $Version" --tag "v$Version" --merge
        if ($LASTEXITCODE) { throw "vpk upload failed" }
        Write-Host "Published: $Repo/releases/tag/v$Version"
    }

    Write-Host ""
    Write-Host "Done. Installer: $releaseDir\SonicMusic-win-Setup.exe"
}
finally {
    Pop-Location
}
