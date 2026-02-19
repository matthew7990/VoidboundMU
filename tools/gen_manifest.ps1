param(
    [string]$GameDir,
    [string]$BaseUrl,
    [string]$Version,
    [string]$Changelog,
    [string]$ServerIp,
    [string]$ServerPort,
    [string]$ManifestPath
)

function Get-Sha256Hash {
    param([string]$FilePath)
    $sha256 = New-Object System.Security.Cryptography.SHA256Managed
    $stream = [System.IO.File]::OpenRead($FilePath)
    try {
        $hashBytes = $sha256.ComputeHash($stream)
        $hashString = [BitConverter]::ToString($hashBytes).Replace("-", "")
        return $hashString
    }
    finally {
        $stream.Close()
        $sha256.Dispose()
    }
}

$files = @()
Get-ChildItem -Recurse -File $GameDir | ForEach-Object {
    $rel     = "MuVoid\" + $_.FullName.Substring($GameDir.Length + 1)
    # Usamos la funcion compatible en vez de Get-FileHash
    $hash    = Get-Sha256Hash $_.FullName
    $size    = $_.Length
    $urlSlug = $rel.Replace("\", "/")
    $files  += [PSCustomObject]@{
        path   = $rel
        sha256 = $hash
        size   = $size
        url    = "$BaseUrl/$urlSlug"
    }
}

$manifest = [PSCustomObject]@{
    version    = $Version
    changelog  = $Changelog
    serverIp   = $ServerIp
    serverPort = $ServerPort
    files      = $files
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $ManifestPath
Write-Host "       OK: version.json con $($files.Count) archivos. IP=$ServerIp Port=$ServerPort"
