[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CadTranslateArguments
)

$scriptPath = Join-Path $PSScriptRoot 'cad_translate.py'
$candidates = @()
$candidates += $env:CAD_TRANSLATE_PYTHON
if ($env:CODEX_BUNDLED_PYTHON) { $candidates += $env:CODEX_BUNDLED_PYTHON }

foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf) -and $candidate -notmatch 'WindowsApps') {
        & $candidate $scriptPath @CadTranslateArguments
        exit $LASTEXITCODE
    }
}

$launcher = Get-Command py -ErrorAction SilentlyContinue
if ($launcher -and $launcher.Source -notmatch 'WindowsApps') {
    & $launcher.Source -3 $scriptPath @CadTranslateArguments
    exit $LASTEXITCODE
}

$python = Get-Command python -ErrorAction SilentlyContinue
if ($python -and $python.Source -notmatch 'WindowsApps') {
    & $python.Source $scriptPath @CadTranslateArguments
    exit $LASTEXITCODE
}

throw 'No real CPython found. Install CPython or set CAD_TRANSLATE_PYTHON; WindowsApps aliases are intentionally refused.'
