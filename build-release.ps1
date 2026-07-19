[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$workspace = [System.IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $workspace "Tyrian2000LevelViewer\Tyrian2000LevelViewer.csproj"
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $workspace "artifacts"))
$publish = Join-Path $artifacts "publish\win-x64"
$release = Join-Path $artifacts "release"
$archive = Join-Path $release "Tyrian2000LevelViewer-win-x64.zip"

if (-not $artifacts.StartsWith($workspace.TrimEnd("\") + "\", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The artifacts directory must stay inside the workspace."
}

if (Test-Path -LiteralPath $artifacts) {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            [System.IO.Directory]::Delete($artifacts, $true)
            break
        }
        catch [System.IO.IOException] {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

dotnet publish $project --configuration Release --runtime win-x64 `
    --self-contained true --property:PublishProfile=Windows-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $workspace "README.md") -Destination $publish
$docs = Join-Path $workspace "docs"
if (Test-Path -LiteralPath $docs) {
    Copy-Item -LiteralPath $docs -Destination $publish -Recurse
}
New-Item -ItemType Directory -Path $release | Out-Null
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $archive -CompressionLevel Optimal

$publishedBytes = (Get-ChildItem -LiteralPath $publish -File | Measure-Object Length -Sum).Sum
$archiveBytes = (Get-Item -LiteralPath $archive).Length
Write-Host ("Published: {0} ({1:N2} MiB)" -f $publish, ($publishedBytes / 1MB))
Write-Host ("Archive:   {0} ({1:N2} MiB)" -f $archive, ($archiveBytes / 1MB))
