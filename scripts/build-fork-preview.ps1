$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$version = '1.2.7-keyban.1'
$publish = [IO.Path]::GetFullPath('out/fork-publish')
$release = [IO.Path]::GetFullPath('out/fork-release')
New-Item -ItemType Directory -Force $publish, $release | Out-Null
dotnet publish src/StrongholdAssistant.Host -c Release -r win-x64 --self-contained true -p:ReleaseBuild=true -p:ForkPreview=true -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
foreach ($name in @('LICENSE','LICENSE.upstream','NOTICE','ASSETS.md')) { Copy-Item $name $publish }
Copy-Item docs/key-ban-risk.md (Join-Path $publish 'FORK-NOTES.md')
foreach ($name in @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll')) {
  Copy-Item (Join-Path ([Environment]::SystemDirectory) $name) (Join-Path $publish 'runtimes/win-x64/native')
}
# Only trim generated output; keep native Maa libraries, plugins and game data.
Get-ChildItem (Join-Path $publish 'runtimes') -Directory | Where-Object Name -ne 'win-x64' | Remove-Item -Recurse -Force
foreach ($path in @('StrongholdAssistant.exe','ui/index.html','data/operators.json','runtimes/win-x64/native')) {
  if (!(Test-Path (Join-Path $publish $path))) { throw "Missing package component: $path" }
}
$zip = Join-Path $release "StrongholdAssistant-v$version-win-x64.zip"
& scripts/package-zip.ps1 -Publish $publish -Zip $zip
# Verify archive round-trip and basic executable startup on the Windows CI runner.
$verify = [IO.Path]::GetFullPath('out/fork-verify')
Expand-Archive $zip $verify
if ((Get-FileHash (Join-Path $verify 'StrongholdAssistant.exe')).Hash -ne (Get-FileHash (Join-Path $publish 'StrongholdAssistant.exe')).Hash) { throw 'Archive executable checksum mismatch' }
$proc = Start-Process (Join-Path $verify 'StrongholdAssistant.exe') -WorkingDirectory $verify -PassThru
Start-Sleep 15
$proc.Refresh()
if ($proc.HasExited) { throw "Startup smoke test exited early: $($proc.ExitCode)" }
Stop-Process -Id $proc.Id
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.Encoding]::ASCII)
