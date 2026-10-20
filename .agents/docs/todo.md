# TODO

Mise à jour : 20 octobre 2026. Les validations restent liées à leur source et à leur périmètre. **[Relevé Roslyn actuel](../../artifacts/linux/help-images-20261020/review/root-inventory-review.json) : 2463 fichiers C#, zéro diagnostic**. Les cinq modifications Modal préexistantes du checkout restent distinctes de la source testée.

La dernière suite complète de référence porte sur `d16548071`. Les anciennes preuves conservent leur source et leur périmètre ; elles ne sont pas transférées implicitement aux changements suivants.

Une case cochée atteste le sous-périmètre décrit, avec preuve dans le code/tests ou résultat exécuté. Elle ne clôt pas sa famille historique. Migration complète : toutes les familles ouvertes couvertes, audit de parité, paquet installé et application démarrée sur Linux.

## État détaillé par fenêtre

La [matrice exhaustive Windows → Linux](windows-linux-window-inventory.md) distingue présence et parité. **Référence `d16548071` : 3566/3566 Desktop (299 classes), 1062/1062 Core, 35/35 Plugins** ; **99 fenêtres métier : 0 N0/N1, 48 N2, 50 N3, 1 hors périmètre ; 32 pages : 0 N0/N1, 31 N2, 1 hors périmètre**. Aucun N4 ni pourcentage de travail déduit des comptes.

Les [suites complètes](../../artifacts/linux/update-scheduler-20261020/review/root-full-review.json) ont tourné sur la source exacte figée dans un worktree propre ; l’[équivalence de source](../../artifacts/linux/update-scheduler-20261020/review/source-import-review.json) conserve les cinq modifications Modal distinctes du primaire, sans qualification de l’ensemble du checkout modifié.

Écarts concrets à traiter selon cette matrice : audit natif étendu du gestionnaire et de l’assistant de conflit de sous-module ; compléments PR GitHub et autres hébergeurs/CI ; compléments des raccourcis, audit étendu des scripts et compléments des fontes ADR0091, compléments de langue ; sorties/cache/stacks/export du journal Git ; audit natif étendu de l’askpass (Wayland, DPI/accessibilité, matériel FIDO et invites localisées). Le tri/priorités et GitDefault sans option sont portés sur le périmètre ADR 0082 ; CommitInfo est porté sur le périmètre partagé de l’ADR 0083, suites complètes et natif Browse ciblé vérifiés ; remotes vides/inactifs et paramètres globaux/locaux/distribués restent ouverts. Les familles globales restent ouvertes.

## Images d’aide des commandes — périmètre vérifié

Source `eb41998b6` : [248/248 tests Desktop ciblés, 21 classes](../../artifacts/linux/help-images-20261020/review/root-targeted-review.json), [équivalence de source](../../artifacts/linux/help-images-20261020/review/source-import-review.json) et [build post-commit des trois projets](../../artifacts/linux/help-images-20261020/build-04/result.json), sans avertissement. Sources et binaires ciblés stables ; aucune nouvelle suite complète sur cette source, référence d165 conservée. Les cinq modifications Modal restent distinctes.

- [x] Neuvième champ Advanced `DontShowHelpImages=false`, Apply/Cancel/reopen, trois consommateurs et reprise Rebase imbriquée ; masquage complet sans écrire les états locaux. Restauration locale, corruption non réparée, profils/journal/index et état Updates conservés couverts par le ciblé. Stash Pull reste mono-ligne à largeur naturelle/MinWidth, avec minimum du footer mesuré une fois à Opened. [ADR0134](adr/preferences/advanced/0134-global-command-help-visibility.md).
- [x] [Natif source X11 privé](../../artifacts/linux/help-images-20261020/review/root-native-review.json) : 52 commandes, 49 captures inspectées, 12 checkpoints et deux fermetures normales, EN/clair puis FR/sombre. Apply masque l’aide ; Cancel du brouillon inverse garde les octets des préférences sauvegardées. États locaux Pull ouvert/Merge replié/Rebase ouvert, dépôt/bare/config et produits inchangés. Six fenêtres masquées lisibles aux largeurs Pull/Merge/Rebase 650/500/745 px ; changement Merge→Fetch français sans dérive, champs Intervalle De/À activés lisibles.
- [x] [Paquet exact eb41998b6](../../artifacts/linux/snapshots/eb41998b6/deb/gitextensions_7.2.1~linux-alpha_amd64.deb), **40 073 680 octets**, [sept phases à 0](../../artifacts/linux/help-images-20261020/review/root-package-review.json), dont installation/lancement DPKG isolés. 356 fichiers publiés, 402 fichiers réguliers et MD5 vérifiés ; publish/extrait, Skia patché et français contrôlés. SHA-256 `9c2545f0846151a0a5d600d48af705bd6930fba7e071afb8ec1af6fa9e520cf4`. Aucun push, release ni installation hôte.

Advanced/Pull/Rebase restent N2 et Merge N3. Aucune opération Git ni réactivation globale exercée en natif : la restauration des états locaux relève ici du ciblé headless. Interaction installée, OS vierge et Wayland/IME/DPI/accessibilité restent ouverts, ainsi que Console Windows, AlwaysAdvanced hors Remotes et les treize familles globales.

## Contrôle hebdomadaire des mises à jour — périmètre ciblé

Source `d16548071` : [195/195 tests Desktop ciblés](../../artifacts/linux/update-scheduler-20261020/review/root-targeted-review.json), puis [suites complètes](../../artifacts/linux/update-scheduler-20261020/review/root-full-review.json) **3 566/3 566 Desktop, 1 062/1 062 Core, 35/35 Plugins**. Les cinq modifications Modal préexistantes restent hors de cette source qualifiée.

