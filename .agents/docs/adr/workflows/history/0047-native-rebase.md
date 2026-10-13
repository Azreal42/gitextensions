# ADR 0047 · Rebase et reprise commune avec Pull

Statut : implémenté et vérifié localement, non livré ; audit natif, paquet et limites dans le [TODO](../../../todo.md).

Besoin : retrouver les choix et actions du Rebase Windows, depuis la sélection History ou après un Pull interrompu. Deux commits sélectionnés définissent la borne exclue et la destination ; Browse prépare les options, le menu contextuel peut démarrer après confirmation.

Git conserve la séquence et l’état du rebase. L’éditeur affiche son todo complet, y compris les instructions de merges/autosquash, sans le reconstruire en liste plate. Après fermeture, la reprise relit les dossiers Git du worktree et les options sauvegardées ; un rebase externe reste récupérable sans prétendre connaître ses options initiales.

Messages et todo utilisent le codec strict Git ; une édition obsolète ou non représentable est refusée avant remplacement. La reprise lit le codec sauvegardé avant la configuration courante ; sans codec fiable, elle garde l’opération et les conflits visibles sans deviner son texte. Abort reste disponible si le codec courant est devenu invalide. Les anciens plans conservent leurs messages préparés lors de Continue/Skip ; leur édition native du todo reste désactivée.

Un journal privé endommagé ne remplace jamais l’état réel de Git : l’opération et Abort restent visibles, sans deviner codec, options ou todo. Un Abort réussi reste un succès même si le journal doit être conservé. Sans rebase actif, une action explicite renomme les journaux dans leur GitDirectory ; fichiers, dossiers et liens restent intacts, puis un nouvel essai est possible.

Fermer laisse le rebase en attente. Une fin au terminal permet un nouvel essai après validation du journal obsolète. Les commandes Rebase de l’application s’excluent entre instances par worktree ; Git au terminal reste extérieur à ce verrou. Pull refuse une session encore active avant autostash/fetch et ne propose sous-modules ou réapplication de son stash qu’après une reprise réussie ; Abort conserve ce stash.

Repères : MainWindow.Rebase, RebaseDialog, NativeRebaseEditorApplication, GitRepository.Rebase.*, GitRebaseNativeEditor, PullDialog.
