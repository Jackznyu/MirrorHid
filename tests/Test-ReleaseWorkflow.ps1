$ErrorActionPreference = 'Stop'
$workflowPath = Join-Path $PSScriptRoot '..\.github\workflows\release.yml'
$workflow = [IO.File]::ReadAllText($workflowPath)
$match = [regex]::Match($workflow,
    '(?ms)^      - name: Verify release tag and version.*?^        run: \|\r?\n(?<body>(?:^          [^\r\n]*(?:\r?\n|$)|^\r?\n)+)')
if (-not $match.Success) { throw 'Release validation step was not found.' }
$script = ($match.Groups['body'].Value -split '\r?\n' | ForEach-Object {
    if ($_.Length -ge 10) { $_.Substring(10) } else { $_ }
}) -join "`n"
$validateRelease = [scriptblock]::Create($script)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) (
    'MirrorHid-release-tests-' + [Guid]::NewGuid().ToString('N'))))
$originalTag = $env:RELEASE_TAG
$originalOutput = $env:GITHUB_OUTPUT
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
Push-Location -LiteralPath $temporaryRoot
try {
    git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize test repository.' }
    git -c user.name=Regression -c user.email=regression@localhost commit --quiet --allow-empty -m old
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create old test commit.' }
    git tag v1.2.3
    git -c user.name=Regression -c user.email=regression@localhost commit --quiet --allow-empty -m current
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create current test commit.' }
    git -c user.name=Regression -c user.email=regression@localhost tag -a v1.2.4 -m current
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create annotated test tag.' }
    $env:GITHUB_OUTPUT = Join-Path $temporaryRoot 'output.txt'

    $env:RELEASE_TAG = 'v1.2.4'
    & $validateRelease
    if ((Get-Content -LiteralPath $env:GITHUB_OUTPUT) -ne 'version=1.2.4') {
        throw 'Release version was not derived from the tag.'
    }
    Write-Output 'PASS Matching annotated tag produces the release version'

    foreach ($invalidTag in @('v1.2.3', 'v9.9.9', 'main', 'v01.2.3')) {
        $env:RELEASE_TAG = $invalidTag
        $rejected = $false
        try { & $validateRelease 2>$null }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid release source was accepted: $invalidTag" }
        Write-Output "PASS Rejected mismatched, missing, or invalid tag: $invalidTag"
    }

    git checkout --quiet --detach v1.2.3
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check out historical test tag.' }
    Clear-Content -LiteralPath $env:GITHUB_OUTPUT
    $env:RELEASE_TAG = 'v1.2.3'
    & $validateRelease
    if ((Get-Content -LiteralPath $env:GITHUB_OUTPUT) -ne 'version=1.2.3') {
        throw 'Historical tag did not produce its own release version.'
    }
    Write-Output 'PASS Historical tag is accepted only after checking out its source'
    Write-Output '6/6 release workflow checks passed.'
}
finally {
    Pop-Location
    $env:RELEASE_TAG = $originalTag
    $env:GITHUB_OUTPUT = $originalOutput
    $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $temporaryRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($temporaryRoot)).StartsWith('MirrorHid-release-tests-')) {
        throw 'Refusing to remove a test directory outside the expected temporary location.'
    }
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}
