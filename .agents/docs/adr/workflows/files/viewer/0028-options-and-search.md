# ADR 0028 · Options et recherche du visualiseur

Statut : accepté ; validation du prochain paquet en cours.

Besoin : retrouver les choix et gestes historiques sans ajouter de rangées de commandes aux lecteurs portés.

Décision : conserver les boutons compacts contexte/fichier entier, les cases cumulatives de whitespace, le dialogue Find réutilisé par lecteur et la page General/Diff coloring originale. Les options des lecteurs déjà ouverts restent locales. Remember conserve une session ; seuls les défauts explicitement enregistrés survivent au redémarrage, sauf le nombre de lignes de contexte mémorisé, persisté lors de son changement.

La sauvegarde des défauts depuis la grille ou les réglages prend la dernière session partagée. Le mode fichier entier désactive les commandes de contexte sans perdre la valeur choisie. Chercher dans une sélection inclut son dernier caractère ; navigation des occurrences et sélection restent indépendantes dans le lecteur en lecture seule.

Repères : `src/app/GitUI/Editor/FileViewer.cs`, `FindAndReplaceForm`, `DiffViewerSettingsPage`, `RevisionGridMenuCommands`; côté Linux : `DiffViewerComparisonMenu`, `DiffViewerSearch`, `NativeDiffViewerSettingsDialog`, `DiffViewerPreferencesStore`. Les tests des composants et `ViewerHostReadingTests` restent la preuve des interactions.
