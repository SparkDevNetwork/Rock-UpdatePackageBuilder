#!/usr/bin/env bash
#
# Builds the delete file list for a Rock update package: merges the package's own list with the delete lists of the
# earlier packages in range, removes duplicates, drops every line that would do harm, and explains each drop.
# Read-only toward the packages: it writes only to the output folder. Run from Git Bash.
#
# Usage:
#   build-delete-list.sh -r <rock repo> -m <minimum version tag> -t <target ref> -v <target version>
#                        -p <new rockpkg> [-w <compiled target RockWeb>] [-d <packages folder>]
#                        [-e <earlier versions>] [-a <lines to add>] [-c <rules file>] [-o <output dir>]
#
#   -r  Rock git checkout with all release tags fetched.
#   -m  Required (minimum) version tag the package was built from, e.g. 18.2.4.
#   -t  Release tag of the target, e.g. 19.8.1-alpha.
#   -v  Target version, e.g. 19.8.
#   -p  The new package as the builder made it.
#   -w  Compiled RockWeb folder of the target (strongly recommended).
#   -d  Folder holding earlier packages named <major>.<minor>.rockpkg. Defaults to the new package's folder.
#   -e  Space-separated earlier versions to merge, overriding the automatic choice, e.g. "18.3 18.4 19.1".
#   -a  File of extra lines to add for this package only.
#   -c  Checklist rules file. Defaults to checklist-rules.txt next to this script.
#   -o  Output folder. Defaults to ./delete-list-output.

set -euo pipefail

repo=""; minimum=""; target=""; targetVersion=""; package=""; rockWeb=""; packagesDir=""; earlierOverride=""
additions=""; rules="$(dirname "$0")/checklist-rules.txt"; output="./delete-list-output"

while getopts "r:m:t:v:p:w:d:e:a:c:o:" option; do
    case $option in
        r) repo=$OPTARG ;;
        m) minimum=$OPTARG ;;
        t) target=$OPTARG ;;
        v) targetVersion=$OPTARG ;;
        p) package=$OPTARG ;;
        w) rockWeb=$OPTARG ;;
        d) packagesDir=$OPTARG ;;
        e) earlierOverride=$OPTARG ;;
        a) additions=$OPTARG ;;
        c) rules=$OPTARG ;;
        o) output=$OPTARG ;;
        *) sed -n '3,26p' "$0"; exit 2 ;;
    esac
done

if [[ -z $repo || -z $minimum || -z $target || -z $targetVersion || -z $package ]]; then
    sed -n '3,26p' "$0"
    exit 2
fi

packagesDir=${packagesDir:-$(dirname "$package")}
work="$output/work"
mkdir -p "$work"
export LC_ALL=C

# Turns raw list lines into "key<TAB>line": key is lowercase with forward slashes, line keeps the original spelling.
normalize() {
    tr -d '\r' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' | grep -v '^$' \
        | awk '{ key = tolower($0); gsub(/\\/, "/", key); print key "\t" $0 }' || true
}

keysOnly() {
    tr -d '\r' | tr '\\' '/' | tr '[:upper:]' '[:lower:]' | grep -v '^$' | sort -u || true
}

deleteList() {
    unzip -p "$1" install/deletefile.lst 2>/dev/null || true
}

contentFiles() {
    unzip -Z1 "$1" | grep '^content/' | grep -v '/$' | sed 's#^content/#RockWeb/#' | keysOnly
}

# Succeeds when a ref's source references a Bin assembly: a NuGet .refresh file, a NuGet package id, a HintPath, or a
# project that builds it.
referencedIn() {
    git -C "$repo" grep -q -i -F -e "$2.dll" -e "id=\"$2\"" -e "$2.csproj" "$1" -- \
        'RockWeb/Bin/*.refresh' '*.csproj' '*packages.config' '*.sln'
}

# Prints major.minor of a version string.
majorMinor() {
    echo "$1" | cut -d. -f1-2
}

# Succeeds when version $1 sorts strictly below version $2.
versionBelow() {
    [[ $1 != "$2" && $(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1) == "$1" ]]
}

minimumMajorMinor=$(majorMinor "$minimum")
targetMajorMinor=$(majorMinor "$targetVersion")
targetMajor=$(echo "$targetVersion" | cut -d. -f1)

# Earlier packages: every <major>.<minor>.rockpkg above the minimum's release and below the target.
if [[ -n $earlierOverride ]]; then
    earlier=$earlierOverride
