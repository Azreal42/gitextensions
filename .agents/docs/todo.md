# TODO

La case cochée exige une preuve dans le code/tests ou un résultat exécuté. Migration complète : toutes les familles ci-dessous couvertes, paquet installé et application démarrée sur Linux.

- [x] Cloner le fork et ses sous-modules ; état initial propre.
- [x] Identifier WinForms/Win32 comme dépendances de runtime ; SDK .NET 10 local.
- [x] Créer documentation C4, arbre ADR, suivi des échecs.
- [x] Noyau Git portable initial : 45 scénarios réels (noms difficiles, binaires, hunks/lignes, conflits, réseau local, signature SSH, rebase interactif, cancellation).
- [x] Reprendre Browse et Commit : panneaux et lignes compactes, icônes et palettes originales, raccourcis historiques ; captures headless et X11 claire/sombre vérifiées, accent OS maîtrisé.
- [x] Filtre historique, notes, bisect, sparse checkout, ouverture sous-dossier/worktree/bare et askpass natifs testés.
- [x] Quatre plugins portés : CreateLocalBranches, DeleteUnusedBranches, FindLargeFiles (analyse), ReleaseNotesGenerator ; 11 scénarios réels.
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

Preuves actuelles : solution Linux Release sans avertissement ; noyau 45 scénarios, plugins 11 scénarios ; 58 tests Desktop couvrant disposition, palettes originales, dialogues Commit/rebase/filtre/sparse/bisect/notes, staging partiel Unicode et askpass. 72 catalogues XLF réemployés. Paquet intermédiaire 177443128 extrait, installé par DPKG dans une racine isolée et lancé sous Xvfb ; APT simulé. Reconstruction avec correctifs filtre/renommage/palette en cours. Cette preuve utilise les bibliothèques/dépendances du host ; APT sur OS vierge et CI distante non exécutés.

Reste concret : dialogues historiques et raccourcis restants ; commit superprojet/branche inline, drafts/historique des messages/templates avancés/options persistées/no-verify/commit vide ; diff image/combined/range-diff et graphes très larges ; 13 plugins/CI/hébergeur restants, réécriture FindLargeFiles et chargement tiers ; préférences et intégrations Linux complètes. Aucun retrait du périmètre.
