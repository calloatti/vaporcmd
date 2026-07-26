# vaporcmd

Steam Workshop upload tool using Steamworks.NET. Replaces steamcmd for Workshop operations (create item, upload content, update metadata).

## Requirements

- Steam running and logged in **Online** mode
- [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- `steam_api64.dll` next to the executable (included in release zip)

## Quick start

```
vaporcmd.exe create manifest.json
vaporcmd.exe upload manifest.json
vaporcmd.exe update manifest.json
vaporcmd.exe get <publishedfileid> output.json
```

Stdout last line is always JSON: `{"success":true}` or `{"success":false,"error":"..."}`.

## Manifest example

```json
{
  "ItemId": "3082207506",
  "Title": "My Mod",
  "DescriptionFile": "description.txt",
  "ContentFolder": "content",
  "PreviewFile": "preview.png",
  "TagsFile": "workshop_tags.txt",
  "KvTagsFile": "workshop_kvtags.txt",
  "Visibility": "unlisted"
}
```

See [examples/](examples/) for usage scripts, and [vaporcmd.md](vaporcmd.md) for full field reference.

## Build from source

```powershell
dotnet publish -c Release
# output: bin\Release\net8.0\win-x64\publish\vaporcmd.exe
```

## License

MIT
