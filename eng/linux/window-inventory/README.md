# Inventaire des fenêtres Windows et du portage Linux

Le rapport de référence est [la matrice de portage](../../../.agents/docs/windows-linux-window-inventory.md). Le CSV permet le tri/filtrage ; le JSON garde les deux sens et les détails du recensement. Les dialogues de l’installateur et les sites système ont leurs CSV annexes.

`Program.cs` utilise le Roslyn livré dans le SDK .NET 10, sans dépendance NuGet supplémentaire. Il analyse les sources C# de `src/app`, `src/plugins`, `src/linux` et `tests/linux`, suivies ou non ignorées par Git, en Release et Debug avec les symboles de plateforme correspondants. Les classes partielles sont fusionnées et l’héritage de Form/Window est résolu transitivement. Le générateur Python ajoute les ressources natives `DIALOG/DIALOGEX`, les pages de paramètres, les fabriques `new Window()`/`new Form()` et les sites explicites de boîtes système C# et C++ (MessageBox/TaskDialog).

`port-map.json` contient l’évaluation humaine de chaque fenêtre/page Windows : niveau N0–N4, équivalents concrets, fonctions présentes, écarts, aspect et sources de validation. Les ressources natives C++ sont évaluées dans `native_entries`, avec le même contrôle de couverture que les types C#. Une fenêtre recensée n’est jamais automatiquement déclarée portée. Les classes de base sont explicites et exclues des comptes métier. Les contrôles intégrés ne sont pas tous des fenêtres ; les pages de paramètres sont ajoutées séparément parce que la seule présence de FormSettings masquerait leurs écarts.

Le rapport donne un bilan global, une répartition par famille et une ligne détaillée par écran. Le niveau fonctionnel et l’aspect visuel sont séparés ; aucun pourcentage du travail restant n’est déduit du nombre de fenêtres. Les liens permettent de rejoindre directement les fenêtres, pages, variantes Linux et annexes.

Les équivalents `in_progress` sont séparés de la dernière version évaluée, pour les fenêtres C# comme pour les ressources natives : une nouvelle classe et ses tests non exécutés ne relèvent pas le niveau. Le rapport signale les fichiers applicatifs/tests modifiés ou non suivis, et indique si le code courant correspond à la source de validation. Le relevé Roslyn contrôle la syntaxe et les déclarations ; il ne remplace pas une compilation ni l’exécution des tests de l’application. Les chemins des changements ne comprennent pas les documents générés eux-mêmes.

L’annexe de l’installateur recense chaque `Dialog` WiX du dépôt ainsi que les cibles de `DialogRef`, `Show`, `SpawnDialog` et `NewDialog`, y compris celles fournies par WiX. Leur adaptation APT/DPKG est décrite dans `installer_entries` ; ces écrans spécifiques à MSI ne gonflent pas le nombre de fenêtres de l’application. Une nouvelle cible sans évaluation fait échouer le contrôle.

`validation-reference.json` conserve un résumé de la suite Desktop existante (source, hash du TRX et compte par classe), pour rendre le rapport reproductible dans un clone sans les volumineux artifacts. Il ne déclenche aucun test et n’est pas une certification de parité. Si le TRX original est disponible, son hash est vérifié. Le JSON généré indique aussi si le code Linux courant diffère de la source de validation. Ne mettre à jour cette référence qu’après une vraie nouvelle exécution vérifiée.

Depuis la racine, avec le SDK configuré :

```bash
mkdir -p artifacts/linux/window-inventory
export DOTNET_ROOT="$PWD/.dotnet"
export DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home"
export NUGET_PACKAGES="$PWD/artifacts/nuget"
.dotnet/dotnet run --project eng/linux/window-inventory/WindowInventory.csproj \
  -c Release -- "$PWD" > artifacts/linux/window-inventory/syntax.json
python3 eng/linux/window-inventory/build-report.py \
  --syntax artifacts/linux/window-inventory/syntax.json
python3 eng/linux/window-inventory/build-report.py \
  --syntax artifacts/linux/window-inventory/syntax.json --check
```

Le premier lancement compile le petit outil ; son stdout est exclusivement le JSON d’analyse. Pour un contrôle en CI, compiler d’abord puis utiliser `dotnet run --no-build`. Le projet est isolé des propriétés WinForms et analyseurs de l’application Windows.

Le contrôle échoue si un type Windows/page manque dans les évaluations, si une entrée évaluée a disparu, si un équivalent Linux/membre ou une référence de test/ADR est introuvable, si un Designer n’est pas classé, si une nouvelle ressource native ou cible WiX manque d’évaluation ou si les fichiers générés sont périmés. `--map` permet de vérifier une autre carte d’évaluation sans modifier celle du dépôt.

Les liens aux sources portent leurs lignes courantes. Le digest des sources C# détecte les changements internes à une fenêtre même si son nom ne change pas ; après une modification fonctionnelle, revoir son évaluation avant de régénérer. Les cas Windows spécifiques, les variantes dynamiques, les fenêtres tierces et les assistants de l’installateur sont décrits dans le périmètre du rapport ; les 13 familles globales restent dans le TODO.

Le relevé Roslyn contient l’empreinte SHA256 des octets réellement analysés, avec détection du BOM lors du décodage. Le générateur compare ces empreintes et une liste Git fraîche avant de lire l’AST, puis avant de publier : un fichier ajouté, retiré ou modifié impose de relancer Roslyn. Un ancien JSON sans empreintes est refusé.
