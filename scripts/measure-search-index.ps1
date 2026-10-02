param(
    [ValidateRange(2048, 100000)][int]$Files = 16384,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectDirectory = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory "artifacts/search-index-benchmark" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$markerPrefix = Join-Path $OutputDirectory ([Guid]::NewGuid().ToString("N"))
$readyPath = "$markerPrefix.ready"
$stopPath = "$markerPrefix.stop"
$previousFiles = $env:DESKTOPPLUS_INDEX_BENCHMARK_FILES
$sampler = $null
Push-Location $projectDirectory
try {
    dotnet build DesktopPlus.Tests/DesktopPlus.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
    # Sample physical disks independently; these values include every Windows process.
    $sampler = Start-Job -ArgumentList $readyPath, $stopPath -ScriptBlock {
        param($readyPath, $stopPath)
        while (-not (Test-Path -LiteralPath $stopPath)) {
            $disks = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfDisk_PhysicalDisk
            $sampleUtc = [DateTime]::UtcNow
            foreach ($disk in $disks) {
                if ($disk.Name -eq "_Total") { continue }
                [pscustomobject]@{
                    Utc = $sampleUtc.ToString("O")
                    Disk = $disk.Name
                    ActivePercent = [Math]::Min(100, [Math]::Max(0, 100 - [double]$disk.PercentIdleTime))
                    BytesPerSecond = [long]$disk.DiskBytesPersec
                    QueueLength = [double]$disk.AvgDiskQueueLength
                }
            }
            Set-Content -LiteralPath $readyPath -Value "ready"
            Start-Sleep -Milliseconds 1000
        }
    }
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($sampler.State -eq "Failed" -or [DateTime]::UtcNow -gt $readyDeadline) { throw "Disk sampler did not start." }
        Start-Sleep -Milliseconds 100
    }
    $env:DESKTOPPLUS_INDEX_BENCHMARK_FILES = "$Files"
    dotnet test DesktopPlus.Tests/DesktopPlus.Tests.csproj -c Release --no-build `
        --filter FullyQualifiedName~PreparationAndCachedLookupBenchmark `
        --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=benchmark.trx' `
        --results-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Index benchmark failed." }
    Set-Content -LiteralPath $stopPath -Value "stop"
    $sampler | Wait-Job -Timeout 30 | Out-Null
    if ($sampler.State -ne "Completed") { throw "Disk sampler did not finish." }
    $samples = @($sampler | Receive-Job)
    $samples | Select-Object Utc, Disk, ActivePercent, BytesPerSecond, QueueLength |
        Export-Csv -LiteralPath (Join-Path $OutputDirectory "disk-samples.csv") -NoTypeInformation
    [xml]$trx = Get-Content -LiteralPath (Join-Path $OutputDirectory "benchmark.trx") -Raw
    $output = [string]$trx.TestRun.Results.UnitTestResult.Output.StdOut
    $started = [DateTimeOffset]::Parse([regex]::Match($output, 'Index preparation started (\S+)\.').Groups[1].Value)
    $finished = [DateTimeOffset]::Parse([regex]::Match($output, 'Index preparation finished (\S+)\.').Groups[1].Value)
    $during = @($samples | Where-Object {
        $at = [DateTimeOffset]::Parse($_.Utc)
        $at -ge $started -and $at -le $finished
    })
    $summary = @($during | Group-Object Disk | ForEach-Object {
        [pscustomobject]@{
            Disk = $_.Name
            Samples = $_.Count
            MeanActivePercent = [Math]::Round(($_.Group | Measure-Object ActivePercent -Average).Average, 2)
            MaxActivePercent = ($_.Group | Measure-Object ActivePercent -Maximum).Maximum
            MeanBytesPerSecond = [long]($_.Group | Measure-Object BytesPerSecond -Average).Average
        }
    })
    [pscustomobject]@{
        Files = $Files
        EmptyDirectories = [int]($Files / 4)
        StartedUtc = $started.ToString("O")
        FinishedUtc = $finished.ToString("O")
        TestOutput = $output.Trim()
        PhysicalDisks = $summary
        MeasurementScope = "Total physical disk activity during indexing, including other Windows processes."
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory "summary.json")
    Get-Content -LiteralPath (Join-Path $OutputDirectory "summary.json")
}
finally {
    $env:DESKTOPPLUS_INDEX_BENCHMARK_FILES = $previousFiles
    if ($sampler) { $sampler | Stop-Job; $sampler | Remove-Job }
    Remove-Item -LiteralPath $readyPath, $stopPath -Force -ErrorAction SilentlyContinue
    Pop-Location
}
