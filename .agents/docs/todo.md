# TODO

Mise à jour : 17 octobre 2026. Les validations ci-dessous restent liées à leur source et à leur périmètre.

Une case cochée atteste le sous-périmètre décrit, avec preuve dans le code/tests ou résultat exécuté. Elle ne clôt pas sa famille historique. Migration complète : toutes les familles ouvertes couvertes, audit de parité, paquet installé et application démarrée sur Linux.

## État détaillé par fenêtre

La [matrice exhaustive Windows → Linux](windows-linux-window-inventory.md) remplace toute lecture « une fenêtre existe donc elle est complète ». Référence du code recensé `b692547e3` ; suite Desktop complète de référence `b692547e3` : 99 fenêtres/dialogues métier (98 WinForms et 1 ressource askpass C++), hors 5 bases WinForms ; 32 pages de paramètres, hors 4 bases. Fenêtres : 6 N0 absentes, 17 N1 substituts minimaux, 25 N2 partielles, 49 N3 fonctionnelles sur un périmètre testé, 2 spécifiques Windows. Pages : 16 N0, 9 N1, 6 N2, 1 spécifique Windows. Aucun N4 de parité complète auditée. Ces nombres ne sont pas un pourcentage du travail de migration.

L’inventaire contient aussi les 84 types Window Avalonia (dont 3 bases abstraites), 31 fabriques Linux sans classe dédiée et 366 sites de boîtes système/confirmations Windows, dont le Yes/No askpass natif C++. Les 13 dialogues de l’installateur Windows et 9 dialogues WiX référencés sont listés à part avec leur adaptation Debian. Chaque type/page est évalué explicitement ; le générateur signale toute nouvelle entrée non classée ou référence disparue. Les 189 fichiers Designer sont réconciliés. Les tests associés sont ceux des exécutions de référence, pas un nouveau test ni un audit complet de chaque écran.


Écarts concrets à traiter selon cette matrice : audit natif étendu du gestionnaire et de l’assistant de conflit de sous-module ; PR/hébergement et CI ; configuration des raccourcis/scripts/langue/fontes ; sorties/cache/stacks/export du journal Git ; audit natif étendu de l’askpass (Wayland, DPI/accessibilité, matériel FIDO et invites localisées). Les réglages de tri/priorités et le repli GitDefault Windows sans option restent également à aligner. Les familles globales restent ouvertes.

## Livré et vérifié

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

- Focus : parcours ciblés verts sur `2e3bba726` ; étendre les variantes de panneaux, thèmes, traductions et plateformes sans transférer automatiquement cette acceptation. Browse reste N2.
- Compare : liste partagée et groupes A→B/Range/BASE→B/BASE→A selon divergence et préférence globale ; contexte initial figé, endpoints index/worktree dynamiques, paire propre à chaque groupe et origine Settings conservée. Restent menus métier, métadonnées staged/tracked/sous-module, variantes de rendu/outils, gestes de plage clavier sans oracle Windows validé et plateformes étendues. FormDiff/Browse restent N2, pas de parité globale certifiée. ADR 0016.
- Scripts : pont de préparation typée inclus dans la suite complète de `2e3bba726`. Relier le contexte actif, le lanceur, les événements, menus et raccourcis ; conserver argv avant Quote et attendre le picker natif. Les trois saisies restent N1. ADR 0057/0078.
- Préférences : pages absentes de raccourcis, scripts/événements, langue, fontes et outils ; réglages distribués et tri/priorités Windows, dont repli GitDefault sans --date-order.
- Navigation : recherche incrémentale, choix interactif des parents/enfants, CI/superproject et véritables fournisseurs PR/hébergement.
- Réseau : scripts Before/After, Process par remote pour suppression distante, variantes multi-URL/atomic, destination locale Fetch/source URL, fermeture native des propriétaires et sortie continue.
- Rebase/Cherry-pick : matrice native des modes, codecs distincts et todo/done illisibles ; reprise après arrêt brutal, rollback des renommages et restauration IO interrompue. ADR 0045/0047.
- Merge/Reset/Revert : resolver avancé, renommages/sous-modules et réglages distribués ; ne pas transférer les audits ciblés à toutes les variantes.
- Fsck/Reflog/Cleanup et exports : audits natifs étendus, picker/Save as avec propriétaire témoin, binaires, annulation et fermetures normales. ADR 0048/0049/0050.
- Grilles Remotes/Worktrees/sous-modules : gestes complets de DataGridView, copies de cellules et variantes natives ; écrans ciblés présents, audit global ouvert. ADR 0067–0071.
- Askpass/plateforme : Wayland, DPI/accessibilité, matériel FIDO, invites localisées, chemins aux octets non UTF-8, intégrations XDG et arm64. Aucun de ces points n’est certifié par le seul headless.

