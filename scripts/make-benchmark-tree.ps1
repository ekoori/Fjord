<#
.SYNOPSIS
    Generates the directory trees Fjord is benchmarked against.

.DESCRIPTION
    Idempotent: a tree whose entry count already matches is left alone, so re-running is
    effectively free. Files are zero-byte unless the tree is specifically about sizes, so
    even the 100,000-entry tree costs no meaningful disk.

    Uses [System.IO.File] directly rather than New-Item: the cmdlet pipeline is roughly
    two orders of magnitude slower and turns the 100k tree into a coffee break.

.PARAMETER Root
    Where to write the trees. Defaults to bench/ beside the repository, which is gitignored.

.PARAMETER Only
    Generate just one tree by name, e.g. -Only flat-100k.
#>
[CmdletBinding()]
param(
    # Nested rather than Join-Path's three-argument form: -AdditionalChildPath is
    # PowerShell 7+, and this script has to run under Windows PowerShell 5.1.
    [string] $Root = (Join-Path (Join-Path $PSScriptRoot '..') 'bench'),
    [string] $Only
)

$ErrorActionPreference = 'Stop'
$Root = [System.IO.Path]::GetFullPath($Root)

$extensions = @('.txt', '.md', '.cs', '.json', '.png', '.log', '.tar.gz', '')

function Test-AlreadyBuilt {
    param([string] $Path, [int] $Expected)

    if (-not (Test-Path $Path)) { return $false }

    # @(...).Count, not .Count(): the LINQ extension method is not callable from
    # PowerShell, which only binds real instance methods.
    $actual = @([System.IO.Directory]::EnumerateFileSystemEntries($Path)).Count
    if ($actual -eq $Expected) {
        Write-Host ("  {0,-16} up to date ({1:N0} entries)" -f (Split-Path $Path -Leaf), $actual)
        return $true
    }

    Write-Host ("  {0,-16} rebuilding ({1:N0} -> {2:N0})" -f (Split-Path $Path -Leaf), $actual, $Expected)

    # \\?\ for the same reason the writers use it: the unicode tree deliberately contains
    # names that push the full path past MAX_PATH, and Windows PowerShell 5.1 is not long
    # path aware. Deleting without the prefix fails on exactly the tree that most needs
    # rebuilding, and leaves it half-removed so every later run fails too.
    [System.IO.Directory]::Delete("\\?\$Path", $true)
    return $false
}

function New-FlatTree {
    param([string] $Name, [int] $Count)

    $path = Join-Path $Root $Name
    if (Test-AlreadyBuilt -Path $path -Expected $Count) { return }

    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    $random = [System.Random]::new(20260803)
    $dirCount = [Math]::Max(1, [int]($Count * 0.05))

    for ($i = 0; $i -lt $Count; $i++) {
        if ($i -lt $dirCount) {
            [System.IO.Directory]::CreateDirectory((Join-Path $path ("dir{0:D6}" -f $i))) | Out-Null
            continue
        }

        # Varied name lengths and a realistic extension mix: both feed the text-layout
        # cache, and uniform names would make it look faster than it is.
        $extension = $extensions[$random.Next($extensions.Length)]
        $filler = 'x' * $random.Next(0, 24)
        $leaf = "file{0:D6}{1}{2}" -f $i, $filler, $extension
        [System.IO.File]::WriteAllBytes((Join-Path $path $leaf), @())
    }

    Write-Host ("  {0,-16} created ({1:N0} entries)" -f $Name, $Count)
}

function New-DeepTree {
    $path = Join-Path $Root 'deep'
    if (Test-Path $path) {
        Write-Host "  deep             up to date"
        return
    }

    # 10 levels of 10. The names are deliberately long so the deepest leaves land past
    # 260 characters: this tree exists to prove Fjord's longPathAware manifest works.
    #
    # Windows PowerShell 5.1 is itself not long-path aware, so creating them needs the
    # \\?\ prefix, which bypasses MAX_PATH. That prefix also disables all path
    # normalisation, so the path must already be absolute and backslash-separated —
    # $Root is GetFullPath'd above, which guarantees both.
    $current = $path
    for ($level = 0; $level -lt 10; $level++) {
        $current = Join-Path $current ("level{0}-a-reasonably-long-directory-name" -f $level)
        [System.IO.Directory]::CreateDirectory("\\?\$current") | Out-Null
        for ($i = 0; $i -lt 9; $i++) {
            [System.IO.File]::WriteAllBytes("\\?\$current\leaf$i.txt", @())
        }
    }

    Write-Host ("  deep             created (10 levels, deepest path {0} chars)" -f $current.Length)
}

