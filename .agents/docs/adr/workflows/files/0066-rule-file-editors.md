# 0066 · Éditeurs des règles du dépôt

Statut : accepté sur un périmètre ciblé ; audit de parité exhaustive ouvert.

Les développeurs doivent pouvoir éditer les règles partagées (.gitignore/.gitattributes/.mailmap) et les exclusions locales sans quitter Git Extensions, ni déclencher de staging ou de commit implicite. Le même FileViewer doit conserver la saisie, le caret et l’undo pendant les changements de présentation.

`RulesFileEditorDialog` reprend les trois dispositions Windows, leurs aides originales et leurs commandes. `AddToGitIgnoreDialog` partage l’ajout à ignore/exclude, avec aperçu Git annulable et dernière saisie prioritaire. Les menus Repository et FileStatusList réutilisent icônes/traductions et ouvrent ces mêmes fenêtres dans Browse et Commit. La recherche/remplacement étend le viewer partagé ; les diffs restent en lecture seule. FormEditor générique et recherche multi-fichiers restent distincts et incomplets.

La sauvegarde garde encodage strict/BOM, fins de ligne et modes existants, refuse les modifications externes détectées et préserve les liens. Elle écrit sur le handle validé : une défaillance disque ou un écrivain simultané sur le même inode après le dernier contrôle n’est pas une transaction atomique. Les erreurs conservent le buffer modifié ; fermer demande Yes/No/Cancel. L’ajout assisté ne poursuit pas après l’échec de la sauvegarde préalable. Les motifs conservent ordre et doublons, y compris les négations ; un chemin sélectionné est échappé comme littéral. Les acteurs d’aperçu annulés sont drainés avant publication/fermeture, et avant l’ajout effectif.

Le chemin info/exclude vient de Git, notamment dans les worktrees liés ; les nouveaux fichiers utilisent UTF-8/LF. Les motifs par défaut gardent la liste historique, avec surcharge XDG DefaultIgnorePatterns.txt. La grille relit les identités mailmap avec %aN/%aE comme RevisionReader Windows.

Repères : `GitRepository.RulesFiles/IgnorePatterns`, `GitRulesFile`, `GitIgnorePatterns`, `RulesFileMenus`, `MainWindow.RulesFiles`, `CommitDialog.RulesFiles`, `DiffView`, `DiffViewerSearch`. Scénarios : `RulesFileScenarios`, `RulesFileEditorDialogTests`, `EditableFileViewTests`. Restent l’audit natif étendu, les traductions longues, les autres variantes/consommateurs Windows, et les plateformes autres que Linux amd64.

Écart de rendu constaté en headless : le groupe des motifs ne contraint pas encore le champ multiligne et la zone Preview à la répartition verticale Windows. Ce point reste ouvert malgré la validation fonctionnelle.
