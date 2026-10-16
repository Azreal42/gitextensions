# 0068 · Gestion et création des worktrees

Statut : accepté sur un périmètre ciblé ; audit natif exhaustif ouvert.

Retrouver les deux fenêtres Windows : grille Path/Type/Branch/SHA-1 sans tri, lignes compactes et chemins absents barrés ; création depuis une branche locale existante ou une nouvelle branche au commit courant. Conserver disposition, icônes, traductions, normalisation et propositions de dossier à partir du worktree principal, même depuis un linked worktree. Complétion de dossiers et contrôles classiques partagés avec les autres fenêtres.

Repository, raccourci, barre et arbre utilisent les mêmes actions. Un clic simple sur un nœud sélectionne son commit ; Open et la création proposent un switch supprimable. La barre bascule directement. Le changement de dépôt a lieu après fermeture du modal et libération de Browse, sans checkout de ses branches.

La suppression Windows efface récursivement un dossier puis prune. Linux utilise exclusivement Git : refus du principal/courant et de leurs alias, revalidation de l’identité dossier/.git/administration, protections natives dirty/untracked/submodules. Discard autorise une force ; un verrou exige aussi Override lock. Le double-force natif contourne la propreté : exiger les deux consentements, même si une lecture préalable paraît propre. Une vérification status séparée ne prouve pas cette sûreté.

Reprendre le défaut Windows worktree.useRelativePaths=true s’il est absent, honorer false. Git capable écrit son extension relativeWorktrees : cela peut rendre le dépôt incompatible avec un Git plus ancien. Ne pas prétendre avoir écrit des chemins relatifs sans lire les métadonnées réelles.

Les étapes Git et hooks peuvent avoir des effets partiels même après erreur/annulation. Drainer les processus puis relire les registrations ; ne pas effacer le dossier ou la branche pour simuler un rollback. Les snapshots protègent les sélections et identités ; ils ne constituent pas une transaction contre toutes les mutations externes concurrentes.

Repères : WorktreeDialog, CreateWorktreeDialog, WorktreeTable, WorktreeDialogControls, MainWindow.Worktrees/Navigation, GitRepository.Worktrees.*, WorktreeSnapshot. Référence Windows : FormManageWorktree, FormCreateWorktree, GitUICommands.Worktree*.

Validation : WorktreeDialogTests, WorktreeNavigationTests, WorktreeScenarios ; exécutions et paquet liés à leur source dans le TODO. Les variantes avancées natives absentes des deux fenêtres Windows (move, repair, orphan, lock/unlock séparés) ne sont pas ajoutées à ces écrans. Audit natif étendu et comparaison des raccourcis/copie de cellules du DataGridView restent ouverts ; N3 ne vaut pas parité intégrale.
