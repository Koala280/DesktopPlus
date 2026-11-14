param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = "Stop"
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$bytes = [IO.File]::ReadAllBytes($Executable)
function U16([int]$Offset) { [BitConverter]::ToUInt16($bytes, $Offset) }
function U32([int]$Offset) { [BitConverter]::ToUInt32($bytes, $Offset) }
if ((U16 0) -ne 0x5A4D) { throw "Not a Windows executable" }
$pe = [int](U32 0x3c)
if ((U32 $pe) -ne 0x4550) { throw "Invalid PE signature" }
$sections = U16 ($pe + 6)
$optionalSize = U16 ($pe + 20)
$optional = $pe + 24
$magic = U16 $optional
$directories = if ($magic -eq 0x20b) { $optional + 112 } elseif ($magic -eq 0x10b) { $optional + 96 } else { throw "Unsupported PE header" }
if ((U32 ($directories + 14 * 8)) -ne 0) { throw "Executable contains a CLR header" }
if ((U16 ($optional + 68)) -ne 2) { throw "Release executable must use the Windows GUI subsystem" }
function RvaOffset([uint32]$Rva) {
    for ($index = 0; $index -lt $sections; $index++) {
        $section = $optional + $optionalSize + $index * 40
        $address = U32 ($section + 12)
        $length = [Math]::Max((U32 ($section + 8)), (U32 ($section + 16)))
        if ($Rva -ge $address -and $Rva -lt ($address + $length)) {
            return [int]((U32 ($section + 20)) + $Rva - $address)
        }
    }
    throw "Unmapped PE address: $Rva"
}
$imports = New-Object 'Collections.Generic.List[string]'
$descriptor = RvaOffset (U32 ($directories + 8))
while ((U32 ($descriptor + 12)) -ne 0) {
    $start = RvaOffset (U32 ($descriptor + 12))
    $end = $start
    while ($bytes[$end] -ne 0) { $end++ }
    $imports.Add([Text.Encoding]::ASCII.GetString($bytes, $start, $end - $start))
    $descriptor += 20
}
foreach ($library in $imports) {
    if ($library -match '(?i)^(mscoree|coreclr|hostfxr|desktopplus_core|webview2loader|vcruntime\d*|msvcp\d*)\.dll$') {
        throw "Unexpected runtime dependency: $library"
    }
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion
Write-Host "Verified native Windows GUI executable: $version, $($bytes.Length) bytes, no CLR header."
Write-Host "Imports: $($imports -join ', ')"
