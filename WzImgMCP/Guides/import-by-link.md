# Import by link: getting files into your tree

The editor runs in a closed box and cannot take a big file through the chat. Instead you give it a **link** and it downloads the file itself. Nothing reaches players from this: the files land in your own tree, and then you pack, test and publish on the WZ Editing page as usual.

## Which route do I take?
| You have | Use | What happens |
|---|---|---|
| A few PNG sprites, or a zip of PNGs | `kind="files"` | The PNGs are stored in a waiting area. The tree is not touched. You place each one with import_png. |
| A whole `<Category>.wz` (for example `Skill.wz`) | `kind="wz"` | The file is unpacked and compared with your tree. You see the differences and confirm. |

**Big categories** (Character, Map, Mob, Npc) are bigger than the download cap ({{maxDownloadMb}} MB here). Do not try to upload them whole. Either send the sprites with `kind="files"`, or make a small `<Category>.wz` with the same category name that holds **only the images you changed**. That is safe: an import never deletes images, so everything not in your small file stays as it is.

## Prepare the link
1. Put the file on Google Drive. It must be a **file**, not a folder.
2. Share it: General access = **Anyone with the link**.
3. Copy the file's link (it looks like `https://drive.google.com/file/d/<id>/view`).
4. Only secure links to these sites are accepted: {{allowedHosts}}. A link with a user name, an IP address or another port is refused. Drive's "cannot scan for viruses" page for big files is handled for you.
5. For a `.wz`: name it `<Category>.wz` (for example `Skill.wz`), key **GMS**. If it has another name, pass `category`.
6. Optional: pass `expectedSha256` (64 hex characters) and the import is refused if the download is not exactly that file.

## Route A: a whole .wz (kind "wz")
1. The editor must have its tree open. If a tool says "No data source initialized", call init_data_source first.
2. `import_fetch(url, kind="wz", expectedSha256?, category?)` returns an **id** at once and downloads in the background.
3. `import_status(id)` until the status is `ready` (it goes downloading, extracting, ready). If it says `failed`, read the error (see "When it fails").
4. `import_preview(id)` is a dry run. It lists images that are **new**, **changed** (with the properties added, removed and changed), **unchanged**, **unreadable**, and the ones that are only in your tree and are left alone. It changes nothing and gives a **confirmation token**. If any image is unreadable you get no token unless you pass `allowUnreadable=true`; then those images are skipped.
5. **Show the preview to the person and wait for a yes.**
6. `import_apply(id, token)` copies only the new and changed `.img` files into the tree, after backing up what it replaces. The token works once. It refuses if a file in the tree changed since the preview, or if an affected image has unsaved edits (call save_image or discard_changes for it, then preview again if needed).
7. Not happy? `import_undo(id)` (see "Undo" below).
8. Then go to the WZ Editing page: Refresh, Pack, test, tick tested, Publish.

## Route B: sprites (kind "files")
1. `import_fetch(url, kind="files")` with a link to one PNG or one **flat** zip of PNGs (no folders inside). Do not pass `category`.
2. `import_status(id)` until `ready`. It lists every staged file with its **path**, size and width x height.
3. Place each sprite with `import_png(category, image, parentPath, name, pngPath)`:
   - `category` and `image`: where it goes (the image must already exist).
   - `parentPath`: the property that will hold the new canvas (empty means the top of the image). It must exist.
   - `name`: the name of the new canvas. Use a name that is not there yet.
   - `pngPath`: the staged path from import_status, exactly as shown.
   - The pixel format is lossless unless you ask for another one.
4. `set_canvas_origin(category, image, path, x, y)` for each canvas that needs an origin (or pass originX and originY to import_png).
5. `validate_image(category, image)`, then `save_image(category, image)`.
6. When you are finished, `import_undo(id)` on the files import to delete the staged files and free its place.
7. Then the WZ Editing page: Refresh, Pack, test, tick tested, Publish.

**First time? Try 3 small PNGs** in a harmless image of your own tree, save, check with view_canvas, and only then do a big batch.

