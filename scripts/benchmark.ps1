param(
    [ValidateRange(1, 100)]
    [int]$Iterations = 15
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repository 'SqliteUnderTheHood.csproj'
$application = Join-Path $repository 'bin\Release\net10.0\SqliteUnderTheHood.dll'
$database = Join-Path $repository 'sample.db'
$sqlite = Get-Command sqlite3 -ErrorAction Stop

dotnet build $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw 'The application build failed.'
}

$query = 'SELECT count(*) FROM inventory'
$databaseArgument = '"' + $database + '"'
$queryArgument = '"' + $query + '"'
$customArguments = '"' + $application + '" ' + $databaseArgument + ' ' + $queryArgument
$sqliteArguments = $databaseArgument + ' ' + $queryArgument

function Measure-CommandLine {
    param(
        [string]$FileName,
        [string]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "Could not start $FileName."
    }

    $output = $process.StandardOutput.ReadToEnd().Trim()
    $errorOutput = $process.StandardError.ReadToEnd().Trim()
    $process.WaitForExit()
    $timer.Stop()

    if ($process.ExitCode -ne 0) {
        throw "$FileName failed: $errorOutput"
    }

    $process.Dispose()
    return [pscustomobject]@{
        Output = $output
        Milliseconds = $timer.Elapsed.TotalMilliseconds
    }
}

$customWarmup = Measure-CommandLine 'dotnet' $customArguments
$sqliteWarmup = Measure-CommandLine $sqlite.Source $sqliteArguments
if ($customWarmup.Output -ne $sqliteWarmup.Output) {
    throw "The query results differ: custom=$($customWarmup.Output), SQLite=$($sqliteWarmup.Output)"
}

$customTimes = [System.Collections.Generic.List[double]]::new()
$sqliteTimes = [System.Collections.Generic.List[double]]::new()
for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
    if ($iteration % 2 -eq 0) {
        $customTimes.Add((Measure-CommandLine 'dotnet' $customArguments).Milliseconds)
        $sqliteTimes.Add((Measure-CommandLine $sqlite.Source $sqliteArguments).Milliseconds)
    }
    else {
        $sqliteTimes.Add((Measure-CommandLine $sqlite.Source $sqliteArguments).Milliseconds)
        $customTimes.Add((Measure-CommandLine 'dotnet' $customArguments).Milliseconds)
    }
}

function Get-Median {
    param([System.Collections.Generic.List[double]]$Values)

    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 0) {
        return ($ordered[$middle - 1] + $ordered[$middle]) / 2
    }

    return $ordered[$middle]
}

$fileInfo = Get-Item -LiteralPath $database
Write-Output "OS: $([System.Environment]::OSVersion.VersionString)"
Write-Output "CPU: $($env:PROCESSOR_IDENTIFIER)"
Write-Output "dotnet: $((& dotnet --version).Trim())"
Write-Output "SQLite: $((& $sqlite.Source --version).Trim())"
Write-Output "Database: sample.db ($($fileInfo.Length) bytes)"
Write-Output "Query: $query; rows returned: $($customWarmup.Output)"
Write-Output "Runs: $Iterations per program after one warm-up; process startup is included"
Write-Output ("Custom reader median: {0:N2} ms" -f (Get-Median $customTimes))
Write-Output ("SQLite CLI median:   {0:N2} ms" -f (Get-Median $sqliteTimes))
