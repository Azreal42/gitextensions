# TODO

Mise à jour : 17 octobre 2026. Les validations ci-dessous restent liées à leur source et à leur périmètre.

Une case cochée atteste le sous-périmètre décrit, avec preuve dans le code/tests ou résultat exécuté. Elle ne clôt pas sa famille historique. Migration complète : toutes les familles ouvertes couvertes, audit de parité, paquet installé et application démarrée sur Linux.

## État détaillé par fenêtre

La [matrice exhaustive Windows → Linux](windows-linux-window-inventory.md) remplace toute lecture « une fenêtre existe donc elle est complète ». Référence du code recensé et de la suite Desktop complète `749f88339` : 99 fenêtres/dialogues métier (98 WinForms et 1 ressource askpass C++), hors 5 bases WinForms ; 32 pages de paramètres, hors 4 bases. Fenêtres : 6 N0 absentes, 16 N1 substituts minimaux, 26 N2 partielles, 49 N3 fonctionnelles sur un périmètre testé, 2 spécifiques Windows. Pages : 16 N0, 8 N1, 7 N2, 1 spécifique Windows. Aucun N4 de parité complète auditée. Ces nombres ne sont pas un pourcentage du travail de migration.

L’inventaire contient aussi les 85 types Window Avalonia (dont 3 bases abstraites), 32 fabriques Linux sans classe dédiée et 366 sites de boîtes système/confirmations Windows, dont le Yes/No askpass natif C++. Les 13 dialogues de l’installateur Windows et 9 dialogues WiX référencés sont listés à part avec leur adaptation Debian. Chaque type/page est évalué explicitement ; le générateur signale toute nouvelle entrée non classée ou référence disparue. Les 189 fichiers Designer sont réconciliés. Les tests associés sont ceux des exécutions de référence, pas un nouveau test ni un audit complet de chaque écran.


Écarts concrets à traiter selon cette matrice : audit natif étendu du gestionnaire et de l’assistant de conflit de sous-module ; PR/hébergement et CI ; configuration des raccourcis/scripts/langue/fontes ; sorties/cache/stacks/export du journal Git ; audit natif étendu de l’askpass (Wayland, DPI/accessibilité, matériel FIDO et invites localisées). Le tri/priorités et GitDefault sans option sont portés sur le périmètre ADR 0082 ; CommitInfo est porté sur le périmètre partagé de l’ADR 0083, suites complètes et natif Browse ciblé vérifiés ; remotes vides/inactifs et paramètres globaux/locaux/distribués restent ouverts. Les familles globales restent ouvertes.

## Livré et vérifié

- [x] Navigate Parent/Child : deux piles réversibles Windows, ancre de dernier clic, voisins du graphe chargé, erreur traduite hors grille et destinations Index/WT typées ; première flèche dans les fichiers, récence et sélection WT/Index après F5 (Git/headless ciblé). ADR0088, périmètre et limites ci-dessous.

- [x] Recherche incrémentale dans la grille : frappe directe, message complet/auteur/références, préfixe SHA, Alt+Haut/Bas, notice/aide et destinations artificielles typées ; ADR0087, validation et limites ci-dessous.

- [x] Réglages d’historique fichier partagés : Follow/Exact, Full/Simplify, valeurs désactivées conservées ; F5 recharge la vraie paire et CommitInfo. ADR0086, couverture Git/headless ; audit GUI natif ouvert.

- [x] Historique fichier par défaut dans Browse indépendant : présélection sans borne, Diff initial/navigation temporaire, noms historiques et endpoints réels ; Blame inline atomique et fermeture drainée. ADR 0085.

- [x] Sockets du lancement Git et de la console : répertoire privé, limite UTF-8 et repli court sous TMPDIR long ; suites complètes et paquet exact vérifiés, commandes/annulations et ouverture X11 sous TMPDIR longs. ADR 0084, sans promotion de niveau.

