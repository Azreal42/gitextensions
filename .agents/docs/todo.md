# TODO

Mise à jour : 18 octobre 2026. Les validations ci-dessous restent liées à leur source et à leur périmètre.

Une case cochée atteste le sous-périmètre décrit, avec preuve dans le code/tests ou résultat exécuté. Elle ne clôt pas sa famille historique. Migration complète : toutes les familles ouvertes couvertes, audit de parité, paquet installé et application démarrée sur Linux.

## État détaillé par fenêtre

La [matrice exhaustive Windows → Linux](windows-linux-window-inventory.md) distingue fenêtres présentes et parité vérifiée. **Référence `069de52b7`, suites complètes 2517/2517 Desktop (226 classes), 739/739 Core, 35/35 Plugins** ; **99 fenêtres métier : N0=5, N1=14, N2=30, N3=49, NA=1 ; 32 pages : N0=10, N1=6, N2=15, NA=1**. Aucun N4 et aucune conversion de ces nombres en pourcentage du travail de migration.

**Relevé Roslyn `069de52b7` : 2208 fichiers C#, zéro diagnostic**. La matrice distingue 90 classes Window Linux et 33 fabriques Linux, 366 sites de boîtes système, 189 Designer, 13 dialogues WiX du dépôt et 9 de bibliothèque. Les classes de base et l’installateur restent séparés des fenêtres métier ; les tests associés sont ceux de la vraie suite de référence, sans certification de chaque écran.


Écarts concrets à traiter selon cette matrice : audit natif étendu du gestionnaire et de l’assistant de conflit de sous-module ; PR/hébergement et CI ; compléments des raccourcis, audit étendu des scripts et compléments des fontes ADR0091, compléments de langue ; sorties/cache/stacks/export du journal Git ; audit natif étendu de l’askpass (Wayland, DPI/accessibilité, matériel FIDO et invites localisées). Le tri/priorités et GitDefault sans option sont portés sur le périmètre ADR 0082 ; CommitInfo est porté sur le périmètre partagé de l’ADR 0083, suites complètes et natif Browse ciblé vérifiés ; remotes vides/inactifs et paramètres globaux/locaux/distribués restent ouverts. Les familles globales restent ouvertes.

## Livré et vérifié

- [x] General : groupes et dépendances Windows, profils partagés et consommateurs réels ; suites complètes, paquet exact et natif source ciblé avec redémarrage vérifiés. ADR0097 ; console et télémétrie indisponibles, niveau N2 et audit étendu ouvert.

- [x] Settings : arbre/icônes Windows, recherche, mémoire, focus et Apply commun ; suites complètes, paquet exact et natif source ciblé avec redémarrage vérifiés. ADR0096 ; FormSettings N2, pages/portées absentes encore ouvertes.

- [x] Config : treize champs Windows, brouillons Local/Global et encodages partagés à deux listes ; suites complètes et paquet exact vérifiés, parcours natif source ciblé avec redémarrage. ADR0094 ; page Config et AvailableEncodings N2, audit étendu ouvert.

- [x] Section Diff/Merge : six champs et catalogue partagés, scopes/brouillons et publication explicite ; suites complètes, paquet exact et natif T01 avec redémarrage vérifiés. ADR0093 ; sous-périmètre historique prolongé par ADR0094.

- [x] Scripts : éditeur brouillon, IDs/ordre et touches dynamiques, menus/toolbar et runner possédé ; suites complètes, paquet exact et natif S01 avec redémarrage/fermeture vérifiés. ADR0057/0078 ; page N2, audit étendu des événements/hôtes encore ouvert.

- [x] Polices/EOL : quatre rôles indépendants, sélection avec aperçu, annulations et préférences au redémarrage ; ADR0091/0092, page Fonts N2 sur le périmètre vérifié.
- [x] Langue : choix initial des traductions XLF/drapeaux, préférence commune avant construction des catalogues, Settings avec redémarrage requis et conservation des choix récents entre fenêtres ; ADR0090, chooser N2 et audit étendu ouvert.

