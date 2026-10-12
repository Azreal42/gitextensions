# TODO

Une case cochée atteste le sous-périmètre décrit, avec preuve dans le code/tests ou résultat exécuté. Elle ne clôt pas sa famille historique. Migration complète : toutes les familles ouvertes couvertes, audit de parité, paquet installé et application démarrée sur Linux.

## Livré et vérifié

- [x] Fork et sous-modules clonés ; build portable .NET 10, C4, arbre ADR et suivi des échecs en place.
- [x] Noyau Git portable : chemins difficiles, binaires, hunks/lignes, encodages stricts, conflits, réseau local, signature SSH, rebase interactif et annulation.
- [x] Browse/Commit compacts : disposition, icônes, palettes et raccourcis historiques ; thèmes clair/sombre et accent OS vérifiés.
- [x] Filtre historique, notes, bisect, sparse checkout, ouverture sous-dossier/worktree/bare et askpass natifs.
- [x] Archive ZIP/TAR, format-patch, apply/am avec reprise de conflit, fsck/statistiques et GC normal ; dialogues raccordés et préférences XDG.
- [x] Création/clonage personnel/bare partagé, branche/sans checkout, clone limité gardant les branches distantes, erreurs et annulation.
- [x] Suggestions Clone : presse-papiers, MRU, remote de branche/URL push, confirmation d'ouverture ; suggestions locales et chemins littéraux.
- [x] Historique fichier/blame : renommages, graphe fusion, révisions/images/save, attribution, avatars, navigation, menus et préférences partagées.
- [x] Branches/tags/checkout : cinq dialogues, tracking, orphan, changements locaux, worktrees, GPG et remotes ; Rename/Compare et endpoints BASE/index/worktree.
- [x] Sélecteur de branche Browse : même menu nom/flèche, 100 branches locales maximum, hors graphe sélectionnable, relecture des refs, Ctrl+. et clic droit ; checkout direct ou dialogue présélectionné. ADR 0034.
- [x] Checkout : update sous-modules proposé après HEAD modifié, choix mémorisables, dialogue annulable et AutoCompile après succès.
- [x] Dashboard, catégories/recherche, récents/favoris et réglages natifs ; ouverture annulable et sélection cohérente.
- [x] Huit plugins et deux services liés au dépôt ; calculs/graphiques Statistics/Impact, Proxy/Gource natifs et avatars injectables. Compléments encore ouverts.
- [x] Commit : drafts/merge/amend, historique des messages, options durables/session, no-verify, trois choix sans index et hooks.
- [x] Codec strict des messages/drafts/templates selon Git ; caractères incompatibles refusés avant mutation, metadata et reflog historiques.
- [x] Dispositions Commit dessous/gauche/droite, Ctrl+2 ; Selection Filter regex/MRU indépendant du filtre d'affichage ; mnémoniques XLF de Clone/Checkout.
- [x] Diffs texte/image/index/worktree, choix du parent, combined et range-diff ; BASE/A/B et bornes à 2/3/4 sélections conservées.
- [x] Aperçus source distincts des patches ; FileStatusList partagé, six modes, provenance A/B, renommages/copies, filtres/flags et portée de Stage all/Unstage all.
- [x] Viewer : ANSI/couleurs Git, différences intra-ligne et patch canonique séparé ; Word Diff/Difftastic readonly et sortie sans ANSI conservée.
- [x] Lecture : ligne/fichier entier, copies et zoom, position, codec de session/BOM, octets exacts du staging ; échec/troncature protégés.
- [x] Options/recherche du viewer : session/défauts, mots/casse/région/occurrences ; barre flottante, débordement, syntaxe/invisibles et priorité Échap de Commit.
- [x] Blame : codec du contenu distinct des métadonnées UTF-8, identité et position conservées/remappées ; texte inchangé après reload.
- [x] Browse selon la Photo 1 Windows : barre unique/débordement, lignes compactes, refs inline, graphe, catégories/recherche et sélection ; quatre flèches alignées, palette et Ctrl+E sous souris immobile.
- [x] Menu Pull/Fetch : six actions, défaut exclusif sans Git, préférences distinctes, raccourcis depuis les popups, confirmations, portée/prune et conflits. ADR 0032.
- [x] Menu et fenêtre Stash : Create/Manage, all/selected/staged, Keep index/untracked, Apply/Pop/Drop, raccourcis et conflits. ADR 0033.
- [x] Fichiers Stash : cherry-pick du fichier affiché, reset vers stash/premier parent, confirmation et conservation de la sélection/focus après lecture ; raccourci R natif. ADR 0035.
- [x] Push natif : branches/tags/mappings multiples, remote/URL, tracking, récursion, force/lease et reprise après rejet. ADR 0036.
- [x] Grep du commit : codec, filtre de fichiers, navigation ligne/colonne, MRU et aperçus binaires sans coordonnées inventées. ADR 0037.
- [x] Palette radio historique commune : coché, survol et pression vérifiés dans les thèmes clair/sombre.
- [x] Build/tests Linux locaux automatisés ; `.deb` amd64 construit, installé par DPKG isolé et lancé sous Xvfb.

