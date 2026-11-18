# Собирает Core.exe встроенным в Windows компилятором C#. Ничего не скачивает и не требует установки.
# Номер версии берётся из файла version.txt; -Out нужен только для проверок.
param([string]$Version, [string]$Out)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Version) { $Version = (Get-Content "$here\version.txt" -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Версия должна быть вида 0.1.0, а не '$Version'" }
if (-not $Out) { $Out = "$here\Core.exe" }

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"

# Компилятор не понимает длинные пути, поэтому исходники на время сборки копируются во временную папку
$tmp = Join-Path ([IO.Path]::GetTempPath()) "CoreBuild_$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    Copy-Item "$here\src\*.cs" $tmp
    Copy-Item "$here\catalog.json" $tmp
    "[assembly: System.Reflection.AssemblyVersion(`"$Version.0`")]`r`n[assembly: System.Reflection.AssemblyFileVersion(`"$Version.0`")]`r`n[assembly: System.Reflection.AssemblyProduct(`"core.txt`")]`r`n[assembly: System.Reflection.AssemblyTitle(`"core.txt`")]" | Set-Content "$tmp\_Version.cs" -Encoding UTF8

    $refs = @(
        "$fw\WPF\PresentationFramework.dll",
        "$fw\WPF\PresentationCore.dll",
        "$fw\WPF\WindowsBase.dll",
        "$fw\System.Xaml.dll",
        "$fw\System.dll",
        "$fw\System.Core.dll",
        "$fw\System.Web.Extensions.dll",
        "$fw\System.Windows.Forms.dll",
        "$fw\System.Drawing.dll"
    )
    $a = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', "/out:$tmp\Core.exe")
    $a += $refs | ForEach-Object { "/r:$_" }
    $a += "/resource:$tmp\catalog.json,Core.catalog.json"
    if (Test-Path "$here\assets\core.ico") { Copy-Item "$here\assets\core.ico" $tmp; $a += "/win32icon:$tmp\core.ico" }
    $a += (Get-ChildItem "$tmp\*.cs" | ForEach-Object { $_.FullName })

    & $csc $a
    if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }
    Copy-Item "$tmp\Core.exe" $Out -Force
    "OK: $Out (версия $Version)"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
