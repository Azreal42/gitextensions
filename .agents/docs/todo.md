# TODO

La case cochée exige une preuve dans le code/tests ou un résultat exécuté. Migration complète : toutes les familles ci-dessous couvertes, paquet installé et application démarrée sur Linux.

- [x] Cloner le fork et ses sous-modules ; état initial propre.
- [x] Identifier WinForms/Win32 comme dépendances de runtime ; SDK .NET 10 local.
- [x] Créer documentation C4, arbre ADR, suivi des échecs.
- [x] Noyau Git portable initial : 30 scénarios réels (noms difficiles, binaires, hunks/lignes, conflits, réseau local, signature SSH, rebase interactif, cancellation).
- [ ] Fidélité du look & feel : disposition, densité, icônes, thèmes, dialogues et raccourcis historiques ; comparaison visuelle et fonctionnelle.
- [ ] Ouvrir/initialiser/cloner, dépôts récents/favoris, historique/graph/ref/filtrage.
- [ ] Status index/worktree, staging fichiers et hunks/lignes, diff texte/image/binaire/combined/range-diff, commit/amend/fixup/squash/templates/signature.
- [ ] Branches/tags/checkout/compare/reset/merge/conflits/cherry-pick/revert/rebase interactif/continue/abort.
- [ ] Fetch/prune/pull/push/force-with-lease, remotes et configuration SSH/proxy.
- [ ] Historique fichier/blame/grep/arbre/export archive et patches.
- [ ] Stashes, sous-modules, worktrees, sparse checkout, reflog, bisect, notes, fsck/cleanup.
- [ ] Préférences globales/locales/distribuées, raccourcis/thèmes/outils externes/scripts/hooks/liens/avatars.
- [ ] Contrat plugins portable, plugins métiers livrés et intégrations CI/hébergeurs.
- [ ] Traductions XLF existantes et accessibilité des écrans portés.
- [ ] Intégration Linux : XDG, terminal/outils PATH, askpass/ssh-agent, actions gestionnaire de fichiers.
- [ ] Build/tests Linux automatisés ; `.deb` construit, dépendances vérifiées, installation et lancement graphique réels.
- [ ] Audit de parité finale contre les écrans/commandes/plugins historiques.

Preuves actuelles : build Desktop sans avertissement ; 27 tests UI/graph/locales, dont disposition classique par coordonnées, diff coloré visible, stage/unstage hunk et ligne Unicode, dialogue rebase ; 72 catalogues XLF réemployés. Première `.deb` extraite/lancée sous Xvfb et reproductible depuis un payload figé. Pas encore d'installation APT réelle ni de parité complète.

En cours : reconstruire le paquet de cette base native ; poursuivre les dialogues historiques, plugins et intégrations OS. Les fonctions restantes restent dans le périmètre.
