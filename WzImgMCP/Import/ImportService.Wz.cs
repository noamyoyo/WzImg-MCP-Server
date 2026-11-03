using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapleLib.Helpers;
using MapleLib.Img;
using MapleLib.WzLib;
using WzImgMCP.Utils;
using WzImgMCP.Viewer;
using WzImgMCP.Viewer.Diff;

namespace WzImgMCP.Import;

// kind "wz": category rules, extraction, preview, apply, undo.
public sealed partial class ImportService
{
    private static readonly string[] RefusedCategories = { "List", "Sound" };
    private const string PlanFile = "plan.json";

    /// <summary>Test seam: called with "apply:&lt;path&gt;" after each file of an apply is written and checked, and with "undo:&lt;path&gt;" before each file of an undo is handled. May throw.</summary>
    internal Action<string>? StepProbe { get; set; }

    /// <summary>Test seam: called with the tree path just before a file of an apply is swapped (after its backup). May throw.</summary>
    internal Action<string>? SwapProbe { get; set; }

    /// <summary>Test seam: called with "acquire" before the lock file is taken over or created. May throw.</summary>
    internal Action<string>? LockProbe { get; set; }
    private const string JournalFile = "journal.jsonl";

    #region Category and file checks

    /// <summary>The category as the tree spells it, or a refusal. List and Sound are never imported this way; any other must already exist in the tree.</summary>
    private string ResolveCategory(string name)
    {
        if (!PathGuard.IsSafeComponent(name, out _))
            throw new ImportException("category_unknown", "That is not a category name. Use a plain name such as Skill.");
        if (RefusedCategories.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ImportException("category_refused", $"{name}.wz cannot be imported this way. List.wz changes how the client reads mob pictures, and Sound.wz is added to by hand; both are left alone.");
        var categories = _session.DataSource.GetCategories().ToList();
        var match = categories.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new ImportException("category_unknown", $"The tree has no category named {name}; an import only fills a category that already exists. Existing categories: {string.Join(", ", categories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))}.");
        return ActualFolderName(match);
    }

    /// <summary>The data source lists categories in lower case; the folder on disk keeps its own spelling (Skill, Ui), which is what the extractor and the file names use.</summary>
    private string ActualFolderName(string category)
    {
        try
        {
            string? loose = null;
            foreach (var dir in Directory.EnumerateDirectories(TreeRoot()))
            {
                var name = Path.GetFileName(dir);
                if (name == category) return name;
                if (loose == null && name.Equals(category, StringComparison.OrdinalIgnoreCase)) loose = name;
            }
            return loose ?? category;
        }
        catch (ImportException)
        {
            return category;
        }
    }

    private string CategoryFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ImportException("category_unknown", "The link did not say what the file is called. Pass category (for example \"Skill\") to import_fetch.");
        var name = fileName.Trim();
        if (!name.EndsWith(".wz", StringComparison.OrdinalIgnoreCase))
            throw new ImportException("not_a_wz", "The file is not called <Category>.wz. If it is a .wz file with another name, pass category to import_fetch.");
        var stem = name.Substring(0, name.Length - 3);
        try
        {
            return ResolveCategory(stem);
        }
        catch (ImportException ex) when (ex.Code == "category_unknown")
        {
            throw new ImportException("category_unknown", ex.Message + " If the file has been renamed, pass category to import_fetch.");
        }
    }

