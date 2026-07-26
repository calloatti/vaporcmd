# vaporcmd

Steam Workshop upload tool using Steamworks.NET. Replaces steamcmd for all
Workshop operations (create item, upload content, update metadata).

## Usage

```
vaporcmd <create|upload|update|get> <manifest.json|publishedfileid outfile>
```

Exit code: 0 = success, 1 = failure.

## Stdout Result

The last line of stdout is always a JSON result object:

| Scenario | Output |
|---|---|
| Success | `{"success":true}` |
| Created | `{"success":true,"itemId":"3082207506"}` |
| Failure | `{"success":false,"error":"Description..."}` |

All human-readable progress goes to stderr and to `vaporcmd.log` in the
same directory as the executable.

Parsing in PowerShell:

```powershell
$result = & vaporcmd.exe create mymod.json | ConvertFrom-Json
$itemId = $result.itemId
```

## Manifest JSON Reference

```json
{
  "ItemId": "3082207506",
  "Title": "My Timberborn Mod",
  "DescriptionFile": "C:/Mods/MyMod/workshop_description.txt",
  "ContentFolder": "C:/Users/me/Documents/Timberborn/Mods/MyMod",
  "PreviewFile": "C:/Mods/MyMod/thumbnail.jpg",
  "TagsFile": "C:/Mods/MyMod/workshop_tags.txt",
  "KvTagsFile": "C:/Mods/MyMod/workshop_kvtags.txt",
  "Visibility": "public",
  "MetadataFile": "C:/Mods/MyMod/metadata.txt",
  "ChangeNoteFile": "C:/Mods/MyMod/workshop_changenote.txt",
  "Language": "english"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `ItemId` | string | create: no, upload/update: **yes** | PublishedFileId as string |
| `Title` | string | create: **yes**, else no | Workshop title |
| `DescriptionFile` | string | create: **yes**, else no | Path to description text file |
| `ContentFolder` | string | no | Path to mod content directory |
| `PreviewFile` | string | no | Path to thumbnail.jpg |
| `TagsFile` | string | no | Path to tag file (one tag per line) |
| `KvTagsFile` | string | no | Path to kvTag file (`key=value` per line; empty value removes key) |
| `Visibility` | string | no | One of: public, friends_only, private, unlisted |
| `MetadataFile` | string | no | Path to metadata text file (≤1024 chars) |
| `ChangeNoteFile` | string | no | Path to change note text file (only for `upload`) |
| `Language` | string | no | Language code (default: "english") |

## Field handling per command

| Field | create | upload | update |
|---|---|---|---|
| `ItemId` | ignored | **required** | **required** |
| `Title` | used | used | used |
| `DescriptionFile` | used | used | used |
| `ContentFolder` | used | used | ignored |
| `PreviewFile` | used | used | used |
| `TagsFile` | used | used | used |
| `KvTagsFile` | used | used | used |
| `Visibility` | used | used | used |
| `MetadataFile` | used | used | used |
| `ChangeNoteFile` | ignored | used | ignored |
| `Language` | used | used | used |

All paths are resolved to absolute paths against the current working directory.
If a specified file or directory does not exist, the command errors out.

## File formats

Each file-based field expects a specific format:

**DescriptionFile** — plain text file with the workshop description.

**TagsFile** — one tag per line:
```
Mod
Update 1.0
Quality of life
```

**KvTagsFile** — `key=value` per line; empty value removes the key:
```
modVersion=1.5
difficulty=hard
oldKey=
```

**ChangeNoteFile** — raw text (only for `upload`):
```
Fixed crash on load; added new building.
```

## Get command output

`vaporcmd get <publishedfileid> <outfile>` writes a JSON file with field names
matching the Steam Web API response format:

```json
{
  "publishedfileid": "3689752391",
  "creator_appid": 1062090,
  "file_description": "...",
  "tags": [{"tag": "Mod", "display_name": "Mod"}],
  "kvtags": [{"key": "version", "value": "1.0.0"}],
  "vote_data": {"votes_up": 1, "votes_down": 1, "score": 0.5},
  "workshop_accepted": false,
  "metadata": "..."
}
```

## Example: Create a new Workshop item

### manifest-create.json

```json
{
  "Title": "Automation Tools",
  "DescriptionFile": "C:/Repos/Mods/Automation Tools/workshop_description.txt",
  "ContentFolder": "C:/Users/me/Documents/Timberborn/Mods/Automation Tools",
  "PreviewFile": "C:/Repos/Mods/Automation Tools/thumbnail.jpg",
  "TagsFile": "C:/Repos/Mods/Automation Tools/workshop_tags.txt"
}
```

```powershell
$result = & vaporcmd.exe create manifest-create.json | ConvertFrom-Json
if ($result.success) {
    $itemId = $result.itemId
    $data = @{ ItemId = $itemId } | ConvertTo-Json
    $data | Set-Content "C:/Users/me/Documents/Timberborn/Mods/Automation Tools/workshop_data.json"
}
```

## Example: Upload content + metadata

### manifest-upload.json

```json
{
  "ItemId": "3082207506",
  "ContentFolder": "C:/Users/me/Documents/Timberborn/Mods/Automation Tools",
  "PreviewFile": "C:/Repos/Mods/Automation Tools/thumbnail.jpg",
  "TagsFile": "C:/Repos/Mods/Automation Tools/workshop_tags.txt",
  "ChangeNoteFile": "C:/Repos/Mods/Automation Tools/workshop_changenote.txt"
}
```

```powershell
$result = & vaporcmd.exe upload manifest-upload.json | ConvertFrom-Json
if (-not $result.success) { throw $result.error }
```

## Example: Update metadata only

```json
{
  "ItemId": "3082207506",
  "Title": "Automation Tools - Updated Title",
  "DescriptionFile": "C:/Repos/Mods/Automation Tools/workshop_description.txt",
  "PreviewFile": "C:/Repos/Mods/Automation Tools/thumbnail.jpg",
  "TagsFile": "C:/Repos/Mods/Automation Tools/workshop_tags.txt"
}
```

```powershell
& vaporcmd.exe update manifest-update.json | Out-Null
```

## Environment

- Requires Steam to be running (logged in as the item owner).
- `steam_appid.txt` with the target AppID must be in the same directory as the
  executable (copied automatically on build).
- Native DLLs (`steam_api64.dll`) are provided by the `Facepunch.Steamworks.Dll`
  NuGet package and copied to the output at build time.
