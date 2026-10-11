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
- [x] Création/clonage natifs : personnel/central partagé, branche/sans checkout, clone limité conservant les autres branches, erreurs et annulation ; sept scénarios réels et palettes/traductions vérifiées.
- [x] Historique fichier/blame natifs raccordés : renommages littéraux, graphe fusion, révisions/images/save, attribution, navigation et fermeture ; huit scénarios UI/Git. Avatars et position de ligne précédente complétés au checkpoint `0c7ddd43e` (22 tests ciblés, dont huit nouveaux) ; options, menus, informations auteur/committer et préférences XDG partagées complétés par sept scénarios supplémentaires.
- [x] Liste de fichiers du commit, choix du parent de merge, images historiques/index/worktree ; centrage et ajustement testés sur pixels.
- [x] Branches/tags/checkout : cinq dialogues natifs et entrées Commands originales avec icônes/raccourcis ; orphan, changements locaux, tracking, worktrees, GPG et remotes couverts par 18 scénarios réels.
- [x] Proxy/Gource natifs et avatars injectables : portée/config Git et XDG sans mot de passe, vrai processus contrôlé, PNG/caches/annulation et dix downloads simultanés ; 18 tests UI ciblés incluent les quatre lifecycle existants.
- [x] Huit plugins accessibles et deux services liés au dépôt ; calculs Statistics/Impact, rendu des graphiques et cycle des services testés. Compléments plugins et réglages d’avatars personnalisés encore ouverts.
- [x] Dashboard, catégories, recherche, récents/favoris et réglages natifs ; 15 scénarios ciblés, chemins littéraux et annulation transactionnelle, captures claire/sombre.
- [x] Rename/Compare natifs : BASE/branche/index/worktree, outils externes et multisélection ; normalisation originale partagée avec Create/Checkout et sept scénarios supplémentaires.
- [x] Ouverture annulable et sélection cohérente : quatre régressions Main sur de vrais dépôts et processus Git.
- [x] Commit : drafts/merge/amend, historique complet des messages, options persistées et de session, no-verify et trois choix sans index ; 13 nouveaux scénarios réels, fermeture/cancellation et hooks vérifiés.
- [x] Encodage du message/draft/template partagé avec Git : UTF-8 et codecs ASCII compatibles mono-octet, rejet explicite des caractères/codecs incompatibles avant mutation ; metadata/reflog historique testé.
- [x] Checkout : proposition/mémorisation d’update sous-modules après HEAD modifié, dialogue annulable et AutoCompile après succès ; 12 scénarios, y compris fermeture ancêtre et deux solutions.
- [x] Clone : presse-papiers, MRU source/destination, remote de la branche et URL push, confirmation d’ouverture ; dix nouveaux scénarios, chemins littéraux, absence de réseau pour les suggestions et glyphes compacts rendus en clair/sombre.
- [x] Browse : dispositions Commit dessous/gauche/droite, bouton/menu avec icônes originales et préférence XDG ; sept nouveaux scénarios, Ctrl+2, splitters et six captures rendues.
- [x] Selection Filter original de Commit : combo éditable regex/MRU de session, Ctrl+F, listes intactes et Stage all complet ; six scénarios et vrai popup clair/sombre.
- [x] Touches Alt Clone/Checkout : mnémoniques contextuelles XLF, focus initial et icônes Browse originales ; quatre scénarios clavier avec vrai clonage/checkout, deux scénarios de catalogue et captures avec soulignement.
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

Checkpoint source : `2833a554e` reprend les dispositions Browse, Selection Filter regex et touches Alt originales, après les workflows Commit/Clone/Checkout de `803df4784`. Six projets Release sans diagnostic ; 244 tests Desktop sans échec/skip. Preuves : `artifacts/linux/test-results/classic-fidelity/`. Le paquet livré ci-dessous reste au checkpoint précédent jusqu’à sa reconstruction vérifiée.

Dernier paquet vérifié : clone propre `b3cf66a75`, six projets Release sans avertissement ; noyau 54 scénarios, plugins 35 scénarios, 190 tests Desktop sans échec/skip. 72 catalogues XLF réemployés. `.deb` amd64 de 33 898 000 octets installé par DPKG dans une racine isolée et lancé sous Xvfb ; captures X11 extrait/installé claire/sombre inspectées. APT simulé et repaquetage à payload/epoch fixes identique octet par octet. SHA256 : `431b6ed88de33347244ad99faf529606d04d3c3285a90dc713f9d4fa823347fc`. Résultats : `artifacts/linux/snapshots/b3cf66a75/proofs/package-verification.json` ; paquet livré sous `artifacts/linux/deb/`, paquet précédent `898658485` conservé. Dépendances du host utilisées, aucun OS vierge. Les dialogues ont leurs scénarios Git/UI et captures headless ; X11 vérifie Browse. Le graphe All montre le commit docs `fc4aa6e42` copié parmi les refs ; HEAD détaché et binaire restent `b3cf66a75`, clone propre. Gource/OpenGL réel reste ouvert, avatars vérifiés par HTTP injecté.

Reste concret : historique/blame (navigateur des fichiers du commit, filtres de la grille, difftools et options globales du viewer, dossiers/sous-modules) ; clonage (autocomplete, aide et sortie/progression Git) ; scripts checkout ; dialogues et raccourcis historiques restants ; commit superprojet/branche inline, templates avancés/validation personnalisée/scripts ; diff combined/range-diff, familles de codecs image et graphes très larges ; plugins CI/hébergeur, réécriture FindLargeFiles et chargement tiers ; viewer Gource/OpenGL réel, réglages personnalisés d’avatars et sortie AutoCompile continue ; préférences et intégrations Linux complètes. Audit visuel final toujours ouvert pour les autres écrans et gestes ; le filtre d’affichage de FileStatusList reste distinct du Selection Filter porté. Aucun retrait du périmètre.
