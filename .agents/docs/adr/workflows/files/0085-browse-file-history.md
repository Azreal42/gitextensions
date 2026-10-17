# ADR 0085 — Historique fichier dans un Browse indépendant

Statut : accepté sur le périmètre décrit.

## Intention

Parcourir l’évolution d’un fichier sans bloquer le Browse d’origine ni cacher les descendants de la révision choisie. Retrouver les vrais chemins et contenus en conservant les préférences ordinaires de Browse.

## Décision

- Reprendre `UseBrowseForFileHistory=true` : processus Browse indépendant, chemin littéral et présélection séparés. H ne fournit pas FirstId et ne transforme pas l’OID choisi en borne d’ancêtres. L’option OFF conserve le dialogue dédié ; Blame explicite garde sa route.
- Diff initial et navigation cachée sont temporaires, sans sauvegarde implicite des préférences ordinaires. La découverte des noms part de HEAD comme sous Windows, puis alimente le graphe normal sur leur union. Depuis le nom courant (Path A), une ancienne révision retrouve son ancien nom ; partir de l’ancien nom (Path B) ne suit pas un renommage futur. Ces deux oracles ont des ensembles attendus distincts.
- Conserver tous les fichiers changés ; sélectionner dans le premier groupe le choix explicite puis le nom historique/fallback. Garder les endpoints Commit, Index, WorkingTree et EmptyTree distincts lors des lectures, du refresh et du grep. Sans HEAD, Current peut être vide tandis que All conserve les autres références. EmptyTree est un arbre vide explicite, notamment le parent de l’index unborn ; Reset le refuse avant confirmation/mutation.
- Réutiliser Blame dans File tree du mode historique, avec ses options, codec et navigation. Index fournit ses vrais octets stage0, WorkingTree ses octets actuels. Git peut attribuer en hash zéro un nouveau fichier indexé ; sans HEAD, son refus natif reste visible. Dossier/grep/vide affichent le viewer normal sans mémoriser Blame OFF.
- Publier le remplacement Blame atomiquement : ancien document et identité restent ensemble pendant la lecture, ses lignes ne servent pas à naviguer pour la nouvelle demande ; succès complet remplace les deux, erreur courante vide l’aperçu. Annuler les générations périmées, garder le clavier passif et drainer lectures, infobulles et avatars avant nettoyage. La navigation utilise le graphe déjà chargé, avec message si la cible est hors filtre.

## Repères et limites

`FileHistoryWorkflow`, `BrowseApplicationLauncher`, `DesktopStartupArguments`, `MainWindow.FileHistory/FileHistoryBlame/BrowserReads/Lifetime`, `FileHistoryBlameController`, `GitRepository.FileHistoryGraph/ComparisonTree/ComparisonBlame`, `BlameView`, `FileHistoryDialog`. Tests : `BrowseStartupTests`, `FileHistoryWorkflowTests`, `FileHistoryBrowseTests`, `ArtificialHistoryRevisionTests`, `FileHistoryInlineBlameTests`, `FileHistoryEmptyTreeMenuTests`, `FileHistoryGraphScenarios` et `ComparisonEndpointScenarios`. ADR [0011](0011-history-and-blame.md), [0030](blame/0030-byte-safe-blame-and-reading-position.md), [0083](../../ui/history/0083-shared-commit-information.md). Le code reste la source de vérité.

Les réglages partagés et la correction F5 des paires FirstId artificiel/révision réelle sont décrits dans l’[ADR0086](0086-shared-file-history-settings.md). Le dialogue dédié conserve son lecteur direct borné et son menu contextuel incomplet ; H n’envoie aucun FirstId. Autres menus, filtres, thèmes/locales et plateformes à auditer. FormFileHistory conserve N3 sur son périmètre dédié historique ; Browse reste N2, treize familles ouvertes, aucun N4.

## Validation

Source `1e6cc163a7b1b0395f1815050e7ab469c0ef4477`. [Full02 vérifié](../../../../../artifacts/linux/file-history-browse-20261017/full-02/parent-verification.json), [paquet exact vérifié isolément](../../../../../artifacts/linux/snapshots/1e6cc163a/proofs/package-parent-verification.json) ; [revue native parent](../../../../../artifacts/linux/snapshots/1e6cc163a/proofs/native-file-history-parent-review.json) : Un parcours Plan A anglais/clair sous X11 privé : H depuis le nom courant, Browse indépendant, présélection sans borne, premier clic/Up/Down sans réparation, renommages et choix explicite conservé après F5, vrais diffs/arbres Index et Working directory, Blame inline et fermeture normale. Enfant arrêté par PID/start, code de sortie indisponible ; Path B, survie après fermeture du parent, mutations, autres modes/thèmes/locales, Wayland, DPI et accessibilité non audités. Les [six échecs de Full01 sur 14b89](../../../../../artifacts/linux/file-history-browse-20261017/full-01/parent-failure-review.json) restent conservés ; le candidat `14b89` est refusé. Aucun ancien natif n’est transféré à cette source.
