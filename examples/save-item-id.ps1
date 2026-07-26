$ErrorActionPreference = 'Stop'
$vaporcmd = ".\vaporcmd.exe"

# Capture the JSON result from create
$result = & $vaporcmd create create.json | ConvertFrom-Json

if (-not $result.success) {
    throw "Create failed: $($result.error)"
}

# Save just the item ID to a text file
$result.itemId | Set-Content "ItemId.txt" -NoNewline

Write-Host "Created item $($result.itemId)"