else
    earlier=$(ls "$packagesDir" | sed -n -E 's/^([0-9]+\.[0-9]+)\.rockpkg$/\1/p' | sort -V | while read -r version; do
        if versionBelow "$minimumMajorMinor" "$version" && versionBelow "$version" "$targetMajorMinor"; then
            echo "$version"
        fi
    done)
fi

# Collect every line with its source.
deleteList "$package" | normalize | sed 's/$/\tthis package/' > "$work/lines.tsv"
: > "$work/precedent.txt"
: > "$work/reshipped.txt"

for version in $earlier; do
    earlierPackage="$packagesDir/$version.rockpkg"
    if [[ ! -f $earlierPackage ]]; then
        echo "Missing earlier package: $earlierPackage" >&2
        exit 2
    fi
    deleteList "$earlierPackage" | normalize | sed "s/\$/\t$version/" >> "$work/lines.tsv"
    deleteList "$earlierPackage" | keysOnly | sed "s/\$/ $version/" >> "$work/precedent.txt"
    if [[ $(echo "$version" | cut -d. -f1) == "$targetMajor" ]]; then
        contentFiles "$earlierPackage" | grep '^rockweb/bin/' | sed "s/\$/ $version/" >> "$work/reshipped.txt" || true
    fi
done

if [[ -n $additions ]]; then
    normalize < "$additions" | sed 's/$/\tadded by you/' >> "$work/lines.tsv"
fi

# Checklist rules: "add" lines for this major join the merge; "never" patterns are checked first below.
grep -i -E "^add[[:space:]]+$targetMajor[[:space:]]" "$rules" | awk '{print $3}' | normalize \
    | sed "s/\$/\tchecklist ($targetMajor.x)/" >> "$work/lines.tsv" || true
grep -i -E '^never[[:space:]]' "$rules" | awk '{print $2}' | normalize | cut -f1 > "$work/never.txt" || true

# Reference sets.
contentFiles "$package" > "$work/shipped.txt"
git -C "$repo" ls-tree -r --name-only "$target" -- RockWeb | keysOnly > "$work/tracked.txt"

if [[ -n $rockWeb ]]; then
    (cd "$rockWeb/.." && find "$(basename "$rockWeb")" -type f) | sed 's#^[^/]*/#RockWeb/#' | keysOnly > "$work/built.txt"
else
    : > "$work/built.txt"
fi

