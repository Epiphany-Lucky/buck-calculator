param(
    [Parameter(Mandatory)]
    [string]$MsiPath,
    [string]$PreviousMsiPath,
    [string]$UpdateHelperPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\installer-test'))
$installDirectory = Join-Path $testRoot 'app'
$upgradeCode = '{C7BB44A5-16CA-4D44-B50F-63970CFC9D6E}'
$registryPath = 'HKCU:\Software\Epiphany-Lucky\BuckCalculator'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'BUCK 计算器.lnk'
$menuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BuckCalculator\BUCK 计算器.lnk'
$msiFile = (Resolve-Path -LiteralPath $MsiPath).Path
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$resultFile = Join-Path $testRoot 'installer-results.txt'

$installerApi = New-Object -ComObject WindowsInstaller.Installer
$existingProducts = @($installerApi.GetType().InvokeMember('RelatedProducts',
    [System.Reflection.BindingFlags]::GetProperty, $null, $installerApi, @($upgradeCode)))
if ($existingProducts.Count -gt 0 -or (Test-Path -LiteralPath $registryPath) -or
    (Test-Path -LiteralPath $installDirectory) -or (Test-Path -LiteralPath $desktopShortcut) -or
    (Test-Path -LiteralPath (Split-Path -Parent $menuShortcut))) {
    $skipMessage = 'SKIPPED: An existing installation, shortcut or test target was detected. Its files and registration were preserved.'
    Set-Content -LiteralPath $resultFile -Value $skipMessage -Encoding utf8
    Write-Host $skipMessage
    return
}

$database = $installerApi.OpenDatabase($msiFile, 0)
$query = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
$query.Execute()
$productCode = $query.Fetch().StringData(1)
$query.Close()
if ($productCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw 'The MSI ProductCode is invalid.' }
$nameQuery = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductName''')
$nameQuery.Execute()
$productName = $nameQuery.Fetch().StringData(1)
$nameQuery.Close()
if ([string]::IsNullOrWhiteSpace($productName)) { throw 'The MSI ProductName is missing.' }
$installedByThisTest = $false
$installedProductCode = $productCode
$previousProductCode = $null
$ownedUpdateDirectory = $null
$installationTransactionPending = $false
$installedExe = Join-Path $installDirectory 'BuckCalculator.exe'
$results = [System.Collections.Generic.List[string]]::new()

function Invoke-TestMsi([string[]]$Arguments, [int[]]$AllowedExitCodes = @(0, 3010)) {
    $script:installationTransactionPending = $true
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    $finished = $false
    for ($minute = 0; $minute -lt 15 -and -not $finished; $minute++) {
        $finished = $process.WaitForExit(60000)
        if (-not $finished) { Write-Host 'Windows Installer is still working. Waiting for the current transaction...' }
    }
    if (-not $finished) { throw 'Windows Installer is still running after 15 minutes. The test installation was preserved for inspection.' }
    $script:installationTransactionPending = $false
    if ($process.ExitCode -notin $AllowedExitCodes) {
        throw "Windows Installer returned $($process.ExitCode). Review the installer log."
    }
}

try {
    if ($PreviousMsiPath) {
        $previousMsiFile = (Resolve-Path -LiteralPath $PreviousMsiPath).Path
        $previousDatabase = $installerApi.OpenDatabase($previousMsiFile, 0)
        $previousQuery = $previousDatabase.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
        $previousQuery.Execute()
        $installedProductCode = $previousQuery.Fetch().StringData(1)
        $previousProductCode = $installedProductCode
        $previousQuery.Close()
        Invoke-TestMsi @('/i', ('"{0}"' -f $previousMsiFile), '/qn', '/norestart',
            ('INSTALLFOLDER="{0}"' -f $installDirectory), '/L*v', ('"{0}"' -f (Join-Path $testRoot 'install-previous.log')))
        $installedByThisTest = $true
        $results.Add('PASS: previous-version MSI fixture installed in the isolated directory')
        if ($UpdateHelperPath) {
            $updateRoot = [System.IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BuckCalculator\Updates'))
            $ownedUpdateDirectory = Join-Path $updateRoot ('verify-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $ownedUpdateDirectory | Out-Null
            $cachedMsi = Join-Path $ownedUpdateDirectory (Split-Path -Leaf $msiFile)
            $helperExe = Join-Path $ownedUpdateDirectory 'BuckCalculator.Updater.exe'
            Copy-Item -LiteralPath $msiFile -Destination $cachedMsi
            Copy-Item -LiteralPath (Resolve-Path -LiteralPath $UpdateHelperPath).Path -Destination $helperExe
            $installerHash = (Get-FileHash -LiteralPath $cachedMsi -Algorithm SHA256).Hash
            $installationTransactionPending = $true
            $helper = Start-Process -FilePath $helperExe -ArgumentList @('--apply-update', ('"{0}"' -f $cachedMsi),
                $installerHash, '2147483647', ('"{0}"' -f $installedExe)) -WindowStyle Hidden -PassThru
            $helperFinished = $false
            for ($minute = 0; $minute -lt 15 -and -not $helperFinished; $minute++) {
                $helperFinished = $helper.WaitForExit(60000)
                if (-not $helperFinished) { Write-Host 'The update helper is still working. Waiting for its installer transaction...' }
            }
            if (-not $helperFinished) { throw 'The update helper is still running after 15 minutes. The installation and update cache were preserved.' }
            if ($helper.ExitCode -notin @(0, 3010)) {
                throw "The isolated update helper returned $($helper.ExitCode). Review $ownedUpdateDirectory."
            }
            $installationTransactionPending = $false
            $restarted = @()
            for ($attempt = 0; $attempt -lt 40 -and $restarted.Count -eq 0; $attempt++) {
                $restarted = @(Get-Process -Name 'BuckCalculator' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Path -eq $installedExe })
                if ($restarted.Count -eq 0) { Start-Sleep -Milliseconds 250 }
            }
            if ($restarted.Count -eq 0) { throw 'The updater did not restart the isolated installed application.' }
            $targetDatabase = $installerApi.OpenDatabase($msiFile, 0)
            $versionQuery = $targetDatabase.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''')
            $versionQuery.Execute()
            $expectedVersion = [Version]$versionQuery.Fetch().StringData(1)
            $versionQuery.Close()
            $actualVersion = [Version][System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion
            if ($actualVersion.Major -ne $expectedVersion.Major -or $actualVersion.Minor -ne $expectedVersion.Minor -or
                $actualVersion.Build -ne $expectedVersion.Build) {
                throw "The restarted application's version $actualVersion differs from the target MSI $expectedVersion."
            }
            if ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath (Resolve-Path -LiteralPath $UpdateHelperPath).Path -Algorithm SHA256).Hash) {
                throw 'The installed application differs from the verified published executable.'
            }
            $restarted | Stop-Process -Force
            $results.Add('PASS: application update helper installed the verified MSI and restarted the target application version')
        } else {
            Invoke-TestMsi @('/i', ('"{0}"' -f $msiFile), '/qn', '/norestart',
                '/L*v', ('"{0}"' -f (Join-Path $testRoot 'upgrade.log')))
        }
        $installedProductCode = $productCode
        $relatedAfterUpgrade = @($installerApi.GetType().InvokeMember('RelatedProducts',
            [System.Reflection.BindingFlags]::GetProperty, $null, $installerApi, @($upgradeCode)))
        if ($relatedAfterUpgrade.Count -ne 1 -or $relatedAfterUpgrade[0] -ne $productCode) {
            throw 'Major upgrade left an unexpected set of registered products.'
        }
        $results.Add('PASS: major upgrade replaced the previous product and retained its installation directory')
        Invoke-TestMsi -Arguments @('/i', ('"{0}"' -f $previousMsiFile), '/qn', '/norestart',
            '/L*v', ('"{0}"' -f (Join-Path $testRoot 'downgrade.log'))) -AllowedExitCodes @(1603, 1638)
        $results.Add('PASS: the older MSI was blocked from downgrading the installed product')
    } else {
        Invoke-TestMsi @('/i', ('"{0}"' -f $msiFile), '/qn', '/norestart',
            ('INSTALLFOLDER="{0}"' -f $installDirectory), '/L*v', ('"{0}"' -f (Join-Path $testRoot 'install.log')))
        $installedByThisTest = $true
    }
    $registeredDirectory = (Get-ItemProperty -LiteralPath $registryPath).InstallLocation
    if ([System.IO.Path]::GetFullPath($registeredDirectory).TrimEnd('\') -ne $installDirectory.TrimEnd('\')) {
        throw 'The installed registry path differs from the isolated test directory.'
    }
    if (-not (Test-Path -LiteralPath $installedExe) -or -not (Test-Path -LiteralPath $desktopShortcut) -or
        -not (Test-Path -LiteralPath $menuShortcut)) {
        throw 'The installed EXE or application shortcuts are missing.'
    }
    $shortcutShell = New-Object -ComObject WScript.Shell
    foreach ($shortcutPath in @($desktopShortcut, $menuShortcut)) {
        $shortcutTarget = $shortcutShell.CreateShortcut($shortcutPath).TargetPath
        if ([System.IO.Path]::GetFullPath($shortcutTarget) -ne [System.IO.Path]::GetFullPath($installedExe)) {
            throw 'An installed application shortcut points outside the verified installation directory.'
        }
    }
    if ($installerApi.ProductInfo($productCode, 'AssignmentType') -ne '0') {
        throw 'The MSI did not register a per-user installation.'
    }
    $uninstallEntry = @('HKCU:', 'HKLM:') |
        ForEach-Object { Get-ItemProperty -LiteralPath "$_\Software\Microsoft\Windows\CurrentVersion\Uninstall\$productCode" -ErrorAction SilentlyContinue } |
        Where-Object { $_.DisplayName -eq $productName -and $_.UninstallString -like "*$productCode*" -and $_.SystemComponent -ne 1 } |
        Select-Object -First 1
    if (-not $uninstallEntry) { throw 'The visible Windows uninstall entry is missing or invalid.' }
    $results.Add('PASS: per-user installation, custom install directory, registry path, shortcuts and uninstall entry')

    $smokeDirectory = Join-Path $testRoot 'smoke-test'
    $process = Start-Process -FilePath $installedExe -ArgumentList @('--smoke-test', ('"{0}"' -f $smokeDirectory)) `
        -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'The installed application smoke test exceeded 60 seconds.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $smokeDirectory 'smoke-results.txt'))) {
        throw 'The installed application smoke test failed.'
    }
    $results.Add('PASS: installed application smoke test')
}
catch {
    $results.Add('FAIL: ' + $_.Exception.Message)
    throw
}
finally {
    if ($installationTransactionPending) {
        $results.Add('PENDING: an installer transaction requires inspection. Installation and update cache were preserved.')
    } elseif ($installedByThisTest) {
        Get-Process -Name 'BuckCalculator' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $installedExe } | Stop-Process -Force
        $remainingProducts = @($installerApi.GetType().InvokeMember('RelatedProducts',
            [System.Reflection.BindingFlags]::GetProperty, $null, $installerApi, @($upgradeCode)))
        foreach ($remainingProduct in $remainingProducts) {
            if ($remainingProduct -notin @($productCode, $previousProductCode)) {
                throw 'An unexpected product was detected during isolated-test cleanup.'
            }
            $registeredDirectory = (Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue).InstallLocation
            if (-not $registeredDirectory -or
                [System.IO.Path]::GetFullPath($registeredDirectory).TrimEnd('\') -ne $installDirectory.TrimEnd('\')) {
                throw 'The registered product is outside the isolated test directory. Cleanup stopped.'
            }
            Invoke-TestMsi @('/x', $remainingProduct, '/qn', '/norestart', '/L*v', ('"{0}"' -f (Join-Path $testRoot 'uninstall.log')))
        }
        if ((Test-Path -LiteralPath (Join-Path $installDirectory 'BuckCalculator.exe')) -or
            (Test-Path -LiteralPath $desktopShortcut) -or (Test-Path -LiteralPath $menuShortcut) -or
            (Test-Path -LiteralPath $registryPath)) {
            throw 'Uninstall left application files, shortcuts or registry values behind.'
        }
        $results.Add('PASS: uninstall removed application, shortcuts and registration')
    }
    if ($ownedUpdateDirectory -and -not $installationTransactionPending) {
        $resolvedCacheDirectory = [System.IO.Path]::GetFullPath($ownedUpdateDirectory)
        $expectedCacheRoot = [System.IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'BuckCalculator\Updates')).TrimEnd('\') + '\'
        if (-not $resolvedCacheDirectory.StartsWith($expectedCacheRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedCacheDirectory) -notmatch '^verify-[0-9a-f]{32}$' -or
            ((Get-Item -LiteralPath $resolvedCacheDirectory).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw 'The update-cache cleanup target failed its ownership and path checks.'
        }
        Get-ChildItem -LiteralPath $resolvedCacheDirectory -File |
            Where-Object { $_.Extension -in @('.log', '.txt') } |
            Copy-Item -Destination $testRoot
        Remove-Item -LiteralPath $resolvedCacheDirectory -Recurse -Force
        $results.Add('PASS: the uniquely owned update-cache directory was removed')
    }
    [System.IO.File]::WriteAllLines($resultFile, $results, [System.Text.UTF8Encoding]::new($false))
}
Get-Content -LiteralPath $resultFile