- [x] Deux choix Windows durables dans Advanced : hebdomadaire activé et préversions désactivées par défaut ; alpha/bêta explicitement préversions, choix manuel de session initialisé depuis la préférence. Apply/Cancel/reopen, reçus/CAS et écriture General coordonnée couverts. [ADR0133](adr/distribution/updates/0133-weekly-update-admission.md).
- [x] Démarrage normal, refresh grille/dashboard/F5 et Apply des options Updates réadmettent le contrôle ; tentative UTC avant HTTP, strictement plus de sept jours et verrou coopératif sous XDG_STATE_HOME. État invalide conservé, échec automatique sans interruption, résultat différé jusqu’à Browse actif/libre/sans modale ; le manuel annule l’automatique sans modifier l’horodatage.
- [x] [Natif source ciblé](../../artifacts/linux/update-scheduler-20261020/review/root-native-review.json) : 35 commandes, 30 captures inspectées et quatre sorties normales. Démarrages désactivé/activé puis horodatage identique au redémarrage ; Apply des deux options en anglais/clair, Cancel du brouillon suivant conservant le profil exact ; cases sauvegardées retrouvées en français/sombre. Réseau volontairement indisponible : ni succès GitHub réel, ni notification positive ou téléchargement natif déduits.
- [ ] Publication Linux initiale, téléchargement réel public, installation interactive et audit étendu Wayland/DPI/accessibilité restent ouverts.

FormUpdates et Advanced demeurent N2 ; huit champs Advanced sont qualifiés dans ce lot d165. Le masquage d’aide est ajouté ensuite par ADR0134, avec ses preuves propres ; console Windows et extension d’AlwaysAdvanced au-delà de Remotes restent ouvertes. Le paquet vérifié du lot manuel ci-dessous reste sa preuve propre.

[Paquet exact d16548071](../../artifacts/linux/snapshots/d16548071/deb/gitextensions_7.2.1~linux-alpha_amd64.deb) : **40 080 932 octets**, [sept phases vérifiées](../../artifacts/linux/update-scheduler-20261020/review/root-package-review.json), dont installation/lancement DPKG isolés. 402 fichiers et leurs sommes MD5, contenu publish/extrait, Skia patché et traduction française contrôlés. SHA-256 `2f227c554b37b72a7ceb5d17e773f1aff2f608c8ad50aeec29367b6ab19628c7`. Aucun push ni installation hôte ; téléchargement public et installation interactive non qualifiés.

## Mises à jour Linux — périmètre vérifié

Source `3a2d4cc85` : compilation des trois projets sans avertissement ; [64/64 tests Desktop ciblés](../../artifacts/linux/updates-20261020/review/root-targeted-review.json), dont les menus classiques. Quatre captures headless EN/FR clair/sombre inspectées. La référence complète de ce lot était b745 ; les cinq fichiers Modal préexistants sont hors source qualifiée.

- [x] Help ouvre Updates possédé et traduit ; flux public du fork, ordre SemVer, architecture/nom/URL et SHA-256 contrôlés. Annulation, délais/corps bornés, erreurs/Retry, liens et second geste d’ouverture du paquet couverts. [ADR0132](adr/distribution/updates/0132-linux-updates.md).
- [ ] Publication initiale sur GitHub (aucune release au 20/10) ; succès de l’installation par le gestionnaire du bureau et interactions natives/Wayland/DPI/accessibilité à qualifier.

Updates passe N1→N2 sur ce périmètre ; aucun N4. Il n’y a plus de fenêtre métier N1, mais les 13 familles et les compléments N2/N3 restent ouverts. [Paquet 3a2d4cc85](../../artifacts/linux/snapshots/3a2d4cc85-updates/deb/gitextensions_7.2.1~linux-alpha_amd64.deb) : **40 070 828 octets**, [vérification](../../artifacts/linux/updates-20261020/review/root-package-review.json). Publication/assemblage, contrôle et démarrage après extraction, installation/lancement DPKG isolés réussis ; 402 fichiers, contenu publish/extrait et manifestes vérifiés. SHA-256 `1015b302e6cf2f6ac9056bd379fce995c84bb5bfc7e726025577a2f0dadf4792`. Aucun push ni installation hôte ; interactions Updates natives et vraie release publique non qualifiées.

## Sélecteur de fichiers — périmètre vérifié

Source `5356de767` : [build et ciblé vérifiés](../../artifacts/linux/file-picker-20261020/review/root-targeted-review.json), **58/58 Desktop et 38/38 Core Scripts**. La référence complète de ce lot était b745 ; les cinq modifications Modal préexistantes restent hors source qualifiée.

- [x] FilePrompt conserve formulaire, texte et citation Windows ; le vrai picker géré se ferme sur annulation token/parent, avec drainage de son erreur possédée. Multisélection, saisie remplaçant une sélection précédente, chemins littéraux, dossiers et refus de fichiers inexistants couverts. [ADR0131](adr/workflows/scripts/0131-owned-file-picker.md).
- [x] [Natif source X11](../../artifacts/linux/file-picker-20261020/review/root-native-review.json) : 49 commandes, 46 captures inspectées, deux fermetures normales EN/clair puis FR/sombre. Vrai menu Run script et toolbar, Cancel conserve le texte sans exécution ; sélection multiple puis nom édité remplacent la sélection et arrivent exactement dans les argv des deux vrais scripts. Dépôt, fichiers d’entrée et profils Scripts/environnement préservés.
- [x] [Paquet exact 5356](../../artifacts/linux/snapshots/5356de767/deb/gitextensions_7.2.1~linux-alpha_amd64.deb), **40 055 836 octets**, [vérifié](../../artifacts/linux/file-picker-20261020/review/root-package-review.json) : cinq phases à 0, 402 fichiers/460 entrées, contenu archive/publish/extraction et traductions contrôlés ; installation DPKG et démarrage isolés. Aucun push ni installation hôte ; interaction installée et OS vierge non qualifiés.

FilePrompt passe N1→N2 sur ce périmètre ; Updates est traité dans le lot ci-dessus. Sélecteur Avalonia local aux scripts, sans portail GTK/KDE ; variantes Wayland/IME/DPI/accessibilité ouvertes. Aucun N4 ; les 13 familles historiques restent ouvertes.

