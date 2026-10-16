# Inventaire des fenêtres Windows et du portage Linux

Le rapport de référence est [la matrice de portage](../../../.agents/docs/windows-linux-window-inventory.md). Le CSV permet le tri/filtrage ; le JSON garde les deux sens et les détails du recensement.

`Program.cs` utilise le Roslyn livré dans le SDK .NET 10, sans dépendance NuGet supplémentaire. Il analyse les sources C# de `src/app`, `src/plugins`, `src/linux` et `tests/linux`, suivies ou non ignorées par Git, en Release et Debug avec les symboles de plateforme correspondants. Les classes partielles sont fusionnées et l’héritage de Form/Window est résolu transitivement. Le générateur Python ajoute les ressources natives `DIALOG/DIALOGEX`, les pages de paramètres, les fabriques `new Window()`/`new Form()` et les sites explicites de boîtes système.

`port-map.json` contient l’évaluation humaine de chaque fenêtre/page Windows : niveau N0–N4, équivalents concrets, fonctions présentes, écarts, aspect et sources de validation. Une fenêtre recensée n’est jamais automatiquement déclarée portée. Les classes de base sont explicites et exclues des comptes métier. Les contrôles intégrés ne sont pas tous des fenêtres ; les pages de paramètres sont ajoutées séparément parce que la seule présence de FormSettings masquerait leurs écarts.

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

Le contrôle échoue si un type Windows/page manque dans les évaluations, si une entrée évaluée a disparu, si un équivalent Linux/membre ou une référence de test/ADR est introuvable, si un Designer n’est pas classé, si une nouvelle ressource native manque d’évaluation ou si les fichiers générés sont périmés. `--map` permet de vérifier une autre carte d’évaluation sans modifier celle du dépôt.

Les liens aux sources portent leurs lignes courantes. Le digest des sources C# détecte les changements internes à une fenêtre même si son nom ne change pas ; après une modification fonctionnelle, revoir son évaluation avant de régénérer. Les cas Windows spécifiques, les variantes dynamiques, les fenêtres tierces et les assistants de l’installateur sont décrits dans le périmètre du rapport ; les 13 familles globales restent dans le TODO.
