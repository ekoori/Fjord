#!/usr/bin/env bash
#
# Generates the directory trees Fjord is benchmarked against. The twin of
# make-benchmark-tree.ps1, and deliberately produces the same trees with the same names and
# counts, so a number measured on Linux means the same thing as one measured on Windows.
#
# Idempotent: a tree whose entry count already matches is left alone, so re-running is
# effectively free. Files are zero-byte unless the tree is specifically about sizes, so even
# the 100,000-entry tree costs no meaningful disk.
#
# Usage:
#   scripts/make-benchmark-tree.sh [--root DIR] [--only NAME]

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/bench"
only=""

while [ $# -gt 0 ]; do
    case "$1" in
        --root) root="$2"; shift 2 ;;
        --only) only="$2"; shift 2 ;;
        -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

mkdir -p "$root"
root="$(cd "$root" && pwd)"

extensions=('.txt' '.md' '.cs' '.json' '.png' '.log' '.tar.gz' '')

# Returns 0 when the tree is already the right size, and clears it out when it is not.
already_built() {
    local path="$1" expected="$2" actual
    local name; name="$(basename "$path")"

    [ -d "$path" ] || return 1

    actual="$(find "$path" -mindepth 1 -maxdepth 1 | wc -l)"
    if [ "$actual" -eq "$expected" ]; then
        printf '  %-16s up to date (%s entries)\n' "$name" "$actual"
        return 0
    fi

    printf '  %-16s rebuilding (%s -> %s)\n' "$name" "$actual" "$expected"
    rm -rf "$path"
    return 1
}

make_flat() {
    local name="$1" count="$2"
    local path="$root/$name"

    already_built "$path" "$count" && return 0

    mkdir -p "$path"
    local dirs=$(( count / 20 ))
    [ "$dirs" -lt 1 ] && dirs=1

    # One awk process writing a list of names, then xargs touching them in batches. A shell
    # loop calling touch 100,000 times is a process spawn per entry and takes minutes; this
    # takes seconds.
    awk -v count="$count" -v dirs="$dirs" -v exts="${extensions[*]}" '
        BEGIN {
            n = split(exts, ext, " ");

            # Same seed as the PowerShell twin uses, for the same reason: the mix of name
            # lengths and extensions feeds the text-layout cache, and it should not change
            # between runs or between platforms.
            srand(20260803);

            for (i = 0; i < count; i++) {
                if (i < dirs) {
                    printf("d%06d\n", i);
                    continue;
                }

                # awk splits a trailing empty field away, so index n+1 is the "no extension"
                # case that the extension list ends with.
                e = int(rand() * (n + 1)) + 1;
                filler = "";
                for (j = int(rand() * 24); j > 0; j--) filler = filler "x";
                printf("f%06d%s%s\n", i, filler, (e > n ? "" : ext[e]));
            }
        }' > "$path/.names"

    (cd "$path" && grep '^d' .names | sed 's/^d/dir/' | xargs -r mkdir -p)
    (cd "$path" && grep '^f' .names | sed 's/^f/file/' | xargs -r touch)
    rm -f "$path/.names"

    printf '  %-16s created (%s entries)\n' "$name" "$count"
}

make_deep() {
    local path="$root/deep"

    if [ -d "$path" ]; then
        printf '  %-16s up to date\n' deep
        return 0
    fi

    # 10 levels of 10. The long names are what push the deepest leaves past 260 characters,
    # which is the whole point of the tree on Windows; on Linux they simply exercise a long
    # path. PATH_MAX is 4096, so nothing here needs a special prefix.
    local current="$path"
    for level in $(seq 0 9); do
        current="$current/level$level-a-reasonably-long-directory-name"
        mkdir -p "$current"
        for i in $(seq 0 8); do
            touch "$current/leaf$i.txt"
        done
    done

    printf '  %-16s created (10 levels, deepest path %s chars)\n' deep "${#current}"
}

make_unicode() {
    local path="$root/unicode"
    local names=(
        '日本語のファイル名.txt'
        '中文文件名称测试.md'
        'مرحبا-بالعالم.txt'
        'עברית-קובץ.txt'
        'Ελληνικά-αρχείο.txt'
        'Русский-файл.txt'
        'emoji-🎉🚀🐛-file.txt'
        'combining-é-vs-é.txt'
        'ligature-ﬁle-ﬀ.txt'
        'spaces   and   runs.txt'
        "apostrophe's-file.txt"
        'ümlaut-Ähnlich-Öl.txt'
    )

    # Two generated names too long to write literally, matching the twin. 27 repetitions of
    # the CJK one, not more: NAME_MAX is 255 bytes and each of those characters is three of
    # them, so a longer name cannot be created here and the tree would rebuild every run.
    local long_ascii="very-long-$(printf 'name%.0s' $(seq 55)).txt"
    local long_cjk="$(printf '日本語%.0s' $(seq 27)).txt"
    names+=("$long_ascii" "$long_cjk")

    already_built "$path" "${#names[@]}" && return 0
    mkdir -p "$path"

    for name in "${names[@]}"; do
        # Still guarded: eCryptfs and some network mounts have a tighter limit than the
        # local filesystem, and losing one stress name must not stop the run.
        touch "$path/$name" 2>/dev/null || echo "  skipped '$name' (name too long for this filesystem)" >&2
    done

    printf '  %-16s created (%s entries)\n' unicode "${#names[@]}"
}

make_mixed_sizes() {
    local path="$root/mixed-sizes"
    local sizes=(0 1 512 1023 1024 1025 999999 1048576 10485760 1073741824 4294967296)

    already_built "$path" "${#sizes[@]}" && return 0
    mkdir -p "$path"

    for size in "${sizes[@]}"; do
        # truncate makes a sparse file, so the 4 GiB entry costs nothing on disk but still
        # formats and sorts as one.
        truncate -s "$size" "$path/size-$size.bin"
    done

    printf '  %-16s created (%s entries)\n' mixed-sizes "${#sizes[@]}"
}

make_broken_links() {
    local path="$root/broken-links"

    if [ -d "$path" ]; then
        printf '  %-16s up to date\n' broken-links
        return 0
    fi

    mkdir -p "$path"
    touch "$path/real.txt"

    # No Developer Mode caveat here: symlinks are unprivileged on Linux and macOS, which is
    # why this tree is worth more on this side than on Windows.
    ln -s "$path/does-not-exist.txt" "$path/dangling.txt"
    ln -s "$path/no-such-dir" "$path/dangling-dir"

    printf '  %-16s created\n' broken-links
}

echo "Benchmark trees under $root"

run() {
    [ -z "$only" ] || [ "$only" = "$1" ] || return 0
    "$2" "${@:3}"
}

run flat-10      make_flat        flat-10   10
run flat-1k      make_flat        flat-1k   1000
run flat-10k     make_flat        flat-10k  10000
run flat-100k    make_flat        flat-100k 100000
run deep         make_deep
run unicode      make_unicode
run mixed-sizes  make_mixed_sizes
run broken-links make_broken_links

echo "Done."