## Limits (live values of this editor)
- Largest download: {{maxDownloadMb}} MB. A download that gets no data for {{stallSeconds}} seconds is stopped; the whole download may take at most {{timeoutSeconds}} seconds.
- Zip of PNGs: at most {{maxFiles}} files, at most {{maxUnpackedMb}} MB once unpacked, and not packed tighter than {{maxRatio}} to 1 (a tighter zip is refused as a zip bomb).
- Every PNG: at most {{pngMaxSide}} x {{pngMaxSide}} pixels and {{pngMaxMb}} MiB. File names: letters, digits, space and `_ ( ) + - .`, starting with a letter or digit, ending in `.png`.
- At most {{maxStaged}} imports waiting at once. One download at a time.
- Waiting imports are deleted after {{retentionDays}} days (for an applied one, counted from the apply).
- A `.wz` must use the **GMS** key, must not be List.wz or Sound.wz, and its category must already exist in your tree.

## What "already used" means
The editor does **not** remember which sprites you placed from an import. import_png is a normal edit and keeps no link to the import.
- Every fetch has its own **id**. Keep a note of which id you used for what.
- Use **one zip per batch** with clear file names (for example `skill-1001-icon.png`), so you can tell what each file is.
- Staged files are deleted after {{retentionDays}} days. After that, fetch the link again.

## Undo
- **kind "wz"**: `import_undo(id)` puts the backups back and removes the images the import added. A file you edited after the import is **skipped and listed**, and stays as it is. On an import never applied it just deletes the staged files. On one that is still downloading it cancels it.
- **kind "files"**: import_undo(id) only deletes the staged files. It does **not** touch your tree, because the fetch changed nothing there.
- **Sprites you already placed**:
  - not saved yet: `discard_changes(category, image)` drops **all** unsaved edits of that image and reloads it from disk;
  - already saved: `delete_property(category, image, path)` for each placed canvas, then `save_image` again.

## The import lock
While import_apply or import_undo runs, the editor keeps a lock file named `.import-lock` in your folder, and removes it afterwards. The Pack button on the WZ Editing page refuses to start while that file exists ("An import is being applied. Try again in a minute."). That check is in the publisher, not in this editor. If it never goes away, an import was interrupted: run import_undo on it, or ask the owner. The editor's own edit tools do not honour the lock, so do not edit the affected images while an apply is running.

## When it fails
Each failure has a short code and a plain sentence. The common ones:
- `url_not_allowed`: "The host ... is not on the list of allowed sites". Use a Google Drive link.
- `not_a_file`: "The link gave a web page, not a file." The file is not shared with "anyone with the link", or the link is a folder.
- `http_error` (403 or 404): "the file is missing or is not shared with "anyone with the link"". Check the sharing.
- `too_large`: the file is over {{maxDownloadMb}} MB. Send fewer images, or use kind files.
- `busy`: "only one download runs at a time", or another preview, apply or undo is running. Wait and try again.
- `too_many_staged`: {{maxStaged}} imports are already waiting. Free one with import_undo(id).
- `category_unknown` / `category_refused`: the category is not in your tree, or it is List or Sound.
- `not_gms`: the .wz is not in the GMS key. Repack it with GMS.
- `unsafe_zip` / `not_a_png` / `unsupported_type`: the zip has folders, odd names or too many or too big files, or a file is not a complete PNG. Fix the zip and fetch again.
- `hash_mismatch`: the download is not the file you expected. The file was deleted.
- `token_invalid`: the token was used, or a newer preview replaced it. Call import_preview again.
- `tree_changed` / `unsaved_edits`: something in the tree changed since the preview, or an affected image has unsaved edits. Save or discard, then preview again.
- `lock_failed`: the lock file could not be taken, so nothing was changed. Ask the owner.
- `interrupted_import_pending`: an earlier apply stopped half way. Run import_undo on the id it names first.
- `not_found`: the id is wrong, or the import expired.
If it still fails and you do not understand why, stop and ask the server owner. 
