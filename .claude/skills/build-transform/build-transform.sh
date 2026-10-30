#!/usr/bin/env bash
#
# Works out the web.config transform for a Rock update package: finds every web.config version a site could have,
# reuses the newest earlier xdt in Artifacts that brings each one to the target's, or drafts a new one. Also lists other
# config and XML files that changed, which may need a transform of their own. Writes only to the output folder.
#
# Usage:
#   build-transform.sh -r <rock repo> -m <minimum version tag> -t <target tag> -v <target version>
#                      [-s <sibling release tags>] [-x <exclude tag regex>] [-a <Artifacts folder>] [-o <output dir>]
#
#   -r  Rock git checkout with all release tags fetched.
#   -m  Required (minimum) version tag, e.g. 18.2.4.
#   -t  Release tag of the target, e.g. 19.8.1-alpha.
#   -v  Target version, e.g. 19.8.
#   -s  Space-separated release tags of other majors shipping at the same time, e.g. "18.8.1-alpha".
#   -x  Regex of release tags to skip (pre-release builds no site runs).
#   -a  Artifacts folder. Defaults to Artifacts in the package builder repo.
#   -o  Output folder. Defaults to ./transform-output.
#
# Exit code is 1 when no transform passes the check.

set -euo pipefail

skill=$(cd "$(dirname "$0")" && pwd)
repo=""; minimum=""; target=""; targetVersion=""; siblings=""; exclude='^$'; artifacts="$skill/../../../Artifacts"
output="./transform-output"

while getopts "r:m:t:v:s:x:a:o:" option; do
    case $option in
        r) repo=$OPTARG ;;
        m) minimum=$OPTARG ;;
        t) target=$OPTARG ;;
        v) targetVersion=$OPTARG ;;
        s) siblings=$OPTARG ;;
        x) exclude=$OPTARG ;;
        a) artifacts=$OPTARG ;;
        o) output=$OPTARG ;;
        *) sed -n '3,19p' "$0"; exit 2 ;;
    esac
done

if [[ -z $repo || -z $minimum || -z $target || -z $targetVersion ]]; then
    sed -n '3,19p' "$0"
    exit 2
fi

mkdir -p "$output"
rm -f "$output/web.config.rock.xdt"

# Succeeds when version $1 sorts at or below version $2.
versionAtOrBelow() {
    [[ $(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1) == "$1" ]]
}

check() {
    pwsh -NoProfile -File "$skill/check-transform.ps1" -Repo "$repo" -Target "$target" -Refs "$refs" "$@"
}

releases=$(git -C "$repo" tag -l | grep -E '^[0-9]+(\.[0-9]+)+$' | grep -v -E "$exclude" | sort -V \
    | while read -r tag; do
        if versionAtOrBelow "$minimum" "$tag" && ! versionAtOrBelow "$targetVersion" "$tag"; then
            echo "$tag"
        fi
    done)

targetHash=$(git -C "$repo" rev-parse --short "$target:RockWeb/web.config")

# 1. web.config versions.
echo "web.config versions sites can have (target $target is $targetHash):"
: > "$output/versions.txt"
for release in $releases $siblings; do
    echo "$(git -C "$repo" rev-parse --short "$release:RockWeb/web.config") $release" >> "$output/versions.txt"
done
awk '{ releases[$1] = releases[$1] (releases[$1] ? ", " : "") $2; if (!($1 in first)) { first[$1] = $2; order[++n] = $1 } }
     END { for (i = 1; i <= n; i++) print "  " order[i] ": " releases[order[i]] }' "$output/versions.txt" \
    | sed "s/^  $targetHash: /  $targetHash (same as target): /"

# One release per distinct web.config. Those already matching the target are checked too: the xdt must do no harm there.
refs=$(awk '!seen[$1]++ { print $2 }' "$output/versions.txt" | tr '\n' ' ' | sed 's/ $//')
needed=$(awk -v t="$targetHash" '$1 != t' "$output/versions.txt")
echo

# 2. Other config and XML files that changed. A shipped copy would overwrite a site's edits to it.
echo "Other config and XML files that changed since a release sites can have:"
for release in $releases $siblings; do
    git -C "$repo" diff --name-only "$release" "$target" -- RockWeb
done | grep -i -E '\.(config|xml)$' | grep -v -i -E '^RockWeb/(web\.config|packages\.config)$|^RockWeb/Bin/' \
    | sort -u | sed 's/^/  /' > "$output/other-configs.txt" || true
if [[ -s $output/other-configs.txt ]]; then
    cat "$output/other-configs.txt"
else
    echo "  (none)"
fi
echo

if [[ -z $needed ]]; then
    echo "Every release already has the target's web.config. No web.config transform is needed."
    exit 0
fi

# 3. Reuse the newest earlier xdt that passes.
echo "Checking earlier transforms in Artifacts, newest first, against: $refs"
chosen=""
seen=""
while read -r folder; do
    xdt="$artifacts/$folder/web.config.rock.xdt"
    hash=$(git hash-object --no-filters "$xdt")
    if [[ $seen == *"$hash"* ]]; then
        continue
    fi
    seen="$seen $hash"
    if check -Xdt "$xdt" > "$output/check-$folder.txt"; then
        chosen=$folder
        echo "  PASS Artifacts/$folder"
        sed 's/^/       /' "$output/check-$folder.txt"
        break
    fi
    echo "  fail Artifacts/$folder"
    if [[ $(echo "$seen" | wc -w) -ge 5 ]]; then
        break
    fi
done < <(ls "$artifacts" | while read -r folder; do
        if [[ -f $artifacts/$folder/web.config.rock.xdt ]] && versionAtOrBelow "$folder" "$targetVersion"; then
            echo "$folder"
        fi
    done | sort -rV)

if [[ -n $chosen ]]; then
    cp "$artifacts/$chosen/web.config.rock.xdt" "$output/web.config.rock.xdt"
    same=$(ls "$artifacts" | while read -r folder; do
        xdt="$artifacts/$folder/web.config.rock.xdt"
        if [[ -f $xdt ]] && cmp -s "$xdt" "$output/web.config.rock.xdt"; then echo "$folder"; fi
    done | sort -V | tr '\n' ' ')
    echo
    echo "Use Artifacts/$chosen/web.config.rock.xdt (identical copies: $same)."
    echo "Copied to $output/web.config.rock.xdt"
    exit 0
fi

# 4. Nothing fits: draft one.
echo
echo "No earlier transform fits. Drafting a new one:"
pwsh -NoProfile -File "$skill/new-transform.ps1" -Repo "$repo" -Target "$target" -Refs "$refs" \
    -Out "$output/web.config.rock.xdt" | sed 's/^/  /'
if check -Xdt "$output/web.config.rock.xdt" | sed 's/^/  /'; then
    echo
    echo "The draft passes. Review it before using it: $output/web.config.rock.xdt"
    exit 0
fi
echo
echo "The draft still fails. Add the sections listed above by hand, then check it again."
exit 1
