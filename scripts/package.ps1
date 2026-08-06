<#
.SYNOPSIS
    Builds the distributable archives, one per platform.

.DESCRIPTION
    Each archive holds a single self-contained executable: no runtime to install, nothing
    to unpack, no installer. Cross-publishing works from any host — the SDK downloads the
    runtime pack for the target — so all four can be built from one machine.

    Trimming is deliberately off; see the note in Fjord.App.csproj. A trimmed build looks
    fine and then reports "unknown command" for every key press.

.PARAMETER Version
    Stamped into the binary and the archive names. Defaults to the version in
    Directory.Build.props.

.PARAMETER Runtime
    Build just one, e.g. -Runtime linux-x64.

.PARAMETER Output
    Where the archives go. Defaults to dist/ beside the repository.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Runtime,
    [string] $Output = (Join-Path (Join-Path $PSScriptRoot '..') 'dist')
)

$ErrorActionPreference = 'Stop'

function Resolve-GnuTar {
    <#
        Windows ships a cut-down bsdtar as tar.exe which rejects --mode outright. Git for
        Windows ships GNU tar, and so does every Linux and macOS host this might run on, so
        the check is for the feature rather than for the platform.
    #>
    foreach ($candidate in @('tar', 'C:\Program Files\Git\usr\bin\tar.exe')) {
        $version = & $candidate --version 2>$null | Select-Object -First 1
        if ($LASTEXITCODE -eq 0 -and $version -match 'GNU tar') { return $candidate }
    }

    throw 'GNU tar is required to set the executable bit. Install Git for Windows, or run this on Linux.'
}

function Compress-Tar {
    <#
        gzip through .NET rather than shelling out: gzip.exe is not on a stock Windows
        install, and GNU tar's own -z would need a third pass over an archive that has
        already been written twice.
    #>
    param([string] $Tar, [string] $Destination)

    $input = [System.IO.File]::OpenRead($Tar)
    try {
        $output = [System.IO.File]::Create($Destination)
        try {
            $gzip = New-Object System.IO.Compression.GZipStream($output, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $input.CopyTo($gzip) } finally { $gzip.Dispose() }
        }
        finally { $output.Dispose() }
    }
    finally { $input.Dispose() }
}

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Output = [System.IO.Path]::GetFullPath($Output)
$project = Join-Path $root 'src/Fjord.App/Fjord.App.csproj'

if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    $Version = ([regex]::Match($props, '<VersionPrefix>([^<]+)</VersionPrefix>')).Groups[1].Value
}

# The tar archives need the executable bit set on the binary, and there is nothing to
# preserve it from: NTFS has no such bit, so a cross-publish from Windows produces a file
# with no Unix permissions at all and the download lands unrunnable.
#
# GNU tar can stamp one with --mode. Windows' own tar.exe is a cut-down bsdtar that does
# not accept the option, so Git Bash's tar is used where it is the one available. The mode
# is applied in two passes because --mode is global to an invocation, and an executable
# README is untidy in something people are meant to unpack and look at.
$runtimes = @(
    @{ Rid = 'win-x64';   Archive = 'zip';    Exe = 'Fjord.exe' }
    @{ Rid = 'linux-x64'; Archive = 'tar.gz'; Exe = 'Fjord' }
    @{ Rid = 'osx-x64';   Archive = 'tar.gz'; Exe = 'Fjord' }
    @{ Rid = 'osx-arm64'; Archive = 'tar.gz'; Exe = 'Fjord' }
)

if ($Runtime) {
    $runtimes = @($runtimes | Where-Object { $_.Rid -eq $Runtime })
    if ($runtimes.Count -eq 0) { throw "Unknown runtime '$Runtime'." }
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
Write-Host "Fjord $Version -> $Output"

foreach ($target in $runtimes) {
    $rid = $target.Rid
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "fjord-pack-$rid"

    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    Write-Host ("  {0,-10}" -f $rid) -NoNewline
    $logFile = Join-Path ([System.IO.Path]::GetTempPath()) "fjord-publish-$rid.log"

    # The executable is named by AssemblyName in the project, not here: passing it on the
    # command line applies it to every referenced project too, and four assemblies called
    # Fjord is an ambiguous-project-name error.
    & dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --output $staging `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        --nologo -v quiet > $logFile 2>&1

    # Redirected to a file rather than piped to Out-Null: the pipeline closes when the
    # cmdlet stops reading, and the SDK writes once more as it exits, which surfaces as
    # "Cannot write to a closed TextWriter" and fails a publish that had already worked.
    if ($LASTEXITCODE -ne 0) {
        Get-Content $logFile | Select-Object -Last 20 | ForEach-Object { Write-Host "    $_" }
        throw "publish failed for $rid"
    }

    # Belt and braces. The project already tells the SDK not to copy these, and the
    # archive should never grow a hundred megabytes of native debug symbols again just
    # because a package started shipping them under a name the property does not cover.
    # Join-Path with a wildcard, not -Include on the directory: -Include is silently
    # ignored unless the path itself is a wildcard or -Recurse is given, which is why the
    # first version of this line removed nothing at all.
    Get-ChildItem (Join-Path $staging '*') -Include *.pdb, *.xml -File | Remove-Item -Force

    Copy-Item (Join-Path $root 'LICENSE') $staging
    Copy-Item (Join-Path $root 'NOTICE.md') $staging
    Copy-Item (Join-Path $root 'README.md') $staging

    $archive = Join-Path $Output "fjord-$Version-$rid.$($target.Archive)"
    if (Test-Path $archive) { Remove-Item $archive -Force }

    if ($target.Archive -eq 'zip') {
        # Windows has no executable bit to carry, so a plain zip is the honest format here.
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive
    }
    else {
        $tar = Resolve-GnuTar
        $plain = 'fjord.tar'

        # Run inside the staging directory and name everything relatively. GNU tar under
        # MSYS rewrites Windows paths in its arguments — "C:\..." becomes a host to resolve,
        # and once that is forced local it arrives with its separators escaped. Relative
        # names have nothing to rewrite.
        Push-Location $staging
        try {
            Remove-Item $plain -Force -ErrorAction SilentlyContinue

            # Two passes: the binary at 755, everything else at 644. --mode applies to the
            # whole invocation, and --append is the only way to mix them in one archive.
            # --owner/--group: without them the archive records this machine's numeric
            # ids, which do not exist on the target. Extracting as root then fails on every
            # entry with "cannot change ownership", which is how a tarball built on Windows
            # arrives broken on a Linux box. root:root is what a distributed archive says.
            $ownership = '--owner=0', '--group=0', '--numeric-owner'

            & $tar --create --file $plain --mode=755 @ownership $target.Exe
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }

            & $tar --append --file $plain --mode=644 @ownership LICENSE NOTICE.md README.md
            if ($LASTEXITCODE -ne 0) { throw "tar append failed for $rid" }
        }
        finally { Pop-Location }

        Compress-Tar -Tar (Join-Path $staging $plain) -Destination $archive
        Remove-Item (Join-Path $staging $plain) -Force
    }

    $exe = Join-Path $staging $target.Exe
    $exeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
    $zipMb = [Math]::Round((Get-Item $archive).Length / 1MB, 1)

    Write-Host ("{0,6} MB executable, {1,6} MB archive" -f $exeMb, $zipMb)
    Remove-Item $staging -Recurse -Force
}

Write-Host "Done."
Get-ChildItem $Output -File | ForEach-Object { Write-Host ("  {0}" -f $_.Name) }