- [x] CommitInfo partagé : détails, notes, références contenant, tags annotés directs et six options, sans bloquer la sélection. Suites complètes et paquet exact vérifiés ; natif Browse anglais/clair/X11 ciblé, autres hôtes et variantes ouverts. ADR 0083.

- [x] Tri/priorités Windows : cinq réglages, neuf critères natifs et deux sens, limites après tri, priorités avant dossiers, menus communs et persistance explicite. GitDefault sans flag et graphe Windows distinct du lecteur ; ADR 0082, page Sorting N2 et audit étendu ouvert.

- [x] Sélecteur rapide de références : F2/Suppr, choix local/remote/tag sans ambiguïté, dialogues partagés, première touche et retour au clavier ; ADR 0074, périmètre N3 ciblé.

- [x] Askpass : saisie/Yes-No/empreinte, autorisation de clé et notification distinctes ; protocole natif, fermeture sûre et vrai helper vérifiés. ADR 0072 ; audit global ouvert.

- [x] Assistant de conflit de sous-module : quatre révisions Windows, Browse indépendant, checkout commun contenant les deux côtés, un seul staging vérifié ; routage partagé et fermeture drainée. ADR 0071 ; audit GUI natif étendu ouvert.

- [x] Gestionnaire de sous-modules : grille/détails Windows, séparateur persistant, Update/Sync sur la sélection, Pull réel du module, Add/Remove partagés, statuts natifs et annulation drainée. ADR 0070 ; audit natif exhaustif ouvert.

- [x] Ajout de sous-module : dialogue Windows compact, Source/MRU SuggestAppend partagé, branche éditable/requête annulable, Force et Process natif, erreurs/effets partiels conservés. ADR 0069 ; audit natif exhaustif ouvert.

- [x] Worktrees : fenêtres Windows de gestion/création, grille compacte, chemins/branches, actions Repository/arbre/barre, switch après modal, Git natif et consentements de suppression. Portage ciblé testé ; ADR 0068.

- [x] Remotes : deux onglets Windows, gestion active/inactive et tracking, fenêtre partagée Browse/Pull/Push ; conserver toutes les valeurs Git, Fetch après Save et couleurs réellement utilisées. ADR 0067.

- [x] Éditeurs de règles et assistant ignore/exclude : aides, dispositions et motifs Windows, viewer partagé modifiable, recherche/remplacement, sauvegarde/dirty-close et aperçu Git annulable. Portage ciblé testé ; ADR 0066.
- [x] Fork et sous-modules clonés ; build portable .NET 10, C4, arbre ADR et suivi des échecs en place.
- [x] Noyau Git portable : chemins difficiles, binaires, hunks/lignes, encodages stricts, conflits, réseau local, signature SSH, rebase interactif et annulation.
- [x] Requêtes Git de version/aide : aucune sonde askpass ajoutée ; arguments et octets natifs conservés, précédence des helpers des vraies commandes couverte par les tests.
- [x] Browse/Commit compacts : disposition, icônes, palettes et raccourcis historiques ; thèmes clair/sombre et accent OS vérifiés.
- [x] Filtre historique, notes, bisect, sparse checkout, ouverture sous-dossier/worktree/bare et askpass natifs.
- [x] Archive ZIP/TAR, format-patch, apply/am avec reprise de conflit, fsck/statistiques et GC normal ; dialogues raccordés et préférences XDG.
- [x] Création/clonage personnel/bare partagé, branche/sans checkout, clone limité gardant les branches distantes, erreurs et annulation.
- [x] Suggestions Clone : presse-papiers, MRU, remote de branche/URL push, confirmation d'ouverture ; suggestions locales et chemins littéraux.
- [x] Historique fichier/blame : renommages, graphe fusion, révisions/images/save, attribution, avatars, navigation, menus et préférences partagées.
- [x] Branches/tags/checkout : cinq dialogues, tracking, orphan, changements locaux, worktrees, GPG et remotes ; Rename/Compare et endpoints BASE/index/worktree.
- [x] Suppression distante : dialogue Windows compact et sélecteur multiple partagé, multi-remotes, confirmation non fusionnée et nettoyage local distinct après succès/relecture du suivi ; Abort et reprise partielle vérifiés. ADR 0065.
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

