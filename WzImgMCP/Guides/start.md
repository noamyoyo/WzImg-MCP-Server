# Start here: what this editor is and how to use it safely

## What this is
This is the WZ editor for MapleStory game files. It works on **your own working copy** of the game data (your tree) and on nothing else.

Editing here does **not** change the game for players. Nothing reaches players until you use the **WZ Editing page**:

1. **Refresh** the page so it sees your changes.
2. **Pack** the changed files.
3. **Download** the pack and **test** it in a private client.
4. **Tick "tested"**.
5. **Publish**.

The editor never publishes anything by itself. Packing, testing and publishing are done by a person on that page.

## The tool families
- **Read and search**: list_categories, list_images_in_category, get_children, get_property, list_properties, search_by_name, search_by_value, batch_search, get_tree_structure, view_canvas, export_png.
- **Edit**: add_property, set_int, set_string, set_float, set_vector, rename_property, copy_property, delete_property, set_canvas_bitmap, set_canvas_origin, import_png, import_sound.
- **Import from a link**: import_fetch, import_status, import_preview, import_apply, import_undo. Read the guide `import-by-link` before using them.
- **Check and save**: validate_image, save_image, discard_changes, find_broken_uols, clear_cache.
- **Help**: get_guide (this tool).

## Safe habits
1. **Look before you change.** Read the image with get_children or list_properties first, so you know what is there.
2. **An edit is not saved until you call save_image** (category, image). Until then it lives only in memory. discard_changes throws away the unsaved edits of one image and reloads it from disk.
3. **After editing an image, call validate_image** (category, image) and read what it says before you save.
4. **Call clear_cache after anything changed the files from outside the editor** (for example a file placed by hand on the server). import_apply and import_undo clear the cache themselves.
5. **Never edit List.wz or the Sound category by hand, and never touch the Sound image BgmGL.** The import tools refuse List.wz and Sound.wz. For the rest this is a project rule that the editor does not enforce, so it is on you.
6. **Test small first.** Try a change on one harmless image before you do a big batch.
7. pack_to_wz only writes a .wz file inside your own folder. It does not reach players. The normal route is the Pack button on the WZ Editing page.

## Packing
The editor does not pack and does not publish. Pack is a button on the WZ Editing page that a person presses after saving. Save your images with save_image first, then tell the person to Refresh and Pack. (pack_to_wz is not that button.)

## If something goes wrong
- A tool says "No data source initialized": call init_data_source with the folder the owner gave you.
- A change looks wrong and is not saved yet: discard_changes (category, image).
- A change was already saved: fix it with the edit tools and save_image again, or ask the owner.
- Anything you do not understand: stop and ask the server owner. Do not guess on game files.

## More guides
Call get_guide with no topic to see all topics. Right now: `start` (this one) and `import-by-link` (getting a .wz file or PNG sprites into your tree from a link).
