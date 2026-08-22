$ErrorActionPreference = 'Stop'
$versionOutput = (& java -version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw 'Java is unavailable. Install JDK 21 and set JAVA_HOME.'
}

if ($versionOutput -notmatch 'version "(?:1\.)?(?<major>\d+)') {
    throw "Unable to determine the Java version: $versionOutput"
}

$major = [int]$Matches.major
if ($major -lt 21) {
    throw "PassCrate Android builds require JDK 21 or newer; detected Java $major."
}

Write-Host "Validated JDK $major."
