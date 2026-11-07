# Helpers for driving the probe ConPTY host. Dot-source this file.
$script:Raw = Join-Path $PSScriptRoot 'raw.log'

function Read-Raw([switch]$Plain) {
    $fs = [IO.File]::Open($script:Raw, 'Open', 'Read', 'ReadWrite')
    try { $t = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    if ($Plain) {
        $t = $t -replace "`e\][^`a`e]*(`a|`e\\)", '' -replace "`e\[[0-9;?<>=]*[ -/]*[@-~]", ' ' -replace "`e[()][0-9A-Za-z]", '' -replace "`e[=>78]", ''
        $t = $t -replace '[ \t]{2,}', ' '
    }
    $t
}

function Wait-Raw([string]$Pattern, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Read-Raw -Plain) -match $Pattern) { return $true }
        Start-Sleep -Milliseconds 400
    }
    $false
}

function Send-Keys([string]$Text) {
    (Invoke-WebRequest http://127.0.0.1:47813/keys -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($Text)) -UseBasicParsing).Content
}
