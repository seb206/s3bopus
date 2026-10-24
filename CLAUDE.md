# S3bopus

Explorateur de fichiers à deux panneaux (inspiré de Directory Opus). Version courante : 0.0.2. Réponses et commentaires en français.

## Structure
- `src/` : version Windows, WinForms sur .NET Framework 4.8 (`net48`, LangVersion 10), appels Win32 (shell32, CopyFileEx).
- `linux/` : version Linux, Avalonia 11 sur .NET 10, publiée en un seul fichier ; paquets .deb (`build-deb.py`) et .rpm (`build-rpm.sh`).
- `shared/Archives.cs` : lecture des archives et historique des emplacements, lié dans les deux projets (`<Compile Include>`).
- `tests/` : xUnit (net10.0), `cd tests && dotnet test`.
- `site/` : page web statique, compteur (`counter/counter.py`), scripts de déploiement (`deploy/`).

## Compiler
- Windows : `cd src && dotnet build -c Release` → `src\bin\Release\net48\S3bopus.exe`. Les DLL (SharpCompress…) sont intégrées dans l'exe.
- Windows light (ZIP seulement, ~156 Ko) : `dotnet build -c Release -p:Light=true -p:OutputPath=bin\Light\ -p:IntermediateOutputPath=obj\light\` (symbole `LIGHT` dans `Archives.cs`). Après un build light, relancer un build normal (la restauration change).
- Linux : `linux\build.cmd` (SDK .NET 10 + Python) → `linux\s3bopus_<version>_amd64.deb` ; le .rpm : `bash linux/build-rpm.sh <version>` (WSL avec `rpm` et `dpkg`, depuis Windows lancer via PowerShell : Git Bash réécrit les chemins `/mnt/...`).
- Sur Mac : seule la partie `linux/` (Avalonia) se compile ; le projet `src/` demande Windows.

## Conventions
- Textes : `Lang.T(fr, en)`. Thème : `Ui.Dark` et les couleurs de `Ui`. Langue et thème sont appliqués au redémarrage sous Windows.
- Réglages : `%APPDATA%\S3bopus\S3bopus.ini` (Windows), `~/.config/S3bopus/S3bopus.ini` (Linux).
- Chemin « virtuel » dans une archive : `fichier.zip\dossier\sous` (`Archives.Split`). Archive en lecture seule ; F5 / Ctrl+C extraient dans `%TEMP%\S3bopus`, nettoyé au démarrage.
- Mettre la version à jour dans `src/S3bopus.csproj`, `linux/S3bopus.Linux.csproj`, `linux/Core.cs` (`AppInfo`), `linux/build.cmd`.
- SharpCompress ≥ 0.48 (avant : faille zip-slip). Tmds.DBus.Protocol épinglé à 0.21.3.
- Pas de relecture visuelle automatique : l'interface Windows et la fenêtre Linux se vérifient à la main.

## Site et publication
- Site : https://s3btify.com/s3bopus/ (même Caddy que S3btify). Déploiement : `powershell -ExecutionPolicy Bypass -File .\deploy_site.ps1` (`-DryRun` pour préparer seulement). Il envoie à l'hôte Proxmox `root@192.168.6.94`, qui pousse dans le conteneur 212. Compteur propre à S3bopus sur le port 8091.
- Avant de déployer : les fichiers de `site/static/downloads/` (ignorés par git) doivent correspondre aux tailles et SHA-256 de `site/static/index.html`.
- Paquets en Releases GitHub (`gh release create vX.Y.Z --repo seb206/s3bopus …`) : `S3bopus-<v>-win.zip`, `S3bopus-light-<v>-win.zip`, `.deb`, `.rpm`.
- Dépôt public : ne rien y mettre de personnel (mots de passe, `.claude` du profil, captures privées).
