# ADR 0031 · Browse selon la référence Windows

Statut : accepté ; validation locale, paquet natif suivant à valider.

La Photo 1 fournie (1280 × 752) devient la référence de disposition. Une barre de 26 px conserve ses commandes dans un débordement à largeur réduite. La navigation de 195 px regroupe les branches et remotes par dossiers, avec recherche et boutons de catégories. Les lignes de 20 px placent les références avant le sujet ; le graphe réserve seulement ses voies visibles, sans en-tête. Auteurs/initiales, dates relatives et palettes reprennent les sources originales. Les positions Commit dessous/gauche/droite déjà choisies restent conservées.

L’arbre et les filtres utilisent les vrais objets Git. Les entrées Working directory/Commit index lisent les vues correspondantes sans staging. Le surlignage d’auteur suit l’auteur du seul commit sélectionné ; une multisélection conserve cet auteur et une sélection vide reprend user.email. Graphe et cellules partagent leur état de sélection/focus/thème.

Repères : `MainWindow.Browser`, `MainWindow.Toolbar`, `MainWindow.Navigation`, `MainWindow.HistoryPresentation`, `CompactBrowserToolbar`, `CommitGraph`, `RevisionHistoryRow`. Original : `FormBrowse.InitMenusAndToolbars`, `RevisionGridControl.Designer`, `MessageColumnProvider`, `RevisionGraphColumnProvider`, `RepoObjectsTree`, `LocalBranchTree`, `AuthorRevisionHighlighting`, `RevisionDataGridView`, `LocalizationHelpers` et `GourceAvatarProtocol`.

Validation : quatorze nouveaux tests, Git réel, clavier/pointeur/défilement et captures claire/sombre aux dimensions de la photo ; scénarios existants des trois positions conservés. Les attentes de tests suivent désormais le titre dossier/branche et les menus originaux. Avatars réseau, colonnes/badges avancés et fidélité des autres fenêtres restent au TODO général. Preuves locales : `artifacts/linux/test-results/windows-browse-reference-20261012`.
