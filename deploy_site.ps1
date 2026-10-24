# Met en ligne le site S3bopus sur s3btify.com/s3bopus (même serveur Caddy que S3btify).
#   powershell -ExecutionPolicy Bypass -File .\deploy_site.ps1          (-DryRun : préparer sans rien envoyer)
#
# 1. vérifie que les fichiers de site\static\downloads sont ceux annoncés par index.html (SHA-256) ;
# 2. prépare un paquet dans %TEMP%, l'envoie par scp sur l'hôte Proxmox, qui le pousse dans le conteneur de S3btify (pct push)
#    où site\deploy\ct_update.sh installe :
#    site dans /usr/share/caddy/s3bopus, compteur (service s3bopus-counter, port 8091), « import » ajouté au Caddyfile
#    (sauvegardé et validé avant le rechargement). Le site et le compteur de S3btify ne sont pas touchés.
# Prérequis : OpenSSH (Windows 11). Le mot de passe root est demandé deux fois (scp puis ssh) sans clé SSH.
param(
    [string]$Server = "root@192.168.6.94",   # hôte Proxmox
    [int]$Container = 212,                    # conteneur LXC de S3btify (Caddy)
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$web = "$root\site\static"
$index = [IO.File]::ReadAllText("$web\index.html")

foreach ($f in Get-ChildItem "$web\downloads" -File) {
    $sha = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
    if ($index -notmatch [regex]::Escape($f.Name) -or $index -notmatch $sha) { throw "index.html ne correspond pas à $($f.Name) (nom ou SHA-256) : mettre la page à jour d'abord" }
}
foreach ($name in [regex]::Matches($index, 'downloads/([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) {
    if (-not (Test-Path "$web\downloads\$name")) { throw "Fichier manquant dans site\static\downloads : $name" }
}

$utf8 = New-Object System.Text.UTF8Encoding $false
function Copy-Lf($src, $dst) { [IO.File]::WriteAllText($dst, ([IO.File]::ReadAllText($src) -replace "`r`n", "`n"), $utf8) }

$stage = Join-Path $env:TEMP "s3bopus-deploy"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory "$stage\site\www" | Out-Null
$v = [long](($web | Get-ChildItem -File | Measure-Object LastWriteTimeUtc -Maximum).Maximum.ToFileTimeUtc() / 10000000)
foreach ($f in Get-ChildItem $web -File) {
    if ($f.Extension -in ".html", ".css", ".js") {
        Copy-Lf $f.FullName "$stage\site\www\$($f.Name)"
        if ($f.Extension -eq ".html") {  # numéro de version = dernière modification : jamais un vieux fichier en cache
            $p = "$stage\site\www\$($f.Name)"
            [IO.File]::WriteAllText($p, ([IO.File]::ReadAllText($p) -replace '(app\.js|style\.css)\?v=\d+', "`$1?v=$v"), $utf8)
        }
    }
    else { Copy-Item $f.FullName "$stage\site\www\" }
}
Copy-Item "$web\downloads" "$stage\site\www\downloads" -Recurse
Copy-Item "$web\img" "$stage\site\www\img" -Recurse
Copy-Lf "$root\site\counter\counter.py" "$stage\site\counter.py"
Copy-Lf "$root\site\deploy\s3bopus.caddy" "$stage\site\s3bopus.caddy"
& "$env:SystemRoot\System32\tar.exe" -czf "$stage\site.tgz" -C "$stage" site
if ($LASTEXITCODE -ne 0) { throw "tar a échoué" }
Remove-Item -Recurse -Force "$stage\site"
Copy-Lf "$root\site\deploy\ct_update.sh" "$stage\ct_update.sh"
Copy-Lf "$root\site\deploy\host_update.sh" "$stage\host_update.sh"

Get-ChildItem -Recurse -File $stage | ForEach-Object { "   {0,-30} {1,9:N0} Ko" -f $_.FullName.Substring($stage.Length + 1), ($_.Length / 1KB) }
if ($DryRun) { Write-Host "Essai à blanc : rien n'a été envoyé ($stage)"; return }

Write-Host "== Copie vers $Server"
& ssh.exe $Server "rm -rf /tmp/s3bopus-deploy"
& scp.exe -r $stage "${Server}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw "scp a échoué" }
Write-Host "== Installation dans le conteneur $Container"
& ssh.exe $Server "bash /tmp/s3bopus-deploy/host_update.sh $Container"
if ($LASTEXITCODE -ne 0) { throw "L'installation sur le serveur a échoué (voir les messages ci-dessus)" }
Remove-Item -Recurse -Force $stage
Write-Host "== Terminé : https://s3btify.com/s3bopus/"