- [x] Raccourcis configurables : neuf contextes, 171 presets Windows, brouillon Apply/Clear/Reset et publication OK ; menus/tooltips/dispatch communs aux commandes existantes. ADR0089 ; page N2 et commandes encore absentes détaillées ci-dessous.

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
- Compare/Browse : listes, groupes et menus communs avec paire/métadonnées par feuille, mutations ciblées, outils/fichiers mémorisés et vraie console interactive. Restent Visual Studio, audit étendu des scripts et découverte/réglages complets des outils, présélection gitlink, cas First vide/root initial et gestes de plage sans oracle Windows validé. Reprise des fichiers masqués par Skip/Assume et première flèche après retrait vérifiées via Commit → Unstaged → Settings ; File tree historique ne sert pas de lecteur des flags live. Stage/Unstage natifs et variantes non atteintes restent à auditer. FormDiff/Browse restent N2 ; ADR 0016/0080/0081.
- Scripts : éditeur brouillon XDG, plan argv avant Quote, runner possédé, catalogue/menus/touches par ID et raccords Before/After validés par les suites complètes ; paquet exact et natif S01 ciblé vérifiés, voir la section dédiée. Les trois saisies restent N1. Étendre natif, erreurs de profil, outils/OS, icônes associées et programmes shell complexes ; ADR 0057/0078.
- Préférences : compléments de raccourcis ADR0089 et audit étendu de la page Scripts ; treize champs Config et encodages partagés vérifiés (ADR0093/0094), Global hors dépôt et pages Git/Checklist portés N2 sur le périmètre ADR0095 ; découverte exhaustive et audit natif étendu ouverts. Fonts/EOL porté au redémarrage sur le périmètre ADR0091 ; langue ADR0090 portée sur son périmètre ciblé ; réglages globaux/locaux/distribués. Tri/priorités ADR 0082 : remotes vides/inactifs et variantes natives ; CommitInfo partagé ADR 0083 : suites complètes, paquet exact et natif Browse ciblé vérifiés ; autres hôtes/variantes encore ouverts, liens associés/avatar/Markdown/mailto et audit étendu encore ouverts. Badges et QuickRef gardent leurs comparateurs distincts.
- Navigation : parcours Parent/Child porté sur le périmètre ADR0088 ; étendre l’audit natif des filtres, ordres, hôtes et gestes. Navigate Windows n’ouvre pas de picker ; liens Parents/Children de CommitInfo disponibles. CI/superproject et véritables fournisseurs PR/hébergement restent ouverts.
- Réseau : raccords Scripts Before/After et chemin Process par remote validés par les suites complètes ; paquet exact vérifié ; audit natif des événements encore ouvert. Variantes multi-URL/atomic, destination locale Fetch/source URL, reprises différées et fermetures natives des propriétaires restent à étendre.
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

Dernier candidat vérifié isolément : [`7.2.1-linux-alpha · 069de52b7`](../../artifacts/linux/commit-completion-20261018/review/package-01-root-review.json), **39 670 564 octets**, SHA256 `a4515e2cb40484c0345de259f3f639d48e89cc9da1ec6fddfbf0fb9147503cd4` ; cinq phases à 0, paquet isolé lié à la preuve exacte.

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


## Validation — navigation parent/enfant

`749f88339` : **1573/1573 Desktop**, **669/669 Core**, **35/35 Plugins**, 162 classes Desktop, aucun ancien cas retiré ; [suites complètes](../../artifacts/linux/parent-child-navigation-20261017/full-02/parent-verification.json), [paquet exact](../../artifacts/linux/snapshots/749f88339/proofs/package-parent-verification.json) et [natif ciblé](../../artifacts/linux/snapshots/749f88339/proofs/native-parent-child-review.json). [Ciblés121/121, Core8/8 et endpoints16/16 acceptés](../../artifacts/linux/parent-child-navigation-20261017/targeted-07/parent-targeted-verification.json), périmètres recouvrants distincts du full. Les deux cas ordinaires WorkingTree et Index depuis le vrai éditeur Changes couvrent F5, texte/identité, focus, premier Parent/Child et état Git inchangé. Le [rouge ciblé06 conservé](../../artifacts/linux/parent-child-navigation-20261017/targeted-06/parent-red-review.json) reproduit leur perte avant correction ; les essais01–04 restent aussi archivés. Les [six rendus frais ciblé07 relus par Root](../../artifacts/linux/parent-child-navigation-20261017/targeted-07/parent-render-review.json) couvrent View/Navigate clair-sombre et la cible invisible anglais clair/français sombre ; cette revue headless ne certifie pas le placement natif des popups. Natif : session anglais/clair/X11 du payload exact, seuls les gestes du rapport sont acceptés ; aucune validation native de F5 ne se déduit de ce statut. Ces ciblés ne remplacent pas le full final.

Filtres/pagination/reflog, multisélection, unborn/bare, autres hôtes, thèmes/locales et Wayland/DPI/accessibilité restent à étendre en natif. Browse reste N2, treize familles ouvertes, aucun N4. Aucun nouveau paquet installé sur l’hôte et aucun push.

## Validation — raccourcis configurables

Source `89672b5c6370a7752d6db441445d5381eb4d58dd` : [full04 accepté](../../artifacts/linux/configurable-hotkeys-20261017/full-04/parent-verification.json), **1686/1686 Desktop, 675/675 Core, 35/35 Plugins**. Sorties réelles à zéro, entrées stables et [aucun ancien cas retiré](../../artifacts/linux/configurable-hotkeys-20261017/full-04/hotkeys-case-retention.json). [Paquet exact](../../artifacts/linux/snapshots/89672b5c6/proofs/package-parent-verification.json) contrôlé séparément ; pas d’installation hôte ni de push de cette tranche.

[Natif ciblé accepté](../../artifacts/linux/snapshots/89672b5c6/proofs/native-hotkeys-review.json) : GoToCommit remappé/None, Apply-Cancel/Apply-OK, deux menus, persistance et première flèche dans Browse anglais/clair/X11 privé. Aucun transfert des audits antérieurs ni preuve native globale des contextes, scripts, erreurs IO, collisions, FR/Dark, Wayland/DPI/accessibilité. [Acteurs privés arrêtés](../../artifacts/linux/snapshots/89672b5c6/proofs/native-hotkeys-retirement.json).

