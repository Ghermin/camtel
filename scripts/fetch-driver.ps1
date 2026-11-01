# Downloads the prebuilt virtual camera DLLs from the latest GitHub release into
# windows/CamTel/driver so the Windows app can be built locally without Visual Studio C++.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'windows\CamTel\driver'
$zip = Join-Path $env:TEMP 'camtel-driver.zip'
gh release download -R Ghermin/camtel -p camtel-driver.zip -O $zip --clobber
Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
Expand-Archive $zip -DestinationPath $dest
Get-ChildItem -Recurse $dest | Select-Object FullName, Length