- Focus : les retours ciblés des menus, outils, prompts et du changement vers File tree sont corrigés et vérifiés dans les périmètres ci-dessous. Étendre les variantes de panneaux, thèmes, traductions et plateformes ; Browse reste N2.
- Compare/Browse : listes, groupes et menus communs avec paire/métadonnées par feuille, mutations ciblées, outils/fichiers mémorisés et vraie console interactive. Restent scripts/Visual Studio, découverte/réglages complets des outils, présélection gitlink, cas First vide/root initial et gestes de plage sans oracle Windows validé. Reprise des fichiers masqués par Skip/Assume et première flèche après retrait vérifiées via Commit → Unstaged → Settings ; File tree historique ne sert pas de lecteur des flags live. Stage/Unstage natifs et variantes non atteintes restent à auditer. FormDiff/Browse restent N2 ; ADR 0016/0080/0081.
- Scripts : pont de préparation typée inclus dans la suite complète de `2e3bba726`. Relier le contexte actif, le lanceur, les événements, menus et raccourcis ; conserver argv avant Quote et attendre le picker natif. Les trois saisies restent N1. ADR 0057/0078.
- Préférences : pages absentes de raccourcis, scripts/événements, langue, fontes et outils ; réglages globaux/locaux/distribués. Tri/priorités ADR 0082 : remotes vides/inactifs et variantes natives ; CommitInfo partagé ADR 0083 : suites complètes, paquet exact et natif Browse ciblé vérifiés ; autres hôtes/variantes encore ouverts, liens associés/avatar/Markdown/mailto et audit étendu encore ouverts. Badges et QuickRef gardent leurs comparateurs distincts.
- Navigation : parcours Parent/Child porté sur le périmètre ADR0088 ; étendre l’audit natif des filtres, ordres, hôtes et gestes. Navigate Windows n’ouvre pas de picker ; liens Parents/Children de CommitInfo disponibles. CI/superproject et véritables fournisseurs PR/hébergement restent ouverts.
- Réseau : scripts Before/After, Process par remote pour suppression distante, variantes multi-URL/atomic, destination locale Fetch/source URL, fermeture native des propriétaires et sortie continue.
- Rebase/Cherry-pick : matrice native des modes, codecs distincts et todo/done illisibles ; reprise après arrêt brutal, rollback des renommages et restauration IO interrompue. ADR 0045/0047.
- Merge/Reset/Revert : resolver avancé, renommages/sous-modules et réglages distribués ; ne pas transférer les audits ciblés à toutes les variantes.
- Fsck/Reflog/Cleanup et exports : audits natifs étendus, picker/Save as avec propriétaire témoin, binaires, annulation et fermetures normales. ADR 0048/0049/0050.
- Grilles Remotes/Worktrees/sous-modules : gestes complets de DataGridView, copies de cellules et variantes natives ; écrans ciblés présents, audit global ouvert. ADR 0067–0071.
- Askpass/plateforme : Wayland, DPI/accessibilité, matériel FIDO, invites localisées, chemins aux octets non UTF-8, intégrations XDG et arm64. Aucun de ces points n’est certifié par le seul headless.

La matrice donne les autres écarts écran par écran ; aucune famille n’est retirée du périmètre.

## Dernière validation — récupération des flags d’index

`a0ba1c140` : route **Commit → Unstaged → Settings**, puis menu pour poser/retirer Skip ou Assume, avec choix de session et statut réel conservé. Le retour clavier reste dans la liste et Ignore/Exclude se réactivent après la lecture. [ADR 0081](adr/workflows/files/0081-hidden-index-flags.md).