[ADR0089](adr/preferences/hotkeys/0089-configurable-hotkeys.md) : seule la page Hotkeys passe N0→N2. Ses commandes absentes restent explicites ; les 13 familles globales restent ouvertes, aucun N4. Les rouges [full01](../../artifacts/linux/configurable-hotkeys-20261017/full-01/parent-failure-review.json), [full03](../../artifacts/linux/configurable-hotkeys-20261017/full-03/parent-failure-review.json) et ciblés06/07 sont conservés. Les ciblés08/10/11 et les six rendus du ciblé05 restent des preuves de leur propre portée/source et ne s’ajoutent pas au full final.

## Validation courante — langue

Source `3dd2fa83b17c5852269f3c3cd83ee073f0762c15` : [suites complètes](../../artifacts/linux/language-preferences-20261017/full-01/parent-verification.json), **1719/1719 Desktop**, **675/675 Core**, **35/35 Plugins**, sans cas ancien retiré ni ignoré ; sorties réelles et entrées figées. [Paquet exact](../../artifacts/linux/snapshots/3dd2fa83b/proofs/package-parent-verification.json), cinq phases à 0 et extraction complète vérifiée.

[Natif ciblé](../../artifacts/linux/snapshots/3dd2fa83b/proofs/native-language-review.json) Light/X11 privé : premier choix French et vrai Browse français, English Cancel puis OK, réouverture Settings English pendant que Browse reste français. Redémarrage complet, Enter/fermeture sans choix, erreurs IO, deux Browse et helpers ne sont pas certifiés par ce natif. Les 33 nouveaux cas Langue et le ciblé de 241 tests restent recouvrants avec le full ; les sept régressions initiales et le geste de combo erroné restent archivés dans [failure](failure.md#langue--bootstrap-et-préférences-récentes).

Chooser N0→N2 seulement ; Appearance N1 inchangée. Help Translate, retraduction Browse à chaud et audits étendus restent ouverts. Treize familles globales ouvertes, aucun N4 ; aucune installation hôte ni push de cette tranche. [ADR0090](adr/preferences/language/0090-language-selection.md).

## Validation courante — polices et EOL

Quatre rôles indépendants, picker possédé, brouillon Cancel/OK et EOL au redémarrage : [ADR0091](adr/preferences/fonts/0091-fonts-and-eol.md) et [ADR0092](adr/platform/rendering/0092-skia-stream-lifetime.md). Le [full b74 accepté](../../artifacts/linux/fonts-preferences-20261017/full-02/parent-verification.json) compte **1778/1778 Desktop (176 classes), 675/675 Core et 35/35 Plugins**, avec **59 cas Desktop ajoutés depuis Language sans retrait**. Build et suites terminent à 0 ; sources et runtime restent inchangés.

Le [paquet exact de cette source](../../artifacts/linux/snapshots/b74bb4522/proofs/package-parent-verification.json) est vérifié, cinq phases produit à 0 et extraction complète conforme : **35 403 640 octets, 442 entrées dont 386 fichiers**. Le [backend livré](../../artifacts/linux/snapshots/b74bb4522/proofs/package-backend-verification.json) conserve identité/surface/ressources, accès internes, dépendances natives et licences ; le lock net8 de base est contrôlé séparément.

Le [natif accepté par Root](../../artifacts/linux/snapshots/b74bb4522/proofs/native-fonts-review.json), lié à ce paquet b74, couvre un profil privé anglais/clair/X11 : Cancel picker et Settings, quatre rôles sauvegardés puis relus, consommateurs existants inchangés avant vraie fermeture et redémarrage, nouveaux rôles et EOL on/off après reprise, grille et SHA Worktrees. Les 77 reçus sont complétés, les deux GUI/WM/wait externe terminent à 0 ; sept frames sont personnellement revues par Root. Les contrôles Git matériels et les intervalles Settings dédiés restent distincts des lectures nécessaires aux consommateurs.

**Fonts seul passe N0→N2** ; Appearance reste N1, **treize familles ouvertes, aucun N4**. Import Windows, arbre/Apply global de Settings, fournisseurs et couverture de tous les glyphes/consommateurs restent ouverts. Le natif ne couvre ni plusieurs Browse, ConEmu, français/sombre, Wayland, DPI ni accessibilité ; il ne certifie pas toutes les fontes, styles ou TTC, ni le confinement universel de l’encre des badges. L’[historique des échecs et diagnostics](failure.md#polices--densité-et-flux-natif), dont le premier natif refusé par le banc, reste conservé avec ses limites causales. Aucun ancien payload ni ciblé headless n’est transféré en preuve native.

## Validation courante — scripts runtime

Source intégrée `19bbfed707ecded0d2d7832069efbb310041b363` : Settings Scripts en brouillon, ordre/IDs et touches dynamiques, toolbar/menus Browse–Commit–Compare, plan RAW/opaque avant Quote, foreground Process et background supervisé, événements aux frontières Windows. ADR0057/0078 ; les limites PowerShell, shell complexe, icônes associées et import global restent explicites.

Le [full02 accepté](../../artifacts/linux/scripts-runtime-20261018/full-02/parent-verification.json) compte **1833/1833 Desktop (183 classes), 689/689 Core et 35/35 Plugins**, build et suites avec sorties réelles à 0 et entrées sources/runtime inchangées. La [rétention](../../artifacts/linux/scripts-runtime-20261018/full-02/case-retention.json) ajoute **55 cas Desktop** et la revue finale **14 Core**, sans retrait. La [référence Desktop revue](../../artifacts/linux/scripts-runtime-20261018/validation-reference-reviewed.json) est liée à cette même source. [Paquet exact](../../artifacts/linux/snapshots/19bbfed70/proofs/package-parent-verification.json) : cinq phases réellement attendues à 0, extraction complète conforme, **35 438 852 octets** ; installation DPKG isolée. **[Natif S01 vérifié](../../artifacts/linux/snapshots/19bbfed70/proofs/native-scripts-review.json)** : brouillon/Cancel, ordre/IDs, renommage et raccourci relus après un vrai redémarrage ; cinq routes foreground, erreur Git128 affichée et background annulé à la fermeture normale. Les six reaps réels et les deux EOF de chaque processus sont vérifiés ; les deux GUI se ferment à 0 et les checkpoints Git sont strictement inchangés sur leur schéma. Les ciblés06 71/71 et07 81/81 recouvrants ne s’additionnent pas. Le [full01 refusé](../../artifacts/linux/scripts-runtime-20261018/full-01/parent-verification.json) et sa [classification des 20 échecs](../../artifacts/linux/scripts-runtime-20261018/full-01/root-cause-review.json) restent conservés ; la correction n’efface aucun oracle historique. Les anciens labels Dark01–03 ne valent pas preuve sombre ; le ciblé04 vérifie réellement le thème, distinct de son résultat global et du natif.

**Scripts seul : N0→N2 ; aspect adapté, pas PropertyGrid Windows identique.** Autres niveaux inchangés, treize familles ouvertes. Le parcours natif vérifié reste la base S01 anglais/clair/X11 : configuration, cinq routes foreground, background et redémarrage réel selon ses preuves. L’extension legacy Remotes/Fetch all est inaccessible et exclue ; aucun Before/After n’est attesté nativement par S01. CLI, lecture source, tests Git/headless et GUI natif restent des preuves distinctes. Pas d’installation hôte, de push ni d’acceptation transférée.


## Configuration des outils Diff/Merge — validation ciblée

Source `d975adafa9e496824af8984052e8790ce18222f2` : six champs Merge/Diff, vraies ComboBox et catalogue, portées Effective/System readonly, brouillons Local/Global et publication OK/Apply explicite. Cancel ne restaure pas les écritures publiées ; readback lorsqu’il est disponible, inconnu possible après annulation. [ADR0093](adr/preferences/tools/0093-diff-merge-tool-configuration.md).

Le [Full01 vérifié](../../artifacts/linux/tools-config-20261018/full-01/parent-verification.json), également figé dans la [preuve source](../../artifacts/linux/snapshots/d975adafa/proofs/source-validation.json), compte **1846/1846 Desktop (184 classes), 703/703 Core et 35/35 Plugins**, sans skip. Build et suites ont de vrais retours à 0 ; sources et runtimes restent stables. Les **1833 cas Desktop antérieurs et 689 Core** sont retenus, avec **13 Desktop et 14 Core ajoutés**, aucun retrait ; Plugins reste à 35. Les ciblés et leurs rendus headless Light/Dark restent distincts de ce full et du natif T01.

Le [paquet exact](../../artifacts/linux/snapshots/d975adafa/proofs/package-parent-verification.json), **35 486 676 octets**, passe les cinq phases, dont installation DPKG et démarrage isolés ; archive et extraction complètes vérifiées. Le [backend livré](../../artifacts/linux/snapshots/d975adafa/proofs/package-backend-verification.json) est vérifié séparément. Le [natif T01](../../artifacts/linux/snapshots/d975adafa/proofs/native-tools-config-review.json) vérifie les portées et brouillons, Cancel, Apply Global/Local, OK sans modification et persistance après un vrai redémarrage ; fermetures normales vérifiées. Périmètre anglais/clair/X11, configuration seule : consommateurs externes et Browse picker exclus, variantes étendues encore ouvertes. À cette source historique, Config restait **N1** ; les sept autres champs et AvailableEncodings sont désormais traités par ADR0094, voir la validation courante ci-dessous. Treize familles ouvertes, aucun N4. Les preuves Scripts restent liées à leur source19bb.


## Validation courante — Config et encodages

Source `a79bc9c08e515954169b4f69048828dd76e23674` : page Windows de treize champs, grille et libellés communs, brouillons Local/Global, Apply/OK explicites ; absence, vide et sans valeur conservés. Choix des encodages à deux listes, préférence XDG partagée et indépendante de Cancel parent. [ADR0094](adr/preferences/configuration/0094-git-configuration-and-encodings.md).

Le [full03 accepté](../../artifacts/linux/git-config-20261018/full-03/parent-verification.json) compte **1889/1889 Desktop (186 classes), 714/714 Core et 35/35 Plugins**, sans skip, build sans avertissement et sorties réelles à 0. Les 1846 cas UI antérieurs sont conservés et 43 ajoutés ; sources et runtimes inchangés. [Paquet exact](../../artifacts/linux/snapshots/a79bc9c08/proofs/package-parent-verification.json) : **35 491 404 octets**, cinq phases à 0, extraction complète et [backend](../../artifacts/linux/snapshots/a79bc9c08/proofs/package-backend-verification.json) vérifiés ; installation DPKG et lancement isolés, pas d’installation hôte.

[Natif source964](../../artifacts/linux/git-config-20261018/native-source-01/root-review.json) : nom Local publié, annulations parent/enfant, préférence enfant persistante et vrai redémarrage, deux fermetures normales à 0 ; anglais/clair/X11 seulement. [a79 ne change que six fixtures de tests](../../artifacts/linux/git-config-20261018/native-source-01/source-equivalence.json). Ce parcours ne certifie pas le paquet interactivement. Les [échecs conservés](failure.md#config-complète-et-encodages--intentions-à-conserver) distinguent full01 rouge, lancement full02 bloqué par les sockets du bac à sable, ciblés et full03.

**Config et AvailableEncodings : N1→N2**, aspect rapproché. À cette source, Global hors dépôt dans l’UI restait ouvert ; il est traité par ADR0095 ci-dessous. Repli Windows aux défauts pour une liste d’encodages enregistrée vide, découverte exhaustive, éditions multivaleurs et audit natif des autres champs/pickers/consommateurs et variantes restent ouverts ; lecteur guidé Git 2.26 minimum, édition brute séparée. **Treize familles ouvertes, aucun N4.** Aucun push ni transfert des preuves natives des lots précédents.

## Validation courante — Environnement Git et Checklist

Source 1deed8d3470baba79d44779bde7e45e81684ec5d ; [ADR0095](adr/preferences/environment/0095-git-environment-and-checklist.md). Pages Git/Paths et Checklist, choix Git appliqué à la frontière suivante, HOME limité aux enfants, PATH complété en fin de liste et Config Global hors dépôt. Neuf observations, corrections guidées, case de démarrage indépendante et Save and rescan ; Cancel conserve les publications déjà acquises.

Le [full01 vérifié](../../artifacts/linux/git-environment-20261018/full-01/parent-verification-02.json) compte **1986/1986 Desktop (193 classes), 720/720 Core, 35/35 Plugins**, sans skip, build sans avertissement ni erreur et sorties réelles à 0. Les **1889 anciens cas UI et 714 Core sont conservés**, 97 UI et 6 Core ajoutés ; oracles historiques inchangés hormis l’enregistrement additif des nouveaux scénarios Core. Sources et runtimes stables. Le premier rejet du vérificateur et les neuf conversions CRLF déclarées restent explicitement tracés, sans réexécution des suites.

[Paquet exact](../../artifacts/linux/snapshots/1deed8d34/proofs/package-parent-verification.json) : **35 529 476 octets**, cinq phases à 0, 442 entrées/386 fichiers vérifiés avec l’extraction complète ; [backend et helper](../../artifacts/linux/snapshots/1deed8d34/proofs/package-backend-environment-verification.json) contrôlés. DPKG et lancement isolés ; aucune installation hôte ni push.

[Revue native source](../../artifacts/linux/git-environment-20261018/native-source-review.json) : reprise CLI, HOME enfant, case instantanée puis auto-off, Cancel, profil retrouvé au redémarrage avec dépôt/diff et Config Global sans dépôt ; quatre sorties normales à 0, anglais/clair/X11 seulement. Aucun transfert au paquet interactif. [Échecs et contrôles corrigés](failure.md#environnement-git-et-checklist--intentions-à-conserver) conservés.

**Git et Checklist : N0→N2 ; HOME : NA→N2 adapté ; FormSettings reste N1. Treize familles ouvertes, aucun N4.**

- [ ] Auditer les séquences de lectures UI ReadBrowserAsync lors d’un changement de Git ; leurs lectures successives n’ont pas de snapshot global garanti.
- [ ] Aligner explicitement la politique des versions : diagnostic Windows 2.43/2.53 et minimum Linux README/.deb 2.38 restent distincts.
- [ ] Compléter l’arbre Settings et l’audit natif des pickers, erreurs, thèmes/langues, Wayland/DPI/accessibilité ; aucune parité globale certifiée.


## Validation courante — Arbre Settings

Source `7c52b39742abaf2a4b11635f910bf78f4a70edca` ; [ADR0096](adr/preferences/navigation/0096-settings-tree-host.md). Arbre/icônes Windows des pages présentes, recherche AND/Entrée, mémoire/Goto, focus conservé et Apply/SaveAll commun. Les pages gardent leurs brouillons ; Cancel conserve les publications déjà acquises.

[Full01 vérifié](../../artifacts/linux/settings-navigation-20261018/full-01/parent-verification.json) : **2006/2006 Desktop (194 classes), 720/720 Core, 35/35 Plugins**, sans skip, build sans avertissement/erreur, sorties réelles à 0. **1986 anciens cas UI conservés, 20 ajoutés, aucun retrait** ; adaptations des gestes de navigation revues, un seul oracle historique change pour le résultat Cancel après SaveAll comme Windows. Sources/runtimes stables. Ciblés03 223/223 et final04 20/20, échecs et [sonde complémentaire 2/2](../../artifacts/linux/settings-navigation-20261018/owner-close-probe-01/root-review.json) séparés dans failure.

[Paquet exact](../../artifacts/linux/snapshots/7c52b3974/proofs/package-parent-verification.json) : **35 545 172 octets**, cinq phases à 0, 442 entrées/386 fichiers et extraction complète vérifiés ; [backend/helper](../../artifacts/linux/snapshots/7c52b3974/proofs/package-backend-environment-verification.json) contrôlés. Installation/lancement isolés. [Revue native source](../../artifacts/linux/settings-navigation-20261018/native-source-01/root-review.json) : premier clic/flèches, recherche/cycle sans publication, Apply/Cancel, mémoire de page, Config enfant et limite retrouvée après vrai redémarrage ; deux sorties normales à 0, anglais/clair/X11 seulement. Aucun transfert de cette preuve à l’interface du paquet.

**FormSettings N1→N2 ; introduction Git N0→N2. Treize familles ouvertes, aucun N4.**

- [ ] Pages Windows absentes, Plugins/CI, sources Global/Local/distribuées et audit natif étendu ; Config demeure un dialogue enfant. Aucun isomorphisme global de Settings acquis.

## General — périmètre vérifié

[ADR0097](adr/preferences/general/0097-general-settings-policies.md) : groupes Windows et brouillons reliés aux profils existants, plafond global distinct des lots, recherche, démarrage optionnel, autostash commun, histogram et compteurs/statuts réels. Console séparée et télémétrie restent indisponibles. Le [full03 accepté](../../artifacts/linux/general-settings-20261018/full-03/parent-verification.json), source `2ceb059bc`, compte **2117/2117 Desktop (201 classes), 720/720 Core, 35/35 Plugins**, sans retrait ni skip : **111 cas Desktop ajoutés**, dont 105 General et six régressions avatar/montage Checkout.

[Paquet exact](../../artifacts/linux/snapshots/2ceb059bc/proofs/package-parent-verification.json) : cinq phases attendues à 0, extraction complète et backend contrôlés, **35 567 676 octets** ; installation DPKG isolée. [Natif source ciblé](../../artifacts/linux/general-settings-20261018/native-source-01/root-review.json), anglais/clair/X11 privé : dépendance sous-modules, Apply avec plafond 321 et délai 200 ms (lot 300 inchangé), brouillon 654 annulé, Entrée sans OK, vrai diff `--histogram`, défaut Clone, premier clic et Haut/Bas sans réparation, puis persistance au vrai redémarrage. Deux sorties normales à 0 ; dépôt, sources et runtime inchangés. Cette preuve ne certifie pas les autres consommateurs natifs, le paquet interactif, Wayland/DPI/accessibilité ou la parité globale.

- [x] Ciblé03 : 145/145 dont 105 nouveaux cas General ; Git réel, profils concurrents/échecs, clavier et rendus EN/FR clair/sombre. Plafond Windows séparé des lots ; deux fixtures historiques adaptées explicitement à cette distinction, sans retrait de cas.
- [x] Garde de rendu des avatars repliés : défaut du full01 reproduit par trois cas rouges puis corrigé, ancien parcours View inchangé ; ciblé04 155/155.
- [x] Full03 frais, paquet exact et parcours natif General ciblé ; full01 interrompu/rejeté et full02 avec un échec Checkout conservés. Niveaux inchangés.
- [ ] Compléments General natifs, erreurs et autres langues/thèmes ; console et télémétrie restent indisponibles. Les treize familles globales restent ouvertes.

## Appearance — périmètre vérifié

[ADR0098](adr/preferences/appearance/0098-appearance-shared-settings.md), source `81a7aca67` : groupes Windows, quatre préférences Windows actives et thème Linux, brouillons partagés et lecteurs de branche drainés. [Full04 accepté](../../artifacts/linux/appearance-settings-20261018/full-04/parent-verification.json) : **2180/2180 Desktop (205 classes), 720/720 Core, 35/35 Plugins**, zéro skip/retrait, 63 nouveaux cas Desktop, build sans avertissement/erreur. Adaptations historiques Checkout/Dashboard revues ; échecs antérieurs conservés dans failure.

[Paquet exact](../../artifacts/linux/snapshots/81a7aca67/proofs/package-parent-verification.json) : **35 573 800 octets**, cinq phases à 0, extraction complète et backend vérifiés, installation DPKG isolée. [Natif source](../../artifacts/linux/appearance-settings-20261018/native-source-01/root-operator-review.json), anglais/clair/X11 privé : dates/avatars/branche des récents et Dashboard, Apply, brouillon annulé, premier clic/Haut/Bas sans réparation et persistance au vrai redémarrage. Deux sorties normales à 0 ; sources, runtime et dépôt inchangés. Aucune installation hôte ou validation interactive du paquet déduite.

- [x] Publication en place, sauvegardes partielles et choix View temporaires ; prélecture Dashboard réutilisée sans modifier l’oracle de réouverture.
- [x] Appearance N1→N2 partiel ; inventaire régénéré et contrôlé : 99 fenêtres métier, 32 pages Settings dont 15 N2. Treize familles ouvertes, aucun N4.
- [ ] Intégration Visual Studio et AutoScale restent ouvertes. Troncature vérifiée sur le périmètre ci-dessous, sans niveau supplémentaire acquis.
- [ ] Colonne de branche Windows, autres consommateurs et variantes natives, langues/thèmes, Wayland/DPI/accessibilité, paquet interactif et macOS.

## Avatars partagés — périmètre vérifié et disposition

[ADR0099](adr/platform/avatars/0099-shared-desktop-runtime.md), source `3954cfc6a` : provider/fallback/template/cache Windows et avatar CommitInfo raccordés au runtime partagé, Clear impératif, durée disque de session, lecteurs visibles et fermeture drainée. [Full01](../../artifacts/linux/shared-avatars-20261018/full-01/parent-verification.json) : **2283/2283 Desktop (214 classes), 720/720 Core, 35/35 Plugins**, 103 ajouts Desktop sans retrait/skip ; build sans avertissement/erreur. Deux anciennes fixtures Appearance adaptées explicitement, sans retrait de cas.

[Paquet exact](../../artifacts/linux/snapshots/3954cfc6a/proofs/package-parent-verification.json) : **35 611 660 octets**, cinq phases produit à 0, préparation/extraction et backend vérifiés ; installation DPKG isolée. [Natif fonctionnel source](../../artifacts/linux/shared-avatars-20261018/native-source-01/root-operator-review.json), anglais/clair/X11 privé : fournisseur local, grille/CommitInfo, Apply/Cancel/Clear, visibilité indépendante, premier clic/Haut/Bas sans réparation et persistance au vrai redémarrage. Deux sorties normales à 0, quatre requêtes locales avec statut 200, serveur drainé ; aucune installation hôte ni interaction du paquet déduite.

- [x] Inventaire régénéré sur 3954 : mêmes 139 entrées, niveaux et aspects ; Appearance reste N2 partiel, treize familles ouvertes, aucun N4.
- [x] Template Custom long : colonne éditeur bornée par le viewport, place des aides réservée. Deux nouveaux cas rouges sur3954 puis **40/40 ciblés verts** (38 historiques), rendus anglais/clair et français/sombre ; aucun ancien oracle modifié. [Revue](../../artifacts/linux/appearance-settings-20261018/review/appearance-layout-root-review.json).
- [x] Correction c831 validée : [full01](../../artifacts/linux/appearance-layout-20261018/full-01/parent-verification.json) **2285/2285 Desktop (215 classes), 720/720 Core, 35/35 Plugins**, deux cas Layout ajoutés sans skip/retrait ; [paquet isolé exact](../../artifacts/linux/snapshots/c831b414b/proofs/package-parent-verification.json) **35 607 404 octets** et [natif source](../../artifacts/linux/appearance-layout-20261018/native-source-01/root-operator-review.json) distincts. Anglais/clair/X11, 67 commandes, 4 GET locaux à 200, deux sorties normales à 0 et drain ; draft de 858 caractères dans Settings à 958 pixels, trois aides visibles sans scroll/resize ni clic externe. Les preuves 3954 et les [refus de préparation/bookkeeping](failure.md#avatars-partagés--activation) restent conservés ; mêmes 139 entrées/niveaux/aspects, treize familles ouvertes, aucun N4.
- [ ] Chooser/classic réel, identité runtime/TTL entre familles et renderer en attente à la sortie réelle ; Blame/Gource natifs, providers externes, autres langues/thèmes, Wayland/DPI/accessibilité et macOS.

## Présentation des chemins — périmètre vérifié

- [x] Quatre choix Windows actifs dans Appearance, préférence par profil et publication en place aux listes communes ; chemin Git et tooltip complets conservés. [ADR0100](adr/preferences/appearance/0100-file-path-presentation.md).
- [x] [Ciblé02](../../artifacts/linux/truncate-path-20261018/review/root-targeted-import-01.json) : **278/278**, 192 historiques et 86 nouveaux, build sans avertissement. Rendus clair/sombre revus ; deux attentes historiques adaptées précisément. Les sorties du banc sont distinctes du vrai retour des tests.
- [x] Source `5a61ab43c` : [suites complètes acceptées par qualification complémentaire](../../artifacts/linux/truncate-path-20261018/review/full-02-postprocessed-root-review-01.json), **2371/2371 Desktop (218 classes), 720/720 Core, 35/35 Plugins** ; 2285 anciens cas et 86 ajouts retenus, zéro skip/retrait. Build/tests réellement à 0 ; récupération distincte à 0, retours du runner et du pipeline à 1 et comparaison runtime brute false conservés. 3150 sources et 1098 anciennes entrées runtime Desktop identiques, onze sorties de tests précisément classées.
- [x] [Paquet exact isolé](../../artifacts/linux/snapshots/5a61ab43c/proofs/package-parent-verification.json) et [backend](../../artifacts/linux/snapshots/5a61ab43c/proofs/package-backend-environment-verification.json) vérifiés ; état du dernier candidat ci-dessus, aucune installation hôte.
- [x] [Natif source ciblé](../../artifacts/linux/truncate-path-20261018/native-source-02/root-operator-review.json), anglais/clair/X11 privé : 39 commandes, quatre choix et Cancel/Apply/OK dans Appearance, rendus Diff/File tree, premier clic/Haut/Bas sans réparation et persistance au vrai redémarrage. Deux sorties normales et owner à 0 ; sources, runtime et dépôt inchangés.
- [ ] Resize et transitions regex natifs, Commit/Compare/Stash natifs, sombre/autres langues, Wayland/DPI/accessibilité et paquet interactif restent ouverts. Compact est portable, suffixe submodule absent ; inventaire régénéré et contrôlé, niveaux inchangés, treize familles ouvertes, aucun N4.

## Dictionary — périmètre vérifié

- [x] [ADR0101](adr/preferences/dictionary/0101-dictionary-and-commit-spelling.md) : choix global Appearance, priorité XML local/distribué puis global XDG, correction NetSpell dans le TextBox natif et fermeture drainée ; None retire les marques sans modifier le texte.
- [x] Source `014c7e53492c32896e7158e3e105a18adf3687c0` : [ciblé05](../../artifacts/linux/dictionary-20261018/review/targeted-05-root-review.json) **297/297 Desktop (192 historiques + 105 nouveaux), 8/8 Core** ; [full03](../../artifacts/linux/dictionary-20261018/full-03/parent-verification.json) **2476/2476 Desktop (223 classes), 728/728 Core, 35/35 Plugins**, build/suites/runner/vérification à 0, sans skip/retrait, entrées stables. [Native01 et full02 rejetés](failure.md#dictionary--catalogue-et-fermeture) conservés.
- [x] [Paquet03 exact](../../artifacts/linux/dictionary-20261018/review/package-03-root-review.json) : cinq phases à 0 ; extraction complète **460 entrées / 402 fichiers**, modes et octets conformes, backend exact vérifié. Installation isolée ; aucune installation hôte ni validation interactive du paquet.
- [x] [Natif03 ciblé](../../artifacts/linux/dictionary-20261018/review/native-source-03-root-review.json), anglais/clair/X11 privé : catalogue de 14 choix, Cancel/Apply/OK, suggestions, remplacement et Undo ; None retire immédiatement les marques à texte inchangé et persiste au vrai redémarrage. Deux sorties normales et owner à 0 ; sources/runtime stables, seuls le draft et le XML local déclarés sont ajoutés au dépôt de test.
- [ ] Ignore conserve une occurrence au même texte, adaptation Linux explicite. Audit GUI étendu et variantes de plateforme ouverts ; niveaux inchangés, treize familles ouvertes, aucun N4.

## Complétion du message Commit — périmètre vérifié

- [x] [ADR0102](adr/preferences/commit-completion/0102-commit-message-completion.md) : Complétion Windows des noms/stems actuels et anciens, symboles des deux côtés MM et cinq préfixes fixes ; patch U5 puis HEAD courant/ancien puis travail, extracteurs/matching Windows liés. Popup 200 ms/≥2, Ctrl+Espace explicite, résultat unique, notice/retour sans rejeu pendant le chargement ; TextBox natif/Undo/focus/IME. None indépendant ; ensemble des mots append-only pendant la vie de l’éditeur, exemptions NetSpell sensibles à la casse en mémoire, réinjectées au changement de moteur, sans user.dic. Adaptations statut du vrai Refresh réussi, contexte Git capturé, XDG, Compiled seule/timeout de 250 ms visible et UI Avalonia (ADR0102).
- [x] Source `069de52b7d17ff0f1faecaef8d2e3e5322c93d09` : [ciblés distincts](../../artifacts/linux/commit-completion-20261018/review/targeted-root-review-01.json) **338 identités = 305 + 33**, 297 historiques et 41 nouveaux, aucun run 338/338 inventé ; Core03 **11/11**. [Full02](../../artifacts/linux/commit-completion-20261018/full-02/parent-verification.json) **2517/2517 Desktop (226 classes), 739 Core et 35 Plugins**, sorties réelles à 0, sans skip/retrait.
- [x] [Paquet exact](../../artifacts/linux/commit-completion-20261018/review/package-01-root-review.json) **39 670 564 octets**, SHA256 `a4515e2cb40484c0345de259f3f639d48e89cc9da1ec6fddfbf0fb9147503cd4` ; cinq phases réelles à 0. Installation isolée ; aucune installation hôte déduite.
- [x] [Natif source borné](../../artifacts/linux/commit-completion-20261018/review/native-source-01-root-review.json) : Anglais/clair/X11 privé : premier clic et saisie, nom/stem, flèche/Tab et Undo ; Ctrl+Espace, métadonnée avec espace final, plus de cinq choix et Échap ; clic de suggestion sans perte de focus, en-US avec symbole exempté et seule faute soulignée, None immédiat, Provide off/on et vrai redémarrage avec brouillon/réglages conservés. Deux sorties normales et owner à 0 ; seuls draft et XML local attendus sont ajoutés, sans user.dic.
- [ ] Survol et sélection du popup utilisent le même bleu : différencier ces états pour se rapprocher de Windows. Audit natif étendu, premier Ctrl+Espace pendant le chargement, autres langues/thèmes, Wayland/IME-daemon/DPI/accessibilité et paquet interactif ouverts. Treize familles ouvertes, aucun N4, niveaux inchangés.
