[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TagName
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$integrationBaseline = '67116866f0e8fc0bd6d3aa753bdc2cee96ee8613'
$projectPath = Join-Path $repositoryRoot 'Dalamud/Dalamud.csproj'

function Invoke-ReleaseGit {
    param([string[]]$Arguments)

    $output = & git -C $repositoryRoot @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed (exit $exitCode):`n$($output -join "`n")"
    }

    return ($output -join "`n")
}

try {
    $releaseMatch = [regex]::Match($TagName, '\Adalamud-plus/v(?<Date>[0-9]{8})(?:-[1-9][0-9]*)?\z')
    $releaseDate = [datetime]::MinValue
    if (-not $releaseMatch.Success -or -not [datetime]::TryParseExact(
        $releaseMatch.Groups['Date'].Value,
        'yyyyMMdd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$releaseDate)) {
        throw 'Use a valid date tag such as dalamud-plus/v20261116 or dalamud-plus/v20261116-2.'
    }

    $branch = Invoke-ReleaseGit -Arguments @('symbolic-ref', '--short', 'HEAD')
    if ($branch -ne 'main') {
        throw 'Prepare Dalamud Plus releases from main.'
    }

    $status = Invoke-ReleaseGit -Arguments @('status', '--porcelain')
    if ($status) {
        throw 'Commit or stash all tracked and untracked changes before preparing a release.'
    }

    $null = Invoke-ReleaseGit -Arguments @('merge-base', '--is-ancestor', $integrationBaseline, 'HEAD')
    $null = Invoke-ReleaseGit -Arguments @('check-ref-format', "refs/tags/$TagName")
    $existingTag = Invoke-ReleaseGit -Arguments @('tag', '--list', '--format=%(refname)', '--', $TagName)
    if ($existingTag) {
        throw "Tag $TagName already exists. Existing tags are preserved."
    }

    # Read the pinned framework version for the annotation; release dates belong
    # only to Git tags. Preparing a release never edits or commits project files.
    $project = [xml][System.IO.File]::ReadAllText($projectPath)
    $versions = @($project.SelectNodes('/Project/PropertyGroup/DalamudVersion'))
    if ($versions.Count -ne 1) {
        throw 'Expected exactly one DalamudVersion property in Dalamud.csproj.'
    }

    $frameworkVersion = $versions[0].InnerText.Trim()
    $releaseLabel = $TagName.Substring('dalamud-plus/v'.Length)
    $null = Invoke-ReleaseGit -Arguments @('tag', '-a', $TagName, '-m', "Dalamud Plus $releaseLabel (Dalamud $frameworkVersion)")
    $revision = Invoke-ReleaseGit -Arguments @('rev-parse', 'HEAD')
    Write-Host "Prepared local tag $TagName at $revision."
    Write-Host "Pinned DalamudVersion: $frameworkVersion."
    Write-Host 'Review the commit and tag, then publish explicitly:'
    Write-Host "git push --atomic origin main refs/tags/$TagName"
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
