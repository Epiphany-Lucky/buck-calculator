param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.2.1',
    [switch]$SkipSmokeTest,
    [switch]$VerifyInstall
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactsDirectory 'publish'
$smokeDirectory = Join-Path $artifactsDirectory 'smoke-test'
$projectFile = Join-Path $projectRoot 'src\BuckCalculator\BuckCalculator.csproj'

New-Item -ItemType Directory -Path $artifactsDirectory, $publishDirectory -Force | Out-Null
Push-Location $projectRoot
try {
    foreach ($checksProject in @('tests\BuckCalculator.Checks.csproj', 'tests\BuckCalculator.UpdateChecks.csproj')) {
        Write-Host "Running $checksProject..."
        & dotnet run --project $checksProject --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "Checks failed for $checksProject with exit code $LASTEXITCODE."
        }
    }

    Write-Host 'Publishing self-contained Windows x64 application for the installer...'
    & dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Application publish failed with exit code $LASTEXITCODE."
    }
    $publishedExe = Join-Path $publishDirectory 'BuckCalculator.exe'
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw 'The published application required by the MSI is missing.'
    }

    if (-not $SkipSmokeTest) {
        New-Item -ItemType Directory -Path $smokeDirectory -Force | Out-Null
        $smokeMarker = Join-Path $smokeDirectory 'smoke-results.txt'
        if (Test-Path -LiteralPath $smokeMarker) { Remove-Item -LiteralPath $smokeMarker }
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

    $wixCommand = Get-Command wix -ErrorAction SilentlyContinue
    $wixExecutable = if ($wixCommand) { $wixCommand.Source } else { '' }
    if (-not $wixExecutable -or ((& $wixExecutable --version) -notlike '6.0.2*')) {
        $toolDirectory = Join-Path $artifactsDirectory 'tools'
        $wixExecutable = Join-Path $toolDirectory 'wix.exe'
        if (-not (Test-Path -LiteralPath $wixExecutable -PathType Leaf)) {
            & dotnet tool install --tool-path $toolDirectory wix --version 6.0.2
            if ($LASTEXITCODE -ne 0) { throw 'WiX 6.0.2 installation failed.' }
        }
        if ((& $wixExecutable --version) -notlike '6.0.2*') { throw 'WiX 6.0.2 is required.' }
    }
    & $wixExecutable extension add --global WixToolset.UI.wixext/6.0.2
    if ($LASTEXITCODE -ne 0) { throw 'The WiX 6.0.2 UI extension is unavailable.' }

    $releaseMsi = Join-Path $artifactsDirectory "BuckCalculator-v$Version-win-x64.msi"
    $cabinetCache = Join-Path $artifactsDirectory 'wix-cabinet-cache'
    Write-Host 'Building the Chinese per-user Windows Installer package...'
    & $wixExecutable build 'installer\Package.wxs' -arch x64 -culture zh-cn `
        -ext WixToolset.UI.wixext/6.0.2 -d "Version=$Version" `
        -d "PublishDirectory=$publishDirectory" -d "ProjectRoot=$projectRoot" `
        -cc $cabinetCache -pdbtype none -o $releaseMsi
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE." }

    $hash = (Get-FileHash -LiteralPath $releaseMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumFile = Join-Path $artifactsDirectory 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllText($checksumFile, ('{0}  {1}{2}' -f $hash, (Split-Path -Leaf $releaseMsi), [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    $updateManifest = [ordered]@{
        version = $Version
        url = "https://github.com/Epiphany-Lucky/buck-calculator/releases/download/v$Version/BuckCalculator-v$Version-win-x64.msi"
        sha256 = $hash
        size = (Get-Item -LiteralPath $releaseMsi).Length
        releaseUrl = "https://github.com/Epiphany-Lucky/buck-calculator/releases/tag/v$Version"
        releaseNotes = '新增 BOOST 升压计算与 BUCK / BOOST 拓扑图，支持双模式切换、全输入范围电流与纹波检查，并保留屏幕自适应和软件内安装更新。'
    }
    $manifestFile = Join-Path $artifactsDirectory 'update.json'
    [System.IO.File]::WriteAllText($manifestFile, (($updateManifest | ConvertTo-Json -Depth 3) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))

    if ($VerifyInstall) {
        if ([Version]::Parse($Version) -le [Version]'1.0.9') {
            throw 'The upgrade-verification target must be newer than the 1.0.9 fixture.'
        }
        $previousVersion = '1.0.9'
        $previousPublish = Join-Path $artifactsDirectory 'upgrade-fixture\publish'
        & dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None -p:DebugSymbols=false "-p:Version=$previousVersion" --output $previousPublish
        if ($LASTEXITCODE -ne 0) { throw 'The previous-version application fixture could not be published.' }
        $previousMsi = Join-Path $artifactsDirectory 'installer-previous-version.msi'
        & $wixExecutable build 'installer\Package.wxs' -arch x64 -culture zh-cn `
            -ext WixToolset.UI.wixext/6.0.2 -d "Version=$previousVersion" `
            -d "PublishDirectory=$previousPublish" -d "ProjectRoot=$projectRoot" `
            -cc $cabinetCache -pdbtype none -o $previousMsi
        if ($LASTEXITCODE -ne 0) { throw 'The upgrade-verification fixture could not be built.' }
        & (Join-Path $projectRoot 'installer\Verify-Install.ps1') -MsiPath $releaseMsi `
            -PreviousMsiPath $previousMsi -UpdateHelperPath $publishedExe
    }
    Write-Host "Installer release files are ready in $artifactsDirectory"
    Get-Item -LiteralPath $releaseMsi, $checksumFile, $manifestFile | Select-Object Name, Length
}
finally {
    Pop-Location
}
