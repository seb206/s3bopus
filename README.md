# S3bopus

Explorateur de fichiers **léger à deux panneaux** pour Windows, inspiré de Directory Opus, avec l'aspect de l'Explorateur Windows.

- Un seul `.exe` d'environ 4 Mo, sans installation (utilise le .NET Framework 4.8 déjà présent dans Windows 10/11).
- Version Linux (Debian/Ubuntu) dans `linux/` : paquet `.deb`, mêmes fonctions sauf le menu contextuel Windows, les propriétés et le glisser-déposer.
- Interface en français ou en anglais, thème clair ou sombre (boutons en haut à droite).
- Icônes, types de fichiers, menu contextuel (clic droit), propriétés et corbeille : **ce sont ceux de Windows**.
- Double-clic sur un fichier : il s'ouvre avec le programme associé dans Windows, comme dans l'Explorateur.

## Fonctions principales

### Taille des répertoires
- **F3** (ou bouton *Tailles*) : calcule la taille des dossiers sélectionnés, ou de tous les dossiers du panneau si rien n'est sélectionné.
- Calcul en arrière-plan, avec mise à jour en direct dans la colonne *Taille* (« … » tant que le calcul n'est pas fini).
- Tri par taille possible (clic sur l'en-tête *Taille*).
- La barre d'état donne le total de la sélection, et le nombre de fichiers et dossiers quand un seul dossier est sélectionné.
- Option *Calculer automatiquement la taille des dossiers* (menu ⋯).

### File de transferts
- **F5** : copier vers l'autre panneau. **F6** : déplacer vers l'autre panneau.
- Le glisser-déposer (entre panneaux, depuis ou vers l'Explorateur) et **Ctrl+V** passent aussi par la file.
- Les tâches sont exécutées **une par une**, avec progression, vitesse, temps restant, pause/reprise, annulation, relance, et ordre modifiable.
- Conflits gérés : *Remplacer*, *Ignorer*, *Conserver les deux*, *Remplacer si plus récent*, avec l'option « Appliquer à tous ».
- Un déplacement sur le même disque est instantané (simple renommage).
- Les chemins longs (plus de 260 caractères) sont pris en charge.

### Onglets
- Chaque panneau a ses onglets, comme Directory Opus : **Ctrl+N** nouvel onglet, **Ctrl+W** fermer, **Ctrl+Tab** onglet suivant, clic molette sur un onglet pour le fermer.
- Les onglets sont rouverts au démarrage.

### Archives
- Les fichiers **ZIP, 7z, RAR, tar, tar.gz/bz2/xz** s'ouvrent dans le panneau comme des dossiers (lecture seule).
- **F5** ou **Ctrl+C** copie des éléments hors de l'archive ; double-clic sur un fichier : il est extrait puis ouvert.

### Presse-papiers et historique
- **Ctrl+V** avec du texte crée un fichier `.txt`, avec une image un fichier `.jpg`, prêt à être renommé.
- La flèche ▾ au bout de la barre d'adresse liste les emplacements déjà saisis.

## Raccourcis

| Touche | Action |
|---|---|
| Tab | Changer de panneau |
| Entrée / double-clic | Ouvrir (dossier : y entrer ; fichier : ouvert par Windows) |
| Retour arrière, Alt+↑ | Dossier parent |
| Alt+← / Alt+→ (boutons souris) | Précédent / suivant |
| F2 | Renommer |
| F3 | Taille des dossiers |
| F5 / F6 | Copier / déplacer vers l'autre panneau |
| F7, Ctrl+Maj+N | Nouveau dossier |
| Suppr, F8 (Maj = définitif) | Supprimer (corbeille) |
| Ctrl+C / Ctrl+X / Ctrl+V | Presse-papiers (compatible avec l'Explorateur ; texte ou image collés comme fichier) |
| Ctrl+N / Ctrl+W | Nouvel onglet / fermer l'onglet |
| Ctrl+Tab, Ctrl+Maj+Tab | Onglet suivant / précédent |
| Alt+Entrée | Propriétés Windows |
| Ctrl+L, Alt+D, F4 | Barre d'adresse |
| Ctrl+F | Filtrer le panneau (accepte * et ?) |
| Ctrl+T | Afficher / masquer la file de transferts |
| Ctrl+H | Éléments masqués |
| Ctrl+U | Échanger les panneaux |
| Ctrl+= | Afficher le même dossier dans l'autre panneau |
| Clic molette sur un dossier | L'ouvrir dans l'autre panneau |
| Clic droit | Menu contextuel Windows (Maj+clic droit : menu étendu) |

Les réglages (dossiers, taille de la fenêtre, colonnes, options) sont enregistrés dans `%APPDATA%\S3bopus\S3bopus.ini`.

Ligne de commande : `S3bopus.exe [dossier gauche] [dossier droit]`

## Compiler

Avec le SDK .NET (6 ou plus récent) sous Windows :

```
cd src
dotnet build -c Release
```

L'exécutable est créé dans `src\bin\Release\net48\S3bopus.exe`. Vous pouvez aussi ouvrir `src\S3bopus.csproj` dans Visual Studio 2022.

Version Linux : `linux\build.cmd` (SDK .NET 10 et Python) crée `linux\s3bopus_<version>_amd64.deb`. Tests : `cd tests` puis `dotnet test`.

## Structure

| Fichier | Rôle |
|---|---|
| `MainForm.cs` | Fenêtre principale, barre de commandes, raccourcis |
| `Tabs.cs` | Onglets d'un panneau |
| `../shared/Archives.cs` | Lecture des archives et historique des emplacements (commun Windows / Linux) |
| `FilePane.cs` | Un panneau : liste, navigation, glisser-déposer, renommage, presse-papiers |
| `AddressBar.cs` | Barre d'adresse en fil d'Ariane, comme celle de l'Explorateur |
| `TransferQueue.cs` | Moteur de la file de transferts (CopyFileEx, MoveFileEx) |
| `QueuePanel.cs` | Affichage de la file et fenêtre de conflit |
| `DirSizes.cs` | Calcul et cache des tailles de dossiers |
| `ShellContextMenu.cs` | Menu contextuel natif de Windows |
| `Native.cs`, `Util.cs` | Appels Win32, icônes système, style, réglages |
