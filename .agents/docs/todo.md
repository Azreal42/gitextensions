# TODO

La case cochée exige une preuve dans le code/tests ou un résultat exécuté. Migration complète : toutes les familles ci-dessous couvertes, paquet installé et application démarrée sur Linux.

- [x] Cloner le fork et ses sous-modules ; état initial propre.
- [x] Identifier WinForms/Win32 comme dépendances de runtime ; SDK .NET 10 local.
- [x] Créer documentation C4, arbre ADR, suivi des échecs.
- [x] Noyau Git portable : 129 scénarios Git et fixtures originales (noms difficiles, binaires, hunks/lignes, encodages stricts, conflits, réseau local, signature SSH, rebase interactif, cancellation).
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
- [x] Selection Filter original de Commit : combo éditable regex/MRU de session, Ctrl+F, listes intactes et Stage all indépendant de ce filtre de sélection ; six scénarios et vrai popup clair/sombre.
- [x] Touches Alt Clone/Checkout : mnémoniques contextuelles XLF, focus initial et icônes Browse originales ; quatre scénarios clavier avec vrai clonage/checkout, deux scénarios de catalogue et captures avec soulignement.
- [x] Diffs combined et range-diff : arbre des parents et groupes BASE/A/B, bornes explicites à 2/3/4 sélections, clavier/contexte, chemins littéraux/bare/annulation ; dix nouveaux scénarios noyau et captures des palettes originales.
- [x] Aperçus de source distincts des patches : numérotation de texte et refus d’indexer le contenu d’un fichier comme un patch ; scénarios Git/UI avec données ressemblant à un diff.
- [x] FileStatusList partagé dans Browse/Working directory/Commit : six modes chemin/extension/statut, dossiers denses, regex d’affichage/MRU, sélection récursive et réglages globaux ; provenance `! B A =`, scores de renommage/copie et icônes originales. Stage all/Unstage all limités aux feuilles filtrées, même repliées ; flags assume-unchanged/skip-worktree relus et liste vide protégée. Tests ciblés : 19 composant, 36 Commit et 31 Main ; captures et popups clairs/sombres inspectés.
- [x] Viewer original : ANSI 16/256/RGB, couleurs Git configurées, zebra/dimmed et différences intra-ligne/ancres ; patch canonique indépendant de l’affichage. Word Diff et Difftastic en lecture seule, colonnes originales, Ctrl+D/T, réglages XDG partagés et menus XLF clairs/sombres. Git/tool contrôlé et fixture originale testés ; vrai Difftastic 0.71.0 vérifié par 98 assertions (colonnes compactes, Unicode, chiffres du code, overrides). Sans ANSI, texte émis conservé intégralement. Binaire publié identique au noyau vérifié ; captures X11 réelles couleur/sans couleur claires/sombres et retour Patch validés.
- [x] Lecture du viewer : Ctrl+G vers la ligne du fichier, Ctrl+E fichier entier, copies historiques et Ctrl+molette ; position conservée au changement de contexte/thème, nouvelle identité réinitialisée. Sélecteur 140×24 et popup ≤250, dialogue compact XLF et palettes originales ; codec de session, BOM et octets exacts du staging. Échec de chargement et preview tronqué protégés, tests Git/UI réels.
- [x] Options et recherche du viewer : commandes et page de réglages originales, options locales/session/défauts durables, Ctrl+F/F3/Shift+F3, mots/casse/région et occurrences. Suite Desktop locale 397/397 ; sept scénarios de comparaison incluent les trois glyphes originaux clairs/sombres, pixels sélectionnés/non sélectionnés, alpha et transitions de thème. Six projets Release sans diagnostic. Validation du nouveau paquet en cours ; paquet accepté ci-dessous inchangé.
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

Checkpoint accepté : `ca86aafd5`. Clone frais exact, six projets Release verrouillés sans diagnostic, noyau 129/129, plugins 35/35 et Desktop 357/357. Core publié identique au noyau exécuté ; 98 assertions avec Difftastic officiel. Acceptation indépendante : `artifacts/linux/snapshots/ca86aafd5/proofs/root-acceptance.json`.

Paquet livré : `artifacts/linux/deb/gitextensions_8.0.0~linux.1_amd64.deb`, 34 022 356 octets ; SHA256 `0e3f09ac5f304a407eca1c49c708c802bb309987bf2cddd83f6db992ee36395e`, version `8.0.0-linux.1+ca86aafd5d7a274ad33266e9da37999d7f1c1f8c`. DPKG isolé et GUI, APT simulé, repaquetage byte-identique, catalogues/notices vérifiés. Huit copies natives claires/sombres sur X11 neuf, sans seed externe ni manager ; patch copié égal au vrai Git. Main/Commit CP1252 : 22 observations réelles de l’index, worktree/config intacts. Trente-deux captures brutes, huit inspectées directement ; palettes originales mesurées, GoTo/popup/ligne181 et sélection visibles. OS vierge, arm64, Wayland seul et CI distante restent ouverts.

Les preuves natives étendues `ba925680b` restent attribuées à ce candidat retenu pour son défaut de presse-papiers. Les 35 fichiers du lot viewer sont identiques au checkpoint accepté ; ses deux seuls changements de production concernent le démarrage X11. Ancien paquet `b76859aa3` archivé et probes rouges conservés. Les vues DFT natives antérieures ne sont pas présentées comme des captures du nouveau paquet.

Reste concret : historique/blame (navigateur des fichiers du commit, filtres de la grille, difftools, autres options globales du viewer et dossiers/sous-modules) ; clonage (autocomplete, aide et sortie/progression Git) ; scripts checkout ; dialogues et raccourcis historiques restants ; commit superprojet/branche inline, templates avancés/validation personnalisée/scripts ; options de viewer restantes (barre flottante et ordre original de ses commandes, caractères invisibles, syntaxe et recherche inter-fichiers, audit de la sélection entre Patch/Word/DFT), encodages des autres lecteurs blame/grep, familles de codecs image et graphes très larges ; plugins CI/hébergeur, réécriture FindLargeFiles et chargement tiers ; viewer Gource/OpenGL réel, réglages personnalisés d’avatars et sortie AutoCompile continue ; préférences et intégrations Linux complètes. Audit visuel final toujours ouvert pour les autres écrans et gestes ; menus contextuels/difftools des listes, détails et compteurs des sous-modules et préférences de noms complets/abrégés encore ouverts. Aucun retrait du périmètre.