    /// <summary>
    /// A category was given and the link names its file: when that name is another known category (or List/Sound), the two disagree
    /// and the import is refused. A name that is not a category at all (a renamed copy) is accepted, the given category decides.
    /// </summary>
    private void RequireNameMatchesCategory(string? fileName, string category)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Trim().EndsWith(".wz", StringComparison.OrdinalIgnoreCase))
            return;
        var stem = fileName.Trim().Substring(0, fileName.Trim().Length - 3);
        string named;
        try
        {
            named = ResolveCategory(stem);
        }
        catch (ImportException ex) when (ex.Code == "category_refused")
        {
            throw new ImportException("category_mismatch", $"You said the file is for {category}, but the link names it {stem}.wz. Nothing was downloaded.");
        }
        catch (ImportException)
        {
            return;   // not a known category: probably a renamed copy
        }
        if (!named.Equals(category, StringComparison.OrdinalIgnoreCase))
            throw new ImportException("category_mismatch", $"You said the file is for {category}, but the link names it {stem}.wz. Nothing was downloaded.");
    }

    private string TreeRoot()
    {
        if (!_session.IsInitialized)
            throw new ImportException("no_data_source", "No data source initialized. Call init_data_source first.");
        var path = _session.OpenDataPath ?? throw new ImportException("no_data_source", "No data source initialized. Call init_data_source first.");
        var confined = ImportFs.Confine(path);
        if (!Directory.Exists(confined))
            throw new ImportException("no_data_source", "The data source folder is missing.");
        return confined;
    }

    private string TreeCategoryDir(string category)
    {
        var tree = TreeRoot();
        string? found = null;
        foreach (var dir in Directory.EnumerateDirectories(tree))
        {
            var name = Path.GetFileName(dir);
            if (name == category) { found = dir; break; }
            if (found == null && name.Equals(category, StringComparison.OrdinalIgnoreCase)) found = dir;
        }
        if (found == null)
            throw new ImportException("category_unknown", $"The tree has no folder for the category {category}.");
        return ImportFs.Confine(found);
    }

    private static bool LooksLikeWzFile(string path)
    {
        var head = new byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Read(head, 0, 4) == 4 && head.AsSpan().SequenceEqual("PKG1"u8);
    }

    private static readonly Regex PlainWzName = new(@"^[A-Za-z0-9 _.()+\-'&,#@!$%=\[\]{}~]{1,100}$", RegexOptions.Compiled);

    /// <summary>
    /// True when the directory of the file reads as text with the GMS key: every folder name and every image name that is looked
    /// at is a plain name, and every image name ends in ".img". With another key the names come out as noise. extract_to_img
    /// silently falls back to GMS for a file with another key, so the import asks first.
    /// </summary>
    internal static bool LooksLikeGms(string path)
    {
        try
        {
            using var wz = new WzFile(path, WzMapleVersion.GMS);
            if (wz.ParseWzFile() != WzFileParseStatus.Success || wz.WzDirectory == null)
                return false;
            int seen = 0;
            var stack = new Stack<WzDirectory>();
            stack.Push(wz.WzDirectory);
            while (stack.Count > 0 && seen < 400)
            {
                var dir = stack.Pop();
                foreach (var image in dir.WzImages)
                {
                    seen++;
                    if (!PlainWzName.IsMatch(image.Name) || !image.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                foreach (var sub in dir.WzDirectories)
                {
                    seen++;
                    if (!PlainWzName.IsMatch(sub.Name))
                        return false;
                    stack.Push(sub);
                }
            }
            return seen > 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }
    }

    private void PrepareWz(ImportRecord record, string downloaded, CancellationToken cancellationToken)
    {
        var category = record.Category ?? throw new ImportException("category_unknown", "The category of the file is not known. Pass category to import_fetch.");
        var dir = DirOf(record.Id);

        lock (_gate) record.Phase = "checking the file";
        Save(record);
        if (!LooksLikeWzFile(downloaded))
            throw new ImportException("not_a_wz", "The downloaded file is not a WZ archive.");
        if (!LooksLikeGms(downloaded))
            throw new ImportException("not_gms", "The file does not look like a WZ file with the GMS key (its folder and image names did not read as text). Imports only accept GMS-key files. The file was deleted.");
        cancellationToken.ThrowIfCancellationRequested();

        var src = Path.Combine(dir, "src");
        var extracted = Path.Combine(dir, "extracted");
        ImportFs.Confine(src);
        Directory.CreateDirectory(src);
        File.Move(downloaded, Path.Combine(src, category + ".wz"));

        lock (_gate) { record.Status = ImportStatus.Extracting; record.Phase = "extracting the images"; }
        Save(record);
        Log(record.Id, "extracting");

        ExtractionResult result;
        try
        {
            result = new WzExtractionService()
                .ExtractAsync(src, extracted, "import", "import", WzMapleVersion.GMS, new List<string> { category }, false, cancellationToken)
                .GetAwaiter().GetResult();
        }
        finally
        {
            ErrorLogger.ClearErrors();
        }
        if (!result.Success || result.CategoriesExtracted.Values.Any(c => c.Errors.Count > 0))
            throw new ImportException("extract_failed", "The WZ file could not be read completely. Nothing was changed.");

        var categoryDir = FindExtractedCategory(extracted, category)
            ?? throw new ImportException("extract_failed", "The WZ file held no images for this category.");
        var images = ImportFs.EnumerateImages(categoryDir, out int links);
        if (images.Count == 0)
            throw new ImportException("extract_failed", "The WZ file held no images.");

        ImportFs.DeleteTree(src);   // the .wz is no longer needed; the images are staged
        lock (_gate)
        {
            record.ImagesInSource = images.Count;
            if (links > 0)
                (record.Warnings ??= new List<string>()).Add($"{links} link(s) inside the extracted files were ignored.");
        }
    }

    private static string? FindExtractedCategory(string extracted, string category)
    {
        if (!Directory.Exists(extracted)) return null;
        string? found = null;
        foreach (var dir in new DirectoryInfo(extracted).EnumerateDirectories())
        {
            if (dir.LinkTarget != null) continue;
            if (dir.Name == category) return dir.FullName;
            if (found == null && dir.Name.Equals(category, StringComparison.OrdinalIgnoreCase)) found = dir.FullName;
        }
        return found;
    }

    #endregion

    #region import_preview

    public ImportResultBase Preview(string? id, bool allowUnreadable = false)
    {
        try
        {
            ImportFs.RequireRoot();
            RequireId(id);
            CleanupExpired();
            var record = Require(id);
            RequireWzReady(record, "preview");
            EnterStep();
            try
            {
                record = Require(id);   // the clean-up needs the step lock too, so from here on the import cannot vanish
                return PreviewCore(record, allowUnreadable);
            }
            finally
            {
                _step.Release();
            }
        }
        catch (ImportException ex)
        {
            return ImportFailure.From(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex);
        }
    }

    private void EnterStep()
    {
        if (!_step.Wait(0))
            throw new ImportException("busy", "Another import step (preview, apply, undo or clean-up) is running. Try again in a moment.");
    }

    private static void RequireWzReady(ImportRecord record, string what)
    {
        if (record.Kind != ImportKind.Wz)
            throw new ImportException("wrong_kind", "This import holds loose files, not a WZ file. Place them with import_png(... pngPath=...) instead.");
        if (record.Status != ImportStatus.Ready)
        {
            var hint = record.Status switch
            {
                ImportStatus.Downloading or ImportStatus.Extracting => "It is still being downloaded or unpacked; check import_status.",
                ImportStatus.Failed => "It failed; see import_status.",
                ImportStatus.Applied or ImportStatus.ApplyInterrupted or ImportStatus.Applying => "It has already been applied; use import_undo to put the old files back.",
                _ => "",
            };
            throw new ImportException("bad_state", $"This import is not ready for {what} (status {record.Status}). {hint}".Trim());
        }
    }

    private ImportPreviewResult PreviewCore(ImportRecord record, bool allowUnreadable)
    {
        RequireWzReady(record, "preview");
        var category = record.Category!;
        var dir = DirOf(record.Id);
        var categoryDir = TreeCategoryDir(category);
        var extractedDir = FindExtractedCategory(Path.Combine(dir, "extracted"), category)
            ?? throw new ImportException("staging_damaged", "The staged files are missing. Start the import again.");

        var treeFiles = ImportFs.EnumerateImages(categoryDir, out int treeLinks);
        var treeIndex = new Dictionary<string, List<(string Relative, string Full)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in treeFiles)
        {
            if (!treeIndex.TryGetValue(f.Relative, out var list)) treeIndex[f.Relative] = list = new();
            list.Add(f);
        }
        var treeDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // lower-case folder path -> the tree's spelling
        foreach (var f in treeFiles)
        {
            var parts = f.Relative.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                var prefix = string.Join("/", parts.Take(i));
                treeDirs.TryAdd(prefix, prefix);
            }
        }

        var imports = ImportFs.EnumerateImages(extractedDir, out _);
        var plan = new List<PlanEntry>();
        var newImages = new List<string>();
        var changedImages = new List<PreviewChangedImage>();
        var unreadable = new List<PreviewProblem>();
        var plannedNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unchanged = 0, changedCount = 0, newCount = 0;

        foreach (var (relative, full) in imports)
        {
            if (!relative.Split('/').All(p => PathGuard.IsSafeComponent(p, out _)))
            {
                unreadable.Add(new PreviewProblem { Path = Shown(relative), Reason = "the name is not a plain file name" });
                continue;
            }

            var importSha = ImportFs.Sha256File(full);
            var size = new FileInfo(full).Length;
            treeIndex.TryGetValue(relative, out var matches);

            if (matches is { Count: > 1 })
            {
                unreadable.Add(new PreviewProblem { Path = Shown(relative), Reason = "the tree has several files whose names differ only in capitals" });
                matchedTree.Add(relative);
                continue;
            }

            if (matches is { Count: 1 })
            {
                var (treeRelative, treeFull) = matches[0];
                matchedTree.Add(relative);
                var treeSha = ImportFs.Sha256File(treeFull);
                if (treeSha == importSha) { unchanged++; continue; }

                var imported = ImgProbe.TryLoad(full, "import/" + relative, _options.MaxImageBytes, out var importProblem);
                if (imported == null)
                {
                    unreadable.Add(new PreviewProblem { Path = Shown(relative), Reason = importProblem });
                    continue;
                }
                try
                {
                    var existing = ImgProbe.TryLoad(treeFull, "tree/" + treeRelative, _options.MaxImageBytes, out _);
                    PreviewChangedImage described;
                    if (existing == null)
                    {
                        described = new PreviewChangedImage { Path = Shown(treeRelative), Note = "the image in the tree could not be read; it would be replaced and no comparison is possible" };
                    }
                    else
                    {
                        try
                        {
                            var diff = ImageDiffer.Diff(existing, imported, new DiffOptions { MaxChanges = 2000 }, CancellationToken.None);
                            if (diff.Semantic == "identical") { unchanged++; continue; }   // other bytes, same content
                            described = Describe(treeRelative, diff);
                        }
                        finally
                        {
                            existing.Release();
                        }
                    }
                    changedCount++;
                    plan.Add(new PlanEntry { Action = "changed", ImportPath = relative, TreePath = treeRelative, ImportSha = importSha, TreeSha = treeSha, Size = size });
                    changedImages.Add(described);
                }
                finally
                {
                    imported.Release();
                }
                continue;
            }

            // not in the tree: a new image, if it can be read and nothing else (a link, a folder) is where it would go
            var wanted = ImportFs.CombineRelative(categoryDir, TreeSpelling(relative, treeDirs));
            var wantedInfo = new FileInfo(wanted);
            if (wantedInfo.Exists || wantedInfo.LinkTarget != null || Directory.Exists(wanted))
            {
                unreadable.Add(new PreviewProblem { Path = Shown(relative), Reason = "something that is not a plain image file (for example a link or a folder) is where this image would go" });
                continue;
            }
            var loaded = ImgProbe.TryLoad(full, "import/" + relative, _options.MaxImageBytes, out var newProblem);
            if (loaded == null)
            {
                unreadable.Add(new PreviewProblem { Path = Shown(relative), Reason = newProblem });
                continue;
            }
            loaded.Release();
            var treePath = TreeSpelling(relative, treeDirs);
            if (!plannedNew.Add(treePath))
            {
                unreadable.Add(new PreviewProblem { Path = Shown(relative), Reason = "another image of the upload has the same name apart from capitals" });
                continue;
            }
            newCount++;
            plan.Add(new PlanEntry { Action = "new", ImportPath = relative, TreePath = treePath, ImportSha = importSha, TreeSha = null, Size = size });
            newImages.Add(Shown(treePath));
        }

        var leftAlone = treeFiles.Where(f => !matchedTree.Contains(f.Relative)).Select(f => f.Relative).OrderBy(n => n, StringComparer.Ordinal).ToList();

        plan = plan.OrderBy(p => p.ImportPath, StringComparer.Ordinal).ToList();
        var warnings = new List<string>();
        if (treeLinks > 0)
            warnings.Add($"{treeLinks} symbolic link(s) in the tree were ignored.");

        var result = new ImportPreviewResult
        {
            Success = true, Id = record.Id, Category = category,
            NewCount = newCount, ChangedCount = changedCount, UnchangedCount = unchanged, UnreadableCount = unreadable.Count, LeftAloneCount = leftAlone.Count,
            Warnings = warnings,
        };

        var cap = _options.PreviewMaxNames;
        result.NewImages = newImages.OrderBy(n => n, StringComparer.Ordinal).Take(cap).ToList();
        result.ChangedImages = changedImages.OrderBy(c => c.Path, StringComparer.Ordinal).Take(_options.PreviewMaxImages).ToList();
        result.UnreadableImages = unreadable.OrderBy(u => u.Path, StringComparer.Ordinal).Take(cap).ToList();
        result.LeftAloneImages = leftAlone.Take(cap).Select(Shown).ToList();
        result.Truncated = newImages.Count > cap || changedImages.Count > _options.PreviewMaxImages || unreadable.Count > cap || leftAlone.Count > cap;
        if (result.Truncated)
            warnings.Add("Some lists are cut; the counts are complete and the confirmation covers every image, not only the ones listed.");
        bool blocked = unreadable.Count > 0 && !allowUnreadable;
        if (unreadable.Count > 0)
            warnings.Add(allowUnreadable
                ? "Images listed as unreadable are NOT copied by import_apply (skipped, as you asked with allowUnreadable)."
                : "Images listed as unreadable are NOT copied by import_apply, so no confirmation token is given.");
        if (result.Warnings.Count == 0) result.Warnings = null;

        // the token: random, shown once, tied to exactly this plan
        WritePlan(dir, plan);
        var fingerprint = Fingerprint(plan);
        lock (_gate)
        {
            if (plan.Count == 0 || blocked)
            {
                record.TokenHash = null;   // an earlier token must not survive a newer preview
                record.PreviewFingerprint = null;
                record.SkippedUnreadable = null;
            }
            else
            {
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                record.TokenHash = ImportFs.Sha256Text(token);
                record.PreviewFingerprint = fingerprint;
                record.SkippedUnreadable = unreadable.Count == 0 ? null : unreadable.OrderBy(u => u.Path, StringComparer.Ordinal).Take(200).Select(u => u.Path + " (" + u.Reason + ")").ToList();
                result.Token = token;
            }
            record.PreviewedUtc = Now;
        }
        Save(record);

        result.NextStep = plan.Count == 0
            ? "Nothing would change: every image is already in the tree. There is nothing to apply."
            : blocked
            ? $"{unreadable.Count} image(s) in the upload are unreadable and would be skipped, so no confirmation token was given. Fix the source file and fetch again, or call import_preview(id, allowUnreadable=true) to accept that these images are skipped and get a token. Nothing has been changed."
            : $"If this is right, call import_apply(id, token) with this token. It copies {newCount} new and {changedCount} changed image(s), backs up what it replaces, and works once; a new preview gives a new token. Nothing has been changed yet.";
        return result;
    }

    /// <summary>
    /// A name from the upload, made safe to show: control characters (a new line, an escape code) become '?', and it is cut at
    /// 200 characters. Names come from files other people made, so they are treated as data and never as anything else.
    /// </summary>
    internal static string Shown(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cut = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
        var sb = new StringBuilder(cut.Length);
        for (int i = 0; i < cut.Length; i++)
        {
            var c = cut[i];
            if (char.IsControl(c)) sb.Append('?');
            else if (char.IsHighSurrogate(c) && i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1])) { sb.Append(c).Append(cut[++i]); }
            else if (char.IsSurrogate(c)) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private PreviewChangedImage Describe(string path, ImageDiffResult diff)
    {
        var added = diff.Changes.Count(c => c.Kind == "added");
        var removed = diff.Changes.Count(c => c.Kind == "removed");
        var changed = diff.Changes.Count(c => c.Kind == "changed");
        var image = new PreviewChangedImage
        {
            Path = Shown(path), Added = added, Removed = removed, Changed = changed,
            Note = diff.Truncated ? "more differences exist than are counted" : (diff.Semantic == "identical_visible" ? "only hidden differences (colour under transparent pixels, or linked pictures)" : null),
        };
        var entries = diff.Changes.Take(_options.PreviewMaxEntries)
            .Select(c => new PreviewEntry { Path = Shown(c.Path), Kind = c.Kind, Type = Shown(c.Type + (c.Detail != null ? " (" + c.Detail + ")" : "")) }).ToList();
        image.Entries = entries;
        image.EntriesTruncated = diff.Changes.Count > entries.Count;
        return image;
    }

    private static string TreeSpelling(string importRelative, Dictionary<string, string> treeDirs)
    {
        var parts = importRelative.Split('/');
        var spelled = new List<string>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var prefix = string.Join("/", spelled.Append(parts[i]));
            if (treeDirs.TryGetValue(prefix, out var actual))
                spelled = actual.Split('/').ToList();
            else
                spelled.Add(parts[i]);
        }
        spelled.Add(parts[^1]);
        return string.Join("/", spelled);
    }

    private static string Fingerprint(IEnumerable<PlanEntry> plan)
    {
        var sb = new StringBuilder();
        foreach (var e in plan.OrderBy(p => p.ImportPath, StringComparer.Ordinal))
            sb.Append(e.Action).Append('|').Append(e.ImportPath).Append('|').Append(e.TreePath).Append('|').Append(e.ImportSha).Append('|').Append(e.TreeSha ?? "-").Append('\n');
        return ImportFs.Sha256Text(sb.ToString());
    }

    private static void WritePlan(string dir, List<PlanEntry> plan)
        => ImportFs.WriteTextAtomic(Path.Combine(dir, PlanFile), JsonSerializer.Serialize(plan));

    private static List<PlanEntry> ReadPlan(string dir)
    {
        var path = Path.Combine(dir, PlanFile);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget != null || info.Length > 64 * 1024 * 1024)
            throw new ImportException("staging_damaged", "The preview data is missing. Call import_preview again.");
        try
        {
            return JsonSerializer.Deserialize<List<PlanEntry>>(File.ReadAllText(path)) ?? new();
        }
        catch (JsonException)
        {
            throw new ImportException("staging_damaged", "The preview data is damaged. Call import_preview again.");
        }
    }

    #endregion

    #region import_apply

    public ImportResultBase Apply(string? id, string? token)
    {
        try
        {
            ImportFs.RequireRoot();
            RequireId(id);
            CleanupExpired();
            var record = Require(id);
            RequireWzReady(record, "apply");
            if (string.IsNullOrWhiteSpace(token))
                throw new ImportException("token_invalid", "A confirmation token is needed. Call import_preview(id) first and pass the token it returns.");
            EnterStep();
            try
            {
                record = Require(id);
                return ApplyCore(record, token.Trim());
            }
            finally
            {
                _step.Release();
            }
        }
        catch (ImportException ex)
        {
            return ImportFailure.From(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex);
        }
    }

    private ImportApplyResult ApplyCore(ImportRecord record, string token)
    {
        RequireWzReady(record, "apply");
        var dir = DirOf(record.Id);
        string? tokenHash; string? fingerprint;
        lock (_gate) { tokenHash = record.TokenHash; fingerprint = record.PreviewFingerprint; }
        if (tokenHash == null || fingerprint == null)
            throw new ImportException("token_invalid", "There is no valid preview for this import (a token works once, a new preview replaces the old token, and a preview with unreadable images gives none unless allowUnreadable=true). Call import_preview(id) again.");
        var given = Encoding.UTF8.GetBytes(ImportFs.Sha256Text(token));
        if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(tokenHash)))
            throw new ImportException("token_invalid", "That token does not belong to the latest preview of this import. Call import_preview(id) again and use the new token.");

        // an earlier import that stopped half way may have left the tree half changed: it must be undone first
        string? pending;
        lock (_gate) pending = _records.Values.FirstOrDefault(r => r.Id != record.Id && r.Status == ImportStatus.ApplyInterrupted)?.Id;
        if (pending != null)
            throw new ImportException("interrupted_import_pending", $"An earlier import ({pending}) was interrupted while applying and may have left the tree half changed. Undo it first with import_undo({pending}). Nothing was applied.");

        var plan = ReadPlan(dir);
        if (Fingerprint(plan) != fingerprint)
            throw new ImportException("token_invalid", "The preview data no longer matches its token. Call import_preview(id) again.");

        var category = record.Category!;
        var categoryDir = TreeCategoryDir(category);
        var extractedDir = FindExtractedCategory(Path.Combine(dir, "extracted"), category)
            ?? throw new ImportException("staging_damaged", "The staged files are missing. Start the import again.");

        // undo data of an earlier attempt must have been cleared (a clean rollback does that); never append to stale entries
        var journalPath = Path.Combine(dir, JournalFile);
        var backupRoot = Path.Combine(dir, "backup");
        if (HasUndoData(dir))
            throw new ImportException("staging_damaged", "Undo data from an earlier attempt is still there. Use import_undo(id) first. Nothing was applied.");

        // 1. the staged files and the tree are exactly what the preview saw
        var changedSince = new List<string>();
        var treeNow = ImportFs.EnumerateImages(categoryDir, out _);
        var treeLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in treeNow) treeLookup[f.Relative] = f.Full;
        long needBytes = 0;
        foreach (var e in plan)
        {
            var staged = ImportFs.CombineRelative(extractedDir, e.ImportPath);
            if (!File.Exists(staged) || new FileInfo(staged).LinkTarget != null || ImportFs.Sha256File(staged) != e.ImportSha)
                throw new ImportException("staging_damaged", "A staged file is missing or changed. Start the import again.");
            needBytes += e.Size * 2;
            if (e.Action == "new")
            {
                if (treeLookup.ContainsKey(e.TreePath))
                    changedSince.Add(e.TreePath);
            }
            else
            {
                if (!treeLookup.TryGetValue(e.TreePath, out var treeFull) || treeFull == null || ImportFs.Sha256File(treeFull) != e.TreeSha)
                    changedSince.Add(e.TreePath);
            }
        }
        if (changedSince.Count > 0)
        {
            var shown = string.Join(", ", changedSince.Take(5)) + (changedSince.Count > 5 ? ", ..." : "");
            throw new ImportException("tree_changed", $"{changedSince.Count} file(s) in the tree changed since the preview ({shown}). Nothing was applied. Call import_preview(id) again.");
        }

        // 2. unsaved edits in the editor would later be saved over the imported file (checked again right before each file is swapped)
        var unsaved = UnsavedEdits(category, plan.Select(p => p.TreePath));
        if (unsaved.Count > 0)
            throw new ImportException("unsaved_edits", $"{unsaved.Count} affected image(s) have unsaved edits in the editor ({string.Join(", ", unsaved.Take(5))}). Call save_image or discard_changes for them, then apply again. Nothing was applied.");

        RequireFreeSpace(dir, needBytes);

        // 3. the lock first: when it cannot be taken nothing has changed, the status stays ready and the token is not spent
        AcquireLock(record.Id);
        int added = 0, replaced = 0;
        try
        {
            lock (_gate) { record.TokenHash = null; record.Status = ImportStatus.Applying; record.Phase = "applying"; }   // from here the token is spent
            Save(record);
            CleanStrayTemps(categoryDir, plan.Select(p => p.TreePath));
            ImportFs.Confine(journalPath);
            ImportFs.Confine(backupRoot);
            Directory.CreateDirectory(backupRoot);
            using (var journal = new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(journal, new UTF8Encoding(false)))
            {
                foreach (var e in plan)
                {
                    var target = ImportFs.CombineRelative(categoryDir, e.TreePath);
                    var staged = ImportFs.CombineRelative(extractedDir, e.ImportPath);

                    // an edit started in the editor since the check above: stop before this file is touched
                    var late = UnsavedEdits(category, new[] { e.TreePath });
                    if (late.Count > 0)
                        throw new ImportException("unsaved_edits", $"{Shown(e.TreePath)} got unsaved edits in the editor while the import was running; the import was rolled back. Save or discard the edits, then preview and apply again.");

                    var entry = new JournalEntry { Action = e.Action == "new" ? "added" : "replaced", TreePath = e.TreePath, OriginalSha = e.TreeSha, NewSha = e.ImportSha };
                    if (e.Action == "new")
                        entry.CreatedDirs = MissingDirectories(categoryDir, e.TreePath);
                    else
                        entry.Backup = e.TreePath;

                    // write ahead: the journal line is on disk before the file is touched
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                    writer.Flush();
                    journal.Flush(true);

                    if (e.Action == "changed")
                    {
                        if (ImportFs.Sha256File(target) != e.TreeSha)
                            throw new ImportException("tree_changed", "A file in the tree changed while the import was running; the import was rolled back.");
                        var backup = ImportFs.CombineRelative(backupRoot, e.TreePath);
                        ImportFs.EnsureDirectories(backupRoot, string.Join("/", e.TreePath.Split('/').SkipLast(1)));
                        ImportFs.CopyAtomic(target, backup, e.TreeSha!);
                    }
                    else
                    {
                        if (File.Exists(target) || treeLookupContains(categoryDir, e.TreePath))
                            throw new ImportException("tree_changed", "A new file appeared in the tree while the import was running; the import was rolled back.");
                        ImportFs.EnsureDirectories(categoryDir, string.Join("/", e.TreePath.Split('/').SkipLast(1)));
                    }

                    SwapProbe?.Invoke(e.TreePath);
                    ImportFs.CopyAtomic(staged, target, e.ImportSha);

                    // the written image must open
                    var check = ImgProbe.TryLoad(target, "applied/" + e.TreePath, _options.MaxImageBytes, out _);
                    if (check == null || check.Sha256 != e.ImportSha)
                    {
                        check?.Release();
                        throw new ImportException("apply_failed", "An image did not open after it was written; the import was rolled back.");
                    }
                    check.Release();
                    if (e.Action == "new") added++; else replaced++;
                    StepProbe?.Invoke("apply:" + e.TreePath);
                }
            }
        }
        catch (Exception ex)
        {
            var rolled = RollbackQuietly(record, categoryDir);
            lock (_gate)
            {
                // A clean rollback gives a clean "ready" import (undo data cleared). If it did not succeed, the backups, the journal
                // and the lock file stay, and the import is "apply_interrupted": import_undo restores from them.
                record.Status = rolled ? ImportStatus.Ready : ImportStatus.ApplyInterrupted;
                record.Phase = null;
            }
            try { Save(record); } catch (IOException) { }
            if (rolled)
                ReleaseLock();
            RefreshEditor(category);
            const string notRolled = " Some files could not be put back, so the tree may be half changed. The backups and the lock file were kept; call import_undo(id) to restore from them.";
            if (ex is ImportException ie)
                throw new ImportException(ie.Code, ie.Message + (rolled ? "" : notRolled));
            Log(record.Id, "apply failed " + ex.GetType().Name);
            throw new ImportException("apply_failed", "The import could not be applied." + (rolled ? " Everything was put back." : notRolled));
        }

        lock (_gate)
        {
            record.Status = ImportStatus.Applied;
            record.Phase = null;
            record.AppliedUtc = Now;
            record.AppliedAdded = added;
            record.AppliedReplaced = replaced;
        }
        try { Save(record); } finally { ReleaseLock(); }
        // an applied import keeps only its backups and journal (for undo); the unpacked images are not needed any more
        ImportFs.DeleteTree(Path.Combine(dir, "extracted"));
        RefreshEditor(category);
        Log(record.Id, $"applied {added} new, {replaced} replaced");
        List<string>? skipped; lock (_gate) skipped = record.SkippedUnreadable?.ToList();
        return new ImportApplyResult
        {
            Success = true, Id = record.Id, AddedCount = added, ReplacedCount = replaced,
            SkippedUnreadable = skipped,
            Message = $"Applied: {added} new and {replaced} replaced image(s). The old versions are backed up; import_undo(id) puts them back (for {_options.Retention.TotalDays:0} days). The editor's image cache was cleared. Next: pack and test as usual."
                + (skipped is { Count: > 0 } ? $" NOT copied, because they were unreadable: {skipped.Count} image(s), listed in skipped_unreadable." : ""),
        };

        bool treeLookupContains(string root, string rel)
        {
            // a file differing only in capitals from the new one
            var parent = Path.GetDirectoryName(ImportFs.CombineRelative(root, rel))!;
            if (!Directory.Exists(parent)) return false;
            var name = Path.GetFileName(rel);
            return Directory.EnumerateFileSystemEntries(parent).Any(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>True when a journal with entries or any backup file is on disk.</summary>
    private static bool HasUndoData(string dir)
    {
        var journal = new FileInfo(Path.Combine(dir, JournalFile));
        if (journal.Exists && journal.Length > 0) return true;
        var backup = Path.Combine(dir, "backup");
        return Directory.Exists(backup) && Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any();
    }

    private static readonly Regex StrayTemp = new(@"^\.import-[0-9a-f]{32}\.tmp$", RegexOptions.Compiled);

    /// <summary>
    /// Removes temporary files an earlier apply or undo left behind when the server stopped in the middle of a copy. Only files with exactly
    /// our name pattern (.import-&lt;32 hex digits&gt;.tmp), only plain files, only in the category folder and the folders of the given images.
    /// </summary>
    private void CleanStrayTemps(string categoryDir, IEnumerable<string> treePaths)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (var p in treePaths)
        {
            var parts = p.Split('/');
            for (int i = 1; i < parts.Length; i++)
                folders.Add(string.Join("/", parts.Take(i)));
        }
        foreach (var rel in folders)
        {
            try
            {
                var folder = rel.Length == 0 ? categoryDir : ImportFs.CombineRelative(categoryDir, rel);
                if (!Directory.Exists(folder) || new DirectoryInfo(folder).LinkTarget != null) continue;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                {
                    if (file.LinkTarget == null && StrayTemp.IsMatch(file.Name))
                    {
                        file.Delete();
                        Log("-", "removed a leftover temporary file");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportException) { }
        }
    }

    private static List<string> MissingDirectories(string categoryDir, string treePath)
    {
        var missing = new List<string>();
        var parts = treePath.Split('/');
        var current = categoryDir;
        var relative = new List<string>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = Path.Combine(current, parts[i]);
            relative.Add(parts[i]);
            if (!Directory.Exists(current))
                missing.Add(string.Join("/", relative));
        }
        return missing;
    }

    private void RequireFreeSpace(string dir, long needBytes)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
            if (free < needBytes + 64L * 1024 * 1024)
                throw new ImportException("disk_full", "There is not enough free disk space to apply this import and back up what it replaces.");
        }
        catch (ImportException) { throw; }
        catch (Exception) { /* unknown: the writes fail by themselves if the disk is full */ }
    }

    #endregion

    #region Lock file and the editor

    private void AcquireLock(string id)
    {
        var path = LockPath;
        bool created = false;
        try
        {
            ImportFs.RequireNoLinks(path);
            if (new FileInfo(path).LinkTarget != null)
                throw new ImportException("lock_failed", "The lock file name is taken by a symbolic link, so no import can start. Remove it (it is called .import-lock, in the allowed folder).");
            LockProbe?.Invoke("acquire");
            if (Directory.Exists(path))
                throw new ImportException("lock_failed", "A folder sits where the lock file .import-lock must go, so no import can start. Remove or rename it.");
            // The step lock is held, so no apply or undo of this process owns an existing lock: it is left over from a crash.
            if (File.Exists(path))
            {
                Log(id, "removing a stale lock file");
                File.Delete(path);
            }
            var text = JsonSerializer.Serialize(new { import = id, startedUtc = Now.ToString("o") });
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created = true;
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created) { try { File.Delete(path); } catch (Exception) { } }
            Log(id, "lock failed " + ex.GetType().Name);
            throw new ImportException("lock_failed", "The lock file .import-lock could not be taken (the folder may be read-only or the old lock file cannot be removed), so no import can start and nothing was changed.");
        }
    }

    private void ReleaseLock()
    {
        try
        {
            var path = LockPath;
            if (File.Exists(path) && new FileInfo(path).LinkTarget == null)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportException)
        {
            Log("-", "the lock file could not be removed: " + ex.GetType().Name);
        }
    }

    private ImgFileSystemDataSource? EditorSource()
    {
        try { return _session.IsInitialized ? _session.DataSource as ImgFileSystemDataSource : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Names of the given images that the editor holds with unsaved changes.</summary>
    private List<string> UnsavedEdits(string category, IEnumerable<string> treePaths)
    {
        var source = EditorSource();
        if (source == null) return new();
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in treePaths) wanted[p.Replace('\\', '/')] = p;
        var result = new List<string>();
        foreach (var (cat, relative, _) in source.Manager.GetChangedImagesInfo())
        {
            if (!cat.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
            var normalised = relative.Replace('\\', '/').TrimStart('/');
            if (wanted.TryGetValue(normalised, out var original)) result.Add(original);
        }
        return result;
    }

    /// <summary>Drops the editor's cached images (unsaved ones stay) and rescans the category so new images show up.</summary>
    private void RefreshEditor(string category)
    {
        try
        {
            var source = EditorSource();
            if (source == null) return;
            source.ClearCache();
            source.Manager.RefreshCategoryIndex(category);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[import] could not refresh the editor cache: {ex.GetType().Name}");
        }
    }

    #endregion

    #region import_undo

    public ImportResultBase Undo(string? id)
    {
        try
        {
            ImportFs.RequireRoot();
            RequireId(id);
            CleanupExpired();
            var record = Require(id);

            if (record.Status is ImportStatus.Downloading or ImportStatus.Extracting)
            {
                CancellationTokenSource? cts;
                lock (_gate) _running.TryGetValue(record.Id, out cts);
                try { cts?.Cancel(); } catch (ObjectDisposedException) { }
                return new ImportUndoResult { Success = true, Id = record.Id, Outcome = "cancelled", StagingDeleted = true, Message = "The import was cancelled and its files are being removed. Nothing in the tree was touched." };
            }
            if (record.Status == ImportStatus.Applying)
                throw new ImportException("busy", "This import is being applied right now. Wait for it to finish, then undo it.");

            EnterStep();
            try
            {
                record = Require(id);   // the clean-up needs the step lock too, so from here on the import cannot vanish
                if (record.Status == ImportStatus.Applying)
                    throw new ImportException("busy", "This import is being applied right now. Wait for it to finish, then undo it.");
                // Never applied: just discard. If undo data is on disk (a backup may be the only copy of an original), restore instead.
                if (record.Status is ImportStatus.Ready or ImportStatus.Failed && !HasUndoData(DirOf(record.Id)))
                {
                    ImportFs.DeleteTree(DirOf(record.Id));
                    lock (_gate) _records.Remove(record.Id);
                    return new ImportUndoResult { Success = true, Id = record.Id, Outcome = "discarded", StagingDeleted = true, Message = "Nothing had been applied to the tree. The staged files of this import were deleted." };
                }
                return UndoCore(record);
            }
            finally
            {
                _step.Release();
            }
        }
        catch (ImportException ex)
        {
            return ImportFailure.From(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex);
        }
    }

    private ImportUndoResult UndoCore(ImportRecord record)
    {
        var category = record.Category!;
        var dir = DirOf(record.Id);
        var categoryDir = TreeCategoryDir(category);
        var entries = ReadJournal(dir);

        var unsaved = UnsavedEdits(category, entries.Select(e => e.TreePath));
        if (unsaved.Count > 0)
            throw new ImportException("unsaved_edits", $"{unsaved.Count} affected image(s) have unsaved edits in the editor ({string.Join(", ", unsaved.Take(5))}). Call save_image or discard_changes for them, then undo again. Nothing was changed.");

        // the lock stays after a failed undo of an interrupted apply: the tree may still be half changed
        bool wasInterrupted = record.Status == ImportStatus.ApplyInterrupted;
        AcquireLock(record.Id);
        UndoOutcome? outcome = null;
        try
        {
            CleanStrayTemps(categoryDir, entries.Select(e => e.TreePath));
            outcome = UndoEntries(dir, categoryDir, entries);
        }
        finally
        {
            if (outcome != null || !wasInterrupted)
                ReleaseLock();
        }
        RefreshEditor(category);

        var result = new ImportUndoResult
        {
            Success = true, Id = record.Id, RestoredCount = outcome.Restored, RemovedCount = outcome.Removed,
            SkippedCount = outcome.Skipped.Count, Skipped = outcome.Skipped.Count > 0 ? outcome.Skipped : null,
        };
        if (outcome.Skipped.Count == 0)
        {
            // every replaced file is back to its original (checked by hash for each one), so the backups are no longer the only copy
            result.Outcome = "undone";
            result.StagingDeleted = true;
            result.Message = $"Undone: {outcome.Restored} image(s) restored from backup and {outcome.Removed} added image(s) removed. The staged files were deleted.";
            ImportFs.DeleteTree(dir);
            lock (_gate) _records.Remove(record.Id);
            Log(record.Id, "undone");
        }
        else
        {
            result.Outcome = "partly_undone";
            result.Message = $"Undone except for {outcome.Skipped.Count} file(s) that were edited after the import or whose backup is missing; those were left as they are. The backups stay until the clean-up.";
            lock (_gate) { record.Status = ImportStatus.UndonePartial; record.Phase = null; }
            Save(record);
            Log(record.Id, "partly undone");
        }
        return result;
    }

    /// <summary>
    /// Puts back everything the journal lists. Only when that was complete (nothing skipped) are the undo data cleared, so that the
    /// next attempt starts clean; otherwise the journal and the backups stay as they are.
    /// </summary>
    private bool RollbackQuietly(ImportRecord record, string categoryDir)
    {
        try
        {
            var dir = DirOf(record.Id);
            var outcome = UndoEntries(dir, categoryDir, ReadJournal(dir));
            if (outcome.Skipped.Count != 0)
                return false;
            File.Delete(Path.Combine(dir, JournalFile));
            ImportFs.DeleteTree(Path.Combine(dir, "backup"));
            return !HasUndoData(dir) && !Directory.Exists(Path.Combine(dir, "backup"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record UndoOutcome(int Restored, int Removed, List<PreviewProblem> Skipped);

    private static List<JournalEntry> ReadJournal(string dir)
    {
        var path = Path.Combine(dir, JournalFile);
        var list = new List<JournalEntry>();
        var info = new FileInfo(path);
        if (!info.Exists) return list;
        if (info.LinkTarget != null || info.Length > 256 * 1024 * 1024)
            throw new ImportException("staging_damaged", "The undo information is damaged; nothing was changed.");
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                if (entry != null) list.Add(entry);
            }
            catch (JsonException)
            {
                // a last line cut off by a crash: its file was never touched (the journal line is written first)
            }
        }
        return list;
    }

    private UndoOutcome UndoEntries(string dir, string categoryDir, List<JournalEntry> entries)
    {
        int restored = 0, removed = 0;
        var skipped = new List<PreviewProblem>();
        var createdDirs = new List<string>();
        var backupRoot = Path.Combine(dir, "backup");

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            StepProbe?.Invoke("undo:" + e.TreePath);
            string target;
            try
            {
                target = ImportFs.CombineRelative(categoryDir, e.TreePath);
                ImportFs.RequireNoLinks(target);
            }
            catch (ImportException)
            {
                skipped.Add(new PreviewProblem { Path = Shown(e.TreePath), Reason = "the path is not safe to write" });
                continue;
            }
            if (e.CreatedDirs != null) createdDirs.AddRange(e.CreatedDirs);

            var current = File.Exists(target) ? ImportFs.Sha256File(target) : null;
            if (e.Action == "replaced")
            {
                if (current == e.NewSha)
                {
                    var backup = ImportFs.CombineRelative(backupRoot, e.Backup ?? e.TreePath);
                    if (!File.Exists(backup) || ImportFs.Sha256File(backup) != e.OriginalSha)
                    {
                        skipped.Add(new PreviewProblem { Path = Shown(e.TreePath), Reason = "the backup is missing or damaged" });
                        continue;
                    }
                    ImportFs.CopyAtomic(backup, target, e.OriginalSha!);
                    restored++;
                }
                else if (current != e.OriginalSha)
                {
                    skipped.Add(new PreviewProblem { Path = Shown(e.TreePath), Reason = "edited after the import; left as it is" });
                }
                // else: the apply never got to this file; it still is the original
            }
            else
            {
                if (current == e.NewSha)
                {
                    File.Delete(target);
                    removed++;
                }
                else if (current != null)
                {
                    skipped.Add(new PreviewProblem { Path = Shown(e.TreePath), Reason = "edited after the import; left as it is" });
                }
            }
        }

        // folders the import made, deepest first, only when empty
        foreach (var rel in createdDirs.Distinct().OrderByDescending(d => d.Length))
        {
            try
            {
                var path = ImportFs.CombineRelative(categoryDir, rel);
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget == null && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or ImportException or UnauthorizedAccessException) { }
        }
        return new UndoOutcome(restored, removed, skipped);
    }

    #endregion
}