[Ciblés réels](../../artifacts/linux/snapshots/a0ba1c140/proofs/index-flags-targeted-validation.json) : **11/11** et **122/122**, sans addition de ces périmètres recouvrants. Les trois régressions initiales, l’essai 120/122 et les diagnostics restent dans les preuves ; aucune de ces exécutions n’est le full final.

Le [full d15 précédent](../../artifacts/linux/snapshots/d15ec670b/proofs/source-validation.json) est vert **1334/1334**, **143 classes**. Son [audit natif](../../artifacts/linux/snapshots/d15ec670b/proofs/native-index-flags-parent-verification.json) conserve les cycles ON/révélation/OFF et Git exacts, mais **les deux premières Down restent rouges** après OFF et Ignore/Exclude restent grisées. Un full vert ne valide pas ces gestes natifs.

[Full actuel](../../artifacts/linux/snapshots/a0ba1c140/proofs/source-validation.json) : **1337/1337**, **143 classes**, zéro échec/ignoré ; ciblés et tentatives précédentes restent distincts. [Paquet exact](../../artifacts/linux/snapshots/a0ba1c140/proofs/package-runtime-verification.json) et [extraction complète](../../artifacts/linux/snapshots/a0ba1c140/proofs/package-parent-verification.json) : cinq phases réelles à 0, octets/modes vérifiés, installation isolée. [Natif actuel](../../artifacts/linux/snapshots/a0ba1c140/proofs/native-index-flags-parent-verification.json) : deux cycles indépendants, première Down après chaque retrait et disponibilité Ignore/Exclude dans le premier popup, fermeture normale et drainage revus. Aucun nouveau paquet installé sur l’hôte.

Les [menus précédents](../../artifacts/linux/snapshots/31b7be8f8/proofs/native-review.json) restent liés à leurs sources : N10 sur `31b7be8f8`, huit autres parcours sur `9ced89a5d`. Les exécutions Core/Plugins canoniques de `10c43096c` ne deviennent pas de nouveaux fulls par l’ajout d’un ciblé Desktop. Stage/Unstage natifs, combinaison des deux bits, Stage all natif et variantes non atteintes restent des périmètres distincts. FormCommit/Browse/FormDiff restent N2, treize familles ouvertes, aucun N4.

## Validation — tri et priorités

Source `5a52db1a9` : [suites complètes fraîches](../../artifacts/linux/snapshots/5a52db1a9/proofs/sorting-tranche-parent-verification.json) **1358/1358 Desktop**, **146 classes**, **609/609 Core**, **35/35 Plugins**, sans échec ni ignoré. Les [23 tests Git et 19 tests UI ciblés](../../artifacts/linux/snapshots/0ac349f18/proofs/sorting-targeted-validation.json) et les [41 tests des contrats corrigés](../../artifacts/linux/reference-sorting-20261017/targeted-test-contract-01/parent-review.json) restent des périmètres recouvrants, sans addition. Le [full01 rejeté](../../artifacts/linux/reference-sorting-20261017/full-01/parent-failure-review.json) est conservé : HEAD ne se déduit pas du premier `--all`, et l’ordre brut du lecteur n’est pas celui du graphe Windows.

[Paquet actuel](../../artifacts/linux/snapshots/5a52db1a9/proofs/package-parent-verification.json) : cinq phases réelles à 0, extraction complète et installation isolée. [Natif du tri ciblé](../../artifacts/linux/snapshots/5a52db1a9/proofs/native-sorting-recovery-parent-verification.json) : cinq réglages enregistrés, ordre Version et première Down dans l’arbre des références, deux tris View décochés vers GitDefault en scope All branches, puis cinq valeurs relues dans un second processus sur le même profil privé. Lecteur Git et grille évalués séparément. [Aides et première saisie](../../artifacts/linux/snapshots/5a52db1a9/proofs/native-tooltip-parent-verification.json) : survol d’éditeur sans aide, aide icône préservée, deux premières saisies sans réparation, Cancel et OK.

