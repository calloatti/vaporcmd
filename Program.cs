using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Steamworks;

namespace vaporcmd;

class Manifest
{
    public string ItemId { get; set; }
    public string Title { get; set; }
    public string DescriptionFile { get; set; }
    public string ContentFolder { get; set; }
    public string PreviewFile { get; set; }
    public string TagsFile { get; set; }
    public string KvTagsFile { get; set; }
    public string ChangeNoteFile { get; set; }
    public string Language { get; set; }
    public string Visibility { get; set; }
    public string MetadataFile { get; set; }
}

class Log : IDisposable
{
    private readonly StreamWriter _writer;

    public Log()
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "vaporcmd.log");
        _writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.Error.WriteLine($"Log: {logPath}");
    }

    public void Info(string msg)
    {
        string line = msg;
        Console.Error.WriteLine(line);
        _writer.WriteLine(line);
    }

    public void InfoFmt(string format, params object[] args)
    {
        Info(string.Format(format, args));
    }

    public void Write(string msg)
    {
        Console.Error.Write(msg);
        _writer.Write(msg);
    }

    public void WriteLine(string msg = "")
    {
        Console.Error.WriteLine(msg);
        _writer.WriteLine(msg);
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

class Program
{
    static string _resultJson;

    static int Main(string[] args)
    {
        try
        {
            return MainInner(args);
        }
        finally
        {
            if (_resultJson != null)
                Console.WriteLine(_resultJson);
        }
    }

    static int MainInner(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "/?")
        {
            ExtractHelpFile();
            PrintUsage();
            _resultJson = """{"success":false,"error":"Usage: vaporcmd <create|upload|update|get> ..."}""";
            return 1;
        }

        string command = args[0].ToLower();

        using var log = new Log();

        log.Info($"Command: {command}");
        try { Console.Title = "vaporcmd"; } catch { }

        if (Process.GetProcessesByName("Timberborn").Length > 0 ||
            Process.GetProcessesByName("Timberborn.x86_64").Length > 0)
        {
            log.Info("Error: Timberborn is currently running.");
            log.Info("Please close the game to prevent file conflicts.");
            _resultJson = """{"success":false,"error":"Timberborn is running. Close the game and try again."}""";
            return 1;
        }

        if (!SteamAPI.Init())
        {
            log.Info("SteamAPI.Init failed. Ensure Steam is running and steam_appid.txt is present.");
            _resultJson = """{"success":false,"error":"SteamAPI.Init failed. Make sure Steam is running and you are logged in."}""";
            return 1;
        }

        log.Info("Steam initialized.");

        if (!SteamUser.BLoggedOn())
        {
            log.Info("Steam user is not logged on. Online mode is required for Workshop operations.");
            log.Info("Restart Steam and make sure you are in Online mode (not Offline/Invisible).");
            SteamAPI.Shutdown();
            _resultJson = """{"success":false,"error":"Steam user not logged on. Restart Steam in Online mode and try again."}""";
            return 1;
        }

        try
        {

            if (command == "get")
            {
                if (args.Length < 3)
                {
                    log.Info("Usage: vaporcmd get <publishedfileid> <outfile>");
                    _resultJson = """{"success":false,"error":"Usage: vaporcmd get <publishedfileid> <outfile>"}""";
                    return 1;
                }
                return GetItem(args[1], args[2], log);
            }

            string manifestPath = args[1];

            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Manifest not found: {manifestPath}");
                _resultJson = """{"success":false,"error":"Manifest not found"}""";
                return 1;
            }

            Manifest manifest;
            try
            {
                string json = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (manifest == null)
                    throw new InvalidOperationException("Manifest JSON is null");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to parse manifest: {ex.Message}");
                _resultJson = $$"""{"success":false,"error":"Failed to parse manifest: {{EscapeJson(ex.Message)}}"}""";
                return 1;
            }

            log.Info($"Manifest: {manifestPath}");
            log.Info($"AppID: {ReadAppId()}");
            if (manifest.ItemId != null) log.Info($"ItemID: {manifest.ItemId}");

            switch (command)
            {
                case "create": return CreateItem(manifest, log);
                case "upload": return UpdateItem(manifest, log, includeContent: true);
                case "update": return UpdateItem(manifest, log, includeContent: false);
                default:
                    log.Info($"Unknown command: {command}. Use create, upload, update, or get.");
                    _resultJson = $$"""{"success":false,"error":"Unknown command: {{EscapeJson(command)}}"}""";
                    return 1;
            }
        }
        finally
        {
            SteamAPI.Shutdown();
            log.Info("Steam shutdown.");
        }
    }

    static int CreateItem(Manifest manifest, Log log)
    {
        string title = ReadField(manifest.Title, "title", log, required: true);
        string desc = ReadFileContent(manifest.DescriptionFile, "descriptionFile", log, required: true);

        if (title == null || desc == null)
        {
            _resultJson = """{"success":false,"error":"Missing required manifest fields"}""";
            return 1;
        }

        string contentFolder = ValidatePath(manifest.ContentFolder, "contentFolder", log, isDir: true);
        string previewFile = ValidatePath(manifest.PreviewFile, "previewFile", log);
        string metadataFile = ValidatePath(manifest.MetadataFile, "metadataFile", log);
        if (contentFolder == null && manifest.ContentFolder != null) return 1;
        if (previewFile == null && manifest.PreviewFile != null) return 1;
        if (metadataFile == null && manifest.MetadataFile != null) return 1;

        List<string> tags = ReadTagsFile(manifest.TagsFile, log);
        if (tags == null && manifest.TagsFile != null) return 1;

        log.Write("Creating new Workshop item...");
        SteamAPICall_t createCall = SteamUGC.CreateItem(new AppId_t(ReadAppId()), EWorkshopFileType.k_EWorkshopFileTypeCommunity);

        PublishedFileId_t newItemId = PublishedFileId_t.Invalid;
        var createDone = new ManualResetEvent(false);
        CallResult<CreateItemResult_t> createResult = CallResult<CreateItemResult_t>.Create((p, fail) =>
        {
            if (fail)
            {
                log.Info("\nCreateItem callback error");
            }
            else if (p.m_eResult != EResult.k_EResultOK)
            {
                log.Info($"\nCreateItem failed: {p.m_eResult}");
            }
            else
            {
                newItemId = p.m_nPublishedFileId;
                log.Info($" done (ID: {newItemId})");
            }
            createDone.Set();
        });
        createResult.Set(createCall);
        PumpCallbacks(createDone, log, "CreateItem");

        if (newItemId == PublishedFileId_t.Invalid)
        {
            _resultJson = """{"success":false,"error":"CreateItem failed"}""";
            return 1;
        }

        log.Info($"New item ID: {newItemId}");

        Dictionary<string, string> kvTags = ReadKvTagsFile(manifest.KvTagsFile, log);
        if (kvTags == null && manifest.KvTagsFile != null) return 1;

        int result = SubmitUpdate(new AppId_t(ReadAppId()), newItemId, title, desc, contentFolder, previewFile, tags, null, manifest.Language, kvTags, ParseVisibility(manifest.Visibility) ?? ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted, metadataFile, log);

        if (result == 0)
        {
            _resultJson = $$"""{"success":true,"itemId":"{{newItemId}}"}""";
        }

        createResult.Dispose();
        return result;
    }

    static int UpdateItem(Manifest manifest, Log log, bool includeContent)
    {
        if (string.IsNullOrEmpty(manifest.ItemId))
        {
            log.Info("Manifest must include itemId for upload/update");
            _resultJson = """{"success":false,"error":"Manifest must include itemId"}""";
            return 1;
        }

        if (!ulong.TryParse(manifest.ItemId, out ulong itemIdVal) || itemIdVal == 0)
        {
            log.Info($"Invalid itemId: {manifest.ItemId}");
            _resultJson = $$"""{"success":false,"error":"Invalid itemId: {{EscapeJson(manifest.ItemId)}}"}""";
            return 1;
        }

        PublishedFileId_t fileId = new PublishedFileId_t(itemIdVal);

        string title = ReadField(manifest.Title, "title", log);
        string desc = manifest.DescriptionFile != null ? ReadFileContent(manifest.DescriptionFile, "descriptionFile", log) : null;
        string contentFolder = null;
        string previewFile = ValidatePath(manifest.PreviewFile, "previewFile", log);
        string metadataFile = ValidatePath(manifest.MetadataFile, "metadataFile", log);
        if (previewFile == null && manifest.PreviewFile != null) return 1;
        if (metadataFile == null && manifest.MetadataFile != null) return 1;

        List<string> tags = ReadTagsFile(manifest.TagsFile, log);
        if (tags == null && manifest.TagsFile != null) return 1;

        Dictionary<string, string> kvTags = ReadKvTagsFile(manifest.KvTagsFile, log);
        if (kvTags == null && manifest.KvTagsFile != null) return 1;

        string changeNote = null;
        if (includeContent)
        {
            contentFolder = ValidatePath(manifest.ContentFolder, "contentFolder", log, isDir: true);
            if (contentFolder == null && manifest.ContentFolder != null) return 1;
            changeNote = ReadChangeNoteFile(manifest.ChangeNoteFile, log);
            if (changeNote == null && manifest.ChangeNoteFile != null) return 1;
        }

        int result = SubmitUpdate(new AppId_t(ReadAppId()), fileId, title, desc, contentFolder, previewFile, tags, changeNote, manifest.Language, kvTags, ParseVisibility(manifest.Visibility), metadataFile, log);

        if (result == 0)
        {
            _resultJson = """{"success":true}""";
        }

        return result;
    }

    static int GetItem(string itemIdStr, string outFilePath, Log log)
    {
        if (!ulong.TryParse(itemIdStr, out ulong itemIdVal) || itemIdVal == 0)
        {
            log.Info($"Invalid publishedfileid: {itemIdStr}");
            _resultJson = $$"""{"success":false,"error":"Invalid publishedfileid: {{EscapeJson(itemIdStr)}}"}""";
            return 1;
        }

        PublishedFileId_t fileId = new PublishedFileId_t(itemIdVal);
        log.Info($"ItemID: {fileId}");

        log.Write("Querying item...");
        UGCQueryHandle_t queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(new[] { fileId }, 1);
        if (queryHandle == UGCQueryHandle_t.Invalid)
        {
            log.Info("\nCreateQueryUGCDetailsRequest failed");
            _resultJson = """{"success":false,"error":"CreateQueryUGCDetailsRequest failed"}""";
            return 1;
        }

        SteamUGC.SetReturnLongDescription(queryHandle, true);
        SteamUGC.SetReturnKeyValueTags(queryHandle, true);
        SteamUGC.SetReturnMetadata(queryHandle, true);

        SteamAPICall_t call = SteamUGC.SendQueryUGCRequest(queryHandle);
        if (call == SteamAPICall_t.Invalid)
        {
            SteamUGC.ReleaseQueryUGCRequest(queryHandle);
            log.Info("\nSendQueryUGCRequest failed");
            _resultJson = """{"success":false,"error":"SendQueryUGCRequest failed"}""";
            return 1;
        }

        EResult queryResult = EResult.k_EResultFail;
        SteamUGCDetails_t details = default;
        var queryDone = new ManualResetEvent(false);
        CallResult<SteamUGCQueryCompleted_t> queryResultObj = CallResult<SteamUGCQueryCompleted_t>.Create((p, fail) =>
        {
            if (fail)
            {
                log.Info("\nQuery callback error");
            }
            else if (p.m_eResult != EResult.k_EResultOK)
            {
                log.Info($"\nQuery failed: {p.m_eResult}");
            }
            else if (p.m_unNumResultsReturned > 0 &&
                     SteamUGC.GetQueryUGCResult(queryHandle, 0, out details))
            {
                queryResult = EResult.k_EResultOK;
                log.Info(" done");
            }
            else
            {
                log.Info("\nQuery returned no results");
            }
            queryDone.Set();
        });
        queryResultObj.Set(call);
        PumpCallbacks(queryDone, log, "QueryUGC");
        queryResultObj.Dispose();

        if (queryResult != EResult.k_EResultOK)
        {
            SteamUGC.ReleaseQueryUGCRequest(queryHandle);
            log.Info($"Query failed: {queryResult}");
            _resultJson = $$"""{"success":false,"error":"Query failed: {{EscapeJson(queryResult.ToString())}}"}""";
            return 1;
        }

        uint kvTagCount = SteamUGC.GetQueryUGCNumKeyValueTags(queryHandle, 0);
        var kvTags = new List<Dictionary<string, string>>();
        for (uint i = 0; i < kvTagCount; i++)
        {
            string key;
            string value;
            if (SteamUGC.GetQueryUGCKeyValueTag(queryHandle, 0, i, out key, 256, out value, 1024))
            {
                kvTags.Add(new Dictionary<string, string> { ["key"] = key ?? "", ["value"] = value ?? "" });
            }
        }

        string metadata = null;
        SteamUGC.GetQueryUGCMetadata(queryHandle, 0, out metadata, 8192);
        if (string.IsNullOrEmpty(metadata)) metadata = null;

        SteamUGC.ReleaseQueryUGCRequest(queryHandle);

        var output = new Dictionary<string, object>
        {
            ["publishedfileid"] = details.m_nPublishedFileId.ToString(),
            ["result"] = 1,
            ["creator"] = details.m_ulSteamIDOwner.ToString(),
            ["creator_appid"] = details.m_nCreatorAppID.m_AppId,
            ["consumer_appid"] = details.m_nConsumerAppID.m_AppId,
            ["title"] = details.m_rgchTitle ?? "",
            ["file_description"] = details.m_rgchDescription ?? "",
            ["time_created"] = (int)details.m_rtimeCreated,
            ["time_updated"] = (int)details.m_rtimeUpdated,
            ["visibility"] = (int)details.m_eVisibility,
            ["banned"] = details.m_bBanned,
            ["workshop_accepted"] = details.m_bAcceptedForUse,
            ["file_size"] = details.m_nFileSize,
            ["preview_file_size"] = details.m_nPreviewFileSize,
            ["filename"] = details.m_pchFileName ?? "",
            ["url"] = details.m_rgchURL ?? "",
            ["num_children"] = (int)details.m_unNumChildren,
            ["vote_data"] = new Dictionary<string, object>
            {
                ["votes_up"] = (int)details.m_unVotesUp,
                ["votes_down"] = (int)details.m_unVotesDown,
                ["score"] = details.m_flScore
            }
        };

        string tagsStr = details.m_rgchTags;
        if (!string.IsNullOrEmpty(tagsStr))
        {
            output["tags"] = tagsStr.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Select(t => new Dictionary<string, string> { ["tag"] = t, ["display_name"] = t })
                .ToList();
        }

        if (kvTags.Count > 0)
        {
            output["kvtags"] = kvTags;
        }

        if (metadata != null)
        {
            output["metadata"] = metadata;
        }

        string json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outFilePath, json);
        log.Info($"Written to: {outFilePath}");

        _resultJson = """{"success":true}""";
        return 0;
    }

    static int SubmitUpdate(AppId_t appId, PublishedFileId_t itemId,
        string title, string desc, string contentFolder,
        string previewFile, List<string> tags, string changeNote, string language,
        Dictionary<string, string> kvTags, ERemoteStoragePublishedFileVisibility? visibility, string metadataFile, Log log)
    {
        UGCUpdateHandle_t handle = SteamUGC.StartItemUpdate(appId, itemId);
        if (handle == UGCUpdateHandle_t.Invalid)
        {
            log.Info("StartItemUpdate failed");
            _resultJson = """{"success":false,"error":"StartItemUpdate failed"}""";
            return 1;
        }

        string lang = string.IsNullOrEmpty(language) ? "english" : language;

        if (title != null || desc != null)
        {
            SteamUGC.SetItemUpdateLanguage(handle, lang);
        }

        if (title != null)
        {
            log.Info($"  Title: {title}");
            if (!SteamUGC.SetItemTitle(handle, title))
                log.Info("  Warning: SetItemTitle returned false");
        }

        if (desc != null)
        {
            log.Info($"  Description: {desc.Length} chars");
            if (!SteamUGC.SetItemDescription(handle, desc))
                log.Info("  Warning: SetItemDescription returned false");
        }

        if (contentFolder != null)
        {
            log.Info($"  Content: {contentFolder}");
            if (!SteamUGC.SetItemContent(handle, contentFolder))
            {
                log.Info("  Warning: SetItemContent returned false");
            }
        }

        if (previewFile != null)
        {
            log.Info($"  Preview: {previewFile}");
            if (!SteamUGC.SetItemPreview(handle, previewFile))
                log.Info("  Warning: SetItemPreview returned false");
        }

        if (tags != null && tags.Count > 0)
        {
            log.Info($"  Tags: {string.Join(", ", tags)}");
            if (!SteamUGC.SetItemTags(handle, tags))
                log.Info("  Warning: SetItemTags returned false");
        }

        if (kvTags != null && kvTags.Count > 0)
        {
            log.Info($"  KV tags: {string.Join(", ", kvTags.Select(kv => $"{kv.Key}={kv.Value}"))}");
            foreach (var kv in kvTags)
            {
                if (kv.Value == "")
                {
                    if (!SteamUGC.RemoveItemKeyValueTags(handle, kv.Key))
                        log.Info($"  Warning: RemoveItemKeyValueTags('{kv.Key}') returned false");
                }
                else
                {
                    SteamUGC.RemoveItemKeyValueTags(handle, kv.Key);
                    if (!SteamUGC.AddItemKeyValueTag(handle, kv.Key, kv.Value))
                        log.Info($"  Warning: AddItemKeyValueTag('{kv.Key}') returned false");
                }
            }
        }

        if (visibility.HasValue)
        {
            log.Info($"  Visibility: {visibility.Value}");
            if (!SteamUGC.SetItemVisibility(handle, visibility.Value))
                log.Info("  Warning: SetItemVisibility returned false");
        }
        else
        {
            log.Info("  Visibility: unchanged (not specified in manifest)");
        }

        if (metadataFile != null)
        {
            string metaContent = File.ReadAllText(metadataFile);
            log.Info($"  Metadata: {metaContent.Length} chars");
            if (!SteamUGC.SetItemMetadata(handle, metaContent))
                log.Info("  Warning: SetItemMetadata returned false");
        }

        log.Write("Submitting update...");
        SteamAPICall_t submitCall = SteamUGC.SubmitItemUpdate(handle, changeNote);
        if (submitCall == SteamAPICall_t.Invalid)
        {
            log.Info("SubmitItemUpdate returned invalid handle");
            _resultJson = """{"success":false,"error":"SubmitItemUpdate returned invalid handle"}""";
            return 1;
        }

        EResult submitResult = EResult.k_EResultFail;
        var submitDone = new ManualResetEvent(false);
        CallResult<SubmitItemUpdateResult_t> submitResultObj = CallResult<SubmitItemUpdateResult_t>.Create((p, fail) =>
        {
            if (fail)
            {
                log.Info("\nSubmitItemUpdate callback error");
            }
            else
            {
                submitResult = p.m_eResult;
                if (p.m_eResult != EResult.k_EResultOK)
                {
                    log.Info($"\nSubmitItemUpdate failed: {p.m_eResult}");
                }
                else
                {
                    log.Info(" done");
                }
            }
            submitDone.Set();
        });
        submitResultObj.Set(submitCall);
        PumpCallbacks(submitDone, log, "SubmitItemUpdate");
        submitResultObj.Dispose();

        if (submitResult != EResult.k_EResultOK)
        {
            log.Info($"Update submission failed with result: {submitResult}");
            _resultJson = $$"""{"success":false,"error":"SubmitItemUpdate failed: {{EscapeJson(submitResult.ToString())}}"}""";
            return 1;
        }

        log.Info("Success.");
        return 0;
    }

    static void PumpCallbacks(ManualResetEvent done, Log log = null, string context = "")
    {
        int ms = 0;
        while (!done.WaitOne(100))
        {
            SteamAPI.RunCallbacks();
            ms += 100;
            if (ms > 60000)
            {
                log?.Info($"  Error: Timed out waiting for Steam callback{(context != "" ? $" ({context})" : "")}");
                break;
            }
        }
        SteamAPI.RunCallbacks();
    }

    static string ReadField(string value, string name, Log log, bool required = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required)
                log.Info($"Manifest missing required field: {name}");
            return null;
        }
        return value;
    }

    static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    static string ReadFileContent(string path, string name, Log log, bool required = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (required)
                log.Info($"Manifest missing required field: {name}");
            return null;
        }
        string fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
        {
            log.Info($"  Error: {name} not found: {fullPath}");
            _resultJson = $$"""{"success":false,"error":"{{EscapeJson(name)}} not found: {{EscapeJson(fullPath)}}"}""";
            return null;
        }
        return File.ReadAllText(fullPath);
    }

    static string ValidatePath(string path, string name, Log log, bool isDir = false)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string fullPath = ResolvePath(path);
        bool exists = isDir ? Directory.Exists(fullPath) : File.Exists(fullPath);
        if (!exists)
        {
            log.Info($"  Error: {name} not found: {fullPath}");
            _resultJson = $$"""{"success":false,"error":"{{EscapeJson(name)}} not found: {{EscapeJson(fullPath)}}"}""";
            return null;
        }
        return fullPath;
    }

    static List<string> ReadTagsFile(string path, Log log)
    {
        string fullPath = ValidatePath(path, "tagsFile", log);
        if (fullPath == null) return null;
        return File.ReadLines(fullPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    static Dictionary<string, string> ReadKvTagsFile(string path, Log log)
    {
        string fullPath = ValidatePath(path, "kvTagsFile", log);
        if (fullPath == null) return null;
        var result = new Dictionary<string, string>();
        foreach (string line in File.ReadLines(fullPath))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            int eq = trimmed.IndexOf('=');
            if (eq < 0)
            {
                log.Info($"  Warning: invalid kvTag line (no '='): {trimmed}");
                continue;
            }
            result[trimmed.Substring(0, eq).Trim()] = trimmed.Substring(eq + 1).Trim();
        }
        return result;
    }

    static string ReadChangeNoteFile(string path, Log log)
    {
        string fullPath = ValidatePath(path, "changeNoteFile", log);
        if (fullPath == null) return null;
        string content = File.ReadAllText(fullPath).Trim();
        return content.Length > 0 ? content : null;
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    static ERemoteStoragePublishedFileVisibility? ParseVisibility(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return s.ToLowerInvariant() switch
        {
            "public" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
            "friends_only" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
            "private" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
            "unlisted" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
            _ => null
        };
    }

    static uint ReadAppId()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
        if (!File.Exists(path)) return 0;
        string content = File.ReadAllText(path).Trim();
        if (uint.TryParse(content, out uint id)) return id;
        return 0;
    }

    static void ExtractHelpFile()
    {
        string destPath = Path.Combine(AppContext.BaseDirectory, "vaporcmd.md");
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("vaporcmd.vaporcmd.md");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                File.WriteAllText(destPath, reader.ReadToEnd());
            }
        }
        catch { }
    }

    static void PrintUsage()
    {
        var ver = typeof(Program).Assembly.GetName().Version;
        string verStr = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "?.?.?";
        Console.Error.WriteLine($"vaporcmd {verStr} - Steam Workshop upload tool");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  vaporcmd <create|upload|update|get> ...");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Commands:");
        Console.Error.WriteLine("  create   Create a new Workshop item (optionally with content + metadata).");
        Console.Error.WriteLine("           (requires title, descriptionFile)");
        Console.Error.WriteLine("  upload   Upload content + metadata for an existing item.");
        Console.Error.WriteLine("           (requires itemId, contentFolder)");
        Console.Error.WriteLine("  update   Update metadata only (title/desc/preview/tags).");
        Console.Error.WriteLine("           (requires itemId, no content uploaded)");
        Console.Error.WriteLine("  get      Query item details and write JSON to a file.");
        Console.Error.WriteLine("           (usage: vaporcmd get <publishedfileid> <outfile>)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Stdout result (last line, JSON):");
        Console.Error.WriteLine("  Success: {\"success\":true}");
        Console.Error.WriteLine("  Create:  {\"success\":true,\"itemId\":\"1234567890\"}");
        Console.Error.WriteLine("  Failure: {\"success\":false,\"error\":\"...\"}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Manifest JSON fields:");
        Console.Error.WriteLine("  ItemId          (string, opt*)        PublishedFileId for upload/update");
        Console.Error.WriteLine("  Title           (string, opt)         Workshop title");
        Console.Error.WriteLine("  DescriptionFile (string, opt)         Path to description text file");
        Console.Error.WriteLine("  ContentFolder   (string, opt)         Path to mod content directory");
        Console.Error.WriteLine("  PreviewFile     (string, opt)         Path to thumbnail image");
        Console.Error.WriteLine("  TagsFile        (string, opt)         Path to tag file (one tag per line)");
        Console.Error.WriteLine("  KvTagsFile      (string, opt)         Path to kvTag file (key=value per line)");
        Console.Error.WriteLine("  Visibility      (string, opt)         One of: public, friends_only, private, unlisted");
        Console.Error.WriteLine("  MetadataFile    (string, opt)         Path to metadata text file");
        Console.Error.WriteLine("  ChangeNoteFile  (string, opt)         Path to change note file (only for upload)");
        Console.Error.WriteLine("  Language        (string, opt)         Language code (default: english)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  vaporcmd create mymod.json");
        Console.Error.WriteLine("  vaporcmd upload mymod.json");
        Console.Error.WriteLine("  vaporcmd update mymod.json");
        Console.Error.WriteLine("  vaporcmd get 1234567890 output.json");
        Console.Error.WriteLine();
        Console.Error.WriteLine("See vaporcmd.md for full examples.");
    }
}
