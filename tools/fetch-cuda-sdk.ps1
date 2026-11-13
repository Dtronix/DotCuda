#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads the CUDA toolkit components DotCuda generates bindings from, without installing a toolkit.

.DESCRIPTION
    Uses NVIDIA's redistributable archives (https://developer.download.nvidia.com/compute/cuda/redist/): reads
    redistrib_<version>.json, downloads each component for this platform, checks its SHA-256 against the manifest,
    and merges the archives' include/ and bin/ (and lib/ on Linux) into one folder. CUDA 13 puts the Windows DLLs
    in bind. Nothing is installed and CUDA_PATH is
    not touched. The result holds the headers (for the generator) and the libraries (for the generator's export check,
    and usable as DOTCUDA_LIB_DIR).

    Default destination: %LOCALAPPDATA%\DotCuda\sdk\cuda-<version> on Windows, ~/.local/share/DotCuda/sdk/cuda-<version>
    elsewhere. The config/cuda13.*.json files look there (or in DOTCUDA_CUDA13_PATH).

.EXAMPLE
    pwsh tools/fetch-cuda-sdk.ps1 -Version 13.4.3
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Destination,
    [string[]] $Components = @('cuda_cudart', 'cuda_crt', 'cuda_nvrtc', 'libnvjitlink', 'libcublas', 'cuda_nvml_dev'),
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with the progress bar

$base = 'https://developer.download.nvidia.com/compute/cuda/redist'
$platform = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows-x86_64' } else { 'linux-x86_64' }
if (-not $Destination) {
    $root = if ($platform -eq 'windows-x86_64') { Join-Path $env:LOCALAPPDATA 'DotCuda/sdk' } else { Join-Path $HOME '.local/share/DotCuda/sdk' }
    $Destination = Join-Path $root "cuda-$Version"
}
$Destination = [IO.Path]::GetFullPath($Destination)

$manifest = Invoke-RestMethod "$base/redistrib_$Version.json"
Write-Host "CUDA $Version (released $($manifest.release_date)) for $platform -> $Destination"
New-Item -ItemType Directory -Force $Destination | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("dotcuda-sdk-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

$fetched = [ordered]@{}
$statePath = Join-Path $Destination 'dotcuda-sdk.json'
$previous = if ((Test-Path $statePath) -and -not $Force) { Get-Content $statePath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
try {
    foreach ($name in $Components) {
        $component = $manifest.$name
        if (-not $component) { throw "component '$name' is not in redistrib_$Version.json" }
        $entry = $component.$platform
        if (-not $entry) { throw "component '$name' has no $platform archive" }
        if ($previous.components -and $previous.components[$name] -eq $component.version) {
            Write-Host ("  {0,-14} {1,-12} already present" -f $name, $component.version)
            $fetched[$name] = $component.version
            continue
        }

        # Each archive is downloaded and extracted in its own empty directory; only files are copied out of it.
        $dir = Join-Path $work $name
        New-Item -ItemType Directory $dir | Out-Null
        $archive = Join-Path $dir (Split-Path $entry.relative_path -Leaf)
        Write-Host ("  {0,-14} {1,-12} {2,8:N1} MB" -f $name, $component.version, ($entry.size / 1MB))
        Invoke-WebRequest "$base/$($entry.relative_path)" -OutFile $archive -UseBasicParsing
        $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $entry.sha256) { throw "$name`: SHA-256 mismatch (expected $($entry.sha256), got $hash)" }

        $extract = Join-Path $dir 'x'
        if ($archive.EndsWith('.zip')) { Expand-Archive $archive -DestinationPath $extract }
        else { New-Item -ItemType Directory $extract | Out-Null; tar -xJf $archive -C $extract; if ($LASTEXITCODE) { throw "tar failed on $archive" } }
        Remove-Item $archive

        # Archives contain one top-level folder (<name>-<platform>-<version>-archive) with include/, bin/, lib/, LICENSE.
        $top = Get-ChildItem $extract -Directory | Select-Object -First 1
        # Windows lib/ holds static and import .lib files nobody here links against (~2 GB); on Linux it holds the .so files.
        $subs = if ($platform -eq 'windows-x86_64') { 'include', 'bin' } else { 'include', 'bin', 'lib' }
        foreach ($sub in $subs) {
            $src = Join-Path $top.FullName $sub
            if (Test-Path $src) { Copy-Item (Join-Path $src '*') (Join-Path $Destination $sub) -Recurse -Force -ErrorAction Stop -Container:$true; }
        }
        $license = Get-ChildItem $top.FullName -File -Filter 'LICENSE*' | Select-Object -First 1
        if ($license) {
            New-Item -ItemType Directory -Force (Join-Path $Destination 'licenses') | Out-Null
            Copy-Item $license.FullName (Join-Path $Destination "licenses/$name-$($license.Name)") -Force
        }
        Remove-Item $dir -Recurse -Force
        $fetched[$name] = $component.version
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

[ordered]@{ version = $Version; platform = $platform; components = $fetched } | ConvertTo-Json | Set-Content $statePath
$cudaH = Join-Path $Destination 'include/cuda.h'
if (-not (Test-Path $cudaH)) { throw "cuda.h not found in $Destination/include" }
$cudaVersion = (Select-String -Path $cudaH -Pattern '^#define CUDA_VERSION (\d+)').Matches[0].Groups[1].Value
Write-Host "Done: CUDA_VERSION $cudaVersion in $Destination"
