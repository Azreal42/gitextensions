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
- [x] Push rapide : Ctrl+Maj+↑ et Maj-clic ; réussite directe, options initialisées après échec/refus. ADR 0038.
- [x] Pull avancé : tags/prune/unshallow, autostash historique et proposition de réapplication ; workflow de sous-modules partagé avec Checkout. ADR 0039.
- [x] Cherry-pick : options mémorisées, parent de merge, choix de révision et dialogue par sélection ; récupération des cibles renommées couvertes. ADR 0040.
- [x] Retrait de sous-module : confirmation, index/configuration préparés sous verrous ; fichiers et dépôt interne conservés. ADR 0041.
- [x] Combo éditable « Branches: » : suggestions Local/Remote/Tag, brouillon conservé, Entrée, expressions, avertissements et Ctrl+E/débordement ; dix parcours natifs clair/sombre sur `a7bdda7d6`. ADR 0042.
- [x] Merge : fenêtre et aide originales, options typées et resolver commun lisant les stages réels ; sans commit et choix Ours vérifiés en natif. ADR 0043.
- [x] Reset/Revert : cinq modes Reset et autre branche, parent de merge, séquence et AutoCommit ; checkout/force et Revert vérifiés en natif. ADR 0044.
- [x] Reprise Cherry-pick : options figées retrouvées après fermeture/redémarrage normaux ; Continue sans commit et restauration exacte Abort/Skip vérifiés en natif. ADR 0045.
- [x] Commit & Push : commit unique, succès distant et conservation du commit après rejet/Cancel vérifiés en natif. ADR 0046.
- [x] Build/tests Linux locaux automatisés ; `.deb` amd64 construit, installé par DPKG isolé et lancé sous Xvfb.

Dernier paquet local vérifié et livré : source `d144dcf69`, `gitextensions_7.2.1~linux-alpha_amd64.deb` (application `7.2.1-linux-alpha`, macOS prévu plus tard). Le [manifeste de livraison](../../artifacts/linux/snapshots/d144dcf69/proofs/delivery-verification-v2.json) contient source complète, checksum et contrôles : 916 tests (270 Core, 35 Plugins, 611 Desktop), zéro échec/ignoré, six projets Release sans diagnostic, 25 étapes de packaging, installation/lancement isolés, APT simulé et repaquetage identique. Les 22 parcours natifs clair/sombre du payload exact couvrent les sous-périmètres Merge/Reset/Revert, reprise Cherry-pick et Commit & Push décrits ci-dessus. La reprise couvre un redémarrage normal ; la résolution Cherry-pick utilise la fixture Git, pas le resolver UI. Les essais rejetés et anomalies du harnais restent conservés hors acceptation. L’ancienne installation hôte reste distincte ; aucun push de ce lot.

Les dix parcours de la combo Branches restent attribués à `a7bdda7d6`. Les audits Push rapide, options Pull, Cherry-pick et retrait de sous-module restent attribués à `0cf3b7026`.

Les preuves des paquets précédents restent dans [les snapshots](../../artifacts/linux/snapshots/). Chaque audit natif garde son SHA et son périmètre : les contrôles courants ne valent pas nouvel audit étendu du sélecteur de branche, de tous les workflows Pull, de Blame ou du reader.

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

## Prochains écarts concrets