## SSH — perimetre verifie

Source `c358d990f` : [compilation des trois projets et ciblé vérifiés](../../artifacts/linux/ssh-settings-20261020/review/root-targeted-review.json), **132/132 Desktop et 104/104 Core** (6 environnement, 93 compatibilité, 5 SSH). Les suites complètes de référence restent celles de b745 ; elles n’ont pas été réexécutées pour ce lot.

- [x] Page SSH sous Git Extensions, icône/contrôles Windows, choix natif ou exécutable littéral. Apply/Cancel/Reload, profils antérieurs, champs inconnus, conflits, startup isolé et picker possédé testés. Vrais clone/fetch/push et remote heads sur dépôt local via client simulé ; priorités Git, contextes engagés, scripts et console conservés. [ADR0130](adr/preferences/environment/0130-native-ssh-settings.md).
- [x] [Natif source X11](../../artifacts/linux/ssh-settings-20261020/review/root-native-review.json) : 44 commandes, 40 captures inspectées, trois vrais lancements/fermetures à 0 ; EN/clair puis FR/sombre. Repair place le focus dans la page existante, Apply publie le chemin exact, Cancel abandonne B en conservant A, A survit au redémarrage et le mode natif au suivant. Deux vrais probes A/-V ; profil étranger préservé. Aucun serveur SSH/authentification, Pageant, picker natif, Wayland/DPI/accessibilité ni interaction du paquet certifié.
- [x] [Paquet exact c358](../../artifacts/linux/snapshots/c358d990f/deb/gitextensions_7.2.1~linux-alpha_amd64.deb), **40 058 040 octets**, [vérifié](../../artifacts/linux/ssh-settings-20261020/review/root-package-review.json) : cinq phases à 0, 402 fichiers/460 entrées, contenu publish/archive/extraction et payload contrôlés ; DPKG et démarrage isolés. Copies SDK/cache physiques dédiées. Aucun paquet installé sur l’hôte, aucun push ; interaction installée et OS vierge non qualifiés.

SSH passe N1→N2. Plink, Pageant et PuTTYgen ne sont pas exclus par la plateforme : restent socket/durée de vie d’un agent possédé, chargement des clés des remotes, action CLI PuTTYgen et interactivité Plink. Les cinq modifications Modal préexistantes du primaire restent hors source qualifiée ; les 13 familles historiques restent ouvertes.

## Portable workflows — perimetre verifie

Source `b745524c1` : [3441/3441 Desktop, 1057/1057 Core, 35/35 Plugins](../../artifacts/linux/portable-workflows-20261020/review/root-full-review.json), 290 classes Desktop. [Lignée des tests](../../artifacts/linux/portable-workflows-20261020/review/test-lineage-review.json) : scénarios W7 conservés ; une identité de validation adaptée au symbole vide désormais valide, sans affaiblir le contrôle d’erreur.

- [x] FormEditor (ADR0123) : fichiers existants, menus Edit Browse/Compare et vrai config local ; sauvegarde BOM/codec, recherche, dirty-close et effets partiels couverts par les suites. Natif EN/clair : F4 Browse, Cancel conserve le brouillon et les octets, Ctrl+S écrit le texte exact sans newline ajoutée. Aucun nouveau geste natif Compare/config ni lancement CLI editor qualifié.
- [x] Fork/Clone (ADR0124) : sélection/parent, POST sans rejeu, clone Git et upstream. Natif EN/clair : recherche, fork accepté conservé après HTTP503 secondaire, vrai clone et remotes vérifiés sur fixture privée ; aucun compte ni service GitHub réel certifié.
- [x] Confirmations (ADR0125) : 13/17 choix Windows et leurs consommateurs testés. Colors (ADR0128) : 5/6 options Graph. Natif EN/clair : Apply/Cancel avec comparaison exacte des profils, champs étrangers préservés ; badges conservés au redémarrage FR/sombre. Multicolor/CSS/thèmes Windows et quatre confirmations restent absents.
- [x] QuickStringSelector/SimplePrompt (ADR0126) : sélecteur EN/FR ancré, vrai script de choix de remote, Rename/Move avec nouveau prompt après validation inchangée et fermeture sans mutation. FilePrompt était N1 dans cette référence ; son picker possédé est traité par ADR0131 ci-dessus.
- [x] Rapport et détail récupérables (ADR0127) : projection/copie et consentements couverts par les suites. Natif EN/clair depuis une erreur Git réelle : onglets, propriété Message, détail au clavier, fermeture enfant puis Ignore et retour au dépôt. Send/Quit externe et circuit fatal NBug non exercés nativement.
- [x] Advanced (ADR0129) : six champs et trois profils, symbole vide valide, intentions et reçus partiels testés. Natif EN/clair puis FR/sombre : AlwaysAdvanced et `(none)` appliqués puis retrouvés au redémarrage. Updates/RC ajoutés ensuite par ADR0133 et masquage d’aide par ADR0134 ; console Windows reste ouverte, SSH distinct.

[Natif source](../../artifacts/linux/portable-workflows-20261020/native-02/root-native-review.json) : 88 commandes, 85 captures inspectées, deux fermetures normales ; premier clic bleu puis Haut/Bas sans perte du focus. Parcours limités à X11 privé. L’arbre Settings FR peut défiler horizontalement sur un libellé long ; Wayland/IME/DPI/accessibilité et interaction du paquet installé sur le poste restent ouverts. L’essai natif01 interrompu ne qualifie aucun parcours.

[Paquet `7.2.1-linux-alpha`](../../artifacts/linux/snapshots/b745524c1/deb/gitextensions_7.2.1~linux-alpha_amd64.deb) : 40 049 344 octets, SHA256 `d8966641dddd617329a25c9da218dd518ea3c21a0476af1d5c021344e239a245` ; [cinq phases vérifiées](../../artifacts/linux/portable-workflows-20261020/review/root-package-review.json), dont installation/lancement dans une racine isolée. Ce résultat ne vaut pas installation du nouveau paquet sur le poste.