function New-UnicodeTree {
    $path = Join-Path $Root 'unicode'
    $names = @(
        '日本語のファイル名.txt',
        '中文文件名称测试.md',
        'مرحبا-بالعالم.txt',
        'עברית-קובץ.txt',
        'Ελληνικά-αρχείο.txt',
        'Русский-файл.txt',
        'emoji-🎉🚀🐛-file.txt',
        'combining-é-vs-é.txt',
        'ligature-ﬁle-ﬀ.txt',
        ('very-long-' + ('name' * 55) + '.txt'),
        # 27 repetitions, not more. NTFS allows 255 UTF-16 units so 80 would fit here, but
        # Linux allows 255 *bytes* and each of these characters is three of them — at 80 the
        # Unix twin cannot create the file, and the tree stops being idempotent there. Still
        # 81 characters of wide glyphs, which is what the text layout is being stressed with.
        ('日本語' * 27 + '.txt'),
        'spaces   and   runs.txt',
        # Doubled quote is PowerShell's escape inside a single-quoted string. A literal
        # '"' is deliberately absent: it is a legal Linux filename character but illegal
        # on Windows, so it belongs in a platform-specific tree, not this one.
        'apostrophe''s-file.txt',
        'ümlaut-Ähnlich-Öl.txt'
    )

    if (Test-AlreadyBuilt -Path $path -Expected $names.Count) { return }
    [System.IO.Directory]::CreateDirectory($path) | Out-Null

    foreach ($name in $names) {
        try {
            # \\?\ again: the deliberately long names here push the full path past
            # MAX_PATH, and skipping them would drop the text-layout stress cases that
            # are the whole reason this tree exists.
            [System.IO.File]::WriteAllBytes("\\?\$path\$name", @())
        }
        catch {
            Write-Warning "  skipped '$name': $($_.Exception.Message)"
        }
    }

    Write-Host ("  unicode          created ({0} entries)" -f $names.Count)
}

function New-MixedSizesTree {
    $path = Join-Path $Root 'mixed-sizes'
    $sizes = @(0, 1, 512, 1023, 1024, 1025, 999999, 1048576, 10485760, 1073741824, 4294967296)

    if (Test-AlreadyBuilt -Path $path -Expected $sizes.Count) { return }
    [System.IO.Directory]::CreateDirectory($path) | Out-Null

    foreach ($size in $sizes) {
        # SetLength on an empty file gives a sparse file on NTFS and most Linux
        # filesystems, so a 4 GiB entry costs nothing but still formats and sorts as one.
        $file = [System.IO.File]::Create((Join-Path $path ("size-{0}.bin" -f $size)))
        try { $file.SetLength($size) } finally { $file.Dispose() }
    }

    Write-Host ("  mixed-sizes      created ({0} entries)" -f $sizes.Count)
}

function New-BrokenLinksTree {
    $path = Join-Path $Root 'broken-links'
    if (Test-Path $path) {
        Write-Host "  broken-links     up to date"
        return
    }

    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $path 'real.txt'), @())

    try {
        # Needs Developer Mode or elevation on Windows. Not having it is normal, so warn
        # and carry on rather than failing the whole run.
        New-Item -ItemType SymbolicLink -Path (Join-Path $path 'dangling.txt') `
            -Target (Join-Path $path 'does-not-exist.txt') -ErrorAction Stop | Out-Null
        New-Item -ItemType SymbolicLink -Path (Join-Path $path 'dangling-dir') `
            -Target (Join-Path $path 'no-such-dir') -ErrorAction Stop | Out-Null
        Write-Host "  broken-links     created"
    }
    catch {
        Write-Warning "  broken-links: symlinks unavailable (enable Developer Mode). Tree has real.txt only."
    }
}

Write-Host "Benchmark trees under $Root"
[System.IO.Directory]::CreateDirectory($Root) | Out-Null

$trees = [ordered]@{
    'flat-10'     = { New-FlatTree -Name 'flat-10'   -Count 10 }
    'flat-1k'     = { New-FlatTree -Name 'flat-1k'   -Count 1000 }
    'flat-10k'    = { New-FlatTree -Name 'flat-10k'  -Count 10000 }
    'flat-100k'   = { New-FlatTree -Name 'flat-100k' -Count 100000 }
    'deep'        = { New-DeepTree }
    'unicode'     = { New-UnicodeTree }
    'mixed-sizes' = { New-MixedSizesTree }
    'broken-links' = { New-BrokenLinksTree }
}

foreach ($name in $trees.Keys) {
    if ($Only -and $Only -ne $name) { continue }
    & $trees[$name]
}

Write-Host "Done."
