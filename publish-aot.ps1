# Publish tts-helper.exe as NativeAOT (self-contained, ~2 MB exe + sherpa-onnx native DLLs) into -OutDir.
# NativeAOT needs the MSVC linker (Visual Studio Build Tools, C++ workload).
#
#   powershell -ExecutionPolicy Bypass -File shared/dotnet/Mso.TtsHelper/publish-aot.ps1 -OutDir <dir>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "Mso.TtsHelper.csproj"
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
$common = @("publish", $proj, "-c", "Release", "-r", "win-x64", "-p:TtsHelperAot=true", "-p:Version=$Version", "-o", $OutDir, "-nologo", "-v", "q")

# The ILCompiler finds link.exe through findvcvarsall.bat -> vswhere/vcvarsall; on some Build Tools installs that
# fails ("'vswhere.exe' is not recognized"). So: load vcvarsall.bat ourselves and use IlcUseEnvironmentalTools;
# fall back to the default lookup only when no vcvarsall.bat exists.
$vcvars = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvarsall.bat", "$env:ProgramFiles\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvarsall.bat" -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -Last 1
if (-not $vcvars) {
    & dotnet @common
    if ($LASTEXITCODE -ne 0) { throw "tts-helper AOT publish failed (NativeAOT needs Visual Studio Build Tools with the C++ workload)" }
}
else {
    $bat = Join-Path ([IO.Path]::GetTempPath()) ("tts-helper-aot-" + [guid]::NewGuid().ToString("N") + ".cmd")
    $argLine = ($common + "-p:IlcUseEnvironmentalTools=true" | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    Set-Content $bat -Encoding ascii -Value "@call `"$($vcvars.FullName)`" amd64 >nul 2>&1`r`n@dotnet $argLine`r`n@exit /b %ERRORLEVEL%"
    try {
        & cmd.exe /c $bat
        if ($LASTEXITCODE -ne 0) { throw "tts-helper AOT publish failed" }
    }
    finally { Remove-Item $bat -ErrorAction SilentlyContinue }
}
Get-ChildItem $OutDir -Filter *.pdb | Remove-Item -Force
foreach ($f in "tts-helper.exe", "sherpa-onnx-c-api.dll", "onnxruntime.dll", "LICENSE-GPL-3.0.txt", "SOURCE.txt") {
    if (-not (Test-Path (Join-Path $OutDir $f))) { throw "tts-helper publish is missing $f" }
}
$ver = & (Join-Path $OutDir "tts-helper.exe") --version
Write-Host "tts-helper AOT: $ver"
