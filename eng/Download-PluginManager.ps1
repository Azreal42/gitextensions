[CmdletBinding()]
Param(
    [Parameter(Mandatory=$True, Position=1)]
    [string] $ExtractRootPath
)

$ErrorActionPreference = 'Stop'
$requiredFiles = @(
    'GitExtensions.PluginManager.dll'
    'PackageManager/PackageManager.UI.exe'
    'PackageManager/PackageManager.UI.runtimeconfig.json'
)

function Test-PluginManagerOutput([string] $Path)
{
    foreach ($file in $requiredFiles)
    {
        if (!(Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf))
        {
            return $false
        }
    }

    return $true
}

$output = Join-Path $ExtractRootPath 'Output'
if (Test-PluginManagerOutput $output)
{
    return
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/gitextensions/gitextensions.pluginmanager/releases'
$asset = $releases[0].assets | Where-Object { $_.name -like 'GitExtensions.PluginManager*.zip' } | Select-Object -First 1
if (!$asset)
{
    throw 'PluginManager release not found.'
}

$stage = Join-Path $ExtractRootPath ([Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try
{
    $archive = Join-Path $stage 'plugin-manager.zip'
    $extracted = Join-Path $stage 'Output'
    Write-Host "Downloading '$($asset.name)'."
    Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $archive

    # Windows PowerShell 5.1 uses the .NET Framework two-argument overload.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    if (!(Test-PluginManagerOutput $extracted))
    {
        throw "The archive '$($asset.name)' is missing a required plugin manager file."
    }

    if (Test-Path -LiteralPath $output)
    {
        if (Test-PluginManagerOutput $output) { return }
        Remove-Item -LiteralPath $output -Recurse -Force
    }

    [System.IO.Directory]::Move($extracted, $output)
}
finally
{
    Remove-Item -LiteralPath $stage -Recurse -Force
}
