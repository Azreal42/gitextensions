# ADR 0086 — Réglages partagés de l’historique fichier

Statut : accepté sur le périmètre Git/headless et paquet décrit ci-dessous.

Comprendre la même histoire depuis Browse et le dialogue dédié, avec quatre choix durables partagés : Follow actif par défaut, Exact/Full/Simplify inactifs. Garder Exact quand Follow est OFF et Simplify quand Full est OFF ; ces valeurs sont distinctes des filtres de session et de Save as default.

Reprendre les vraies surfaces Windows : Follow/Exact dans Settings General (Exact reste activable ici), Full/Simplify dans Advanced filter ; menu Follow/Exact et dropdown Full/Simplify du dialogue dédié. OK sauvegarde/recharge, Cancel ne change rien ; les menus dédiés appliquent immédiatement et rechargent sans perdre la sélection ni la première flèche. Le stockage XDG atomique avec écriture immédiate est une adaptation Linux ; aucune synchronisation instantanée entre processus n’est promise.

Dans Browse, Follow/Exact concernent la découverte HEAD, sans `--find-copies-harder`. Le graphe normal applique Full seulement avec un filtre de révisions, puis Simplify seulement sous Full ; dates/branches/reflog/first-parent seuls ne les activent pas. Le lecteur brut reste séparé. F5 sur une paire FirstId Index/Working directory et révision réelle garde ses vrais côtés et recharge CommitInfo depuis la sélection retenue, sans substituer HEAD ni réactiver une ancienne paire.

Repères : `FileHistoryPreferencesStore`, `DesktopSettingsDialog`, `HistoryFilterDialog`, `MainWindow.History/FileHistory/ToolsHelp`, `FileHistoryDialog`, `GitLogQuery`, `GitRepository.FileHistoryGraph/BlameHistory`. Tests : `FileHistoryPreferencesTests`, `FileHistoryTraversalSettingsTests`, `FileHistoryInitialComparisonRefreshTests`. Complète l’[ADR0085](0085-browse-file-history.md) ; le code reste la source de vérité.

Le dialogue dédié conserve son lecteur direct borné par révision et son menu contextuel incomplet : les quatre réglages ne le rendent pas équivalent au graphe Windows en deux passes. General reste partielle ; niveaux existants conservés, Browse N2 et FormFileHistory N3 sur son périmètre legacy, treize familles ouvertes, aucun N4.

Source `f4e99bec0fc503cede9996e9c7238f2338d4c94b` : [suites complètes vérifiées](../../../../../artifacts/linux/file-history-options-20261017/full-01/parent-verification.json) et [paquet exact vérifié](../../../../../artifacts/linux/snapshots/f4e99bec0/proofs/package-parent-verification.json). Couverture : Git réel et headless rendus EN/FR clair/sombre, pointeur/clavier et F5 Index/Working directory avec sélection réelle ; cinq phases du paquet à 0 et extraction complète. Audit GUI natif complet de cette tranche non réalisé ; variantes de plateforme et accessibilité restent ouvertes. Les preuves et limites antérieures ADR0085 restent attribuées à leur source.
