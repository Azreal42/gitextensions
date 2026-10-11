# TODO

La case cochée exige une preuve dans le code/tests ou un résultat exécuté. Migration complète : toutes les familles ci-dessous couvertes, paquet installé et application démarrée sur Linux.

- [x] Cloner le fork et ses sous-modules ; état initial propre.
- [x] Identifier WinForms/Win32 comme dépendances de runtime ; SDK .NET 10 local.
- [x] Créer documentation C4, arbre ADR, suivi des échecs.
- [x] Noyau Git portable : 54 scénarios réels (noms difficiles, binaires, hunks/lignes, conflits, réseau local, signature SSH, rebase interactif, cancellation).
- [x] Reprendre Browse et Commit : panneaux et lignes compactes, icônes et palettes originales, raccourcis historiques ; captures headless et X11 claire/sombre vérifiées, accent OS maîtrisé.
- [x] Filtre historique, notes, bisect, sparse checkout, ouverture sous-dossier/worktree/bare et askpass natifs testés.
- [x] APIs d’archive ZIP/TAR, format-patch, apply/am avec reprise de conflit, fsck/statistiques et GC normal ; tests Git réels.
- [x] Dialogues d’archive/format-patch/apply-am/résolution/maintenance raccordés ; 13 scénarios UI réels, réglages XDG et choix de révisions conservés.
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

Preuves actuelles : clone propre `ffc32051a`, six projets Release sans avertissement ; noyau 54 scénarios, plugins 35 scénarios, 83 tests Desktop sans échec/skip. 72 catalogues XLF réemployés. `.deb` amd64 installé par DPKG dans une racine isolée et lancé sous Xvfb ; captures X11 claire/sombre inspectées. APT simulé et repaquetage à payload/epoch fixes identique octet par octet. Résultats/checksum : `artifacts/linux/snapshots/ffc32051a/proofs/package-verification.json` ; paquet livré sous `artifacts/linux/deb/`. Dépendances du host utilisées, aucun OS vierge. Le paquet exclut le portage historique fichier/blame encore en chantier après ce checkpoint.

Reste concret : historique fichier/blame en cours de portage/raccord ; dialogues et raccourcis historiques restants ; commit superprojet/branche inline, drafts/historique des messages/templates avancés/options persistées/no-verify/commit vide ; diff combined/range-diff, familles de codecs image et graphes très larges ; plugins CI/hébergeur, réécriture FindLargeFiles et chargement tiers ; fenêtres Proxy/Gource originales, fournisseur d’avatars Gource et sortie AutoCompile continue ; préférences et intégrations Linux complètes. Aucun retrait du périmètre.
