---
name: build-delete-list
description: Walk a packager through building the delete file list (install/deletefile.lst) for a Rock update package. Merges the builder's list with the earlier packages' lists, removes duplicates, applies version-specific checklist rules, drops harmful lines, explains every drop in plain English, and can put the result into the package safely. Use when someone is on the "Verify Delete File list" packaging step, asks which delete lists to combine, or asks why a line is or isn't in a package's delete list. Delete list only; the transform, content files and file checks are other steps.
---

# Build a package's delete file list

The installer copies the package's files first, then deletes every file in `install/deletefile.lst`. A wrong line either
leaves an old file behind or deletes a file the site needs. This skill builds the list, explains each choice, and can
put it into the package.

## Step 1: Confirm the inputs

Fill in every answer this conversation already establishes (versions, paths, decisions the packager made). Use the
defaults in brackets for the rest. Show all of them in one block, mark which ones you filled in, and ask the packager to
correct anything and say when to start. Ask only for answers you can't fill in.

1. **Target version and release tag**, e.g. `19.8` and `19.8.1-alpha` [the newest tag from
   `git -C <checkout> tag -l '<target>.*' --sort=-v:refname`].
2. **Required (minimum) version tag** the package was built from, e.g. `18.2.4`.
3. **The new package** [`D:\source\rock\_Apps\Rock-UpdatePackageBuilder\RockPackages\<target>.rockpkg`]. Its list can
   be the builder's own or one already merged by hand; both work.
4. **Rock checkout** with tags fetched [`D:\source\rock\Rock-packaging`].
5. **Compiled RockWeb of the target** [`<checkout>\RockWeb`]: a fresh Release build of the target. Without it,
   generated files (theme CSS, `styles-v2`) aren't checked.
6. **Earlier packages to merge** [every `<major>.<minor>.rockpkg` in the packages folder above the minimum's release and
   below the target; e.g. minimum 18.2.4, target 19.8: 18.3 to 18.8 and 19.1 to 19.7]. List them by name.
7. **Checklist rules** [`checklist-rules.txt` in this folder]. Show its `never` and `add` lines for the target's major
   and ask whether these rules are correct for the target's major version. If not, edit the file first.
8. **Put the result into the package?** [yes, after the packager sees the report].

## Step 2: Check before running

- `git -C <checkout> tag -l <minimum> <target tag>` prints both tags. If not, ask them to `git fetch --tags`.
- The compiled RockWeb's checkout has the target tag's files (`git -C <checkout> diff --quiet HEAD <target tag>`).

## Step 3: Run

```bash
bash .claude/skills/build-delete-list/build-delete-list.sh \
    -r /d/source/rock/Rock-packaging -m 18.2.4 -t 19.8.1-alpha -v 19.8 \
    -p RockPackages/19.8.rockpkg -w /d/source/rock/Rock-packaging/RockWeb \
    -o <scratchpad>/delete-list-19.8
```

Add `-e "18.3 18.4 ..."` if the packager changed the earlier packages, and `-a <file>` for one-off lines. It takes 2 to 3
minutes, reads the packages without changing them, and writes `deletefile.lst` (CRLF, sorted) and `report.txt`.

## Step 4: Explain the result

Read `report.txt` and tell the packager, briefly:

- The counts: lines in, duplicates removed, dropped, kept.
- Each drop reason with its count, one line each (the rules are below).
- Anything in REVIEW that looks wrong. Bin dlls with a vendor-style name (`org.*`, `com.*`, `tech.*`) are plugins; check
  "Settled decisions" before raising one.

The rules, checked in this order:

| Rule | Why |
|---|---|
| The checklist says `never` | Plugins that ship with Rock; deleting them removes them from every site. |
| The package ships the file | Deletes run after the copy, so the line would delete the file just installed. |
| The compiled target has the file | The target needs it, even when git doesn't track it (generated files). |
| Target source tracks the file | Same. |
| A chop migration added after the minimum deletes the block | The chop job deletes the `.ascx` and `.ascx.cs` itself (checklist IMPORTANT 2). Swap migrations and legacy-guid takeovers don't delete files, so those lines stay. |
| A Bin file no earlier package deleted, and the minimum's source never referenced it | A stray file in the builder's base folder (checklist IMPORTANT 1), e.g. `System.Security.Permissions.dll` in 19.8. Deleting it could break a plugin. |
| A Bin file no earlier package deleted, and an earlier package of the target's major shipped it | Sites already have it and run fine; a patch shouldn't start removing it (e.g. `System.IO.Packaging.dll` in 19.8). |
| A Bin file the target's source still references | The target needs it. |

Everything else is kept, including every line an earlier package already deleted and the checklist's `add` lines.

## Step 5: Put it into the package

If the packager said yes in step 1, run this once they've seen the report. Otherwise ask first.

```bash
pwsh -NoProfile -File .claude/skills/build-delete-list/apply-delete-list.ps1 \
    -Package RockPackages/19.8.rockpkg -List <output>/deletefile.lst -BackupFolder <output>
```

It refuses if another program has the package open, backs it up to the output folder, replaces only
`install/deletefile.lst`, and confirms every other file in the package is unchanged. If anything fails it restores the
backup. Report what it printed, including the backup path.

To do it by hand instead: open the `.rockpkg` in 7-Zip (it's a zip), replace `install\deletefile.lst` with
`<output>\deletefile.lst`, and close it so it saves.

## Settled decisions

- `RockWeb\Bin\org.ourcitychurch.ConnectionRequestDigest.dll` stays in 19.x lists:
  https://app.asana.com/1/20866866924293/project/1208364266328691/task/1213986400140431
- `tech.triumph.Lava.Helix.dll`/`.pdb` stay after 18.x: Helix became core Lava Applications in 18.0, and a leftover
  plugin dll conflicts with it.
- `._org.mywell.MyWellGateway.dll` is a macOS junk file, not the MyWell plugin; the checklist struck it from `never`.
- `System.Interactive.Async.dll` stays even though the checklist says no `System.*` dlls: Rock really removed it, and
  every package since 18.1 deletes it. The report lists kept `System.*` dlls under REVIEW.
- The Kiosk blocks and `Themes\KioskStark` stay in 18.x lists. The 18.1 kiosk job removes them too, so a 17.x site may
  see one-time "does not exist" errors before the job runs; they clear on their own.

## Limits

- Only `ReplaceWebformsWithObsidianBlockMigration` calls with `"Chop"` are detected as file-deleting jobs. Other jobs
  that delete files (like the 18.1 kiosk job) aren't.
- A removed theme's compiled CSS (e.g. `Themes\KioskStark\Styles\theme.css`) isn't in git; add it with `-a` the first
  time. Later packages carry it forward.
- Tested by rebuilding the shipped 18.8 (minimum 17.6.1) and 19.8 (minimum 18.2.4) lists exactly, 19.8 from the
  builder's raw list.