Après les lots SSH, FilePrompt et Updates ci-dessus, aucun écran métier ne reste N1 ; les compléments, dont la publication et l’installation interactive des mises à jour, restent ouverts ; le contrôle automatique est décrit par ADR0133. Les compléments SSH/Pageant restent identifiés sous N2. Les neuf promotions N1→N2 du lot Portable workflows restent partielles ; aucun N4 et les 13 familles historiques restent ouvertes.

## Livré et vérifié

- [x] Detailed : six champs Windows, portées et XML partagés, brouillons Apply/Cancel, options Graph et consommateurs Push/Merge ; ADR0105. Suites complètes, paquet et parcours X11 source avec redémarrage vérifiés ; N2 et audit étendu ouvert.

- [x] General : groupes et dépendances Windows, profils partagés et consommateurs réels ; suites complètes, paquet exact et natif source ciblé avec redémarrage vérifiés. ADR0097 ; fenêtre console Windows séparée et télémétrie indisponibles, niveau N2 et audit étendu ouvert.

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

- Advanced : initialiser Push/Merge depuis AlwaysAdvanced ; menu ciblé des références encore ouvert. Rebase normal affiche déjà les options comme Windows. [Audit des consommateurs](../../artifacts/linux/help-images-20261020/review/always-advanced-audit.md).
- Focus : les retours ciblés des menus, outils, prompts et du changement vers File tree sont corrigés et vérifiés dans les périmètres ci-dessous. Étendre les variantes de panneaux, thèmes, traductions et plateformes ; Browse reste N2.
- Compare/Browse : listes, groupes et menus communs avec paire/métadonnées par feuille, mutations ciblées, outils/fichiers mémorisés et vraie console interactive. Restent Visual Studio, audit étendu des scripts et découverte/réglages complets des outils, présélection gitlink, cas First vide/root initial et gestes de plage sans oracle Windows validé. Reprise des fichiers masqués par Skip/Assume et première flèche après retrait vérifiées via Commit → Unstaged → Settings ; File tree historique ne sert pas de lecteur des flags live. Stage/Unstage natifs et variantes non atteintes restent à auditer. FormDiff/Browse restent N2 ; ADR 0016/0080/0081.
- Scripts : éditeur brouillon XDG, plan argv avant Quote, runner possédé, catalogue/menus/touches par ID et raccords Before/After validés par les suites complètes ; paquet exact et natif S01 ciblé vérifiés, voir la section dédiée. QuickStringSelector et SimplePrompt sont N2 (ADR0126) ; FilePrompt est N2 sur le périmètre ADR0131. Étendre natif, erreurs de profil, outils/OS, icônes associées et programmes shell complexes ; ADR 0057/0078.
- Préférences : compléments de raccourcis ADR0089 et audit étendu de la page Scripts ; treize champs Config et encodages partagés vérifiés (ADR0093/0094), Global hors dépôt et pages Git/Checklist portés N2 sur le périmètre ADR0095 ; découverte exhaustive et audit natif étendu ouverts. Fonts/EOL porté au redémarrage sur le périmètre ADR0091 ; langue ADR0090 portée sur son périmètre ciblé ; réglages globaux/locaux/distribués. Tri/priorités ADR 0082 : remotes vides/inactifs et variantes natives ; CommitInfo partagé ADR 0083 : suites complètes, paquet exact et natif Browse ciblé vérifiés ; autres hôtes/variantes encore ouverts, liens associés/avatar/Markdown/mailto et audit étendu encore ouverts. Badges et QuickRef gardent leurs comparateurs distincts.
- Navigation : parcours Parent/Child porté sur le périmètre ADR0088 ; étendre l’audit natif des filtres, ordres, hôtes et gestes. Navigate Windows n’ouvre pas de picker ; liens Parents/Children de CommitInfo disponibles. Compléments CI/superproject et autres hébergeurs restent ouverts ; PR GitHub partielles dans ADR0110.
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

Dernier candidat vérifié isolément : [`.deb 7.2.1-linux-alpha · fe1cff986`](../../artifacts/linux/snapshots/fe1cff986/deb/gitextensions_7.2.1~linux-alpha_amd64.deb), **39 681 680 octets**, SHA256 `b33b3d1bc815c0ee2570df6ec02fb522aa6ac53b35574e2d618886f533999b7c` ; [revue du paquet03](../../artifacts/linux/commit-templates-20261018/review/package-03-root-review.json), cinq phases à 0, extraction complète et installation/smoke isolés.

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
- [x] Source `8d9c99259` : `ClassicCompletionList` partagé par Commit/Clone/champs dépôt, seule la sélection est bleue. [Build et ciblés](../../artifacts/linux/completion-list-20261018/review/targeted-01-root-review.json) **106/106 Desktop, 7 classes existantes**, build à zéro avertissement/erreur ; rendus Commit clair/sombre inspectés. Aucun nouveau full ni paquet déduit.
- [x] [Natif du style](../../artifacts/linux/completion-list-20261018/review/native-source-01-root-review.json), anglais/clair/X11 privé : souris sur la première suggestion, Down sélectionne la seconde et laisse la première blanche ; Tab, clic et Undo sans réparation de focus. Une génération, application/owner à 0, source/runtime stables ; seul `COMMITMESSAGE=unsta` ajouté au dépôt de test, XML local et user.dic absents. Ce natif de style ne constitue pas une preuve de paquet.
- [ ] Audit natif étendu, premier Ctrl+Espace pendant le chargement, autres langues/thèmes, Wayland/IME-daemon/DPI/accessibilité et paquet interactif ouverts. Treize familles ouvertes, aucun N4, niveaux inchangés.

## Bibliothèque de messages Commit — périmètre vérifié

