# 0134 — Masquer l’aide visuelle des commandes

Statut : implémenté ; [ciblé, natif source borné et paquet isolé vérifiés](../../../todo.md#images-daide-des-commandes--périmètre-vérifié).

Advanced reprend la case Windows `chkDontSHowHelpImages`, false par défaut. Chaque ouverture de Pull, Merge ou Rebase capture le choix global `DontShowHelpImages`, y compris les enfants de reprise. Une fenêtre déjà ouverte conserve son choix pendant les changements de mode.

Masquer retire toute l’aide, son bouton Show/Hide et sa colonne ; les options/actions occupent l’espace libéré, footer Pull compris. Le libellé Stash reste sur une ligne avec largeur naturelle et minimum ; le minimum du footer est mesuré une fois à Opened, après application des styles/fontes, pour conserver les traductions entières. Le choix global ne change ni ne sauvegarde les états locaux Pull/MergeBranches/Rebase. Après réactivation, chaque fenêtre retrouve son propre repli antérieur. Un profil illisible laisse l’aide visible sans réparer ses octets ; Advanced garde sa barrière d’édition explicite.

Repères : `OriginalCommandHelpVisibility`, constructeurs `PullDialog/Layout`, `MergeBranchDialog`, `RebaseDialog`, `AdvancedSettingsPage/Session/Values` ; `OriginalCommandHelp` et son store local restent distincts. `HelpImagesPreferencesTests` cible les vrais consommateurs, les dimensions et la conservation des profils/index/journal ; les tests d’aide et de workflows antérieurs restent requis.

Ce neuvième champ ne clôt ni Console Windows, ni AlwaysAdvanced hors Remotes, ni la parité des trois workflows. Les preuves de suites, rendu/natif et paquet sont séparées dans TODO.