# Block files the chop job deletes: BlockTypes in "Chop" migrations added after the minimum.
git -C "$repo" diff --name-only --diff-filter=A "$minimum" "$target" -- Rock.Migrations | grep '\.cs$' \
    | while IFS= read -r migration; do
        git -C "$repo" show "$target:$migration" | tr -d '\r' | awk '
            /ReplaceWebformsWithObsidianBlockMigration\(/ { inCall = 1; commented = ($0 ~ /^[ \t]*\/\//); strategy = ""; count = 0 }
            inCall {
                if ($0 ~ /migrationStrategy/) { inDictionary = 0; if ($0 ~ /"Chop"/) strategy = "Chop" }
                else if ($0 ~ /blockTypeReplacements/) inDictionary = 1
                else if (inDictionary && $0 !~ /^[ \t]*\/\// && match($0, /"[0-9A-Fa-f-]{36}"/)) guids[++count] = toupper(substr($0, RSTART + 1, 36))
                if ($0 ~ /jobGuid/ || $0 ~ /\);/) {
                    if (strategy == "Chop" && !commented) for (i = 1; i <= count; i++) print guids[i]
                    inCall = 0; inDictionary = 0
                }
            }'
    done | sort -u > "$work/chop-guids.txt"

createDatabase=$(git -C "$repo" ls-tree -r --name-only "$target" -- Rock.Migrations | grep -i 'CreateDatabase\.sql$' | head -1)
git -C "$repo" show "$target:$createDatabase" | iconv -f UTF-16 -t UTF-8 2>/dev/null > "$work/create-database.sql" || true

: > "$work/chop.txt"
while read -r guid; do
    {
        git -C "$repo" grep -h -i "$guid" "$target" -- Rock.Migrations || true
        grep -i "$guid" "$work/create-database.sql" || true
    } | { grep -o -i -E '~/Blocks/[^"'"'"']+\.ascx' || true; } | sed 's#^~/#RockWeb/#' | keysOnly \
        | while read -r block; do printf '%s\n%s.cs\n' "$block" "$block"; done >> "$work/chop.txt"
done < "$work/chop-guids.txt"
sort -u -o "$work/chop.txt" "$work/chop.txt"

# Decide every distinct line. The first spelling seen wins; this package's own spelling comes first.
awk -F'\t' '!seen[$1]++' "$work/lines.tsv" > "$work/unique.tsv"

: > "$work/kept.tsv"
: > "$work/dropped.tsv"

while IFS=$'\t' read -r key line source; do
    reason=""
    precedent=$(grep -F "$key " "$work/precedent.txt" | awk '{print $2}' | sort -uV | tr '\n' ' ' | sed 's/ $//' || true)

    while read -r pattern; do
        if [[ $key == $pattern ]]; then
            reason="The Asana checklist says this line must never be in the list."
        fi
    done < "$work/never.txt"

    if [[ -n $reason ]]; then
        :
    elif grep -qxF "$key" "$work/shipped.txt"; then
        reason="This package ships the file. The installer copies files first and deletes after, so it would remove the file it just installed."
    elif grep -qxF "$key" "$work/built.txt"; then
        reason="The compiled $targetVersion RockWeb has this file."
    elif grep -qxF "$key" "$work/tracked.txt"; then
        reason="$targetVersion source tracks this file."
    elif grep -qxF "$key" "$work/chop.txt"; then
        reason="The chop job deletes this block file after the update runs."
    elif [[ $key == rockweb/bin/* && -z $precedent && $source != "added by you" && $source != checklist* ]]; then
        # A Bin file no earlier package deleted. Keep it only when Rock really removed it: the minimum's source references
        # it, the target's doesn't, and no earlier package of this major shipped it again.
        name=${key#rockweb/bin/}
        base=$(echo "$name" | sed -E 's/\.(dll\.refresh|dll|xml|pdb)$//')
        reshipped=$(grep -F "rockweb/bin/$base.dll " "$work/reshipped.txt" | awk '{print $2}' | sort -uV | tr '\n' ' ' | sed 's/ $//' || true)
        if ! referencedIn "$minimum" "$base"; then
            reason="No sign Rock ever shipped it: nothing in $minimum references it. It was likely a stray file in the build folder."
        elif [[ -n $reshipped ]]; then
            reason="Earlier $targetMajor.x packages shipped it again ($reshipped). Sites have it, so a patch shouldn't start removing it."
        elif referencedIn "$target" "$base"; then
            reason="$targetVersion source still references it."
        fi
    fi

    if [[ -n $reason ]]; then
        printf '%s\t%s\t%s\n' "$line" "$source" "$reason" >> "$work/dropped.tsv"
    else
        printf '%s\t%s\t%s\n' "$line" "$source" "$precedent" >> "$work/kept.tsv"
    fi
done < "$work/unique.tsv"

# Results.
cut -f1 "$work/kept.tsv" | sort -f | sed 's/$/\r/' > "$output/deletefile.lst"

total=$(wc -l < "$work/lines.tsv")
distinct=$(wc -l < "$work/unique.tsv")
kept=$(wc -l < "$work/kept.tsv")
dropped=$(wc -l < "$work/dropped.tsv")

{
    echo "Delete list for $targetVersion (minimum $minimum, source $target)"
    echo
    echo "Lists merged: this package, then $(echo $earlier | sed 's/ /, /g')"
    echo "$total lines in, $((total - distinct)) duplicates removed, $distinct distinct, $dropped dropped, $kept kept."
    echo
    echo "DROPPED (reason, then the line and where it came from)"
    if [[ -s $work/dropped.tsv ]]; then
        sort -t$'\t' -k3,3 -k1,1f "$work/dropped.tsv" | awk -F'\t' '
            $3 != last { print ""; print "  " $3; last = $3 }
            { print "    " $1 "   [" $2 "]" }'
    else
        echo "  (none)"
    fi
    echo
    echo "REVIEW (kept, but worth a look)"
    echo "  Bin dlls, with the earlier packages that already deleted them:"
    awk -F'\t' 'tolower($1) ~ /^rockweb\\bin\\.*\.dll$/ {
        why = $3 != "" ? $3 : ($2 ~ /^checklist|^added/ ? $2 : "this package only: Rock removed it")
        print "    " $1 "   [" why "]" }' "$work/kept.tsv"
    echo "  System.* dlls (the checklist says none should be here; these are real removals earlier packages already delete):"
    awk -F'\t' 'tolower($1) ~ /^rockweb\\bin\\system\..*\.dll$/ { print "    " $1 }' "$work/kept.tsv"
    echo "  Override files:"
    awk -F'\t' 'tolower($1) ~ /override/ { print "    " $1 }' "$work/kept.tsv"
} > "$output/report.txt"

echo "Kept $kept, dropped $dropped (from $distinct distinct lines)."
echo "List:   $output/deletefile.lst"
echo "Report: $output/report.txt"