Le [banc natif précédent](../../artifacts/linux/snapshots/0ac349f18/proofs/native-sorting-partial-parent-review.json) conserve trois premières Down, menus et erreurs observés ; son lanceur a renvoyé **143** et le redémarrage n’a pas été exécuté. Il reste **incomplet**, sans transfert de ses captures vers le paquet actuel.

Seule SortingSettingsPage passe **N1→N2**. Autres niveaux inchangés, treize familles ouvertes, aucun N4. Remotes vides/inactifs, compléments de CommitInfo (ADR 0083), configuration distribuée et audit natif étendu restent ouverts ; pas de certification Wayland/DPI/accessibilité ni 9×2 UI exhaustive. Le clic bloqué par une aide sur 0ac et le retour externe 143 restent conservés ; les contrôles frais gardent leur périmètre ciblé. Aucun nouveau paquet installé sur l’hôte et aucun push.

## Validation — CommitInfo partagé

Source `b356d5f6713de70ca744ca82cb07a04b59e74f7b` : [Full02 vérifié](../../artifacts/linux/commit-information-20261017/full-02/parent-verification.json), **1402/1402 Desktop**, **149 classes**, **625/625 Core**, **35/35 Plugins**, sorties réelles à 0 et entrées stables. Aucun cas ancien retiré : **+44 Desktop**, **+16 Core**. Les ciblés recouvrants ne s’additionnent pas aux suites complètes.

