# TODO

La case cochée exige une preuve dans le code/tests ou un résultat exécuté. Migration complète : toutes les familles ci-dessous couvertes, paquet installé et application démarrée sur Linux.

- [x] Cloner le fork et ses sous-modules ; état initial propre.
- [x] Identifier WinForms/Win32 comme dépendances de runtime ; SDK .NET 10 local.
- [x] Créer documentation C4, arbre ADR, suivi des échecs.
- [x] Noyau Git portable : 54 scénarios réels (noms difficiles, binaires, hunks/lignes, conflits, réseau local, signature SSH, rebase interactif, cancellation).
- [x] Reprendre Browse et Commit : panneaux et lignes compactes, icônes et palettes originales, raccourcis historiques ; captures headless et X11 claire/sombre vérifiées, accent OS maîtrisé.
- [x] Filtre historique, notes, bisect, sparse checkout, ouverture sous-dossier/worktree/bare et askpass natifs testés.
- [x] APIs d’archive ZIP/TAR, format-patch, apply/am avec reprise de conflit, fsck/statistiques et GC normal ; tests Git réels.
- [x] Liste de fichiers du commit, choix du parent de merge, images historiques/index/worktree ; centrage et ajustement testés sur pixels.
- [x] Huit plugins accessibles et deux services liés au dépôt ; calculs Statistics/Impact, rendu des graphiques et cycle des services testés. Parité des dialogues et avatars Gource encore ouverte.
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
- [x] Build/tests Linux locaux automatisés ; `.deb` amd64 construit, installé par DPKG isolé et lancé sous Xvfb.
- [ ] CI distante, installation APT sur OS vierge et validation arm64/Wayland seul.
- [ ] Audit de parité finale contre les écrans/commandes/plugins historiques.

Preuves actuelles : noyau 54 scénarios, plugins 35 scénarios ; 70 tests Desktop, aucun ignoré. Source `257be49b7` ; Release sans avertissement. 72 catalogues XLF réemployés. Nouveau paquet et preuves d’installation en cours de vérification depuis ce commit propre ; le paquet vérifié précédent reste `e42bdf3dd` jusqu’à remplacement confirmé.

Reste concret : dialogues d’export/patch/maintenance en cours de raccord aux APIs ; dialogues et raccourcis historiques restants ; commit superprojet/branche inline, drafts/historique des messages/templates avancés/options persistées/no-verify/commit vide ; diff combined/range-diff, familles de codecs image et graphes très larges ; plugins CI/hébergeur, réécriture FindLargeFiles et chargement tiers ; fenêtres Proxy/Gource originales, fournisseur d’avatars Gource et sortie AutoCompile continue ; préférences et intégrations Linux complètes. Aucun retrait du périmètre.
