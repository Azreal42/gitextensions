# ADR 0031 · Browse selon la référence Windows

Statut : accepté pour Browse guidé par la Photo 1 ; fidélité globale et autres fenêtres encore ouvertes.

La Photo 1 fournie (1280 × 752) devient la référence de disposition. Une barre compacte conserve ses commandes dans un débordement à largeur réduite. Navigation par dossiers, recherche et catégories, lignes de 20 px avec références avant le sujet et graphe limité aux voies visibles sans en-tête : garder ces repères et les auteurs/initiales, dates relatives et palettes originales. Les positions Commit dessous/gauche/droite déjà choisies restent conservées.

L'arbre et les filtres utilisent les vrais objets Git ; Working directory/Commit index sont des lectures sans staging. Le surlignage d'auteur suit le seul commit sélectionné, reste stable en multisélection et reprend user.email si la sélection est vide. Graphe et cellules partagent sélection/focus/thème. Les boutons de catégories gardent la palette originale pour tous les états, indépendamment de l'accent OS.

À largeur réduite, Ctrl+E peut ouvrir Filter sous la souris immobile. Suspendre seulement le survol synthétique au mapping clavier, relayer une fois le premier vrai mouvement et nettoyer la garde sur clic/navigation/fermeture. Aucun timer ni refocus continu.

Repères : `MainWindow.Browser`, `MainWindow.Toolbar`, `MainWindow.Navigation`, `MainWindow.HistoryPresentation`, `CompactBrowserToolbar`, `CommitGraph`, `RevisionHistoryRow`. Original : `FormBrowse.InitMenusAndToolbars`, `RevisionGridControl.Designer`, providers des colonnes, `RepoObjectsTree`, `LocalBranchTree`, `AuthorRevisionHighlighting`, `RevisionDataGridView`, `LocalizationHelpers` et `GourceAvatarProtocol`.

Preuves de ce lot : [snapshot a8e591f17](../../../../../artifacts/linux/snapshots/a8e591f17/proofs/root-acceptance.json), tests locaux `artifacts/linux/test-results/windows-browse-reference-20261012`. Avatars réseau, colonnes/badges avancés et audit des autres fenêtres restent dans [TODO](../../../todo.md).
