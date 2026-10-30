---
name: build-transform
description: Walk a packager through the "Create the transform files" step for a Rock update package. Finds every web.config version a site allowed to install the package could have, reuses the newest earlier web.config.rock.xdt from Artifacts that brings each one to the target's (or drafts a new one), proves it in memory, lists other config or XML files that changed, and can save the result to Artifacts and the package safely. Use when someone is creating, choosing or checking a package's transform, or asks why a site's web.config is wrong after an update. Transform only; the delete list and file checks are other steps.
---

# Build a package's transform

Rock can't replace a site's `web.config`: churches edit it. Instead the package carries `content/web.config.rock.xdt`.
After installing, Rock applies it to the site's `web.config` and deletes it. The same xdt runs on every site that can
install the package, so it must turn every `web.config` version those sites have into the target's, and change nothing
on a site that already matches.

Rock's xdts change only binding redirects and compilation assemblies, each as a `RemoveAll` followed by an `Insert`.
That makes them safe to run on any version, so one xdt usually serves a whole major version.

## Step 1: Confirm the inputs

Fill in every answer this conversation already establishes (versions, tags, paths, the package). Use the defaults in
brackets for the rest. Show all of them in one block, mark which ones you filled in, and ask the packager to correct
anything and say when to start. Ask only for answers you can't fill in.

1. **Target version and release tag**, e.g. `19.8` and `19.8.1-alpha` [the newest tag from
   `git -C <checkout> tag -l '<target>.*' --sort=-v:refname`].
2. **Required (minimum) version tag**, e.g. `18.2.4`.
3. **Sibling release tags**: other majors' releases shipping at the same time [their newest tags, e.g. `18.8.1-alpha`
   for 19.8]. Sites on them can install this package too.
4. **Pre-release tags to skip** [`^(1\.17\.|18\.0\.)`: 17.0 pre-releases and 18.0, which never shipped]. Tags ending
   in `-alpha`, `-beta` or `-pre` are skipped automatically. Ask before skipping anything else.
5. **Rock checkout** with tags fetched [`D:\source\rock\Rock-packaging`].
6. **The package** [`D:\source\rock\_Apps\Rock-UpdatePackageBuilder\RockPackages\<target>.rockpkg`].
7. **Artifacts folder** for this release [`Artifacts\<target version>`, e.g. `Artifacts\19.8`; the checklist may name it
   with the patch number].
8. **Save the result to Artifacts and the package?** [yes, after the packager sees the result].

Check before running: `git -C <checkout> tag -l <minimum> <target tag> <sibling tags>` prints every tag; if not, ask
them to `git fetch --tags`.

## Step 2: Run

```bash
bash .claude/skills/build-transform/build-transform.sh \
    -r /d/source/rock/Rock-packaging -m 18.2.4 -t 19.8.1-alpha -v 19.8 -s 18.8.1-alpha \
    -x '^(1\.17\.|18\.0\.)' -o <scratchpad>/transform-19.8
```

It takes under a minute and changes nothing outside the output folder. It:

1. Groups the releases by `web.config` version (one line per version, with the releases that have it).
2. Lists other `.config` and `.xml` files under RockWeb that changed. `web.config`, `packages.config` and `Bin` are left
   out.
3. Checks the xdts in `Artifacts`, newest first, against one release per `web.config` version, including a version that
   already matches the target. It uses the first that passes and copies it to the output folder.
4. If none passes, drafts a new xdt (`new-transform.ps1`) and checks that.

A check passes when, for each version, the result has exactly the target's binding redirects and every other section
matches. Element order doesn't matter.

## Step 3: Explain the result

Tell the packager, briefly:

- How many `web.config` versions sites can have, and which releases have each.
- Which xdt was chosen and which Artifacts folders hold identical copies, or that a draft was made.
- Any other changed config or XML files. For each, ask whether sites edit it. If they do, it needs its own
  `<file>.rock.xdt` instead of shipping the file. This skill doesn't draft those.
- If every release already has the target's `web.config`, no transform is needed. Make sure the package doesn't carry
  one.

A draft needs a person to read it before it ships. If the draft still fails, the check lists the sections that differ;
add those to the xdt by hand, then check again:

```bash
pwsh -NoProfile -File .claude/skills/build-transform/check-transform.ps1 -Repo <checkout> -Target <target tag> \
    -Refs "<one release per version>" -Xdt <output>/web.config.rock.xdt
```

## Step 4: Save it

If the packager said yes in step 1, run this once they've seen the result. Otherwise ask first.

```bash
pwsh -NoProfile -File .claude/skills/build-transform/apply-transform.ps1 -Package RockPackages/19.8.rockpkg \
    -Xdt <output>/web.config.rock.xdt -ArtifactsFolder Artifacts/19.8 -BackupFolder <output>
```

It saves the xdt to the Artifacts folder, keeping a backup of a different one already there. It then puts the xdt into
the package as `content/web.config.rock.xdt`: it refuses if another program has the package open, backs the package
up, changes only that file, and confirms every other file is unchanged, restoring the backup if anything fails. Report
what it printed, including the backup paths.

To check a package's xdt later, run the check with `-Package <rockpkg>` in place of `-Xdt`.

## Limits

- Only `web.config` is checked automatically. Other config files are listed for the packager to judge.
- The check uses each release's stock `web.config`. A site's own edits aren't modelled; `RemoveAll` + `Insert` keeps
  them safe for the entries the xdt touches.
- Drafts cover binding redirects and compilation assemblies. Changes to other sections are listed by the check and
  added by hand.

## Known history

- 18.x: one xdt (`Artifacts\18.0`, `18.1`, `18.5`, `18.8`) serves 17.6.1 to 18.8.
- 19.x: one xdt (`Artifacts\19.0`, `19.1`, `19.5`, `19.8`) serves 18.2.4 to 19.8.