Dernier paquet local vérifié et livré : source `f4ba18594`, `gitextensions_7.2.1~linux-alpha_amd64.deb` (application `7.2.1-linux-alpha`, macOS prévu plus tard). Le [manifeste de livraison](../../artifacts/linux/snapshots/f4ba18594/proofs/delivery-verification.json) contient source complète, checksum et contrôles : 743 tests (182 Core, 35 Plugins, 526 Desktop), zéro échec/ignoré, six projets Release sans diagnostic, installation/lancement isolés, APT simulé et repaquetage identique. Push, Grep, actions de fichiers Stash et palette radio ont été vérifiés nativement en clair/sombre sur le contenu exact du paquet. Ce nouveau paquet n'a pas remplacé l'ancienne version déjà installée sur l'hôte ; aucun push de ce lot.

Les preuves des paquets précédents restent dans [les snapshots](../../artifacts/linux/snapshots/). Chaque audit natif garde son SHA et son périmètre : les contrôles courants ne valent pas nouvel audit étendu du sélecteur de branche, de Pull, de Blame ou du reader.

## Familles encore ouvertes

- [ ] Fidélité du look & feel : disposition, densité, icônes, thèmes, dialogues et raccourcis historiques ; comparaison visuelle et fonctionnelle.
- [ ] Ouvrir/initialiser/cloner, dépôts récents/favoris, historique/graph/ref/filtrage.
- [ ] Status index/worktree, staging fichiers et hunks/lignes, diff texte/image/binaire/combined/range-diff, commit/amend/fixup/squash/templates/signature.
- [ ] Branches/tags/checkout/compare/reset/merge/conflits/cherry-pick/revert/rebase interactif/continue/abort.
- [ ] Fetch/prune/pull/push/force-with-lease, remotes et configuration SSH/proxy.
- [ ] Historique fichier/blame/grep/arbre/export archive et patches.
- [ ] Commandes contextuelles avancées des Stashes, sous-modules, worktrees, sparse checkout, reflog, bisect, notes, fsck/cleanup.
- [ ] Préférences globales/locales/distribuées, raccourcis/thèmes/outils externes/scripts/hooks/liens/avatars.
- [ ] Contrat plugins portable, plugins métiers livrés et intégrations CI/hébergeurs.
- [ ] Traductions XLF existantes et accessibilité des écrans portés.
- [ ] Intégration Linux : XDG, terminal/outils PATH, askpass/ssh-agent, actions gestionnaire de fichiers.
- [ ] CI distante, installation APT sur OS vierge et validation arm64/Wayland seul.
- [ ] Audit de parité finale contre les écrans/commandes/plugins historiques.

## Travail local en cours

- Push rapide : raccourci et Maj-clic, formulaire après échec/refus ; 17 tests Push passent, audit natif/paquet à renouveler. ADR 0038.
- Pull avancé : tags/unshallow et autostash historique ; mise à jour des sous-modules réutilisant le workflow Checkout.
- Cherry-pick : options persistantes, parent de merge, choix de révision et dialogue par commit sélectionné.
- Sous-modules : retrait guidé de l'enregistrement/index, avec conservation des fichiers et du dépôt interne comme Windows.

## Prochains écarts concrets

- Push : scripts Before/After, PR hébergeurs, grille ls-remote directe et sortie continue ; SSH/askpass Git système conservés.
- Pull avancé : tags/unshallow/autostash/sous-modules et sortie Git continue.
- Historique/blame : navigateur des fichiers du commit, filtres de grille, difftools, dossiers/sous-modules et audit natif étendu.
- Clone : autocomplétion, aide et sortie/progression Git ; scripts checkout.
- Commit : superprojet/branche inline, templates avancés, validation personnalisée et scripts ; dialogues/raccourcis historiques restants.
- Viewer : recherche inter-fichiers, autres options globales, sélection entre Patch/Word/DFT, encodages des autres contextes de lecture et grep, codecs image et graphes très larges.
- Listes : menus contextuels/difftools, détails/compteurs des sous-modules et noms complets/abrégés.
- Plugins : CI/hébergeurs, FindLargeFiles, chargement tiers, viewer Gource/OpenGL réel, avatars personnalisés et sortie AutoCompile continue.
- Préférences/intégrations Linux complètes ; roundtrip des chemins à octets non UTF-8 et notes reflog personnalisées ambiguës (voir Failure).

Aucune famille ouverte n'est retirée du périmètre.
