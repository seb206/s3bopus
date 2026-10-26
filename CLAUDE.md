# S3bopus

Explorateur de fichiers à deux panneaux (inspiré de Directory Opus). Version courante : 0.0.3. Réponses et commentaires en français.

## Structure
- `src/` : version Windows, WinForms sur .NET Framework 4.8 (`net48`, LangVersion 10), appels Win32 (shell32, CopyFileEx).
- `linux/` : ancienne version Linux (Avalonia 11 sur .NET 10, ~40 Mo), plus proposée sur le site ; remplacée par `x11/`.
- `x11/` : version Linux en C avec Xlib + Xft, sans .NET (60 Ko, `make` dans WSL/Linux avec libx11-dev libxft-dev, `make test` sous Xvfb + xdotool + xclip + imagemagick, libs libx11-dev libxft-dev zlib1g-dev). Archives zip / tar / tar.gz en lecture seule (zlib, F5 extrait ; pas de zip64), Ctrl+V colle une image (format du presse-papiers, sans conversion JPEG) ou un texte (.txt). Pas encore de réglages ni de boutons thème/langue. Paquets : `bash x11/build-packages.sh <version> <révision>` (WSL, via PowerShell) → `.deb` et `.rpm` (glibc ≥ 2.34 : éviter `atol`/`strtol`, ils tirent 2.38 ; vérifier avec `objdump -T`).
- `mac/` : version Mac native, Swift 6 + AppKit, sans .NET (SwiftPM, macOS 13+, binaire universel ~1,4 Mo). Pas d'archives pour l'instant.
- `shared/Archives.cs` : lecture des archives et historique des emplacements, lié dans les deux projets (`<Compile Include>`).
- `tests/` : xUnit (net10.0), `cd tests && dotnet test`.
- `site/` : page web statique, compteur (`counter/counter.py`), scripts de déploiement (`deploy/`).

## Compiler
- Windows : `cd src && dotnet build -c Release` → `src\bin\Release\net48\S3bopus.exe`. Les DLL (SharpCompress…) sont intégrées dans l'exe.
- Windows light (ZIP seulement, ~156 Ko) : `dotnet build -c Release -p:Light=true -p:OutputPath=bin\Light\ -p:IntermediateOutputPath=obj\light\` (symbole `LIGHT` dans `Archives.cs`). Après un build light, relancer un build normal (la restauration change).
- Linux : `x11/build-packages.sh` (voir `x11/` ci-dessus ; depuis Windows lancer via PowerShell : Git Bash réécrit les chemins `/mnt/...`). L'ancien `linux/build.cmd` fabrique encore l'ancien .deb Avalonia.
- Mac : `bash mac/build-app.sh [--dmg]` → `mac/build/S3bopus.app` (signature ad hoc, Command Line Tools suffisent, pas besoin de Xcode). Autotest de la file de transferts : `cd mac && swift build && .build/debug/S3bopus --selftest`.
- Le projet `src/` demande Windows ; `linux/` (Avalonia) se compile aussi sur Mac mais n'est plus la cible du portage.

## Conventions
- `mac/` : textes `Lang.T(fr, en)` (même principe), réglages dans UserDefaults (`com.s3btify.s3bopus`), mise en page racine à la main (`RootView`) : le `NSSplitView` se laisse mal contraindre par Auto Layout. Raccourcis : F-keys comme Windows, ⌘ pour le reste (⌘T onglet, ⌘J file, ⌘⌫ corbeille, ⌥⌘C/X copier/déplacer vers l'autre panneau).
- Textes : `Lang.T(fr, en)`. Thème : `Ui.Dark` et les couleurs de `Ui`. Langue et thème sont appliqués au redémarrage sous Windows.
- Réglages : `%APPDATA%\S3bopus\S3bopus.ini` (Windows), `~/.config/S3bopus/S3bopus.ini` (Linux).
- Chemin « virtuel » dans une archive : `fichier.zip\dossier\sous` (`Archives.Split`). Archive en lecture seule ; F5 / Ctrl+C extraient dans `%TEMP%\S3bopus`, nettoyé au démarrage.
- Mettre la version à jour dans `src/S3bopus.csproj`, `linux/S3bopus.Linux.csproj`, `linux/Core.cs` (`AppInfo`), `linux/build.cmd`, `mac/Sources/S3bopus/Core.swift` (`AppInfo`).
- SharpCompress ≥ 0.48 (avant : faille zip-slip). Tmds.DBus.Protocol épinglé à 0.21.3.
- Pas de relecture visuelle automatique : l'interface Windows et la fenêtre Linux se vérifient à la main.

## Site et publication
- Site : https://s3btify.com/s3bopus/ (même Caddy que S3btify). Déploiement : `powershell -ExecutionPolicy Bypass -File .\deploy_site.ps1` (`-DryRun` pour préparer seulement). Il envoie à l'hôte Proxmox `root@192.168.6.94`, qui pousse dans le conteneur 212. Compteur propre à S3bopus sur le port 8091.
- Avant de déployer : les fichiers de `site/static/downloads/` (ignorés par git) doivent correspondre aux tailles et SHA-256 de `site/static/index.html`.
- Paquets en Releases GitHub (`gh release create vX.Y.Z --repo seb206/s3bopus …`) : `S3bopus-<v>-win.zip`, `S3bopus-light-<v>-win.zip`, `.deb`, `.rpm` (X11, révision `-3`) + binaire portable `s3bopus-<v>-linux-x86_64` (statique), `.dmg` Mac.
- Dépôt public : ne rien y mettre de personnel (mots de passe, `.claude` du profil, captures privées).
