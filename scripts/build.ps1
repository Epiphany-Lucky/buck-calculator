param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'publish'
$smokeDirectory = Join-Path $artifactsDirectory 'smoke-test'
$projectFile = Join-Path $projectRoot 'src\BuckCalculator\BuckCalculator.csproj'
$checksProject = Join-Path $projectRoot 'tests\BuckCalculator.Checks.csproj'
$readmeFile = Join-Path $projectRoot 'README.md'

New-Item -ItemType Directory -Path $artifactsDirectory, $publishDirectory -Force | Out-Null

Push-Location $projectRoot
try {
    Write-Host 'Running calculation and report checks...'
    & dotnet run --project $checksProject --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Calculation checks failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Publishing self-contained Windows x64 application...'
    & dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Application publish failed with exit code $LASTEXITCODE."
    }

    $publishedExe = Join-Path $publishDirectory 'BuckCalculator.exe'
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw 'The published BuckCalculator.exe is missing.'
    }

    if (-not $SkipSmokeTest) {
        New-Item -ItemType Directory -Path $smokeDirectory -Force | Out-Null
        $smokeMarker = Join-Path $smokeDirectory 'smoke-results.txt'
        if (Test-Path -LiteralPath $smokeMarker) {
            Remove-Item -LiteralPath $smokeMarker
        }
        Write-Host 'Verifying the published application and rendering its window...'
        $smokeProcess = Start-Process -FilePath $publishedExe `
            -ArgumentList @('--smoke-test', ('"{0}"' -f $smokeDirectory)) `
            -WindowStyle Hidden -PassThru
        if (-not $smokeProcess.WaitForExit(60000)) {
            $smokeProcess.Kill()
            throw 'The application smoke test exceeded 60 seconds.'
        }
        if ($smokeProcess.ExitCode -ne 0) {
            throw "Application smoke test failed with exit code $($smokeProcess.ExitCode). See $smokeDirectory."
        }
        if (-not (Test-Path -LiteralPath $smokeMarker -PathType Leaf)) {
            throw 'The application did not create its smoke-test result file.'
        }
        Get-Content -LiteralPath $smokeMarker
    }

    $releaseName = "BuckCalculator-v$Version-win-x64"
    $releaseExe = Join-Path $artifactsDirectory "$releaseName.exe"
    $releaseZip = Join-Path $artifactsDirectory "$releaseName.zip"
    Copy-Item -LiteralPath $publishedExe -Destination $releaseExe -Force
    Compress-Archive -LiteralPath @($publishedExe, $readmeFile) -DestinationPath $releaseZip -Force

    $hashLines = foreach ($releaseFile in @($releaseExe, $releaseZip)) {
        $hash = (Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256).Hash.ToLowerInvariant()
        '{0}  {1}' -f $hash, (Split-Path -Leaf $releaseFile)
    }
    $checksumFile = Join-Path $artifactsDirectory 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllLines($checksumFile, $hashLines, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Release files are ready in $artifactsDirectory"
    Get-Item -LiteralPath $releaseExe, $releaseZip, $checksumFile | Select-Object Name, Length
}
finally {
    Pop-Location
}
