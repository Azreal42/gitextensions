# 0024 — Affichage et sélection des fichiers

Décision : reprendre les six modes chemin/extension/statut en arbre ou à plat, leurs réglages partagés et les icônes de provenance originales. Une préférence de présentation doit suivre le développeur entre Browse, Working directory et Commit.

Le filtre d’affichage masque des fichiers ; le Selection Filter de Commit sélectionne parmi les fichiers affichés. Les deux intentions restent indépendantes. « Stage all » / « Unstage all » agissent sur toutes les feuilles retenues, même dans un dossier replié, puis effacent leur filtre d’affichage. Une liste vide ne devient jamais une mutation globale.

La provenance A/B décrit où le changement a eu lieu, indépendamment du sens du patch. Les boutons `! B A =` ne masquent pas le range-diff. Une regex invalide conserve le dernier résultat valide et rend l’erreur visible. Les noms présentés peuvent être abrégés ; Git reçoit toujours les chemins bruts.

Repères : `GitRevisionFileClassifier`, `FileStatusListBuilder`, `FileStatusListView`, `FileStatusListPreferencesStore`, `MainWindow.FileStatus`, `MainWindow.Diffs`, `CommitDialog`.