La matrice donne les autres écarts écran par écran ; aucune famille n’est retirée du périmètre.

## Dernière validation — groupes avancés Compare

`b692547e3` : groupes ordonnés avec leurs paires typées, Range capturé, BASE/HEAD de topologie figés et index/worktree dynamiques. La préférence globale Browse/Compare est relue au calcul ; actions et fermeture de ce menu Settings reprennent seulement l’origine filtre/liste encore valide, avec caret/sélection, sans focus de rebuild. [ADR 0016](adr/workflows/references/0016-revision-comparisons.md).

[Suites complètes finales acceptées](../../artifacts/linux/snapshots/b692547e3/proofs/source-validation.json) : Desktop **1281/1281**, **137 classes** ; Core **565/565**, Plugins **35/35**, soit **1881 cas dans les suites finales acceptées**, tous fraîchement exécutés dans cette tranche, zéro échec/ignoré et aucun cas réutilisé de l’ancienne tranche. Les 25 nouveaux cas comprennent neuf Core et seize Desktop. [Core/Plugins](../../artifacts/linux/snapshots/b692547e3/proofs/component-validation.json) ont été exécutés sur `2e79da0c7`, puis rattachés au candidat par contrôle des sources/runtimes inchangés ; ils n’ont pas été relancés sur `b692547e3`.

Le [full 989 refusé](../../artifacts/linux/comparison-groups-desktop-checks-20261017-02-full/parent-failed-full-review.json), **1280 réussites et un échec sur 1281 exécutions**, reste conservé. La fixture RemoteDeletion observait ProcessNotice avant sa matérialisation visuelle ; le correctif de test conserve les assertions NoPush/refs, avec 29 ciblés verts. Ces exécutions et les 91 ciblés UI ne sont pas inclus dans les 1881 : ce total décrit les suites finales acceptées, pas le cumul de tous les essais.

[Paquet exact](../../artifacts/linux/snapshots/b692547e3/proofs/package-runtime-verification.json) : cinq phases exit0, extraction et installation isolée vérifiées. [Huit parcours natifs du payload](../../artifacts/linux/snapshots/b692547e3/proofs/native-parent-verification.json) verts sur leur périmètre X11/Openbox anglais clair ; les audits précédents ne sont pas transférés. Wayland, autres variantes, DPI/accessibilité et parité globale restent ouverts. Aucun nouveau paquet installé sur l’hôte ni push ; treize familles ouvertes, Browse/FormDiff N2, aucun N4.

## Livraison et preuves

Le paquet installé sur l’hôte reste `7.2.1-linux-alpha+879f7bae3…` : [version relue le 17 octobre](../../artifacts/linux/snapshots/b692547e3/proofs/host-version-observation.json), sortie réelle 0 ; [installation antérieure vérifiée](../../artifacts/linux/snapshots/879f7bae3/proofs/host-installation-verification-20261016-01.json). Aucun nouveau paquet installé sur l’hôte ; les candidats restent distincts en isolation, aucune application utilisateur arrêtée et aucun push.

Les [snapshots](../../artifacts/linux/snapshots/) conservent résultats, captures, checksums et diagnostics avec leur SHA. Un audit natif ancien n’est pas transféré automatiquement à un candidat récent. L’[ancienne chronologie détaillée](../../artifacts/linux/migration-notes-20261016/todo-before-compaction-983a0d809.md) est archivée depuis le commit 983a0d809 ; le présent TODO garde les périmètres et les suites à donner.