- Cherry-pick : audit natif après crash et des autres modes/séries ; destinations imprévues refusées avant rollback, restauration IO interrompue conservée en état Restoring.
- Candidat Rebase `0ea90bef9`, vérifié, non livré : grille et contexte séparés avec sortie Git longue, aide originale défilable, infobulles clair/sombre lisibles. Un todo/done illisible conserve le hash et l’état Git réels ; les actions de continuation sont refusées, Abort reste disponible. [1008 tests vérifiés](../../artifacts/linux/rebase-layout-status-root-checks-20261013-02/local-source-verification.json) (313 Core, 35 Plugins, 660 Desktop), zéro échec/ignoré, six projets Release sans diagnostic ; 95 tests ciblés et [douze rendus headless contrôlés](../../artifacts/linux/rebase-layout-status-root-checks-20261013-02/root-headless-visual-review.json). [Paquet du même commit vérifié](../../artifacts/linux/snapshots/0ea90bef9/proofs/root-candidate-verification-01.json) : 25 étapes réussies, installation/lancement isolés, APT simulé et repaquetage identique. Audit natif du payload encore requis ; le paquet livré reste `d144dcf69`.
- Audit natif Rebase `0ea90bef9` : [parcours sombre du journal accepté](../../artifacts/linux/snapshots/0ea90bef9/proofs/native-rebase-progress-01.json). Grille peuplée séparée du contexte à 500/460 px, fin de sortie Git accessible, infobulle X11 lisible ; restauration canonique, préservation exacte, nouveau démarrage et fermetures normales vérifiés. Un cas sur les quatorze d’origine accepté ; clair et six autres scénarios restent ouverts. [Essai clair non accepté](../../artifacts/linux/snapshots/0ea90bef9/proofs/native-rebase-progress-02.json) faute de capture intermédiaire ; à reprendre. Les quatre cas supplémentaires todo/done illisibles sont préparés, non exécutés.
- Audit natif Rebase `d8ee8f5eb` : [deux parcours de journal clair/sombre](../../artifacts/linux/snapshots/d8ee8f5eb/proofs/native-rebase-progress-01.json), restauration, préservation exacte, nouvel essai et fermetures normales vérifiés. Hash et notice sans journal, sujets CP1252 et todo/done UTF-8 mesurés. Affichage complet refusé : grille peuplée recouvrant le contexte des conflits avec sortie Git longue, infobulle sombre illisible. Zéro cas final accepté ; six autres scénarios restent à auditer dans chaque thème.
- Rebase restant : audit natif complet du payload `0ea90bef9` (menus, éditeur aux codecs distincts, palettes, reprise et todo/done illisibles), avant livraison ; reprise après arrêt brutal réel, sortie Git continue et réglages distribués. Git au terminal reste extérieur au verrou de l’application.
- Merge/Reset/Revert : scripts historiques, sortie continue, réglages distribués et resolver avancé (diffs/renommages/sous-modules).
- Push : scripts Before/After, PR hébergeurs, grille ls-remote directe et sortie continue ; SSH/askpass Git système conservés.
- Pull : sortie Git continue, scripts Before/After, source URL et destination locale Fetch.
- Reflog implémenté et vérifié localement : fenêtre historique HEAD/refs, toutes les entrées, grille, copie SHA, création sans checkout et Reset avec gardes dirty/detached. [1031 tests réussis](../../artifacts/linux/reflog-desktop-root-checks-20261013-05/local-source-verification.json) (321 Core, 35 Plugins, 675 Desktop), zéro échec/ignoré ; huit nouveaux scénarios Core et quinze Desktop, six projets Release sans diagnostic, rendus clair/sombre contrôlés. ADR 0048. Candidat Debian et audit natif du payload exact encore requis ; absent du paquet livré.
- Récupération fsck : [six workflows cartographiés](../../artifacts/linux/tools/fsck-parity-plan-20261013-01/plan.md), à implémenter : scan/filtres, preview/copie, refs sur commit, export blob, LOST_FOUND groupé/cleanup et lost-found/prune.
- Browse : dialogue Clean working directory avec preview, modes ignored/nonignored/all, filtres et portée ; entrée Undo last commit et transmission des deux bornes sélectionnées à Bisect encore absentes.
- Historique/blame : navigateur des fichiers du commit, filtres de grille, difftools, dossiers/sous-modules et audit natif étendu.
- Clone : autocomplétion, aide et sortie/progression Git ; scripts checkout.
- Commit : superprojet/branche inline, templates avancés, validation personnalisée et scripts ; dialogues/raccourcis historiques restants.
- Viewer : recherche inter-fichiers, autres options globales, sélection entre Patch/Word/DFT, encodages des autres contextes de lecture et grep, codecs image et graphes très larges.
- Listes : menus contextuels/difftools, détails/compteurs des sous-modules et noms complets/abrégés.
- Plugins : CI/hébergeurs, FindLargeFiles, chargement tiers, viewer Gource/OpenGL réel, avatars personnalisés et sortie AutoCompile continue.
- Préférences/intégrations Linux complètes ; roundtrip des chemins à octets non UTF-8 et notes reflog personnalisées ambiguës (voir Failure).

Aucune famille ouverte n'est retirée du périmètre.