[Paquet03 exact vérifié](../../artifacts/linux/snapshots/b356d5f67-package03/proofs/log-finalized-parent-verification-05/package-parent-verification.json) : cinq phases à 0, **402 entrées / 346 fichiers** conformes octet par octet et en modes, version liée à la source ; **35 111 692 octets**. Les [essais refusés](failure.md#commitinfo--diagnostics-et-fermeture), dont Full01 EA6, restent conservés. Aucun nouveau paquet installé sur l’hôte ni push.

[Natif Browse ciblé accepté](../../artifacts/linux/snapshots/b356d5f67/proofs/native-commit-information-parent-review.json) : une session **anglais/clair sous X11 privé**, premier clic et premières Up/Down sans réparation, six options et Remote ON, Show all branches dix→seize sans nouvel événement Git dans le bracket, Start→Close puis fermeture WM normale. Git/index et payload conservés ; [archive et clôture revérifiées](../../artifacts/linux/snapshots/b356d5f67/proofs/native-commit-information-archive-and-retirement.json).

Aucune validation native de FileHistory/Blame, des autres dispositions/thèmes/locales, de Show all tags, Wayland ou DPI. Les trois relevés AT-SPI sont indisponibles, sans certification d’accessibilité. Aucun niveau modifié, treize familles ouvertes et aucun N4. Liens associés, avatar CommitInfo, Markdown, mailto et audit étendu restent ouverts.

## Livraison et preuves

Le paquet installé sur l’hôte reste `7.2.1-linux-alpha+879f7bae3…` : [version relue le 17 octobre](../../artifacts/linux/snapshots/5a52db1a9/proofs/host-application-version-observation.json), sortie réelle 0 ; [installation antérieure vérifiée](../../artifacts/linux/snapshots/879f7bae3/proofs/host-installation-verification-20261016-01.json). Aucun nouveau paquet installé sur l’hôte ; les candidats restent distincts en isolation, aucune application utilisateur arrêtée et aucun push.

Les [snapshots](../../artifacts/linux/snapshots/) conservent résultats, captures, checksums et diagnostics avec leur SHA. Un audit natif ancien n’est pas transféré automatiquement à un candidat récent. L’[ancienne chronologie détaillée](../../artifacts/linux/migration-notes-20261016/todo-before-compaction-983a0d809.md) est archivée depuis le commit 983a0d809 ; le présent TODO garde les périmètres et les suites à donner.

## Validation — sockets et TMPDIR

Source `dc85753b178daad3f8bbf5ecd1b10adbce26c70d` : [suites complètes fraîches](../../artifacts/linux/unix-socket-temp-20261017/full-01/parent-verification.json), **1406/1406 Desktop**, **150 classes**, **636/636 Core**, **35/35 Plugins** ; sorties réelles à 0, entrées stables et aucun ancien cas retiré (**+4 Desktop, +11 Core**). Les [ciblés de préparation](../../artifacts/linux/unix-socket-temp-20261017/targeted-01/summary.json), les [quatre tests sous TMPDIR ASCII long](../../artifacts/linux/unix-socket-temp-20261017/desktop-long-01/ascii/result.json) et [UTF-8 long](../../artifacts/linux/unix-socket-temp-20261017/desktop-long-01/utf8/result.json) sont des périmètres recouvrants.

[Paquet exact](../../artifacts/linux/snapshots/dc85753b1/proofs/package-parent-verification.json) : cinq phases réelles à 0, installation isolée et **402 entrées / 346 fichiers** conformes. [Revue des processus et ouvertures X11](../../artifacts/linux/snapshots/dc85753b1/proofs/unix-socket-native-parent-review.json) : **21 processus neufs, 13 succès et 8 annulations après démarrage**, 78 identités PID/start arrêtées et six sockets de repli supprimés ; dépôt ouvert sous Xvfb avec TMPDIR de 128 et 148 octets. Le contenu complet reste inchangé après ces vérifications.

Le [rouge antérieur b356](../../artifacts/linux/unix-socket-temp-20261017/probe/campaigns/b356-full-matrix/summary.json) et les sondes précommit restent séparés du paquet exact. Aucun nouvel audit visuel ou des gestes de menus/focus ; Wayland, DPI et accessibilité restent ouverts. Niveaux inchangés, treize familles ouvertes, aucun N4 ; aucune installation hôte ni push. ADR [0084](adr/platform/git/0084-bounded-unix-sockets.md).

## Historique fichier — validation ADR 0085

Source `1e6cc163a7b1b0395f1815050e7ab469c0ef4477` : [Full02 vérifié](../../artifacts/linux/file-history-browse-20261017/full-02/parent-verification.json). **1466/1466 Desktop**, **657/657 Core**, **35/35 Plugins**, suites finales fraîches ; 156 classes Desktop lues dans le TRX. [paquet exact vérifié isolément](../../artifacts/linux/snapshots/1e6cc163a/proofs/package-parent-verification.json) ; [revue native parent](../../artifacts/linux/snapshots/1e6cc163a/proofs/native-file-history-parent-review.json) : Un parcours Plan A anglais/clair sous X11 privé : H depuis le nom courant, Browse indépendant, présélection sans borne, premier clic/Up/Down sans réparation, renommages et choix explicite conservé après F5, vrais diffs/arbres Index et Working directory, Blame inline et fermeture normale. Enfant arrêté par PID/start, code de sortie indisponible ; Path B, survie après fermeture du parent, mutations, autres modes/thèmes/locales, Wayland, DPI et accessibilité non audités.

Les quatre réglages partagés et la correction F5 du CLI à FirstId artificiel relèvent de l’ADR0086, distincte de H sans FirstId. Le dialogue dédié garde son lecteur direct borné et un menu contextuel incomplet. Path A au nom courant et Path B à l’ancien nom ont des oracles différents ; ne pas déduire une borne d’ancêtres d’un renommage futur non suivi. Le dialogue dédié demeure N3 sur son périmètre ancien, Browse N2. Autres menus/filtres, thèmes/locales et plateformes encore à auditer ; treize familles ouvertes, aucun N4, aucune installation hôte ni push.

## Validation — réglages d’historique fichier ADR0086

Source `f4e99bec0fc503cede9996e9c7238f2338d4c94b` : [suites complètes vérifiées](../../artifacts/linux/file-history-options-20261017/full-01/parent-verification.json) et [paquet exact vérifié](../../artifacts/linux/snapshots/f4e99bec0/proofs/package-parent-verification.json). **1492/1492 Desktop**, **661/661 Core**, **35/35 Plugins**, suites fraîches ; 159 classes Desktop dans le TRX. Git réel, headless EN/FR clair/sombre et pointeur/clavier ; cinq phases paquet à 0. Audit GUI natif complet toujours ouvert ; lecteur dédié direct borné/menu incomplet, niveaux inchangés, treize familles ouvertes, aucun N4.


## Validation — recherche incrémentale ADR0087

Source `b7967dbcfc4126285cc438e516f6068bab95311d` : [suites complètes vérifiées](../../artifacts/linux/revision-quick-search-20261017/full-01/parent-verification.json) et [paquet exact vérifié](../../artifacts/linux/snapshots/b7967dbcf/proofs/package-parent-verification.json), **1534/1534 Desktop**, **669/669 Core**, **35/35 Plugins**, 160 classes Desktop ; aucun ancien cas retiré. Cinq phases paquet à 0, extraction complète. [Douze rendus EN/FR clair/sombre revus](../../artifacts/linux/revision-quick-search-20261017/targeted-05/parent-render-review.json), Git réel, pointeur/clavier headless, collage multiligne et réponses tardives, fermeture drainée et retours Index/WT→commit.

[Natif ciblé accepté](../../artifacts/linux/snapshots/b7967dbcf/proofs/native-quick-search-parent-review.json) : une session anglais/clair sous X11 privé du payload exact ; seuls les gestes et frames du rapport font foi. Autres thèmes/locales, presse-papiers natif, Wayland/DPI/accessibilité et audit étendu restent ouverts.

Browse reste N2, treize familles ouvertes, aucun N4. Placement des lignes artificielles et panneau Working directory sont adaptés ; délai 4000ms sans réglage global porté. [Audit statique de la suite Parent/Enfant](../../artifacts/linux/revision-quick-search-20261017/next-parent-child-plan.md) : piles et cible filtrée, sans picker inventé. Aucun nouveau paquet installé sur l’hôte et aucun push.


## Validation courante — navigation parent/enfant

`749f88339` : **1573/1573 Desktop**, **669/669 Core**, **35/35 Plugins**, 162 classes Desktop, aucun ancien cas retiré ; [suites complètes](../../artifacts/linux/parent-child-navigation-20261017/full-02/parent-verification.json), [paquet exact](../../artifacts/linux/snapshots/749f88339/proofs/package-parent-verification.json) et [natif ciblé](../../artifacts/linux/snapshots/749f88339/proofs/native-parent-child-review.json). [Ciblés121/121, Core8/8 et endpoints16/16 acceptés](../../artifacts/linux/parent-child-navigation-20261017/targeted-07/parent-targeted-verification.json), périmètres recouvrants distincts du full. Les deux cas ordinaires WorkingTree et Index depuis le vrai éditeur Changes couvrent F5, texte/identité, focus, premier Parent/Child et état Git inchangé. Le [rouge ciblé06 conservé](../../artifacts/linux/parent-child-navigation-20261017/targeted-06/parent-red-review.json) reproduit leur perte avant correction ; les essais01–04 restent aussi archivés. Les [six rendus frais ciblé07 relus par Root](../../artifacts/linux/parent-child-navigation-20261017/targeted-07/parent-render-review.json) couvrent View/Navigate clair-sombre et la cible invisible anglais clair/français sombre ; cette revue headless ne certifie pas le placement natif des popups. Natif : session anglais/clair/X11 du payload exact, seuls les gestes du rapport sont acceptés ; aucune validation native de F5 ne se déduit de ce statut. Ces ciblés ne remplacent pas le full final.

Filtres/pagination/reflog, multisélection, unborn/bare, autres hôtes, thèmes/locales et Wayland/DPI/accessibilité restent à étendre en natif. Browse reste N2, treize familles ouvertes, aucun N4. Aucun nouveau paquet installé sur l’hôte et aucun push.