- [x] [ADR0103](adr/preferences/commit-templates/0103-commit-message-templates.md) : bibliothèque applicative distincte de Git `commit.template`, deux onglets/dix emplacements initiaux, six règles et Conventional ; intentions par profil, publication Saved et WordWrapper Windows lié. **FormCommitTemplateSettings N1→N2**, FormCommit reste N2.
- [x] Source `fe1cff98626a24e9bc099714cc96a5d8524dfa8f` : [ciblé primaire02](../../artifacts/linux/commit-templates-20261018/review/targeted-primary-02-root-review.json) **214/214 sur 13 classes, 144 historiques + 70 nouveaux**, 206 identités précédentes retenues et huit ajoutées ; build sans diagnostic, tests/vrai retour à 0, aucun skip, 3796 sources stables. [Quatre rendus primaire02 relus](../../artifacts/linux/commit-templates-20261018/review/targeted-primary-02-render-review.json), anglais/clair et français/sombre. La suite complète069 demeure historique et inchangée.
- [x] [Natif source02 accepté](../../artifacts/linux/commit-templates-20261018/review/native-source-02-root-review.json), anglais/clair/X11 privé : Name/Text, dix slots et première Échap gardant Settings/brouillon ; six défauts visibles, Save, application/Undo ; Ctrl+T et Ctrl+Shift+T puis Entrée/Undo sous parent survolé, sans réparation. Fermetures normales Commit/Browse et app/contrôleur/outer à 0, identités retirées ; sources/runtime stables. Seuls COMMITMESSAGE et profil UI attendus ajoutés, XML/user.dic absents ; [refus native01 et bruts](failure.md#messages-commit--deux-écarts-natifs) conservés.
- [x] [Paquet03 exact](../../artifacts/linux/commit-templates-20261018/review/package-03-root-review.json) : **39 681 680 octets**, cinq phases à 0, extraction complète **460 entrées/402 fichiers**, backend/dictionnaires/licences et installation/smoke isolés vérifiés. Cette preuve reste distincte du natif source ; aucune installation hôte, paquet interactif ni nouveau full déduit.
- [ ] Redémarrage, validation personnalisée/erreurs/regex/fournisseurs/reprise réelle du pointeur en natif, autres langues/thèmes, Wayland/IME-daemon/DPI/accessibilité et paquet interactif restent ouverts. Branche/superproject suivent l’[ADR0104](adr/workflows/commits/0104-commit-branch-and-superproject.md) ; autres réserves Commit conservées. Ce lot Templates conservait 139 clés/aspects et 137 autres entrées identiques, treize familles ouvertes, sans nouveau N3/N4 ni complétude certifiée.

## Branche et superproject depuis Commit — qualification ciblée

- [x] [ADR0104](adr/workflows/commits/0104-commit-branch-and-superproject.md) : bouton/raccourci Create branch, statut et lien tracking ; HEAD détachée hors rebase avec Cancel par défaut, Checkout/Create/Continue et checkout ciblé revalidé. Préférence globale Stage vraie par défaut, publication immédiate/relecture stricte ; seul gitlink associé au HEAD enfant final après commit/push/récupération, erreurs/effets et HasCommitted conservés, sans auto-close. Toolbar canonique avec Options à droite/débordement et CommandLink commun.
- [x] [Core primaire01](../../artifacts/linux/commit-branch-superproject-20261018/review/core-primary-01-root-review.json), source `1cbebda758` : **20/20**, build/tests et vrai retour à 0, acteurs retirés. [W06](../../artifacts/linux/commit-branch-superproject-20261018/review/worktree-targeted-06-root-review.json) **415/415** puis [ciblé primaire01 frais](../../artifacts/linux/commit-branch-superproject-20261018/review/targeted-primary-01-root-review.json), source UI `8872bdb6773dc7769bfe6b8415bac1a8b2893111` : **415/415 Desktop, 24 classes, 347 historiques + 68 nouveaux (47 branche/21 superproject)** ; mêmes 415 identités W06 et 214 identités Templates conservées. Build sans diagnostic, vrais retours à 0, aucun skip/retrait, 3807 sources stables, acteurs retirés. Manifeste runtime2137 après tests, sans attestation de stabilité pendant les tests ; aucun nouveau full, complète069 historique inchangée. [Échecs antérieurs](failure.md#commit-branche-et-superproject--essais-et-gardes) conservés.
- [x] [Natif source01 borné](../../artifacts/linux/commit-branch-superproject-20261018/review/native-source-01-root-review.json), français/sombre/X11 privé : Stage coché/libellé complet, statut main→origin/main, Options à droite ; débordement → Create branch avec main présélectionné puis Cancel. Sous pointeur immobile sur Create, Ctrl+T conserve feat sélectionné, Entrée produit `feat: unsta`, Undo restaure `unsta` et le focus. Une génération, fermetures normales Commit/Browse et sortie à 0, acteurs retirés, sources/runtime stables ; seul `COMMITMESSAGE=unsta` ajouté au dépôt enfant résolu. Aucun commit/push/staging, geste detached ou redémarrage natif exécuté.
- [x] [Paquet01 exact](../../artifacts/linux/commit-branch-superproject-20261018/review/package-01-root-review.json), source8872 : **39 702 588 octets**, SHA256 `db46e15aa6220c0dadcfa932aec55e0215d6d6d94e536ffda75adf8bb8ab69f4` ; cinq phases à 0, extraction complète **460 entrées/402 fichiers**, backend/dictionnaires/licences et installation/smoke isolés vérifiés. Trois owners réellement attendus à 0 et retirés ; primaire/runtime/SDK/NuGet stables. Gestes natifs liés à la source, sans installation hôte, paquet interactif ni nouveau full déduits.
- [ ] Création Worktrees depuis un sous-module absorbé suivie séparément par l’[ADR0106](adr/workflows/worktrees/0106-absorbed-main-checkout.md), Core ciblé acquis et Desktop/native encore ouverts ; le [linked child natif Core03 historique](../../artifacts/linux/commit-branch-superproject-20261018/worktree-validation/core-03/core.log) reste une preuve distincte. Audit natif étendu, autres langues/thèmes, Wayland/IME/DPI/accessibilité et paquet interactif ouverts. FormCommit reste N2, FormCreateBranch N3 historique et aspects/niveaux conservés ; treize familles ouvertes, aucun nouveau N3/N4 ni complétude certifiée.

## Detailed et worktrees absorbés — qualification partielle

- [x] [ADR0105](adr/preferences/detailed/0105-detailed-graph-and-xml-settings.md) : six champs Windows, trois Graph globaux et trois clés XML ; Effective readonly, Local/CommonDirectory, Distributed/workdir, Global/XDG. Brouillons Apply/Cancel, XML commun Dictionary, publication du graphe dans ses trois hôtes ; Push Multiple/Process ls-remote sans fetch/repli et options Merge instantanées.
- [x] Source `344a910d1`, [revue Root des suites complètes](../../artifacts/linux/detailed-settings-20261019/review/root-full-review.json) : **2742/2742 Desktop, 787/787 Core, 35/35 Plugins**, sans skip. Exécution dans W puis import exact au primaire, pas exécution primaire revendiquée ; 2516 identités historiques inchangées et un cas renommé/étendu lors du lot Templates ([filiation vérifiée](../../artifacts/linux/detailed-settings-20261019/review/test-lineage-review.json)). Les bruts runtime incluent une trace JSON de test : [exception précise vérifiée](../../artifacts/linux/detailed-settings-20261019/review/runtime-delta-review.json), aucun binaire modifié. Les [trois métadonnées d’arbre préexistantes](../../artifacts/linux/detailed-settings-20261019/review/build-runtime-delta-review.json) et la [nouvelle trace de gabarits Pull](../../artifacts/linux/detailed-settings-20261019/review/full-test-output-review.json) sont vérifiées séparément par leur writer et leurs pièces TRX. [Échecs et préparation](failure.md#detailed--premiers-ciblés-core) conservés ; aucune reconstruction d’un full vert à partir des essais interrompus.
- [x] [ADR0106](adr/workflows/worktrees/0106-absorbed-main-checkout.md) : principal absorbé rattaché au vrai checkout réciproque, création principal/linked, relatif/absolu et effets natifs partiels. Vingt scénarios Worktrees dont cinq nouveaux ; parcours Desktop/natif depuis un absorbé encore ouvert.
- [x] [Paquet exact isolé](../../artifacts/linux/detailed-settings-20261019/review/package-root-review.json) : source `344a910d1`, **39734208 octets**, SHA256 `3ea21c438901f9413142018efff4afb56073b497c49b17f1a5a3dfd74961c095` ; cinq phases à 0, extraction, backend et installation/démarrage DPKG privés contrôlés. Aucun geste métier du paquet, installation hôte ou push déduit.
- [x] Detailed seul passe N0→N2, aspect rapproché. Les ajouts à Push/Merge et Worktrees gardent leurs niveaux historiques ; matrice régénérée et vérifiée sur cette source, 139 évaluations, 134 autres entrées identiques. Treize familles globales ouvertes, aucun N4.
- [x] [Parcours X11 privé de Detailed](../../artifacts/linux/detailed-settings-20261019/native-01/root-native-review.json) : Browse → Settings, Apply des trois options Graph, réouverture, vrai redémarrage et persistance ; brouillon Local annulé sans écriture. Deux processus sortis normalement à 0 ; observation limitée à ce parcours source.
- [ ] Audit natif étendu de Detailed/Push/Merge/Worktrees absorbé, langues/thèmes supplémentaires, Wayland/IME/DPI/accessibilité, paquet interactif et parité globale ouverts. Tri-state, Global XDG, bare sans Distributed et valeur Merge hors0–100 visible/bloquante sont des adaptations explicites.

- [x] [Installation machine demandée](../../artifacts/linux/detailed-settings-20261019/host-install-01/root-install-review.json) : APT à 0, `7.2.1-linux-alpha+344a910d1`, 402/402 fichiers du manifeste et entrée desktop contrôlés. Aucun parcours graphique hôte exécuté ; retours utilisateur attendus.

## Contributeurs, console, liens et GitHub Actions — validation ciblée

Source `95db3ae7188e7c12eb670fc75ed81c7460644eb0` ; 4041 sources/assets figés, suites complètes et filiation des 2742 cas Desktop antérieurs contrôlées. [Revue complète](../../artifacts/linux/remaining-windows-20261019/review/root-full-review.json), [natif ciblé](../../artifacts/linux/remaining-windows-20261019/review/root-native-review.json), [paquet](../../artifacts/linux/remaining-windows-20261019/review/root-package-review.json), [lecture GitHub publique](../../artifacts/linux/remaining-windows-20261019/live-github-public-02/root-tool-wait-receipt.json). Le relevé et la matrice sont régénérés ; aucun N4 et les 13 familles globales restent ouvertes.

- [x] Contributeurs : crédits Windows liés, trois onglets compacts depuis About ; N3 fonctionnel ciblé, audit étendu ouvert (ADR0107).
- [x] Console : terminal installé réel, palettes/fontes partagées, Apply/Cancel, palette peinte après navigation/survol/focus et persistance au redémarrage ; N2, profils externes et ConEmu non reproduits (ADR0107).
- [x] Revision links : XML et règles Windows, modèles GitHub/Azure DevOps, Related links sans recharger le message, baseline commune avec Detailed/CI ; N2, audit natif de tous les hôtes et activations ouvert (ADR0108).
- [x] CI/GitHub Actions : paramètres scoped, lecture GET, états par SHA sans recréer les lignes, cache/ETag et polling drainé ; N2. Lecture anonyme du client compilé vérifiée sur le dépôt public (200/304) ; interface authentifiée/live Enterprise encore ouverte. Build report liste les runs et ouvre leur page explicitement dans le navigateur, sans viewer HTML/jobs/logs équivalent Windows (ADR0109).
- [ ] Compléments PR/authentification dans ADR0110 ci-dessous ; autres fournisseurs CI et compléments TeamCity ADR0111, audit étendu Wayland/IME/DPI/accessibilité, interface du paquet installé et parité globale restent ouverts.

## Pull requests et identifiants — qualification partielle

Source `877bb29f02247188237e3ab98abd448166c7ccf0` : [revue des suites complètes](../../artifacts/linux/pull-requests-20261019/review/root-full-review.json), **2889/2889 Desktop, 848/848 Core, 35/35 Plugins**. Les 2810 identités Desktop antérieures restent présentes ; +79 cas Desktop, +28 Core. Les ultimes changements touchent trois fichiers Desktop ; la [comparaison Core/Plugins](../../artifacts/linux/pull-requests-20261019/review/core-plugins-final-scope-review.json) conserve sa portée explicite. Import au primaire avec les cinq changements Modal préexistants conservés ; aucune réexécution primaire revendiquée.

- [x] Create/View PR GitHub : menu et icônes originaux, branches/template, liste/diffs/discussion, création/commentaire/fermeture explicites ; Fetch et Checkout/Scripts partagés. **Deux fenêtres N0→N2** ; éditeurs multilignes ordinaires, sans règles de message de commit. [ADR0110](adr/extensions/github/0110-pull-requests.md).
- [x] Identifiants Guest/Basic/Bearer : modèle Windows partagé, brouillons et Save parent ; **N0→N1**, consommateurs CI historiques encore absents. GitHub Hosting et CI Actions gardent leurs réglages séparés.
- [x] [Natif source ciblé](../../artifacts/linux/pull-requests-20261019/native-05/root-native-review.json) anglais/clair/X11 : focus initial, PR/commentaire/fermeture sur serveur HTTPS local, premier Enter et Cancel parent ; vraie sortie à 0. [Paquet exact](../../artifacts/linux/pull-requests-20261019/review/root-package-review.json) : cinq phases et installation DPKG isolée vérifiées. Aucun push ni installation sur l’hôte dans ce lot.
- [ ] Markdown/HTML enrichi, navigation inter-fichiers Windows et variantes visuelles ; GitHub réel authentifié/Enterprise et audit natif étendu. Historique du lot PR : TeamCityBuildChooser et cinq pages CI étaient encore N0. Le lot ADR0111 ci-dessous met à jour ces niveaux ; **treize familles ouvertes, aucun N4**.

## TeamCity — périmètre vérifié

Source `6534a924c3a37a19d69a3af7a15069c2b71d4126` : **2969/2969 Desktop, 873/873 Core, 35/35 Plugins** ; +80 cas Desktop et +25 Core, toutes les identités précédentes conservées. [Revue des suites](../../artifacts/linux/teamcity-20261019/review/root-full-review.json), [paquet exact isolé](../../artifacts/linux/teamcity-20261019/review/root-package-review.json), [ADR0111](adr/extensions/ci/0111-teamcity.md).

- [x] Chooser et page TeamCity **N0→N2** : dimensions/arbre/gestes Windows, lecture REST/XML, collage explicite, clés/portées et Apply/Cancel partagés. Statuts/rapport et polling communs avec Actions, lignes/sélection/focus conservés, annulation et fermeture drainées.
- [x] Identifiants CI partagés **N1→N2** : consommateurs Hosting et TeamCity, drafts Guest/Basic/Bearer indépendants de LogAsGuest, store utilisateur privé par instance ; ancien statut/lien retiré si nouvelle configuration invalide. Aucun secret TeamCity distribué en XML.
- [x] Captures headless du chooser EN clair et FR sombre contrôlées ; [parcours source X11 anglais/clair](../../artifacts/linux/teamcity-20261019/native-02/root-native-review.json) : première flèche/Escape du chooser, premier Enter des credentials, Apply d’un Bearer invalide sans ancien statut/lien et sortie à 0 vérifiés. Paquet `7.2.1-linux-alpha+6534a924c3a37a19d69a3af7a15069c2b71d4126` vérifié par publication, structure, smoke et DPKG isolés ; aucune installation hôte ni push.
- [ ] Serveur TeamCity réel/authentifié, gestes natifs installés, NTLM/SSO et coffre ; variables de dépôt du modèle Core à raccorder au remote actif, détails sérialisés et grands historiques à rapprocher. Rapport HTML embarqué et audit Wayland/DPI/accessibilité ouverts. À la clôture du lot TeamCity, AppVeyor, Azure DevOps, GitLab et Jenkins restaient **quatre pages N0** ; GitLab est réévalué dans ADR0112 ci-dessous ; **treize familles ouvertes, aucun N4**.

## GitLab — périmètre vérifié

Source `95f628aea7d29db61f21fc81458ac1779b9204fa` : **3001/3001 Desktop (256 classes), 903/903 Core, 35/35 Plugins**. [Suites complètes](../../artifacts/linux/gitlab-20261019/review/root-full-review.json), [import contrôlé](../../artifacts/linux/gitlab-20261019/review/source-import-review.json), [ADR0112](adr/extensions/ci/0112-gitlab.md). Exécutions sur W4 figé ; les cinq modifications Modal préexistantes du primaire restent distinctes, sans full primaire revendiqué.

- [x] Page Gitlab **N0→N2** : trois champs et deux liens Windows, token masqué, Effective en lecture seule sans lookup automatique ; activation automatique seulement dans une portée éditable visible. Remotes assortis à l’instance et namespaces imbriqués ; brouillons Apply/Cancel et CAS partagés. Pipelines et statuts par SHA raccordés au suivi CI commun.
- [x] [Natif source ciblé](../../artifacts/linux/gitlab-20261019/native-02/root-native-review.json) accepté dans le seul périmètre de cette revue, après correction HotTrack/notice/captions. [Paquet exact isolé](../../artifacts/linux/gitlab-20261019/review/root-package-review.json) `7.2.1-linux-alpha+95f628aea7d29db61f21fc81458ac1779b9204fa` : cinq phases et smoke/DPKG isolés ; aucune installation hôte ni push.
- [ ] Serveur GitLab réel/authentifié, SSO/coffre, jobs/logs, mutation de pipelines, hébergement/PR GitLab et interactions du paquet installé. Token PRIVATE-TOKEN dans le XML choisi ; HTTP/HTTPS explicites, bornes et Truncated visibles. ShowInBuildReportTab=false reste le choix Windows original.
- [ ] Autres langues/thèmes/Wayland/IME/DPI/accessibilité et parité étendue. À la clôture du lot GitLab, AppVeyor, Azure DevOps et Jenkins restaient **trois pages N0** ; AppVeyor est réévalué par ADR0113 ci-dessous ; **treize familles ouvertes, aucun N4**.

## AppVeyor — périmètre vérifié

- [x] [ADR0113](adr/extensions/ci/0113-appveyor.md) : page Windows compte/token masqué/projets multiples et option tests nullable ; scopes, suggestion sans écriture implicite, Apply/Cancel et CAS partagés. Statuts par SHA, durée/tests/PR, cadence running et actions build/PR sans recréer la grille. **AppVeyor seul N0→N2** ; Browse et CI restent N2.
- [x] Source `c108a6bb3` : [suites complètes](../../artifacts/linux/appveyor-20261019/review/root-full-review.json), **3035/3035 Desktop (259 classes), 936/936 Core, 35/35 Plugins**. Les cinq modifications Modal du checkout restent hors source exécutée.
- [x] [Natif source](../../artifacts/linux/appveyor-20261019/native-01/root-native-review.json) : anglais/clair puis français/sombre sous X11 privé, premier clic/Bas/Haut, scopes, Annuler/Appliquer et persistance après redémarrage ; deux fermetures normales. CI désactivée, aucune qualification du serveur par ce parcours.
- [x] [Paquet exact](../../artifacts/linux/appveyor-20261019/review/root-package-review.json), cinq phases et installation DPKG isolée vérifiées ; 39916592 octets. Ni installation hôte ni push.
- [ ] Rapport HTML embarqué Windows, serveur AppVeyor réel/authentifié et contrat v2, interactions installées et variantes Wayland/IME/DPI/accessibilité. À la clôture AppVeyor, Azure DevOps/Jenkins restaient deux pages N0 ; leur portage ciblé est décrit ci-dessous. Treize familles ouvertes, aucun N4.

## Fenêtres portables et CI — lot vérifié du 20 octobre

- [x] Azure DevOps/TFS : trois champs et deux liens Windows, PAT masqué lié aux URLs concrètes du dépôt, XML scoped et lecteur CI commun ; ADR0114. Donate : texte/badge originaux, fenêtre possédée et activation externe explicite ; ADR0115.
- [x] Jenkins : trois champs, projets/dossiers/multibranch et variables, Guest/Basic privé par endpoint, lectures bornées/cache/ETag et statuts communs ; ADR0116. Les six fournisseurs CI de l'inventaire ont désormais un équivalent ; cela ne clôt pas leurs écarts.
- [x] Git Advanced : huit triétats Windows dans la session Config/Advanced, portées et publication communes ; ADR0117. Checkout revision : formulaire compact, picker/Force et Before/native/After avec vrais effets Git ; ADR0118.
- [x] Aide CLI autonome, visualiseur de texte en mémoire et Quick view readonly, aide Scripts non modale et introduction Plugins ; ADR0119–0122. FormEdit ne renvoie pas de saisie et la page d'introduction ne contient pas de catalogue d'activation.

[Suites complètes W7](../../artifacts/linux/jenkins-20261020/review/root-full-review.json) sur `63812eebc4b0994f625b7da259cee72735c46dd4` : 3217/3217 Desktop, 1026/1026 Core, 35/35 Plugins ; anciens scénarios conservés, dont un libellé Core renommé avec ses corps de tests inchangés et vérifiés dans la revue de filiation. Les ciblés ayant révélé les défauts Quick view/focus restent avec leurs résultats bruts ; la correction finale et le parcours historique Recovery passent dans cette source.

[Natif W7](../../artifacts/linux/jenkins-20261020/native-01/root-native-review.json) : Jenkins anglais/clair et français/sombre, premier clic/Haut/Bas, portées/credentials/Cancel/Apply/redémarrage, puis `help` autonome ; Git Advanced, Plugins, aide Scripts, Checkout/Échap et Recovery Quick view inspectés sans mutation Git. [Azure/Donate natif W6](../../artifacts/linux/azuredevops-20261019/native-01/root-native-review.json) reste lié à `2e96c8dea`, sans transfert implicite à W7. [Paquet exact isolé](../../artifacts/linux/jenkins-20261020/review/root-package-review.json) : cinq phases à 0, **39977848 octets**, SHA256 `8a283b832a92feec91f3d72cede0f91934a50c116d187df2c36f6d0467dbe5e3`, 402 fichiers contrôlés ; aucune installation hôte ni push.

- [ ] Rapport HTML CI embarqué, services/authentifications réels, GCM/SSO et audit étendu Wayland/IME/DPI/accessibilité ; interactions du paquet installé distinctes des tests et de DPKG isolé.
- [ ] Resserrer la table Recovery : les SHA complets se replient en lignes trop hautes dans le parcours natif W7. Conserver la copie littérale des hashes.
- [x] Tranche qualifiée sur b745 (ADR0123–0129, périmètres partiels ci-dessus) : FormEditor, Fork/Clone GitHub et Confirmations. Suites, natif source et paquet gardent leurs preuves et limites dédiées.

Les niveaux N2 de ces neuf entrées restent partiels ; les treize familles globales restent ouvertes, aucun N4.
