param(
  [int]$Count = 5,
  [string]$Prefix = "LIC",
  [string]$OutFile = "license_keys.txt"
)

$keys = @()
1..$Count | ForEach-Object {
  $suffix = -join ((65..90) + (48..57) | Get-Random -Count 8 | ForEach-Object {[char]$_})
  $keys += "$Prefix-$suffix"
}

$keys | Set-Content -Encoding UTF8 $OutFile
Write-Host "Generated $Count keys -> $OutFile"
