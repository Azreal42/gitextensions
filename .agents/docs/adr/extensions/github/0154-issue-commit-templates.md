# ADR 0154 · Issues GitHub dans les modèles de commit

Statut : accepté.

Besoin : retrouver l’assistant GitHub3 de Windows sans interrompre la rédaction du message. Les issues sont des modèles proposés à l’ouverture de Commit, indépendants des hooks exécutés avant une écriture Git.

Décision : chaque `CommitDialog` capture son dépôt et son profil Hosting, charge les issues assignées avec le client REST existant et conserve une liste privée. Les deux options originales restent dans le même groupe `PluginSettings` que les identifiants Hosting, distinct du token CI Actions. Save fusionne uniquement les modifications explicites.

Le filtre des remotes précède le tri et la limite des propositions. Le menu déjà ouvert garde son snapshot et sa sélection ; la prochaine ouverture prend la nouvelle liste. Aucune arrivée de données ne touche au texte, au caret, à l’undo ou au focus. Le clic passe par la commande de modèle existante ; les notices à modèle vide conservent la sémantique Windows, uniquement au clic explicite.

Closing annule la lecture et joint sa vraie tâche au drainage de Commit, même pendant une opération Git ou après un veto de sauvegarde du draft. Closed retire la liste privée et détache la source d’annulation avant sa destruction, pour rendre les fermetures répétées idempotentes. Aucun enregistrement d’issues dans le registre global des modèles.

Repères : `CommitDialog.GitHubIssues/Templates`, `GitHubIssueHelperSettings`, `GitHubIssueCommitTemplates`, `GitHubHostingClient.GetAssignedIssuesAsync`, `GitHubHostingSettingsDialog`. Le contrôle numérique partagé garde son brouillon brut via `TextConverter` : Save valide la saisie avant normalisation, sans réécrire une ancienne valeur inconnue non éditée.

Transport : [GET issues assignées](https://docs.github.com/en/rest/issues/issues?apiVersion=2022-11-28#list-issues-assigned-to-the-authenticated-user), origine/credentials, pagination et bornes du client Hosting communs ; troncature visible, aucun rendu HTML. Serveur authentifié/Enterprise réel et audit natif étendu restent ouverts ; preuves exécutées dans le TODO.
