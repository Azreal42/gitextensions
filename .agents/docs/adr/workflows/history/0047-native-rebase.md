# ADR 0047 · Rebase et reprise commune avec Pull

Statut : implémenté et vérifié localement, non livré ; audit natif, paquet et limites dans le [TODO](../../../todo.md).

Besoin : retrouver les choix et actions du Rebase Windows, depuis la sélection History ou après un Pull interrompu. Deux commits sélectionnés définissent la borne exclue et la destination ; Browse prépare les options, le menu contextuel peut démarrer après confirmation.

Git conserve la séquence et l’état du rebase. L’éditeur affiche son todo complet, y compris les instructions de merges/autosquash, sans le reconstruire en liste plate. Après fermeture, la reprise relit les dossiers Git du worktree et les options sauvegardées ; un rebase externe reste récupérable sans prétendre connaître ses options initiales.

Messages et todo utilisent le codec strict Git ; une édition obsolète ou non représentable est refusée avant remplacement. Fermer laisse le rebase en attente. Pull ne propose sous-modules et réapplication de son stash qu’après une reprise réussie ; Abort conserve ce stash.

Repères : MainWindow.Rebase, RebaseDialog, NativeRebaseEditorApplication, GitRepository.Rebase.*, GitRebaseNativeEditor, PullDialog.
