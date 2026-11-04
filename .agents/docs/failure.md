# Failure

## Raccord Desktop POSIX — 4 novembre 2026

Le premier lancement Native.Tests sans build explicite ne découvre aucun cas ; le projet est compilé séparément de la solution. Après compilation, les 14 contrats passent. Desktop donne 188/189 : le faux pair Python au mauvais nonce envoyait directement du JSON avant le nouvel en-tête pidfd. Il transmet désormais sa vraie référence noyau ; les assertions de nonce et de confidentialité restent inchangées. Puis 189/189 passent, et 192/192 avec les trois parcours du vrai helper. Core 1 210/1 210, Plugins 42/42 et cinq contrôles Debian passent également. [ADR0165](adr/platform/0165-macos-interactive-console.md).

Les derniers workflows candidats 4a/317 sont annulés ; le troisième push ne crée aucun run. Le dispatch explicite échoue HTTP422 « Actions has been disabled for this repository », alors que les permissions affichent enabled=true. Ce refus ne qualifie aucun parcours Mac et ne remplace pas la suite Desktop Linux complète. La branche migration/linux demeure sur le commit 36c8f7761 déjà qualifié.

## Mesure stty et mode Console .NET — 4 novembre 2026

La reprise de saisie seule ne corrige pas le second Mac Intel (10/11). Le runtime peut configurer le terminal avant un enfant qui en hérite, puis revenir à son mode applicatif : le banc doit mesurer sans cette intervention. stty interroge désormais le périphérique ttyname_r avec ses trois flux redirigés ; read natif vérifie la saisie reprise. Tous les attributs et les snapshots immédiats sont conservés, sans masque. Dix contrats Linux passent ; troisième passage Mac requis. [ADR0164](adr/platform/0164-native-posix-terminal.md).

## Oracle de restauration du terminal Darwin — 4 novembre 2026

Le nouveau PTY passe 10/11 contrats sur Mac Intel ; `stty -g` immédiatement après restauration contient le drapeau noyau transitoire PENDIN. Le noyau Apple le pose au retour à ICANON et le retire lors du traitement de l’entrée suivante. Le banc reprend désormais une vraie saisie UTF-8 après retour de l’API, en vérifie le contenu, puis compare tous les attributs sans retirer de bit ; le snapshot immédiat reste enregistré. Dix contrats Linux passent ; le nouveau passage Mac reste requis. [ADR0164](adr/platform/0164-native-posix-terminal.md).

## Suites complètes après supervision POSIX — 4 novembre 2026

Le Linux `44ccea0c4` atteint la limite de 90 minutes : Core/Plugins passent, Desktop reste incomplet avec douze échecs conservés. Douze reprises inchangées passent localement, puis le [Linux complet `ed1161b8c`](https://github.com/Azreal42/gitextensions/actions/runs/41732665464) passe les 4 625 Desktop, Core et Plugins, et installe/démarre son paquet reproductible. Cela qualifie ce nouveau passage ; le run annulé reste un échec. La correction des attentes est décrite dans [ADR0163](adr/platform/0163-process-exit-notifications.md). Le [natif final `36c8f7761`](https://github.com/Azreal42/gitextensions/actions/runs/41734483458) passe les 42 Plugins Windows et Mac, sans affaiblir la preuve PID/données du test Trace2.

## Raccord POSIX Linux — 3 novembre 2026

[ADR0161](adr/platform/0161-posix-process-lifecycle.md). EOF et parent sorti ne prouvaient pas la fin d’un enfant à flux fermés : le nouveau banc échouait sur `226d0b29a`. Le sous-récollecteur privé garde ce front jusqu’à l’arrêt réel ; les cinq contrats passent. Les anciennes assertions de parent direct attendent désormais la chaîne complète et les images du superviseur/lanceur. Le statut naturel 130 reste observé au vrai `waitpid` par une bibliothèque de test indépendante ; il n’est pas remplacé par un booléen ou un statut déclaré par l’acteur.

Un processus non enfant observé par `Process.HasExited` peut rester vu vivant lorsqu’il est zombie. Le banc utilise désormais pidfd/kqueue pour l’événement de sortie, tout en laissant le superviseur réserver ses identités. Le contrôle ELF a également révélé les conversions C23 activées par `_GNU_SOURCE`, qui exigeaient glibc 2.38 contre le minimum Debian 2.34. `_DEFAULT_SOURCE` suffit au superviseur ; un garde-fou vérifie les vrais imports de chaque helper avant emballage. Le binaire incompatible et le contre-exemple corrigé sont conservés.

## Supervision macOS — 3 novembre 2026

Un helper de notification de démarrage sans annotation OS a été rejeté par CA1416 ; ajouter la même borne macOS que son appelant, sans désactiver l’analyse. Recompilation sans avertissement/erreur. La revue des en-têtes Apple montre aussi que `fcntl` est variadique : le pont C à signature fixe laisse le compilateur traiter les arguments de pile Apple Silicon. La compilation C locale et deux vrais arbres Linux passent ; l’ABI et l’exécution macOS restent soumises à la CI native.

## Fixture d’héritage Windows — 3 novembre 2026

Le premier job noyau `41694781899` passe trois cas sur cinq, dont le parent sorti avec pipes indépendants et les deux échanges binaires. Les deux cas hérités échouent sur leur marqueur stdout : `Process.Start` sans aucun flux redirigé et sans console ne pose pas `STARTF_USESTDHANDLES`. La fixture redirige désormais stdin pour transmettre explicitement les deux handles de sortie du parent. Les marqueurs stdout/stderr et les oracles de drainage restent obligatoires ; quatre cas comparables Linux repassent. Les journaux et le TRX Windows initiaux sont conservés.

## Descendants sans pipe — 3 novembre 2026

[ADR0160](adr/platform/0160-native-process-ownership.md). L’ancien chemin Windows/macOS ne tentait `Kill(true)` que si le parent vivait encore ; ce code ne couvre pas les écrivains survivants. Le nouveau banc contrôle aussi un enfant à pipes indépendants : sur Linux, l’opération est revenue normalement après la sortie du parent alors que cet enfant vivait encore. Échec isolé conservé, hors annonce de qualification Windows ; aucun oracle supprimé. Les quatre cas comparables réussis ne clôturent pas cette limite de propriété POSIX. Un superviseur POSIX reste à réaliser, avec réservation des identités jusqu’au drainage.

## Qualification native et courses de tests — 3 novembre 2026

[ADR0159](adr/platform/0159-avalonia-three-platforms.md) conserve les limites et les runs. Les runners réels ont révélé les séparateurs Git for Windows, l’ABI inode64 Darwin Intel, l’ordre de signature et les notices versionnées placées dans un emplacement de code Mac. Corrections dans le lecteur commun et le packaging ; signature récursive contrôlée après extraction.

Le vérificateur traitait à tort askpass sans invite comme une erreur : l’invite par défaut OpenSSH est conservée, et les arguments en excès restent refusés sans sortie. La capture Mac a révélé un repli serif arbitraire : familles sans empattements/fixes explicites, métriques contrôlées. Un Apply de test précédait le chargement asynchrone des pages ; l’attente réelle garde le clic et ses assertions. Un autre test envoyait un key-up après la fermeture effective du backend ; deux parcours équivalents n’envoient plus d’événement à une fenêtre détruite, sans retirer les assertions sur le callback en attente et les processus Git.

Le premier pipeline natif complet `41660089091` passe 131 cas par système et démarre ses trois livraisons. Les annulations ARM de `41656633912`/`41657405557` ont eu lieu avant toute exécution, avec diagnostic GitHub de capacité ; elles ne sont jamais comptées comme des tests. La dernière source (PATH capturé et fermetures) a 167 tests locaux ciblés réussis, puis reste soumise aux suites complètes et aux jobs natifs.

Le [run Linux `41663702574`](https://github.com/Azreal42/gitextensions/actions/runs/41663702574) expose une vraie course : fermer un propriétaire retire son worker avant la propagation asynchrone du token lié. Un résultat pouvait donc être publié après fermeture. Vérifier l’état du propriétaire et le token appelant sous le verrou commun avant publication, aussi pour les fournisseurs empruntés ; un `Task` déjà terminé peut contourner `WaitAsync(token)`. Deux régressions déterministes échouent sur l’ancien code, puis les 174 tests combinés passent sur le correctif.

Les fixtures exposaient d’autres courses : fichier PID visible avant fin d’écriture (`41656633828`), hit-test avant publication du rendu (`41660089023`), Settings temporairement désactivé par un scan, et culture askpass dépendant du profil réel. Publier les signaux par renommage après fermeture, utiliser le pilote de pointeur strict commun, attendre le contrôle effectivement disponible et isoler/restaurer le profil. Les 240 scénarios touchés passent. La passe complète précédente (4 619/4 622) reste conservée ; elle ne devient pas rétroactivement verte.

L’audit complémentaire applique le même protocole au lecteur Index de `ArtificialHistoryRevisionTests` : sept cas passent, sans modifier l’oracle PID ni l’annulation. Les lecteurs déjà protégés par contenu ou signal distinct restent conservés. macOS ARM est exercé sur le minimum 15 déclaré par le bundle, image déjà qualifiée ; les tentatives 26 restées sans allocation ne constituent aucune preuve d’exécution.

## Portabilité Avalonia — premiers contrats

Le SDK considérait les bibliothèques intermédiaires comme indépendantes du RID : une publication Windows depuis Linux recevait le helper ELF de Core. Le garde-fou de livraison a arrêté le premier essai ; `IsRidAgnostic=false` propage désormais le RID dans le graphe concerné. L’archive suivante contient des exécutables PE x64 et aucun helper Linux. Le premier test de règles attendait à tort InvalidOperationException : le contrat existant est IOException, préimage toujours préservée. Le build suivant a signalé une annotation nullable incorrecte dans le nouveau test de grille ; correction de type uniquement, puis 56/56 ciblés. Les preuves locales statiques restent distinctes des futurs contrôles natifs.

Ne garder ici que les causes qui évitent de refaire une impasse. Logs, candidats refusés et mesures historiques restent dans `artifacts/linux/`, avec leur source ; état du dernier paquet dans [TODO](todo.md).

<a id="ci-linux-bisect"></a>
## CI Linux — formats Git et durée réelle

- Git 2.55 entoure `bad` de guillemets dans les conclusions de `BISECT_LOG` ; les préfixes et offsets fixes perdaient le premier mauvais commit et les candidats après Skip. Corriger les deux consommateurs via un lecteur commun, couvrir ancien/nouveau format et journal partiel ; ne pas modifier l’oracle métier du commit recherché. La sortie native visible dans Process peut aussi porter ces guillemets.
- Le [premier run corrigé](https://github.com/Azreal42/gitextensions/actions/runs/41345878865) fait 1 209/1 210 Core : un snapshot strict rencontrait `.git/objects/maintenance.lock`, supprimé par une maintenance Git détachée après commit. La fixture Core commune désactive `maintenance.auto` et `gc.auto` avant son premier commit. Garder les captures et assertions byte-exactes ; ne pas ignorer les locks ni modifier les préférences des dépôts utilisateur.
- Le [run suivant](https://github.com/Azreal42/gitextensions/actions/runs/41347383572) passe 1 210 Core et 42 Plugins, puis 4 568/4 580 Desktop. Trois preuves fresh-process exigeaient un SDK dans le checkout au lieu du vrai host .NET ; les pilotes visaient un bouton débordé ou une zone vide dans une courte liste de polices. Résoudre le host courant, cliquer la vraie commande visible et une ligne réalisée, sans affectation artificielle de sélection. Une checkbox partiellement masquée doit être entièrement révélée avant son clic.
- La légende française Find dépasse réellement les 78 px avec DejaVu Sans : mesurer sa colonne partagée, garder l’éditeur de 317 px et agrandir la fenêtre au besoin. Le ciblé local avec polices isolées expose aussi un clic Settings pendant le scan initial ; attendre son vrai résultat et Save disponible. Référence locale : 130/131 puis **131/131**, douze échecs distants couverts ; aucune assertion métier retirée.
- Le [run Desktop corrigé](https://github.com/Azreal42/gitextensions/actions/runs/41352407601) passe **1 210 Core, 42 Plugins et 4 580 Desktop**, ainsi que le paquet extrait et son démarrage ; seul `cmp` de l’archive reconstruite échoue (octet 125). Les archives distantes n’étaient pas uploadées : leur différence exacte n’est pas récupérable. Le constructeur fixe désormais le mode XZ et calcule Installed-Size en unités logiques Debian, sans dépendre des blocs alloués. Cinq tests de métadonnées et deux vrais paquets locaux identiques, même avec des limites de threads externes différentes ; l’ancien contrôle local sans compression ne reproduit pas le rouge distant. Les contrôles de paquet précèdent les suites longues et les prochains échecs conservent archives et différences de contrôles.
- La limite CI de 30 minutes était inférieure à la durée observée de la seule suite Desktop complète H36 (46 minutes). Garder tous les tests et dimensionner la limite ; ne pas confondre une validation locale ciblée et un run distant complet.
- `tee` sans `pipefail` masque un échec. Choisir explicitement Bash dans Actions, vérifier la propagation d’un code non nul et uploader les logs même si Core échoue avant la création du premier TRX.

[Périmètre et validation](todo.md#ci-linux-bisect).

<a id="h37-compact-browse-layout"></a>
## H37 — taille réduite et lisibilité

- Un PNG de 860 px peut contenir une scène de 1440 px simplement recadrée. Après resize, attendre le nouveau layout puis capturer un rendu frais ; `GetLastRenderedFrame` seul ne le prouve pas. La barre réelle tenait déjà : aucun correctif produit de barre n’était nécessaire.
- Réserver 300 px à l’historique ne suffit pas lorsque ses seules colonnes fixes prennent 325 px ; le sujet devient nul. Le budget doit inclure colonnes effectivement visibles, graphe et chrome, sans recopier la politique des colonnes du composant partagé. [Référence rouge : quatre sujets nuls](../../artifacts/linux/compact-browse-layout-20261022/review/root-baseline-review.json).
- `Bounds.Width` est la largeur limitée par l’hôte, pas celle demandée au séparateur. La mémoriser au changement de position perdait le choix 535 au profit de 361. Conserver la largeur absolue demandée ; [référence des gestes](../../artifacts/linux/compact-browse-layout-20261022/review/root-baseline-width-review.json).
- Dix tests verts et un sujet positif ne prouvaient pas la lisibilité de CommitInfo : les valeurs étaient découpées lettre par lettre. [Premier candidat visuellement refusé](../../artifacts/linux/compact-browse-layout-20261022/review/root-corrected01-visual-rejection.json). Vérifier lignes réellement rendues, absence d’ellipse et accès au dernier caractère par un vrai geste horizontal ; conserver message/références repliés.

[Décision](adr/ui/layout/0158-compact-browse-layout.md), [preuve et limites actuelles](todo.md#h37-compact-browse-layout). Les résultats rouges et le candidat refusé restent distincts du passage final.

<a id="h36-progressive-host"></a>
## H36 — historique progressif (garde-fous)

- Le parseur peut livrer avant le nombre cible lorsque le texte retenu atteint 1 MiB. Dans le fixture, les messages de 4 096 caractères présents dans plusieurs champs ont déclenché un lot d’environ 63 commits avant la gate à 100. Dimensionner le fixture selon ce budget ; ne pas imposer une limite par message ni confondre un lot court et EOF.
- Poser la culture dans l’appelant avant construction UI : une affectation dans une factory async ne remonte pas dans son contexte. Ne pas corriger l’oracle français en le remplaçant par un attendu anglais.
- `HistoryLimit = 1` ne garantit plus qu’un parent ou un commit de Blame soit hors grille. Les cas qui exigent cette absence doivent fixer `MaxRevisionGraphCommits = 1` ; conserver leurs SHA, endpoints, focus et drains. Les cas qui attendent tous les descendants à EOF gardent leur lot de 1 sans plafond artificiel.
- EOF ne constitue pas une nouvelle sélection : ne pas réannuler le panneau inchangé. Les fins d’inventaire, de reprise ou de panneaux ne doivent pas effacer un rapport de mutation ou une erreur tardive du flux par un statut de succès.
- Une lecture de diff ne récupère pas l’erreur des métadonnées du même commit. Conserver la propriété dépôt/hash de l’erreur ; seul le réessai du lecteur concerné la récupère. Une mutation peut aussi admettre un nouveau graphe pour ses effets partiels avant de relancer son erreur : rattacher cette erreur à la génération admise par cette mutation.
- Une liste de fichiers visible ne prouve pas que son document sélectionné est chargé. Les tests de commandes locales doivent établir la fin du lecteur foreground déjà admis avant de remettre le journal à zéro ; attendre seulement les entrées rend le résultat dépendant de la vitesse de Git.

- La présélection d’un fichier sous `_refreshing` ne lance aucun aperçu : l’activation de Working directory doit admettre son lecteur, et une sélection vide doit révoquer l’image antérieure.
- `Ready` ne prouve pas la fin de l’inventaire ; les observations finales de tracking/sous-modules attendent la frontière histoire possédée. Les publications de succès ne remplacent pas une erreur CI encore courante.

- Une clé Kind/Revision ne distingue pas deux dépôts pour WT/Index. Invalider groupes, aperçu et clé au premier succès du nouveau dépôt ; ne pas vider le dernier dépôt valide avant ce succès.
- SelectedItems conserve son ordre natif, distinct de la récence des comparaisons. Une publication interne garde la paire initiale réelle seulement tant que son intention, ses deux endpoints et son autorisation survivent ; une révocation utilisateur ne doit jamais la recréer.

Intention et frontières : [ADR0157](adr/workflows/history/0157-progressive-browse-host.md). Preuves et limites courantes : [TODO](todo.md#h36-progressive-history).

<a id="h35-browse-shared-snapshot"></a>
## H35 — snapshot et endpoints Browse (garde-fous)

- Une clé seule ne garantit pas un message ou des parents frais. Comparer le payload complet, publier sans Reset et garder les endpoints typés ; la base du diff ne remplace pas le vrai parent Index/WT.
- Le menu View peut encore être l’origine capturée lorsqu’il retire la dernière ligne. Renvoyer alors vers la grille vide à Loaded, avec les gardes existantes ; ne pas reprendre un focus utilisateur plus récent. La [trace et la correction bornée](../../artifacts/linux/browse-inline-history-20261021/review/ui06-correction/review.md) restent distinctes du contrôle suivant.
- Fenêtre ouverte ou busy déjà false ne prouve ni données prêtes ni action postée engagée. Attendre la cible et son bouton prêts, puis l’opération admise. Le journal est limité à 500 entrées : utiliser l’Id monotone, pas Count/Skip ; un `for-each-ref --contains` de CommitInfo visible n’est pas un refresh historique.
- `WindowWorkCompletion` comprend les sessions modeless enfants. Le récepteur attend sa frontière d’opération Git ; attendre toute la fenêtre avec l’enfant encore ouvert peut créer un cycle. Le drainage global reste requis après fermeture.
- Une annulation après reset peut laisser des effets réels. Le clean reste explicite après succès sous la même lease ; drainer puis rafraîchir même en échec partiel.

- Dans les six helpers stricts de retrait PID/start audités, la disparition Linux du processus pendant la lecture de son `stat` est un cas distinct des autres erreurs IO ; conserver ces dernières et ne jamais assimiler un zombie à un acteur retiré. Les anciens helpers plus larges restent une dette séparée.

Validation finale : [suites complètes](../../artifacts/linux/browse-inline-history-20261021/review/root-full-review.json), **4 560 Desktop / 1 197 Core / 42 Plugins**, toutes réussies, puis [paquet isolé](../../artifacts/linux/browse-inline-history-20261021/review/root-package-review.json) sur les mêmes sources `3dd4eb926`. Les essais intermédiaires refusés restent dans les artefacts ; ils ne sont pas la qualification finale.

<a id="h34-inline-artificials"></a>
## H34 — lignes de travail dans le flux

- [Ciblé01 186/187](../../artifacts/linux/inline-artificial-history-20261021/targeted-01/results/desktop.trx) : ListBox Avalonia délègue PageUp/PageDown au défilement sans déplacer la sélection. La liste simple commune déplace désormais la sélection d’une page et replace le focus sur son ancre native ; première flèche suivante vérifiée, sans réparation dans le test. [187/187 après correction](../../artifacts/linux/inline-artificial-history-20261021/review/root-targeted-review.json).
- Les anciens tests imposaient un préfixe séparé, une sélection de liste vide et deux arrêts Tab artificiels. Leur contrat structurel a été corrigé avant exécution vers la liste unique Windows, avec endpoints typés et assertions métier conservées ; [revue avant tests](../../artifacts/linux/inline-artificial-history-20261021/review/historical-test-contract-update.json). Les dix cas incrémentaux H33 restent byte-identiques.
- Les deux premiers builds ont signalé un import du namespace des colonnes/badges et une construction de chaîne ambiguë dans un nouveau test. Le moteur/layout legacy est conservé ; aucun échec n’est reclassé en succès.

<a id="h33-streamed-chooser"></a>
## H33 — historique incrémental du sélecteur

- [Build01](../../artifacts/linux/streamed-revision-history-20261021/build-01/core.log) et [Build03](../../artifacts/linux/streamed-revision-history-20261021/build-03/desktop.log) : nullabilité de tuples/lignes déjà vérifiés dans les nouveaux tests et garde Linux requise par CA1416. Corriger ces nouveaux scénarios sans modifier les historiques.
- [Ciblé01 99/101](../../artifacts/linux/streamed-revision-history-20261021/targeted-01/results/desktop.trx) : étendre le cache natif peut traverser un score invalidé par Add ; Windows recontrôle CacheTo/GetCachedCount. Une seconde passe bornée consomme cette invalidation, sans boucle ni faux EOF. Un Render masqué/sans dimensions préparait quatre lignes : quitter avant le cache, uniquement en mode incrémental. Moteur natif, assertions et anciens tests conservés.
- [Core complet 1178/1179](../../artifacts/linux/streamed-revision-history-20261021/core-full-01/core.log) : `%H %P` ajoute un espace structurel sur parent vide. Le nouvel oracle retire seulement cet espace ; ordre, identités et preuve du parent réécrit restent requis. [21/21 après correction, produit identique](../../artifacts/linux/streamed-revision-history-20261021/review/root-core-review.json) ; ne pas appeler cet assemblage une nouvelle suite complète unique réussie.
- Un Ready initial ne termine pas la lecture : garder son lease jusqu’au drain réel, y compris remplacement et fermeture. Ne pas abandonner un callback retenu via WaitAsync. Un filtre accepté doit franchir le contrôle d’annulation avant publication ; le résultat local seul ne prouve pas ce succès. La projection UTF-8 existante échappe les octets : ne pas la remplacer par un décodeur strict.

<a id="h32-chooser-rendu-filtre"></a>
## H32 — sélecteur : rendu et filtre

- [Compilation01](../../artifacts/linux/shared-revision-chooser-20261021/build-01/desktop.log) : `SelectedItems` est nullable dans le nouveau test ; conserver la garde/nullabilité du contrôle natif.
- [Premier ciblé](../../artifacts/linux/shared-revision-chooser-20261021/targeted-01/results/desktop.trx) : 241/242. Le nouveau test comparait le corps brut au texte affiché, qui porte déjà deux espaces de séparation dans `MessageCell`. Corriger les deux attendus exacts, sans changer le produit ni un test historique. Les passages suivants sont dans le TODO.
- `--parents` ne signifie pas que grep seul supprime les parents non sélectionnés : [observation Git](../../artifacts/linux/shared-revision-chooser-20261021/review/grep-parent-policy-root-check.json). Garder l’ancien test singleton ; le nouveau filtre chemin distingue réellement parent brut et parent de graphe.
- Revue avant/finalisation : conserver le guard HEAD unborn et une admission `_work` unique pendant le modal. Une gouttière de largeur zéro ne cache pas les enfants d’un Canvas ; masquer les Canvas artificiels aussi, avec deux cas visible/masqué. La borne Rebase est indépendante du filtre Current éditable ; OK/Reset doivent conserver les champs invisibles du query.

## H31 — Proxy et issues GitHub

- [Compilation02](../../artifacts/linux/plugin-defaults-20261021/build-02/desktop.log) : `GitRemote` expose `FetchUrls`, pas `Url` ; reprendre le consommateur Hosting existant. [Compilation03](../../artifacts/linux/plugin-defaults-20261021/build-03/desktop.log) : modificateurs explicites du clavier headless. [Compilation06](../../artifacts/linux/plugin-defaults-20261021/build-06/desktop.log) : CS9124, conserver le texte initial dans un champ readonly.
- [Premier ciblé](../../artifacts/linux/plugin-defaults-20261021/integration-01/results/desktop.trx) : 287/303. Onze échecs venaient du CTS déjà détruit lors d’un deuxième Close : détacher sa référence à Closed avant Cancel/Dispose. Le fournisseur GitHub actif ajoute aussi une section au menu ; adapter seulement ce comptage, conserver les oracles d’ordre, d’undo et de désenregistrement.
- Quatre échecs numériques : le template/LostFocus normalisait le texte brut et Save pouvait accepter l’ancienne valeur. Le brouillon doit précéder cette normalisation. [Relance02](../../artifacts/linux/plugin-defaults-20261021/integration-02/desktop.log) interrompue après 165 succès : le sens du `TextConverter` était inversé, Save refusait de fermer une modale attendue sans délai. [État vivant préservé](../../artifacts/linux/plugin-defaults-20261021/integration-02/live-stall-evidence.json) et séquence de test conservés ; borner ces attentes. La [source Avalonia exacte](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.22/src/Avalonia.Controls/NumericUpDown/NumericUpDown.cs) utilise Convert pour texte → valeur, ConvertBack pour valeur → texte.
- [Diagnostic](../../artifacts/linux/plugin-defaults-20261021/github-diag-01/results/desktop.trx) : 105/106, seul le champ vide + Entrée conservait l’ancienne limite. Le contrôle saute le converter pour le vide ; capturer ce texte à Save avant le default button/LostFocus. Retirer la clé comme le binding Windows, garder les deux gestes réels et les autres oracles.

Le générateur d’inventaire indexe les classes, pas les records : référencer `GitHubIssueHelperSettings` par son fichier source, sans modifier les fenêtres détectées ni leurs niveaux.

[Validation finale](../../artifacts/linux/plugin-defaults-20261021/review/root-targeted-review.json) : 305/305 Desktop, 35/35 Core et 42/42 Plugins sur le checkout commité `40d8e1a32`, sans transfert aux tests natifs/serveurs réels. Les octets du checkout restent stables à l’alignement du commit ; ne pas confondre les blobs Git et les fichiers avec les fins de ligne déclarées par `.gitattributes`.

## H30 — fenêtres et réglages portables

Les essais refusés restent dans [le lot](../../artifacts/linux/portable-window-options-20261021/). Ne pas confondre ces causes produit et les corrections de fixtures :

- La largeur fixe du dialogue de branches débordait en français ; laisser la taille au contenu. Dans sparse, l’éditeur multiligne consommait Ctrl+Entrée avant le handler bubble : traiter le geste en tunnel. [Mesures et correction](../../artifacts/linux/portable-window-options-20261021/review/dialog-diagnostic01-root-corrections.json).
- Le ListBox du sélecteur refusait le focus malgré des appels Focus ; activer Focusable dans ce dialogue. Un GotFocus programmatique après RunAsync remplaçait ensuite Index/WorkingTree par le premier commit : distinguer Tab des retours internes. [Diagnostic](../../artifacts/linux/portable-window-options-20261021/settings-focus-diagnostic-01/desktop.log), [correction](../../artifacts/linux/portable-window-options-20261021/review/settings-chooser02-corrections.json).
- Une fenêtre enfant occupée pouvait veto la fermeture avant que Compare reçoive Closing, perdant la demande du propriétaire racine. Garder la politique OwnerWindowOnly pendant le seul picker possédé, puis restaurer la politique capturée après drainage ; réemployer DeferredOwnerCloseReplay. Le [témoin rouge](../../artifacts/linux/portable-window-options-20261021/picker-diagnostic-02/desktop.log) attend réellement le propriétaire fermé ; le [diagnostic suivant](../../artifacts/linux/portable-window-options-20261021/picker-diagnostic-03/root-retirement-review.json) passe 26 cas ; le [suivant](../../artifacts/linux/portable-window-options-20261021/picker-diagnostic-04/root-retirement-review.json) ajoute les quatre fermetures du sélecteur de branches, sans qualification globale.
- Les tests doivent viser l’intersection visible de l’entête Settings, conserver absence/octets des profils facultatifs et établir la culture dans leur appelant : le changement fait dans une factory async ne s’y propage pas. La fixture attend aussi le replay réel du propriétaire, pas seulement la disparition des enfants. Les assertions et les gestes restent conservés.

Un seuil JSON ancien invalide au format invariant ne doit pas être réinterprété selon la langue (1,5 devenait 15 ou 1.5) ; reprendre le défaut sans réécrire le profil. Le XML garde le parsing Single localisé Windows.

## H29 — ouverture locale

Le diagnostic du réessai avec espaces a confirmé un focus conservé : un popup filesystem en erreur, sans suggestion, absorbait Entrée. Réserver la touche seulement au MRU ou à une vraie suggestion ; sinon fermer ce popup et valider avec la même Entrée. [Échec et preuve](../../artifacts/linux/open-repository-dialog-20261021/review/retry-cause-review.json), régression conservée. Le pilote de test transmet un seul clic réel à travers l’overlay connu et utilise Escape avant de viser un éditeur recouvert.

[Build01](../../artifacts/linux/open-repository-dialog-20261021/build-01/desktop.log) : `AccessText` requiert `Avalonia.Controls.Primitives`. [Build02](../../artifacts/linux/open-repository-dialog-20261021/build-02/desktop.log) : le test Unix/procfs demande une garde `OperatingSystem.IsLinux` avant `SetUnixFileMode` ; les vingt cas sont désormais exécutés sur Ubuntu dans le ciblé04.

La revue a aussi évité `//` lors de Parent vers la racine et l’accumulation de complétions à chaque ouverture du MRU : conserver le séparateur racine et partager `Dismiss` sans recréer le composant. La découverte Git reste l’unique validation, y compris pour le fichier `.git` d’un worktree ; aucun filtre préalable « dossier seulement ».

## H28 — notifications partagées

Le [ciblé01](../../artifacts/linux/shared-repository-notifier-20261021/targeted-01/desktop.log) conserve **51/54 Desktop, trois échecs**. Deux timeouts étaient dans la fixture : le gate `GitRepository.RunAsync` tenait le même verrou de mutation que le StageAll de l’enfant. Acquérir l’effet Git réel avant ce gate, en gardant son opération ouverte par le tail, puis seulement la drainer ; garder l’attente réelle du receveur et ses deux variantes.

Le troisième échec produisait deux lectures d’historique après deux effets sous Log. Un Notify créé seulement après await pouvait échapper au verrou modal déjà libéré. Réserver pending + Notify sous verrou dès l’admission, puis UnLock après le drainage source. L’observateur reste hors des leases de la source ; aucune attente du travail global du receveur ni relecture de Log.

Qualification de la correction : 54/54 Desktop (cinq classes) et 42/42 Plugins vérifiés en Git/headless sur les octets commités `27c190a78` ; [54/54 Desktop, 42/42 Plugins](../../artifacts/linux/shared-repository-notifier-20261021/review/root-targeted-review.json), identités conservées, sources/binaires stables et processus retirés. Les essais refusés et leurs assertions restent distincts du résultat final ; aucune suite complète, preuve native ou installation hôte déduite.

## Limites ouvertes

| Observation | Cause / suite |
| --- | --- |
| Tableau Recovery replié | Le code Windows utilise WrapMode=True et AllCells : le repli des SHA est intentionnel. Ne pas traiter la capture étroite comme une fenêtre absente ; audit natif étendu de la grille encore ouvert. |
| Flux Updates sans publication Linux | Au 20/10/2026, aucune release du fork ; les artifacts CI ne sont pas des assets de release. Le dialogue ne transforme pas cette absence en erreur réseau ni en promesse de paquet. |
| Solution originale WinForms/Win32 | Portage UI et frontières OS encore incomplet ; le packaging ne résout pas cette dépendance. |
| Chemins Linux à octets non UTF-8 | API .NET basée sur string UTF-8 ; Unicode valide couvert, roundtrip des octets invalides à traiter. |
| APT sur OS vierge non exécuté | Installation/lancement DPKG isolés utilisent les dépendances hôte ; la simulation APT ne clôt pas ce gate. |
| Texte préparé pris pour un argv Linux exact | Quote hérité n’est pas injectif : antislashs/guillemets distincts peuvent produire le même texte. Conserver les bornes avant encodage ; ArgumentList testé17/17, texte hérité12/17. Aucun shell implicite ni runner intégré. |
| Notes reflog personnalisées ambiguës | `%gs` n'est pas recodé par Git ; la relecture du sujet du commit couvre le cas standard, pas toutes les notes libres. |
| Avertissements IBus lors du lancement hôte | `Destroy` absent de `org.freedesktop.IBus.Service` dans le journal Avalonia. Effet sur la saisie et lien avec une fermeture non établis ; ne pas déduire un crash de ces seuls avertissements. [Journal](../../artifacts/linux/snapshots/aae27bff4/proofs/host-desktop-launch-20261015-01.log). |
| Lecture de branches Clone laisse des descendants actifs derrière un relais | Interrompre le relais avant de capturer ses enfants peut perdre l’ancre de descendants aux pipes intermédiaires. Capture avant signaux vérifiée par trois régressions avant/après et un Abort Clone natif clair sur `aae27bff4` ; reprise de la requête de branches et autres variantes encore requises. SID seul reste insuffisant ; nettoyage de l’ancien essai distinct de la réussite produit. |

## Piles du journal — publication et erreurs

La révision Desktop01 sans drain a été rejetée par revue avant tout SDK : attendre le travail de fenêtre ne garantit pas qu’un effet durable acquis et sa publication finissent après l’arrêt de la boucle UI. La révision02 draine leur porte hors dispatcher ; [revue d’import](../../artifacts/linux/command-log-capture-20261020/review/desktop-candidate-import-review.json). Ce rejet de préparation n’est pas un test exécuté.

Le ciblé01 réel donne **58/59 Desktop** : un bool XML invalide levait InvalidDataException, omise de IsPreferenceError ; ce type n’est pas un sous-type d’IOException. Root a [corrigé le catch](../../artifacts/linux/command-log-capture-20261020/review/invalid-bool-fix.json) sans modifier les tests ; le [ciblé02 passe 59/59](../../artifacts/linux/command-log-capture-20261020/review/targeted-candidate-review.json). Garder le refus d’écriture et les octets corrompus, avec erreur visible et réouverture, plutôt que fabriquer une baseline valide.

Le bool UI reste celui du callpoint avant askpass, sans prétendre que l’opération bloque l’interface. Les budgets bornent les symboles retenus, pas le coût transitoire d’une StackTrace ni le RSS. Build03 exact, Core complet, natif source X11 privé et paquet exact sont acquis par leurs reçus distincts dans le TODO ; les ciblés seuls ne les qualifient pas. Le natif n’atteste pas une interaction installée, le paquet n’atteste pas une installation hôte ou une release publique. [ADR0139](adr/ui/dialogs/0139-command-journal-call-stacks.md) et TODO séparent leurs preuves.

## Journal de commandes — focus et limites

Les ciblés01/02 ont trouvé le contrôle non focalisable puis la perte du conteneur recyclé (30/36 puis 33/36). Focusable explicite et réparation locale après perte réelle corrigent ces cas. Ciblé03 passe 35/36 : son F12 n’atteignait pas le second Main malgré Activate ; deux vrais clics et compteurs de routage renforcent le test. Ciblé04 passe 36 Desktop et 10 Core ; les anciens essais restent conservés dans le [lot](../../artifacts/linux/command-log-20261020/).

Natif01 s’arrête après l’iconification du groupe par Openbox, sans geste de restauration disponible dans la fixture. Application fermée normalement à 0 ; contrôleur interrompu à 1, 122 identités retirées, dépôts/profils et sources/SDK conservés. Cet essai est rejeté ; un menu clients WM est ajouté à une nouvelle fixture. Restaurer le groupe par ce menu puis F12 ne prouve pas une restauration par F12 seul.

Natif02 confirme le menu clients Openbox et la première flèche, mais découvre le détail tronqué malgré Wrap coché. La valeur locale HorizontalScrollBarVisibility.Auto masque la liaison native Wrap→Disabled ; les contrôles intermédiaires du template peuvent aussi remplacer la police héritée. Les anciens tests inspectaient uniquement les propriétés du TextBox extérieur. Essai rejeté : contrôleur 77574/1 après interruption, application normale à 0, 142 identités retirées ; correction locale acb0a6c9f : les mêmes oracles du présentateur passent après un vrai rouge 35/44. Natif03 est ensuite validé séparément : mono/wrap effectifs, 22 checkpoints et deux fermetures normales ; voir le TODO.

Le picker n’a pas de token natif ; ignorer sa continuation tardive ne ferme pas sa fenêtre système. Un export déjà ouvert peut rester partiel après erreur. Le journal conserve le répertoire, jamais les arguments, environnements, entrées/sorties ou erreurs brutes. Le cache réel et les données brutes Windows restent distincts de l’export de métadonnées ; les piles sûres opt-in sont traitées par ADR0139. Preuves et portées dans [TODO](todo.md#journal-de-commandes), [ADR0138](adr/ui/dialogs/0138-command-journal-lifecycle.md).

## Bisect contextuel

Le [premier natif](../../artifacts/linux/bisect-context-20261020/native-01/root-retirement-review.json) a reçu SIGTERM dans son contrôleur avant tout verdict : sortie réelle 130, application arrêtée par nettoyage, trois captures et aucun checkpoint. Il est rejeté ; l’émetteur du signal n’est pas établi. La reprise dans une session PTY privée a ses [preuves propres](../../artifacts/linux/bisect-context-20261020/review/root-native-review.json) : deux fermetures normales, huit checkpoints et retrait indépendant. Aucun changement produit n’a été requis. Le ciblé 103/103 est distinct de ces essais natifs.

## Git et état des workflows — résolus

| Piège | Cause / règle retenue |
| --- | --- |
| Sonde askpass après `--version` produit exit129 | Une requête de version/aide ne demande pas d’authentification. Garder son résultat natif sans sous-processus de configuration ; respecter les helpers explicites et les valeurs littérales des options des vraies commandes. |
| Stash non suivi laissé malgré exit 0 | `--literal-pathspecs` global casse le nettoyage de `stash push -u` ; réserver les pathspecs littéraux aux arguments concernés. |
| Bare bloque à `stash list` | Lire le reflog de `refs/stash`, sans exiger un worktree. |
| Action de fichier Stash revient aux changements courants | Le refresh générique perd la sélection ; comme FormStash, conserver le snapshot et ses fichiers après Cherry pick/Reset, puis relire le status pour les conflits. |
| R ne répond plus après un clic de fichier Stash | Le chargement du diff désactivait le TreeView pendant PointerPressed et effaçait son focus. Garder la liste immuable active ; IsBusy protège les commandes et la sélection suivante queue une relecture. |
| Stage all touche des fichiers masqués | Agir sur les feuilles du filtre d'affichage ; Selection Filter ne change que la sélection. Une liste vide ne devient pas mutation globale. |
| Indexer un renommage vise l'ancien nom absent | Indexer le nom courant ; désindexer les deux noms. |
| Skip am avance au conflit suivant mais affiche « appliqué » | Relire l'état am après exit non nul ; le code retour seul ne dit pas où la série s'est arrêtée. |
| Undo résout HEAD~1 avant le reset | Le SHA change le message reflog et masque le diagnostic natif sans parent ; conserver le littéral historique, soft/quiet et séparateur final. |
| Bisect start ajoute un séparateur sans pathspec | Même vide, `--` modifie BISECT_LOG ; ne l’ajouter que pour de vrais filtres de chemins. |
| Bisect reçoit deux fermetures successives | Le finally du workflow peut fermer un dialogue déjà fermé ; rendre Closing idempotent avant Cancel du token déjà disposé. |
| Annulation rend la main avant la fin du process | Tuer l'arbre puis attendre sa sortie réelle ; tolérer la disparition d'un descendant pendant la lecture procfs. |
| Test d’annulation signale un enfant déjà mort | Procfs peut passer de Z à X avant de disparaître ; les tests traitent Z/X/x comme terminés, sans modifier les exigences sur les tâches actives. Reproduction et correction sous `artifacts/linux/cancellation-diagnosis/`. |
| Ouverture annulée remplace seulement le backend | Charger les métadonnées avant le changement atomique de dépôt ; créer le token avant de désactiver la barre. |
| Référence supprimée/renommée bloque navigation ou refresh | Scope automatique disparu → All ; conserver les critères manuels, reconstruire clé/SelectedItem/expansion après Rename. |
| Filtrer une multisélection ajoute un commit | Choisir d'abord un sélectionné survivant, avant tout fallback. |
| Bornes de range-diff perdues, F5 change de groupe | Conserver primaire, ordre choisi, bases explicites et groupe actif ; l'ordre du graphe ne remplace pas celui de sélection. |
| Clic Working/index lit un ancien fichier | Mettre à jour sélection/checkbox atomiquement avant l'unique lecture. |
| Ancienne fenêtre écrase un nouveau MERGE_MSG | Baseline du texte réellement affiché après initialisation/template ; ne pas écraser un message Git créé depuis. |
| Commit redevient disponible entre contrôle et mutation | Garder l'action busy jusqu'au retour complet, avec état visuel et refus des gestes concurrents. |
| Propriétaire fermé mais checkout busy reste ouvert | Propager la fermeture ancêtre, annuler/attendre Git avant fermeture des fenêtres. |
| Deuxième confirmation AutoCompile hors thread UI | Marshaler chaque confirmation, y compris après continuation sur worker. |
| Clone réécrit le sous-dossier saisi | Observer TextProperty immédiatement, avant le TextChanged différé. |
| Clone attendu fermé avant son acquittement Process | Keep/OK diffère découverte, MRU et ouverture ; un test de raccourci doit cliquer réellement OK avant les assertions finales. [Essai conservé](../../artifacts/linux/clone-process-root-checks-20261014-01/root-failed-full-review-01.json). |
| Valeur initiale d’une combo disparaît à l’ouverture | AutoCompleteBox peut reporter le texte vide du gabarit après montage ; initialiser la sélection présente dans ItemsSource et tester la vraie activation modale. |
| Message déclaré Latin1 mais écrit UTF-8 | Codec strict partagé, stdin binaire et draft atomique ; valider avant toute mutation. |
| Auteur UTF-8 altéré dans un commit au message CP1252 | `git show` recode aussi les en-têtes ; les formes de commande Windows/Linux produisent la même altération dans l’aperçu. Pour la grille, lire l’identité brute puis `check-mailmap`, garder le formatage Git du sujet/date. Ne pas changer le codec global du viewer ; codecs message et hunks distincts. |
| `cleanup` choisit un autre dépôt après refus d’accès | `Directory.Exists` masque les erreurs IO comme des absences ; classifier avec `File.GetAttributes`, réserver le repli aux chemins absents/non-dépôts. Test Linux avec permission search refusée et cwd distinct. |
| Exclusion Clean interprète un antislash Linux comme séparateur | `ToPosixPath` original remplace le séparateur natif seulement. Garder les antislashs Linux comme données du motif Git ; transformer les espaces en `?` une seule fois dans Core. |
| CP1252 du hunk appliqué aux chemins range-diff UTF-8 | Séparer sections contenu/metadata/fichier. Git peut tronquer un en-tête contenant newline ; ne pas reconstruire des octets absents. |
| Merge ignore `pull.ff=only` | `--no-rebase` prime aussi sur la config ff-only ; réappliquer `--ff-only`. Rebase garde la priorité de l'oracle Git natif. |
| Précontrôles Pull divergent de FormPull | Token Main dès l'initialisation ; [ All ] force Fetch, confirmation rebase garde No sans commande de repli. |
| Abandon du cherry-pick laisse le conflit sur une cible renommée | Le diff source seul ne décrit pas le checkout ; sauvegarder les cibles de renommage pertinentes et refuser les redirections hors couverture avant toute écriture. |
| Sous-module retiré revient après refresh | La dernière .gitmodules reste volontairement sur disque ; la liste suit les gitlinks de l'index, comme git submodule status. |
| Retrait incohérent avec .gitmodules modifiée | rm --cached refuse parfois avant Save+Stage ; Windows ignorait ce résultat. Préparer le futur contenu sur copies si des sections restent, garder les protections du gitlink et publier les fichiers sous verrous Git. |
| Fermer le propriétaire après succès Git réapplique encore le stash | Les continuations enfant précèdent le masquage du parent ; mémoriser la demande de fermeture et arrêter le workflow sans réécrire le résultat natif. [Lot corrigé et vérifié](../../artifacts/linux/pull-process-root-checks-20261014-04/root-full-result-review.json). |
| Done avant récupération d’un Push rejeté | Le hook de fin précède Done/fermeture ; le Pull imbriqué et son acquittement précèdent la reprise dans le même Process. |
| Sortie d’une ancienne tentative attribuée au nouveau transfert | Vider la console courante au redémarrage ; garder distinctes les sorties natives des tentatives. |
| Clôture imbriquée tardive ou deuxième Close transforme un succès en annulation | Observer la clôture du propriétaire avant le veto d’un enfant occupé ; arrêter les continuations, attendre Git, garder l’issue native et rendre la fermeture finale idempotente. |

## Mises à jour — résolus

| Piège | Cause / règle retenue |
| --- | --- |
| Publication RID refusée par NU1004 | Le verrou générique ne contient pas linux-x64. Générer le verrou RID dans obj et vérifier que sa section de base conserve toutes les dépendances verrouillées. [Échec réel](../../artifacts/linux/updates-20261020/package-01/publish.log). |
| Vérification GUI sans Xvfb dans PATH | Le serveur privé existe sous artifacts/linux/tools/xvfb/root/usr/bin ; le rendre accessible dans l’environnement de validation, avec un bus D-Bus possédé. L’absence de prérequis n’est pas une panne du paquet. [Essai conservé](../../artifacts/linux/updates-20261020/package-02/verify-deb.log). |
| Retry sans effet après échec d’un lien sans digest | Mémoriser l’action échouée, y compris le launcher ; restaurer le statut après succès. Régression par vrai clic dans `UpdatesDialogTests`. |
| Fréquence écrasée par Settings ou checks répétés entre fenêtres | Garder la tentative UTC dans XDG_STATE_HOME, hors du profil éditable ; prendre le verrou coopératif et publier avant HTTP. Un état invalide reste intact, sans nouvelle requête. |
| Browse ouvert plus d’une semaine sans nouvelle recherche | Réadmettre au refresh de la grille/dashboard et à Apply des options Updates ; le même throttle et l’unique opération possédée empêchent les doublons. Régression F5 dans `AutomaticUpdatesTests` ; full01 a été arrêté volontairement pour ajouter ce parcours, sans échec produit déduit. |
| Préversion notifiée après changement des préférences ou au-dessus d’une modale | Relire les deux choix avant affichage ; attendre Main actif, libre et sans enfant. Le manuel annule la recherche automatique et garde son propre choix de session, sans toucher l’horodatage. |
| Probes de nouveau processus sans SDK local | Trois tests historiques recherchent `.dotnet/dotnet` dans les parents du checkout. Fournir une copie physique du SDK isolé ; 3/3 probes puis full03 verts, sans changement produit. [Preuve](../../artifacts/linux/update-scheduler-20261020/review/runtime-environment-review.json). |
| Préflight natif avec empreintes héritées obsolètes | Vérifier les paquets Git/libXtst installés avant de lier leurs empreintes actuelles ; conserver les scripts et l’essai refusé. Aucune application ne démarre avant ce contrôle. [Preuve](../../artifacts/linux/update-scheduler-20261020/review/native-preflight-01.json). |

## Options avancées — régressions et qualification séparée

| Piège | Cause / règle retenue |
| --- | --- |
| Apply d’un profil Advanced illisible échappe au thread UI | Session.Save refuse correctement la baseline avec InvalidDataException ; ajouter ce type aux deux filtres SaveAll. Message, brouillon Apply/OK et publications acquises conservés, aucun repair implicite. |
| Boîte Push 24 px mais ligne éditable coupée | AutoComplete transmet le padding Fluent 10,6,6,5 à son TextBox ; padding local 4,2, sans augmenter Height. Vérifier TextPresenter dans la viewport et le contrôle parent, au repos/focus/édition. |
| Maj+F10 absent sous X11 ou ouvre deux fois | La configuration Win32 ajoute ce geste, la générique non. Fallback KeyUp seulement si KeyDown/natif non traités et focus inchangé ; conserver la vraie touche du test et le menu du File tree focalisé, sans Open de secours. |
| Cible/focus perdus au refresh ou à la fermeture | Garder nom complet et RevisionHash, invocation Rebase séparée du hit-test effacé ; l’intention Select-left invalide le rappel grille, sans filtre/reconstruction. Scripts avant CI/PR et séparateurs Other suivent Windows. |

| Essai conservé | Résultat et suite |
| --- | --- |
| [Panneaux01](../../artifacts/linux/advanced-options-20261020/panels-early-01/results/desktop.trx) puis [02](../../artifacts/linux/advanced-options-20261020/panels-early-02/results/desktop.trx) | 15 exécutés / 13 passés / 2 échecs sur la barrière Settings, puis 15/15 après correction. Douze captures initiales ont révélé le texte Push coupé ; quatre Push corrigées inspectées. Preuves préliminaires seules, sessions 15818/1 et 72114/0 conservées. |
| [Ciblé01](../../artifacts/linux/advanced-options-20261020/targeted-01/results/desktop.trx) | 243 exécutés / 242 passés / 1 échec : attente Maj+F10 ligne 219. Session 22761/1 retirée/stable ; vraie route clavier corrigée, aucun transfert des 242 succès. |
| [Ciblé02](../../artifacts/linux/advanced-options-20261020/targeted-02/results/desktop.trx) | 247 exécutés / 246 passés / 1 échec : helper envoie KeyUp Escape au dialogue déjà fermé par KeyDown (« TopLevel must be a headless window »). Test corrigé : attendre fermeture/focus naturel, relâcher sur MainWindow ; produit inchangé pour cette correction. |
| [Build05](../../artifacts/linux/advanced-options-20261020/build-05/desktop.log) | Deux CS7036 test-only : RawInputModifiers manquant sur KeyPress/KeyRelease ; argument RawInputModifiers.None explicite, sans changement produit. |
| [Ciblé03](../../artifacts/linux/advanced-options-20261020/review/root-targeted-review.json) et [équivalence source](../../artifacts/linux/advanced-options-20261020/review/source-import-review.json) | Source ac59ad500b, 247/247, 19 classes et 12 captures inspectées ; session réelle 49368/0 retirée, source/binaires stables. Build07 trois projets sans avertissement, 43713/0. |

[Natif01 revu par Root](../../artifacts/linux/advanced-options-20261020/review/root-native-review.json) : périmètre X11 source EN/FR accepté, 93 commandes/90 captures/21 checkpoints et deux fermetures applicatives wait0, mais **contrôleur 66910/1 conservé**. Commande 61 : Root a envoyé keysym au lieu de key, KeyError avant input/image inchangée ; vraie Escape 62 réussie. Deux préflights clients avec vue /proc restreinte avaient été refusés avant opération ; [recheck même hôte corrigé](../../artifacts/linux/advanced-options-20261020/process-view-recheck.json) confirme les 305 anciennes identités retirées, puis 38 identités natives retirées. Ne pas convertir ce défaut opérateur en succès technique global ni l’étendre au paquet. Suites complètes acquises sur ac59 (3616 Desktop, 1062 Core, 35 Plugins), paquet exact isolé et inventaire qualifiés séparément ; preuves et limites dans TODO.

## Images d’aide — contrats et régressions

Le ciblé01 passait 248/248, mais sa capture française coupait Stash. Le ciblé02 renforcé (247/248) a exposé un débordement de 4 px en aide repliée. Après correction, [ciblé03 248/248 et six rendus revus](../../artifacts/linux/help-images-20261020/review/root-targeted-review.json), puis [natif source EN/FR borné](../../artifacts/linux/help-images-20261020/review/root-native-review.json). Le [paquet exact](../../artifacts/linux/help-images-20261020/review/root-package-review.json) est qualifié séparément en DPKG isolé ; interaction installée ouverte.

| Piège | Règle retenue |
| --- | --- |
| Confondre masquage global et repli local | Retirer le contrôle entier sans changer IsExpanded ni publier command-help-preferences ; restauration locale couverte headless, pas en natif. |
| Masquer seulement l’image | Traiter body/footer, largeurs et viewport Rebase ; capturer aussi dans le constructeur de l’enfant de reprise. |
| Réparer un profil pour une préférence cosmétique | Aide visible après erreur de lecture ; conserver les octets et la barrière d’édition explicite dans Advanced. |
| Bouton dans la fenêtre, glyphes français coupés | Width130/AccessText Wrap ne suffisaient pas. Largeur Auto/MinWidth, texte mono-ligne et oracle vérifiant le libellé entier. |
| Footer débordant en aide repliée | Mesurer les commandes ensemble une fois à Opened après styles/fontes, puis intégrer aide, marges et espacement au minimum. |

## Lecteurs et UI — résolus

| Piège | Cause / règle retenue |
| --- | --- |
| Quick view refuse de s'ouvrir | Renommer le viewer après son attachement à Content lève « styled element already styled ». Fixer le Name avant styling ; régression de l'ancien parcours Recovery conservée. [Échec](../../artifacts/linux/jenkins-20261020/targeted-03/results/desktop.trx). |
| Échap du visualiseur ne remonte pas à la fenêtre | AvaloniaEdit consomme la touche sans retirer la sélection ni fermer. Relayer depuis le TextArea focalisé en tunnel, sélection avant Close, priorité Find/menu contexte conservée ; même contrat que Stash/Commit. [Échec réel](../../artifacts/linux/jenkins-20261020/portable-targeted-01/results/desktop.trx), [correction ciblée](../../artifacts/linux/jenkins-20261020/portable-targeted-02/results/desktop.trx). |
| L'aide Scripts perd la sélection au retour | TextBox effaçait la plage à LostFocus ; préserver la sélection tout en masquant son highlight inactif. L'oracle compare le caret réellement établi au clavier, sans lui imposer l'extrémité d'une autre API. |
| Page inactive annoncée visible dans un test | Un onglet détaché peut garder IsEffectivelyVisible ; contrôler l'arbre visuel réellement présenté et attendre les lectures possédées. Le défaut de cet oracle ne justifie pas un changement du produit. |
| Compare perd la saisie rapide du filtre | Remplacer ItemsSource réémet SelectionChanged puis RunAsync désactive le champ. Garder la lecture ciblée indépendante, identifier la demande et ne publier que la sélection courante ; rouge natif7a conservé. |
| Filtre historique visible mais inactif | Focaliser un contrôle à Opened ne garantit pas l’activation X11. Réutiliser ModalWindowActivation.AfterMapping déjà partagé par les dialogues de dépôt et GoTo ; le replay7a expire avant la première Échap, sans preuve de son traitement. |
| Destination LF vide au focus ou après Ctrl+A | Hauteur fixe puis défilement horizontal jusqu’au caret masquent les lignes courtes. Replier les valeurs multiligne avec hauteur bornée, sans transformer le texte ; conserver 24 px pour les chemins ordinaires. Glyphes, copie littérale et six rendus clair/sombre vérifiés en headless ; audit natif restant dans TODO. |
| Récupération Pull placée après une longue sortie dans le scroller | Un clic sur Abort peut tomber hors de la fenêtre. Épingler Continue/Abort au-dessus des boutons du bas ; laisser seulement options/sortie défiler. Les parcours Merge et Rebase avec conflits passent dans les [60 tests](../../artifacts/linux/pull-layout-checks-20261015-09/targeted.trx). |
| Surcharge des glyphes dans un Window dérivé sans effet | Le sélecteur doit viser la clé de style Window avec une classe locale, puis les parties du template. Vérifier les dimensions effectivement rendues : le premier essai gardait 20 px ; le contrôle mesure désormais 13 px dans les deux thèmes. |
| Menu de références/copie vide après ouverture programmée | Le peuplement lié à Opening seul ne couvrait pas Open ; actualiser aussi à Opened. Les tests de workflow repèrent les actions par leur Name stable ; le test visuel contrôle séparément les libellés historiques. |
| Diff différé perdu après annulation d’un dialogue du menu | Conserver la demande pendant l’action, puis la relancer depuis le retour à l’état libre ; ne pas la lancer pendant la sélection ou avant le clic sur la commande. [Régression reproduite sur le code précédent](../../artifacts/linux/revision-menu-cancel-regression-20261015-02/before.trx), [27 cas corrigés](../../artifacts/linux/revision-menu-checks-20261015-10/targeted.trx), dont Cherry-pick multisélection réel. |
| Autocomplétion perdue au clic | La perte de focus effaçait la sélection et fermait la liste avant release. Préserver ce cycle et accepter le chemin littéral de la ligne réellement pointée. [Premier essai conservé](../../artifacts/linux/clone-input-root-checks-20261014-01/root-failed-targeted-review.json). |
| Erreur Git absente avant le démarrage du suivi | Une sonde readonly peut échouer avant tout callback ; afficher son vrai GitResult final sans inventer de chunks. Correctif vérifié dans les 30 tests ciblés et les 1159 tests complets. |
| Erreur de préférence masque les avertissements Git | Séparer les notices, effacer l’erreur après une sauvegarde réussie et borner les détails défilables ; test réel réussi dans les suites ciblée et complète. |
| Libellé français du sélecteur Clean tronqué | Une traduction sur deux lignes était coupée par le bouton de 25 px ; mesurer le texte réel, garder NoWrap et adapter padding/largeur au caption. |
| Saisie native multirefs perd ses espaces | ComboBox.OnKeyDown traite Space comme choix puis sélectionne tout. Bypass seulement quand le vrai éditeur est focused, sans injection de texte ; tests par key/TextInput et saisie native multirefs clair/sombre vérifiés sur `a7bdda7d6`. |
| Choix de suggestion copie un éditeur vide | SetValue local masque le TemplateBinding Fluent ; SetCurrentValue garde l’éditeur synchronisé. L’assertion Combo.Text et un Focus correcteur masquaient le défaut. UI 22 et parcours natifs du payload `a7bdda7d6` vérifiés. |
| Clic dans Branches débordé rend Entrée inactive | Le survol MenuItem tardif après mapping reprend le focus ; protéger PointerReleased seul ne suffit pas. Bloquer PointerEnteredItem uniquement sur la combo focused réellement survolée. UI 22 et parcours natifs du payload `a7bdda7d6` vérifiés. |
| Combo Branches perd sélection, caret ou premier clic extérieur | Annuler les refresh de suggestions différés ; restaurer le caret après focus ; fermer le popup dans le tunnel du propriétaire avant le vrai PointerPressed. Relayer Ctrl+E depuis PopupRoot. Preuves de développement `artifacts/linux/branch-filter-combo-development/`. |
| Suggestion Branches reprend l’accent Ubuntu | Le vrai PART_ContentPresenter Fluent possède des couleurs propres : surcharger fond/texte normal, selected, hover et pressed localement. Contrôles rendus UI 09 et natifs clair/sombre `0683f7184` ; ancien candidat `d5ad2dc17` préservé. |
| Fetch→Rebase conserve parfois All tags | Les événements radio Avalonia arrivent avant le décochage du précédent ; appliquer le mode depuis la case qui vient d’être cochée. |
| Entrée sur une case du cherry-pick ne lance pas Pick | Garder le bouton par défaut historique depuis les cases et la liste des parents ; le focus initial appartient à la case ou au parent. |
| Fermer le parent pendant un prompt accepte malgré tout le cherry-pick | Les enfants se ferment avant IsVisible=false du parent ; mémoriser la fermeture acceptée avant les callbacks et ne pas enregistrer les options. |
| Texte source interprété comme patch | Mode texte explicite ; indexabilité séparée du contenu affiché. |
| Staging réutilise l'ancien aperçu après échec | Invalider au début du chargement et sur transition explicite, même si le texte est identique. Codec reste accessible pour réessayer. |
| Sélection LF touche l'ajout suivant, recherche omet le dernier caractère | Adapter les bornes inclusives historiques aux intervalles semi-ouverts natifs. |
| UTF-8 tronqué à la limite devient hex | Décoder sans flush seulement pour un preview tronqué ; erreurs intérieures et EOF complet restent stricts. |
| Blame legacy corrompu | Décoder le contenu selon le codec du fichier, les métadonnées séparément en UTF-8. |
| Reload identique laisse le lecteur bloqué | Compléter explicitement la lecture réussie ; TextChanged ne sera pas émis. |
| Ancien commit/viewport conservé sur mauvais fichier | Identité de requête explicite ; restaurer l'offset après recalcul du layout, remapper seulement la même identité. |
| Find/lecture perd le focus ; Alt+flèches perd la sélection | Capturer Owner avant Hide, restaurer le viewer après lecture ; scope readonly officiel d'AvaloniaEdit pour caret hors sélection. |
| Ctrl+T ouvre Create tag, Ctrl+Entrée devient newline | Priorité aux gestes disponibles du viewer et à la validation de Commit avant l'éditeur ; sélection staged chargée explicitement sans voler le focus libre. |
| F8 inactif depuis le popup Pull | Racines popup hors route clavier Main : relais tunnel dans menu et sous-menu. |
| Échap du diff ne rejoint pas Commit | Relais sur TextArea : Find/popup, sélection, puis fermeture existante et brouillon conservé. |
| Difftastic compacte ses colonnes ou supprime un chiffre légitime | Lire les préfixes ANSI réellement émis ; sans preuve de colonnes, conserver intégralement la sortie. |
| Invisibles absents après 3000 caractères | AvaloniaEdit saute ses générateurs ; dessin des caractères visibles et EOL avec étendue complète. |
| Blancs sélectionnés gardent la couleur de marqueur | Recolorer les runs courts et dessin viewport long sélectionnés ; EOL garde sa couleur. |
| Glyphes noirs en sombre, contraste popup erroné, couleurs périmées | Adaptation monochrome conservant alpha ; ressources locales du vrai présentateur et variante du propriétaire ; invalidation au changement de thème. |
| Accent Ubuntu orange malgré styles headless bleus | Templates Fluent ont leurs propres états checked/hover/pressed. Ressources locales et contrôle natif clair/sombre pour listes, catégories et radios Pull. |
| Ancre intra-ligne invisible, petite image floue | Peindre les ancres après les fonds sur pixel entier ; dimensionner/arrondir avant centrage. |
| Icônes absentes ou bitmap encore référencé après Dispose | Headers explicites pour menus supérieurs ; détacher sources et callbacks avant libération. |
| Champs numériques/chevrons compacts tronqués | MinWidth/marges internes Fluent ; templates locaux adaptés aux dimensions historiques. |
| Reparenting/renouvellement/refresh plante | Détacher avant insertion, garde null des templates, nom fixé avant application des styles ; ne pas réassigner le Name d'un contrôle stylé. |
| Combo regex invalide reste visuellement neutre | Le fond du vrai éditeur est distinct du ComboBox ; reprendre ce fond et libérer le binding neutre. |
| Menu Settings invisible, premier débordement vide | Pas de holder logique invisible ; collection notifiable établie avant construction du présentateur, peuplée avant clic. |
| Menus français tronqués ; données Git traduites | Lever le plafond Fluent uniquement sur menus du viewer ; traduire commandes/catégories, garder branches/dossiers littéraux. |
| URL de clone mal affichée | Distinguer URL/SCP/local, décoder le nom visuel et conserver valeurs littérales ; contraste et templates XLF complets. |
| Ctrl+E perd Branches sous souris immobile | Garde du survol synthétique au mapping clavier ; premier vrai mouvement relayé une fois, nettoyage sur clic/navigation/fermeture. |
| Dropdown Pull centré sous la flèche | Ancrage explicite sur le bord gauche du segment principal ; mesurer le présentateur réel. |
| Premier Copier X11 vide | Avalonia met en cache les atoms absents : initialiser les sept noms avant GUI/askpass, garder la connexion ; aucun owner/contenu fabriqué. |

## Workflows — pièges validés

| Piège | Cause / sortie |
| --- | --- |
| Merge -F - échoue malgré un message valide | Git merge lit le fichier nommé -, contrairement à commit. Utiliser un fichier privé encodé, nettoyé après Git. |
| Mergetool relit un nom quoté ou un glob | Le helper Git consomme des lignes, pas du NUL ; comparer sa sortie au nom exact et propager GIT_LITERAL_PATHSPECS, refuser les noms incompatibles avant mutation. |
| Blob staged encore présent après GC | resolve-undo (REUC) peut le retenir ; attester sa suppression native puis absence réelle avant de tester la restauration. |
| Fixture de commit prétend CP1252 sur argv UTF-8 | Fournir les octets via stdin/fichier et contrôler le header/octet du commit source avant toute comparaison. |
| Revert ne rejette pas le hook attendu | Reproduire les hooks réellement exécutés par Git : prepare-commit-msg peut échouer en 128, préserver le draft puis remonter stderr natif. |
| Codec Rebase invalide masque la reprise et bloque Abort | Lire le codec sauvegardé avant la config ; sans codec fiable garder l’état minimal. Abort ne dépend ni du codec courant ni de l’éditeur. |
| Journal privé invalide masque Abort | Projeter le backend Git minimal sans codec/options devinés ; conserver les métadonnées et laisser Abort disponible. |
| Grille Rebase vide avec un journal illisible | Garder le hash courant réel de Git visible sans deviner les messages ou options ; défaut observé sur `6533625a9`, hash réel vérifié en natif clair/sombre sur `d8ee8f5eb`. |
| Notice de journal longue coupe la reprise | Garder l’action lisible, les détails accessibles et les boutons dans la fenêtre ; borner la zone de détails. Notice complète et Abort visibles en natif clair/sombre sur `d8ee8f5eb`. |
| Sujets Rebase CP1252 détruits dans les diagnostics | Conserver les octets de sortie ; traiter séparément sujets de commit, todo et résumé, avec leurs codecs déclarés ; chemins et texte externe restent UTF-8. Octets invalides échappés, jamais remplacés. Tests réels vérifiés ; sujets affichés en natif clair/sombre sur `d8ee8f5eb`. Le traçage de processus ne prouve pas les octets bruts stdout/stderr. |
| Grille Rebase recouvre contexte et en-têtes | Sortie Git longue non bornée et hauteur minimale dépassant l’espace, observées en natif sur `d8ee8f5eb`. Zones désormais bornées/défilables et boutons conservés, vérifiés en headless à 500/460 px clair/sombre ; paquet `0ea90bef9` vérifié ; grille et contexte séparés en natif sombre à 500/460 px. Clair et audit complet restent requis. |
| Infobulle sombre presque blanche sur fond clair | Fond Fluent non relié à la palette historique, observé sur `d8ee8f5eb`. Ressources et présentateur désormais cohérents, rendu réel headless vérifié ; popup X11 sombre vérifié sur `0ea90bef9` avec la palette historique ; clair encore à auditer. |
| Todo/done illisible masque la reprise | Capturer les erreurs IO/décodage de ces seuls fichiers, conserver l’état Git et le codec sauvegardé valide sans texte partiel ; refuser les actions de continuation avant mutation, garder Abort indépendant. Tests Git réels et ouverture/reload UI vérifiés. |
| Todo natif CP1252 mixte ou `Ã©` transformé en `é` | Git réencode son buffer cumulatif de formatage. Générer le todo natif en UTF-8, déclaré dans le journal ; messages et objets gardent leur codec strict. Ne pas détecter le codec par la validité UTF-8 des bytes. |
| Ancien journal natif non UTF-8 sans codec du todo | Provenance insuffisante : état Git minimal et Abort, sans convertir les données. Garder le journal après fin pour une récupération explicite ; Apply reste lisible pendant son backend actif. |
| Nettoyage invalide masque un Abort Git réussi | Séparer succès Git et avertissement de journal conservé ; après fin, récupération explicite par renommage sans suivre les liens. |
| Journal Rebase obsolète après une fin au terminal | Nettoyer uniquement un journal reconnu du worktree sans backend actif, sous verrou de commande ; préserver les contenus inconnus. |
| Pull périmé peut remiser une résolution Rebase stagée | Revérifier le backend sous verrou avant autostash/fetch, même si la fenêtre Pull était déjà ouverte. |

## Build et validation — repères

| Piège | Cause / sortie |
| --- | --- |
| Environnement `/proc` sans séparateur `=` dans un audit natif | Le scan global du dispositif de capture échoue sur un acteur étranger. Lire seulement les enfants du tracer et tolérer les entrées mal formées. [Premier essai conservé](../../artifacts/linux/snapshots/b21e51028/native-history-removal/pilots/light/H01-attempt-01/proofs/root-bootstrap-failure-review-01.json) : aucun geste, dépôt intact et aucune acceptation GUI. |
| Échelle décimale d’un réglage NumericUpDown | Incrémenter puis décrémenter peut enregistrer `1.0` pour une valeur affichée `1`. Comparer la valeur décimale, conserver les octets réels ; [premier lecteur refusé](../../artifacts/linux/history-removal-root-checks-20261014-04/h02-proof-review-attempt-01.json), [correction vérifiée](../../artifacts/linux/history-removal-root-checks-20261014-04/h02-proof-review-attempt-02.json). |
| Identité d’un enfant lue avant la fin de `exec` | Une naissance conserve un environnement vide puis la garde refuse l’acteur stabilisé. Conserver la première observation et attendre deux lectures réelles concordantes. [Second essai sans acceptation GUI](../../artifacts/linux/snapshots/b21e51028/native-history-removal/pilots/light/H01-attempt-02/proofs/root-failed-guard-review-01.json) : fermeture normale, dépôt intact. |
| Fermer le parent pendant un Process occupé | Le processus s’arrête mais le parent reste visible. [37/38 tests ciblés](../../artifacts/linux/history-removal-root-checks-20261014-03/targeted.log), essai conservé. Retenter la fermeture du propriétaire après arrêt et fermeture réels du Process ; [39 ciblés et 1186 complets réussis](../../artifacts/linux/history-removal-root-checks-20261014-04/root-full-result-review.json). |
| NUnit `Is.Ordered.Using(StringComparer.Ordinal)` ambigu | Le comparateur implémente les deux interfaces ; préciser `Using<string>`. [Build refusé conservé](../../artifacts/linux/history-removal-root-checks-20261014-01/build.log), tests non lancés. [Build suivant](../../artifacts/linux/history-removal-root-checks-20261014-02/build.log) : six projets sans diagnostic ; deux tests UI échouent ensuite, essai conservé. [Validation finale](../../artifacts/linux/history-removal-root-checks-20261014-04/root-full-result-review.json) : build sans diagnostic et 1186 tests réussis. |
| Commentaire XML contenant deux tirets consécutifs | Le projet ne se charge pas ; éviter cette séquence même pour citer une option CLI. Essai arrêté conservé. |
| Annulation C01 : scans `/proc` toujours repris au début | Sous trace native, les quanta expirent avant les détenteurs de pipes ; absence d’ajout ne prouve pas fin de parcours. [Candidat refusé](../../artifacts/linux/snapshots/fbd82d6bb/native-undo-bisect-process/final-cases/light/C01-attempt-01/proofs/root-attempt-review-01.json), progression conservée et grâce par acteur [vérifiées sous trace](../../artifacts/linux/progressive-orphan-probe-trace-20261014-01/root-trace-verification.json). [Variante GUI claire corrigée acceptée](../../artifacts/linux/snapshots/91a47a6b6/native-undo-bisect-process/final-cases/light/C01-attempt-01/proofs/root-c01-native-verification-01.json) : deux SIGINT/exit130 réels, Good conservé et fermeture normale. L’ancien refus et sa libération de fixture restent un nettoyage d’échec. |
| Témoin pré-HELLO absent : chemin LD_PRELOAD avec espaces | [ld.so traite les espaces comme séparateurs](https://man7.org/linux/man-pages/man8/ld.so.8.html), sans échappement. Bibliothèque privée renommée ; chemins Git littéraux et assertions conservés. [375/376 Core, un échec conservé](../../artifacts/linux/graceful-process-root-checks-20261014-02/core-tests.log), suites suivantes non exécutées. Fixture corrigée validée dans [la suite complète suivante](../../artifacts/linux/graceful-process-root-checks-20261014-03/local-source-verification.json). |
| OutputPath global du helper ≠ chemin annoncé | Le build observé produit `bin/ProgressiveOrphanProbe.dll`, sans sous-dossier net10.0 ; l’annonce script/API était une hypothèse. [Chemin réel lié avant exécution](../../artifacts/linux/progressive-process-root-inputs-20261014-01/root-orphan-probe-run-admission.json). |
| Catch et pattern locaux homonymes, build CS0136 | Noms distincts pour l’erreur du handshake et celle de Stop. [Essai échoué conservé](../../artifacts/linux/graceful-process-root-checks-20261014-01/build.log) ; nouvel essai : six projets sans diagnostic et 30 tests ciblés réussis. |
| Helper C : symbole privé en collision avec glibc | `group_member` est déjà déclaré ; préfixer les symboles internes. Premier build échoué conservé, build suivant réussi. |
| Trace du build attend le serveur Roslyn après compilation | `strace -f` suit aussi VBCSCompiler ; désactiver UseSharedCompilation pour les nouveaux essais. Ancien serveur possédé arrêté via la commande SDK normale, sans relancer le build. |
| Fixture stdin vide dans un shell asynchrone | Sauver le vrai fd 0 avant la liste en arrière-plan puis le transmettre au producteur ; `<&0` pouvait lire /dev/null. [Git direct a confirmé le défaut](../../artifacts/linux/bisect-process-root-checks-20261013-04/root-duplex-fixture-diagnosis.json) ; fixture corrigée vérifiée dans la suite Core complète. |
| SDK Snap inutilisable dans le sandbox | Utiliser le SDK officiel local `.dotnet` et une version complète dans global.json. |
| Version Debian contient HOME | Expansion Bash de `~` ; citer le caractère et vérifier le paquet reproductible. |
| Fixture blob résolue comme commit | ResolveRevisionAsync ajoute `^{commit}` ; résoudre le blob par la vraie commande rev-parse sans cette contrainte, conserver les assertions de transfert. |
| `dotnet test` annonce succès sans scénarios Core/Plugins | Projets console IsTestProject=false : `dotnet run --no-build --no-restore` et compteurs des logs. Apphost et DOTNET_ROOT local requis pour helpers enfants. |
| CA1416 sur les permissions d’un helper Linux | Placer la garde Linux au site d’appel ; build relancé sans diagnostic, essai initial conservé. |
| Tests Pull attendent la suite avant acquittement Process | Acquitter le vrai modal terminé, y compris dans Push → Pull et Commit & Push ; garder les oracles Git et les essais refusés. |
| VSTest arrêté avant tout scénario dans le sandbox | Socket TCP local refusé (`SocketException 13`) ; exécuter les tests isolés avec cet accès, garder l’essai arrêté hors validation. |
| Assert.ThrowsAsync bloque le dispatcher Avalonia | Attendre réellement la tâche et capturer l’exception ; l’assertion NUnit synchrone ne laisse pas avancer le modal. |
| Bouton visible mais clic central traverse au Panel | Un Background null local efface le style et son fond ; ClearValue rend aussi survol/pression au thème. Conserver un seul clic au centre dans le test. |
| Compteur Click reste nul malgré fermeture | Button.ClickEvent route en Bubble ; écouter la route réelle, sans rejouer le geste. |
| Modal déjà OwnedWindows mais hit-test encore vide | Stabiliser les frames via les API publiques avant le clic ; conserver visibilité, zone et hit-test stricts. Helper partagé Push/Bisect ; [échecs conservés](../../artifacts/linux/push-process-root-checks-20261014-05/root-failed-full-review-01.json), [validation finale](../../artifacts/linux/push-process-root-checks-20261014-07/root-full-result-review.json). |
| Fixture RemoteDeletion lit ProcessNotice avant sa matérialisation visuelle | Attendre au plus 20 s, une vraie frame et la notice visible aux deux observations ; conserver les assertions NoPush/refs. [Full refusé conservé](../../artifacts/linux/comparison-groups-desktop-checks-20261017-02-full/parent-failed-full-review.json). |
| Tests Avalonia bloquent ou teardown masque l'exception | Attente async normale sur dispatcher, BindingPriority.Template pour TemplateBinding ; ne pas modifier l'isolation pour masquer le défaut. |
| Test d’infobulle exige PopupRoot en headless | Le host réel est OverlayPopupHost ; tester son ouverture, son rattachement, le présentateur et ses pixels sans changer la configuration des popups. Le natif reste une validation distincte. |
| AvaloniaTheory non découvert | Constructeur NUnit TheoryAttribute incompatible : entrées AvaloniaTest et parcours partagé. |
| Culture/Alt/HEAD du test ne correspondent pas à l'écran | Fixer culture avant construction, poser focus initial, sélectionner le vrai commit attendu ; réouverture conserve volontairement la sélection. |
| Pixels sélectionnés absents ou rejetés | Attendre une frame, exiger la vraie sélection, comparer le mélange anticrénelé plutôt qu'un pixel opaque. |
| Geste XTest envoyé avant rendu, focus logique sans focus X11 | IsViewable peut précéder une frame encore noire ; mesurer une vraie frame rendue et le titre/PID avant saisie. Calibration sans WM explicite, sans preuve Wayland. |
| WM_DELETE_WINDOW absent dans Xvfb sans WM | Avalonia met en cache les atoms absents ; le harnais pose les trois noms standard avant GUI et garde la connexion jusqu’à la fin du driver. Aucun comportement du WM hôte revendiqué. |
| Clic natif sur un menu refusé malgré le bon PID | Un popup Avalonia possède sa propre fenêtre X11 ; cibler son ID issu de la dernière capture, pas celui du parent. Garder l’essai refusé hors acceptation et reprendre un cas complet. |
| Contrôle du runtime inclut les rendus des tests | Les PNG et traces de fermeture produits dans le working directory de test sont des sorties ; conserver leurs témoins séparément et vérifier les vrais fichiers construits avec leur ensemble de chemins exact. |
| Capture Git intermédiaire oubliée ou tardive dans un audit natif | Les états finaux ne remplacent pas le témoin avant redémarrage ; Cleanup S01 : chemin relatif refusé, capture après lancement suivant. Conserver l’essai non accepté et reprendre un cas complet. |
| Script de preuve écrit son JSON puis termine en erreur | Un défaut de résumé ne vaut pas sortie0 : garder l’exit réel et relire séparément les observations ; aucune acceptation native transférée. |
| Plan natif vert puis launcher exit 2 | Figer/copier le launcher avant lancement, vérifier séparément exécution et fermeture. |
| Harnais ne trouve pas un titre contenant des guillemets | Lire le vrai titre via XFetchName et vérifier le PID par XRes ; ne pas découper la sortie de xwininfo sur le premier guillemet. |
| Coordinateur disparu malgré app/keeper exit 0 | Conserver l’exit extérieur et le rapport manquant ; ne pas inventer coordinator0. Session/groupe isolés et nouveau cas avant acceptation. |
| Manifeste du harnais contient son propre SHA vide | Écrire le manifeste après le gel des fichiers puis le hasher à l’extérieur ; conserver l’ancienne anomalie et vérifier chaque fichier réel. |
| Hash DPKG hôte différent après validation | Comparer avant/après dans l'intervalle du test ; changement hôte ultérieur consigné séparément. |
| Objet Git LF comparé au checkout CRLF | Respecter la conversion EOL déclarée dans les attributs figés ; contrôler le contenu compilé après cette conversion et garder les fixtures binaires exactes. Comparaison brute initiale refusée, rattachement source au commit vérifié. |
| Inventaire mêlant compilation et métadonnées Git cachées | Conserver les entrées compilées et contrôler séparément `.gitattributes`, dont les règles préservent les fixtures brutes ; ne pas exclure globalement les fichiers cachés. |
| WM_DELETE sur le propriétaire désactivé d’un Process modal ignoré | Avalonia X11 vérifie IsEnabled avant Closing ; Pull P01 `7fc8756da` reste non accepté. Conserver les matériaux avant acquittement et le nettoyage normal distinct ; ne pas assimiler l’envoi du message à la fermeture du propriétaire. |

Les correctifs locaux du lecteur/Blame sont inclus dans les paquets livrés ; leurs audits natifs restent attribués aux lots d'origine. Les anciennes mentions « paquet suivant à valider » ne remplacent pas les limites actuelles du TODO.

## Menus partagés

| Piège | Cause / règle retenue |
| --- | --- |
| Tools/Help restent anglais avec le nom de leur classe Windows | Leurs entrées sont fusionnées dans la catégorie XLF FormBrowse ; traduire avec les identifiants du Designer. |
| Le style commun ne change pas certains menus de diff | Height/MinHeight locaux et palettes par entrée ont priorité sur les styles ; retirer ces copies et vérifier les bounds des vrais popups. |
| Une coche masque un PNG de diff | Forcer toutes les coches dans la colonne d’icône remplace le glyph original ; conserver la colonne native distincte quand une entrée a une icône, et garder les vérifications de pixels. |
| Translate reste sombre malgré la correction des pixels noirs | Le PNG historique contient du RGB 1 avec alpha partiel. Réutiliser la correction achromatique complète du diff et vérifier les pixels non transparents du vrai bitmap en thème sombre. |

## Arbre de navigation

| Piège | Cause / règle retenue |
| --- | --- |
| Chevron compact impossible à cliquer | Un padding du TreeView provoque un BringIntoView horizontal de 6 px au focus, entre appui et relâchement. Mettre le retrait dans les entêtes, garder le viewport sans padding et vérifier un vrai clic immobile. |
| Une hauteur fixe masque les descendants | La hauteur du TreeViewItem inclut ses enfants ; fixer uniquement le Border d’entête à 18 px et garder une hauteur automatique pour l’item. |
| Des pointillés deviennent un trait gris | Des limites de tirets de 1 px à demi-pixel mélangent les pixels voisins. Aligner les limites longitudinales sur des pixels entiers et contrôler alternance d’encre et de fond dans le rendu effectif. |

## Panneaux de fichiers

| Piège | Cause / règle retenue |
| --- | --- |
| Les tests du menu voient des actions périmées | ContextMenu.Open(Control) contourne Opening dans Avalonia 11.3.22. Utiliser ContextRequested ou un vrai clic droit, comme l’utilisateur ; le handler Opening capture alors la sélection actuelle. |
| Fermer un menu perturbe la recherche modale | Ne recharger à la fermeture que si le clic droit a changé la sélection. Un rechargement systématique vole le focus et peut chevaucher grep. |
| Arbre présent, contenu effacé au changement de commit | Recharger explicitement le blob du chemin conservé quand le panneau File tree est actif, y compris après rafraîchissement. |
| Une lookup de couleur empêche le chargement du dépôt | Avant attachement, une ressource peut être UnsetValue. Employer TryFindResource avec thème explicite et un fallback ; réappliquer après attachement et changement de thème. |
| Clic de commit/fichier fait clignoter toute la fenêtre et perd les flèches haut/bas | Une lecture passait par RunAsync/SetActionsEnabled et libérait le focus natif. Séquences annulables par panneau, tokens explicites et publication complète ; charger les onglets visibles comme FormBrowse/RevisionDiffControl. Ne pas restaurer le focus par un Focus différé qui volerait le clic suivant. ADR 0063. |
| Ancien diff réapparaît après un filtre ou s’applique au mauvais fichier | Annuler aussi au vidage de sélection et invalider l’état prêt du viewer dès le début et garder staging partiel indisponible tant que le nouveau patch n’est pas publié, même après échec de lecture. Tester avec une vraie lecture Git bloquée, pas un délai supposé. |
| Une correction visible dans l’historique semble sans effet dans l’application | Le commit courant du dépôt n’identifie pas le binaire en cours. Vérifier le launcher avec --version et l’issue réelle d’APT : la session d’installation du focus était interrompue (143), le binaire installé restait b6b60308b. Le comparatif natif reproduit le problème sur celui-ci et valide le paquet corrigé 10cad7cb8. |

## View et Navigate

| Piège | Cause / règle retenue |
| --- | --- |
| Icônes, gestes et coches divergent entre menu principal et clic droit | Définir les commandes une seule fois, avec les clés XLF RevisionGrid originales et la disponibilité dynamique. |
| Le recyclage d’une ligne transmet un commit null | Le template crée une ligne vide puis applique ses options seulement après un Tag GitCommit valide. |
| Retour arrière impossible après la première navigation | Enregistrer aussi la sélection initiale après la fin du chargement de l’historique. |
| Un élément masqué revient dans le débordement | Appliquer le même prédicat de visibilité aux hôtes inline et aux éléments du popup ; garder IsVisible propre aux commandes conditionnelles. |
| Un tri ou une option de label perd le focus | Les options visuelles actualisent les contrôles existants. Seules les options nécessitant de nouvelles données relisent Git. |
| Alt+flèche navigue puis sélectionne Start au relâchement | Le handler du tunnel de la fenêtre marquait la touche traitée avant le preview AccessKeyHandler, qui ignorait alors le chord. Traiter les gestes Alt dans le tunnel enfant ; vérifier aussi les événements des modificateurs et le rendu X11 réel. |

## Inventaire des fenêtres

| Piège | Cause / règle retenue |
| --- | --- |
| Le nombre de fichiers Form*.cs donne un faux total | Fusionner les classes partielles/Designer et suivre l’héritage Form ; inclure les noms sans préfixe Form, plugins, askpass C++ et fabriques dynamiques. |
| FormSettings semble porté alors que ses pages manquent | Évaluer séparément chaque page et les six paramètres CI, sans compter les bases techniques. |
| Un prompt ou un lien externe devient une fenêtre « complète » | Distinguer N1 substitut minimal, N2 partiel, N3 fonctionnel ciblé et N4 parité entièrement auditée. Aucun N4 attribué au recensement du 16 octobre. |
| Un test voisin certifie abusivement l’interface d’un plugin | Lier le vrai scénario et son type de preuve : tests UI Desktop ou moteur Plugins. Un test de moteur ne valide pas sa fenêtre. |
| Tests ciblés déjà passés affichés « Non exécuté » | Distinguer « hors suite complète de référence » et « jamais exécuté » ; préciser la source et le périmètre dans les travaux en cours sans relever leur niveau. |
| Une résolution de conflit fichier est assimilée à celle d’un sous-module | Ne pas appliquer Base/Ours/Theirs de fichier à un gitlink 160000 ; diriger vers l’assistant dédié SubmoduleConflictDialog et le dépôt enfant (ADR 0071). |
| Le contrôle d’un CSV LF/CRLF échoue immédiatement après génération | Générer les CSV avec un terminateur LF explicite pour que le contenu relu soit identique sur Linux. |

## Suppression distante

| Piège | Cause / règle retenue |
| --- | --- |
| Nettoyer des branches locales après un échec d’une autre remote | Conserver chaque résultat atomique et ne proposer que les branches locales des remotes réussies ; elles gardent un dialogue de suppression distinct. |
| Supprimer une autre branche ou un serveur changé après confirmation | Inverser un refspec de branche unique ; revalider destination, configuration et hash, puis utiliser un lease explicite. Ne pas deviner les mappings ambigus. |
| L’annulation ressemble à un rollback distant | Après réception serveur, le résultat peut être inconnu. Conserver sorties/état partiels et demander une relecture des références. |
| Fermer le propriétaire termine le process mais laisse son propriétaire ouvert | Avalonia peut interroger les enfants avant le propriétaire ; garder la demande de fermeture et la rejouer après drainage. |
| Faux échecs Askpass/Rebase en lançant les tests Core par DLL | Ces scénarios relancent Environment.ProcessPath comme enfant ; lancer l’apphost compilé ou dotnet run, jamais dotnet Tests.dll pour la suite complète. |
| Le nettoyage local réutilise une liste antérieure au bouton OK | Relire les branches/configurations actuelles des seules remotes réussies ; une branche reconfigurée ou recréée sans tracking ne doit pas rester sélectionnée. |
| Un premier retry après Abort échoue sur une branche déjà supprimée | Recharger aussi la sélection après annulation du seul Process, en retirant les succès prouvés ; aucune continuation vers le nettoyage local. |
| Preview de suppression réduit aux seules premières URL fetch | Git pousse sur toutes les URLs sans pushurl explicite. Le faux test sans push réel masquait une suppression sur deux serveurs ; résoudre les destinations natives et leurs rewrites avant toute mutation. Régression rouge puis verte conservée. |

## Éditeurs des règles du dépôt

| Piège | Cause / règle retenue |
| --- | --- |
| Perdre l’undo ou la saisie en changeant de palette | Modifier le document partagé ; ne réaffecter son texte que lors d’un vrai chargement de fichier. |
| Ajouter un motif après une sauvegarde refusée | Arrêter avant l’ouverture/rechargement de l’assistant ; buffer et erreur restent accessibles. |
| Nouvelle cible écrasée ou lien cassé lors du save | Valider identité/contenu/chemin et écrire sur le handle contrôlé ; ne pas remplacer le fichier par rename. Écriture non transactionnelle face aux erreurs disque. |
| Fichier déjà writable via groupe refusé par chmod | Tenter l’ouverture writable avant toute modification temporaire du mode propriétaire. |
| Motif littéral devenant wildcard, ou négation réordonnée | Échapper les chemins sélectionnés ; conserver ordre/casse/doublons des motifs saisis, séparer les sorties Git par NUL seulement. |
| Aperçu annulé encore actif ou masquant une erreur Save | Drainer les acteurs précédents avant publication, fermeture et append ; aucune sortie d’aperçu tardive ne doit effacer l’échec d’écriture. |
| Cadre des motifs traversant le texte, compteur trop haut | Utiliser le cadre à légende contraint partagé avec Pull ; le StackPanel mesurait le contenu sans borne. Tester les vrais viewports et les rectangles du Designer en plusieurs tailles. |
| Menu vide au premier clic / Close appelé deux fois | Peupler le ContextMenu avant Opening ; rendre la fermeture après drainage idempotente. |
| Cancel ferme quand même à l’action suivante | Consommer la demande de fermeture différée avant Close ; restaurer le choix de codec si le rechargement est refusé. |

## Remotes — résolus

| Piège | Règle retenue |
| --- | --- |
| Remotes team et team.foo déplacés ensemble | Identifier exactement la sous-section Git ; un préfixe de clé n’est pas un nom de remote. |
| Copies de config privées devenues lisibles pendant Save | Créer copies et locks en 0600 avant toute écriture ; restaurer le mode cible à publication. |
| Tracking hérité vidé fait échouer Git natif | Git 2.53 segfault sur branch.remote vide avec upstream ; refuser cet effacement avant mutation. Le merge hérité est aussi une liste non masquable localement. |
| Save perdu pendant Validated, champs/snapshots périmés | Sérialiser les écritures sans désactiver l’éditeur ; conserver drafts et erreurs distinctes, renouveler tous les guards seulement sur la révision publiée, recharger les refs après Fetch. |
| Premier Alt sans focus, changement d’onglet vide | Donner un focus réel aux listes et rétablir une cible visible après changement de page ; Label.Target porte les touches d’accès. |
| Couleur ou bouton Save réagissent un événement trop tard | Observer TextProperty immédiatement ; garder le véritable geste utilisateur dans les tests. |
| Effets attendus artificiellement par le harnais | Comparer au Git réel : ordre des includes, remote imbriqué existant, upstream futur et refs déjà fetchées. Relâcher Enter sur le propriétaire revenu après fermeture du prompt. |


## Worktrees — résolus

| Observation | Cause / suite |
| --- | --- |
| Switch accepté mais Browse reste sur le dépôt initial | Le modal garde MainWindow busy ; retourner le chemin, fermer puis basculer après ce scope. |
| Double-force autorisé après une lecture status propre | Cette lecture ne protège pas une écriture concurrente ; un verrou exige aussi le consentement Discard avant tout remove natif. |
| Menu worktree construit busy reste désactivé lors d’Open(control) | Avalonia ne passe pas par Opening dans ce chemin ; recalculer aussi dans Opened, avec les mêmes gardes. |
| Colonnes de données dérivent de 1 px chacune | Un Grid de ligne arrangé sur la largeur avec gouttière redistribue les pixels ; borner sa largeur à la somme exacte des colonnes comme le header. |
| Abort test clique OK après fermeture du Process | Abort ferme après drainage ; acquitter uniquement un modal encore visible, puis vérifier les acteurs et l’état Git réel. |


## Ajout de sous-module — résolus

| Observation | Cause / suite |
| --- | --- |
| Branches proposées depuis un autre dépôt que celui cloné | Une URL relative se combine à la dernière URL brute du remote avant insteadOf ; `ls-remote` peut aussi réinterpréter un dossier comme un remote. Utiliser une définition `-c` éphémère après résolution native. |
| Destination LF supposée réussie alors que le dossier existe | Git peut cloner puis refuser la clé de sous-module. Conserver le vrai code retour et le clone partiel ; existence du dossier ne signifie pas succès. |
| Tab ne suit pas Source → Browse | L’ordre d’ajout aux conteneurs diffère du Designer ; conserver les TabIndex Windows explicites. |
| Tout le texte sélectionné après clic de suggestion | ComboBox sélectionne au GotFocus ; rendre le focus avant de terminer la sélection/caret d’acceptation. |
| Un vieux message de branches ou « Add submodule… » persiste après la fin | Effacer l’erreur lors d’un retry de la même source, et afficher l’état réellement terminé quand Process est fermé sans OK. |
| Une seconde valeur de tracking validée pendant Save disparaît sans erreur | Le Reload final hors file prenait IsBusy avant l’écriture suivante. Réserver le Reload dans la même file avant RunAsync, y compris les validations provoquées par la perte de focus. Conserver le rafraîchissement Initialize de Windows, même sans champ modifié. |
| Le test Save lit une seule des deux clés après !IsBusy | IsBusy peut être faux entre deux écritures en file. Attendre la ligne complète attendue, puis vérifier la configuration native ; aucun clic Save supplémentaire. |


## Gestion des sous-modules — résolus

| Observation | Cause / suite |
| --- | --- |
| Update/Sync changent aussi le module voisin | Le panneau générique appelait les API globales. Capturer et revalider le snapshot ; Update récursif sélectionné, Sync sélectionné sans récursion. |
| Branch diffère de Windows ou un module inactif semble modifié | Le seul describe --all --always et la présence de .git ne reproduisent pas submodule status. Reprendre le vrai statut natif par chemin littéral ; inactif affiche l’index, fichier sale seul reste Up-to-date. |
| Un vrai tag nommé tags/release apparaît comme release | La normalisation du résultat describe --all ne convient plus à une annotation native status. Conserver toute l’annotation sans retirer un préfixe de nom littéral. |
| Conflit de gitlink absent ou faux commit zéro | La lecture de retrait exige un stage0 et la regex Windows perd certaines lignes. Fournir un relevé dédié tolérant U, sans autoriser Remove ni inventer un SHA. |
| Fermeture suspendue pendant relecture, ou Browse reste ouvert après drainage | CancellationToken.None dans le post-refresh ignorait Close ; propager le token extérieur et rejouer la fermeture propriétaire après drainage. Abort Process seul laisse relire les effets partiels. |
| Browse ignore une tentative Remove dont la restauration IO échoue | Marquer conservativement la tentative autorisée avant publication ; toujours demander la relecture parent. No ne marque aucun changement. |
| Ctrl+C diffère pour un dépôt à un seul module | AutoHeader WinForms ajoute les en-têtes lorsque toutes les lignes sont sélectionnées. Garder valeurs complètes et règles TAB/CRLF natives, avec largeurs/défilement communs aux entêtes et cellules. |


## Conflits de sous-modules — résolus

| Observation | Cause / suite |
| --- | --- |
| Current prend le HEAD parent, ou Stage Current supprime le gitlink absent | Git découvre le parent depuis un dossier vide ; add d’un chemin absent résout par suppression. Exiger un vrai dépôt enfant distinct, sans dépendre de .gitmodules. Delete reste un choix explicite séparé. |
| Add/add ne propose pas de branche, ou une branche ne contient qu’un côté | Windows active sur Base/Remote mais filtre Local/Remote. Exiger Local ET Remote, puis l’intersection de deux contains et la ref/hash annoncée encore valide. |
| Un checkout natif réussi puis annulé stage quand même le parent | LastResult est rempli avant les prompts imbriqués. Attendre Succeeded après tout le workflow et relire Current ; Cancel ne stage rien. |
| Pull, Stash ou Apply Patch traitent un gitlink comme un fichier | Constructeurs directs de PatchConflictDialog. Routage partagé selon les stages réels, gardes moteur et refresh du parent après retour ; aucun double add ni commit/Continue implicite. |
| Close reste bloqué après un add ou un hook déjà exécuté | Les vérifications postmutation doivent garder le token annulable et drainer les acteurs. Conserver les effets natifs, jamais fabriquer une réussite ni restaurer silencieusement l’index. |


## Askpass — modes natifs

| Piège | Intention conservée |
| --- | --- |
| `confirm` avec stdout vide et exit 0 autorise l’usage d’une clé | No/Cancel/fermeture échoue ; une notification `none` n’est pas une demande d’autorisation. |
| `(yes/no)` dans une URL/clé, ou nom SSH commençant par Username | Reconnaître les vrais formats Git/SSH et retries de passphrase avant les marqueurs de question ; inconnu reste masqué. |
| Empreinte vide + Entrée déclenche le Yes par défaut | Intercepter Entrée dans le champ et ne soumettre qu’une ligne non vide ; OpenSSH valide l’empreinte exacte, MD5 compris. |
| Fixture helper absente malgré un DISPLAY privé | Les clés d’environnement nulles sont encore des overrides explicites pour le runner. Les omettre réellement pour tester le fallback ; restaurer les variables isolées. |
| Clic de test après un état d’erreur et avant publication du rendu | Réutiliser le pilote partagé qui stabilise les frames puis garde le hit-test strict ; ne jamais remplacer le clic par RaiseEvent. |

## Aller à un commit — résolus

| Piège | Règle retenue |
| --- | --- |
| Deux listes indépendantes se bloquent malgré --no-optional-locks | Éviter RunAsync, qui prend le verrou applicatif des mutations ; utiliser les lectures natives bornées. |
| Le champ affiche B tandis que Go valide A | Figer les trois sources dès validation ; refuser aussi une proposition tardive du presse-papiers. |
| Vrai clic sur la flèche provoquant une récursion fatale | Attendre la fin de PopupOpened avant de remplacer le popup natif ; garder les contrôles de fermeture et restituer saisie/sélection. |
| Annulation manquée en rouvrant le même chemin | Annuler la séquence explicite au changement de contexte, indépendamment du texte ou de l’identité du chemin ; vérifier le token avant application. |
| Un popup partagé reste plafonné après agrandissement | Garder le plafond MRU pour les chemins ; les références GoTo suivent la largeur réelle du champ. |
| Headless vert, mais fermer la modale par la croix renvoie vers Start sous Openbox | Restaurer explicitement le focus de la grille après retour et drainage du dialogue, uniquement si le contexte reste valide ; conserver la preuve native rouge puis rejouer le paquet corrigé avec un vrai gestionnaire de fenêtres. |
| Première touche consommée pour activer une modale déjà visible | Mapper la fenêtre et focaliser son champ logique ne prouvent pas l’activation OS. Activer au passage Loaded, après rendu et avant saisie, sans refaire le choix du champ ; vérifier la toute première frappe sous un vrai gestionnaire de fenêtres. |
| PID masqué dans le bac à sable pris pour un acteur arrêté | Observer les PID/start-times dans le même environnement que le lancement ; attendre le vrai résultat avant toute relance. |

## Sélecteur rapide de références

| Piège | Règle retenue |
| --- | --- |
| Déduire le peeling des tags depuis le seul caractère `*` | Comparer au format Windows exécuté par le Git installé ; ne pas forcer une profondeur supposée. Conserver aussi le hash brut pour identifier le choix. |
| Suppr sur une HEAD distante isolée perd son choix | Conserver le nom initial et expliquer l’absence de mapping supprimable, sans relâcher les gardes Core. |
| Événement de relâchement envoyé au TopLevel déjà fermé | Le sélecteur ferme synchroniquement ; le pilote headless cesse d’adresser ce TopLevel détruit. Garder les vrais gestes et le hit-test strict. |
| Confondre relation modale et propriété IsEffectivelyEnabled headless | Vérifier owner et tâche modale dans ce moteur ; l’activation/désactivation OS relève du test natif. |
| Réouverture supposée sélectionner HEAD | Browse conserve le commit sélectionné ; préparer explicitement la cible de la fixture. |
| Sélecteur vert mais premier Échap du dialogue métier perdu | La vraie frappe, après chargement complet, est l’oracle : le candidat 10d889d9b est refusé. Partager l’activation Loaded dans le socle RepositoryDialog, sans refocaliser de contrôle et sans activer un propriétaire d’une autre modale. |

## Rattachement des tags annotés

| Piège | Règle retenue |
| --- | --- |
| Un tag annoté existe dans le sélecteur mais manque à la grille | Séparer le hash brut de la cible d’affichage et partager l’expression native Windows ; ne pas remplacer l’identité utilisée pour la revalidation. |
| Les badges changent mais le tooltip garde les anciens tags | Actualiser le tooltip au même point de présentation, y compris sans aucune référence restante. |
| Une capture réécrite fait croire que le runtime testé a changé | Capturer explicitement DLL/PDB/SO/XLF, deps/runtimeconfig et helpers ; lister captures/traces comme sorties. Garder le lot brut et sa comparaison fausse, sans réécrire son résultat. |

- Clic droit sur une révision : sélectionner un élément ne lui donne pas automatiquement le focus Avalonia. Focaliser sa ligne avant le menu, même déjà sélectionnée ; ne pas réparer le test par clic/Ctrl+1. Deux origines natives validées sur 189.

- Première frappe de Create branch : Opened focalise le champ, puis InitializeAsync/RunAsync le désactive et perd le focus. Préserver le vrai contrôle suivi et le restaurer sous gardes après UpdateActions. Une vraie lecture Git retardée et la première frappe native sont nécessaires ; attendre seulement une fenêtre non busy peut précéder le début de son initialisation.

- Retour au navigateur : les champs d’un dialogue corrigés ne prouvent pas le retour après Cancel. Sur 189, Up sélectionne Start puis Down l’ouvre ; conserver ce rouge et auditer séparément MainWindow.RunAsync et le remplacement des lignes au refresh.

## Saisies de scripts

| Piège | Règle retenue |
| --- | --- |
| Le picker ferme le propriétaire avant lecture du token CTS | Capturer le token avant l’appel ; ignorer les réponses tardives et exposer leur drainage. L’API native n’accepte pas d’annulation, son arrêt reste à auditer. |
| Échap intercepté partout dans SimplePrompt | Garder le handler sur txtUserInput comme Windows ; Échap sur OK ne ferme pas la fenêtre. |

## Retour clavier après opération Browse

| Piège | Règle retenue |
| --- | --- |
| Réutiliser un conteneur de commit retiré par F5 | Garder dépôt/hash, puis résoudre la nouvelle ligne seulement si elle reste sélectionnée. |
| Le popup possède le focus au lancement, pas la grille | Transmettre explicitement l’origine pour les actions directes, dynamiques, partagées et leurs raccourcis. Ne pas restaurer à chaque Closed. |
| Le bookmark survit à la première opération et contamine la suivante | Consommer l’origine à la capture ; nettoyer par identité sans réinstaller un ancien scope. |
| Le filtre reste ouvert après Échap malgré un popup déjà fermé | Tracer la destination réelle de la touche. Ici le focus restait au propriétaire ; donner au filtre son premier contrôle interactif. La seule fermeture préalable du popup ne suffisait pas. |

## Préparation typée des scripts

- Défaut UserInput terminé par des espaces : le remplacement générique trimait son nom et ne consommait plus le jeton. Remplacer le jeton littéral ; deux régressions bornées conservent les refus/annulations et les autres variables.
- Ordre des demandes : UserInput précède UserFiles, même si le texte place UserFiles en premier. Les fixtures doivent suivre ce contrat hérité, sans changer silencieusement le parseur.

## Runtime des scripts — points à conserver

| Piège | Règle retenue / limite |
| --- | --- |
| Reconstituer argv depuis Quote, non injectif | Conserver les fragments RAW et valeurs doubles opaques avant Quote ; l’aperçu historique n’est pas un sérialiseur. |
| Splicer les fichiers dans le programme bash -c du modèle VS Code | Reconnaître le shell explicite et garder un seul programme auteur ; citer les doubles marqueurs nus par valeur. Quotes internes refusées, modèles inchangés. Le RAW des tests -f/-d garde sa limite avec l’apostrophe. |
| Fixture « custom » sans révision courante | La garde héritée c*/HEAD s’applique aussi aux providers ; donner les vrais faits au fixture, afficher Error/Option, sans affaiblir la production pour faire passer le test. |
| Background Started pris pour un succès final | Started suit le vrai exec ; garder la Task possédée jusqu’au wait/drain. Fermer le propriétaire scelle les continuations et annule ses acteurs connus. |
| Appliquer After uniquement après exit0 partout | Garder les frontières Windows propres au checkout révision, merge/conflit et Pull/reprise/autostash ; pas de nouvel événement Clone ni de rollback implicite. |
| Un batch distant réussi efface une suite veto/échouée | Prévalider tout, revalider hash/URL/leases par batch, conserver les succès précédents et les résultats partiels. Mutex par instance ≠ transaction avec tout éditeur externe. |

Le full01 réel de cette tranche rejette 20 cas, [classifiés par Root](../../artifacts/linux/scripts-runtime-20261018/full-01/root-cause-review.json) :

| Cause | Cas | Correction / oracle conservé |
| --- | ---: | --- |
| Menu Scripts Commit remplace index/règles | 11 | Composer le menu existant et ses commandes ; ne pas retirer leurs assertions. |
| Ancien snapshot du menu révision efface les ajouts tardifs | 1 | Modifier seulement la contribution Scripts, préserver les autres contributeurs. |
| Deux overflows partagent la même identité | 2 | Identités qualifiées, sans changer les commandes ni les attentes de disposition. |
| Ancien test choisit l’unique toolbar par type | 1 | Cibler la toolbar Repository par son identité. |
| Cancel de récupération empêche les options Push | 3 | Distinguer choix de récupération et arrêt owner/token ; restituer les options comme Windows. |
| Question de conflit Pull nouvelle puis erreur native perdue | 2 | Vrai geste négatif conforme Windows, puis erreur Git d’origine conservée après After ; aucun replay à Abort. |

Les identités historiques restent présentes. Les seules adaptations d’oracle portent sur le catalogue Scripts dynamique, la toolbar qualifiée et les deux réponses explicites au nouveau parcours Windows ; aucun assert métier n’est retiré pour masquer un rouge. Les ciblés06 71/71 et07 81/81 recouvrants restent séparés du [full02 accepté sur 19bb](../../artifacts/linux/scripts-runtime-20261018/full-02/parent-verification.json) : 1833/1833 Desktop, 689/689 Core, 35/35 Plugins, aucun cas retiré. Le full01 rejeté reste attribué à 135729236 ; son rouge ne devient pas un succès historique. Les captures01–03 nommées Dark étaient Light ; le ciblé04 affirme le thème réel, sans valider son lot globalement ni créer une preuve native. Les détails de validation restent dans les JSON et le [TODO](todo.md#validation-courante--scripts-runtime).

Le [premier lecteur de traces S01](../../artifacts/linux/scripts-runtime-20261018/kernel-read-01/report.json) reste refusé : il décodait l’argv du helper avant de filtrer l’exécutable Python, et butait sur son PATH tronqué par strace. Le [second lecteur](../../artifacts/linux/scripts-runtime-20261018/native-root-kernel-review.json) vérifie les six chaînes sur les mêmes traces. Filtrer d’abord le chemin complet, puis exiger l’argv Python exact ; ne jamais reconstituer un argument tronqué ni confondre cette erreur d’analyse avec un échec applicatif.

## Comparaisons et listes partagées

| Piège | Règle retenue |
| --- | --- |
| Compare garde un ancien aperçu après choix d’un parent ou d’une multisélection | Contrat Compare uniquement : une feuille fichier unique ouvre les vues fichier ; Range utilise son lecteur dédié. Une racine parent ne sélectionne pas implicitement son descendant. |
| Browse conserve prompt et identité pour un groupe ordinaire filtré sans enfant | Vider ce groupe ordinaire seulement ; conserver les résumés de dossiers, prompts de groupes non vides et le RangeDiff affichable sans fichiers. |
| La première flèche ne navigue pas après clic de ligne ou action de mode | Le Border interne ne prend pas le focus ; après l’action de mode ou son popup de modes, reprendre l’origine valide une seule fois. Aucun repli général à chaque popup ou reconstruction ; la saisie garde son éditeur. |
| Un clic abandonné détourne une action clavier ultérieure | Désarmer l’origine pointeur lorsque le clavier prend la suite ; ne pas réutiliser un geste qui n’a pas produit d’action. |
| Le survol reprend les couleurs Fluent malgré le thème historique | Ressources locales sur le présentateur réellement peint, dans les deux thèmes. |
| Un fichier BASE→A est lu avec la paire A→B du formulaire | Chaque groupe transporte sa paire typée ; identité de lecture, blobs, enregistrement et outil fichier suivent le groupe choisi. L’outil de dossier suit la paire effective du formulaire. |
| Range dérive avec HEAD ou passe pour un fichier | Capturer les vrais commits et lire Range séparément ; ni vues/actions fichier ni filtre de contenu hérité de la liste. |
| Browse et Compare appliquent des valeurs ShowAll différentes | Préférence globale activée par défaut et relue dans le calcul affiché ; pas de surcharge propre à FormDiff. |
| Settings perd filtre, caret/sélection ou clavier après Escape | Ce menu Settings rend l’origine filtre/liste encore valide après action ou Closed, y compris Escape. Gardes locales de comparaison, filtre et focus ; aucun retour général à chaque popup ou rebuild. |

Périmètre et preuves : [TODO](todo.md), ADR 0016/0080. Une validation ciblée ne certifie pas toutes les variantes ni la parité FormDiff.


## Menus de fichiers et validation

| Piège | Règle retenue |
| --- | --- |
| Première Échap de Commit enfant ou Rename sert seulement à activer la fenêtre | Activer après mapping avant la première saisie, partager ce comportement et laisser l’initialisation mémorisée au constructeur du Commit. Une entrée native réellement livrée est requise ; Focus de test ne valide pas cette frontière. |
| Deux fichiers comparés dans le sens inverse ou sélection perdue après outil | SelectedItem n’est pas le focus. Conserver la dernière ligne réellement focalisée encore présente/sélectionnée ; bookmark local à l’action et restauration de tous les tags sélectionnés après relecture. |
| Show in File tree affiche le bon fichier mais Up atteint Start | Le changement d’onglet doit sélectionner puis focaliser la ligne cible après layout ; ne pas focaliser le TreeView avant la sélection ni restaurer l’ancien Diff caché. Le full 9ced vert ne couvrait pas ce geste natif. |
| Clic droit sélectionne un fichier mais garde l’ancien diff tant que le popup reste ouvert | La lecture ciblée ne doit pas attendre Closed ; elle actualise le viewer sans désactiver ni reconstruire la liste. Première flèche après fermeture conservée. |
| Reset ne décrit que First/Second sans la révision réellement restaurée | Partager la description des endpoints entre menu et confirmation ; les groupes BASE ne suivent pas les endpoints globaux du formulaire. |
| Première flèche après retrait d’un flag atteint la toolbar | La ligne du clic droit peut ne pas être sélectionnée au PointerPressed tunnel. Mémoriser le vrai conteneur puis capturer son identité à Opening après sélection ; consommer l’origine par action, invalider au clavier/à la fermeture, sans Focus préparatoire. Correctif a0ba revu selon le TODO ; rouge natif d15 conservé. |
| Ignore/Exclude restent grisés dans le premier popup | La sélection lance une lecture qui rend Commit occupé ; réévaluer les commandes au retour disponible, sans fermer/rouvrir le menu. Correctif a0ba revu selon le TODO. |
| Skip/Assume masque le fichier et sa commande inverse | Route Commit → Unstaged → Settings, puis menu de la ligne révélée ; aucun faux changement ni staging. File tree historique et ShowSkip de FormDiff Windows à deux révisions restent distincts. d15 a rétabli les bits, mais ses deux premières Down sont rouges ; correctif a0ba revu selon le TODO. [Preuve d15](../../artifacts/linux/snapshots/d15ec670b/proofs/native-index-flags-parent-verification.json), ADR 0081. |
| Métadonnées de menu relisant tout l’arbre à chaque commit | Lire les modes Git sur les chemins littéraux concernés ; garder l’assertion de non-rechargement et ignorer les descendants hors sélection exacte lorsqu’un gitlink devient dossier. Les flèches ne construisent pas de menu. |
| Outil personnalisé refusé ou éditions perdues | Git refuse `--gui` avec `--tool`. Les côtés Commit/Index sont des copies privées ; WT existant reste vivant et éditable. Le code natif 1 de `--no-index` est attendu. |
| Reset applique chaque source à tous les fichiers | Partitionner par source et refuser un même chemin avec deux sources ; l’opération Index→WT conserve l’index ; sa relecture UI peut rafraîchir les seules données stat, sans changer OID/modes/flags. Garder les deux observations distinctes. R vise seulement First ; Cancel reçoit la première touche dans la confirmation. |
| Console bloquée ou résultat terminé transformé en annulation | Acquitter avant Git, attendre les vrais acteurs, sonder l’entrée de façon annulable et conserver le résultat Git déjà obtenu. `Reset chunk` appelle `checkout --patch`. |
| TMPDIR privé trop long | Le rouge AF_UNIX antérieur reste conservé. Git et la console partagent désormais un répertoire privé dont le chemin complet tient en 107 octets UTF-8 ; repli sous `/tmp` pour la seule longueur, sans changer TMPDIR. Reproduction rouge conservée, puis suites complètes et paquet exact `dc85753b1` vérifiés ; annulation drainée et nettoyage du repli contrôlés (ADR 0084). |
| Openbox sans thème dans le banc natif | Conserver le share du thème et les diagnostics de préparation ; un démarrage sans dépôt/cadre WM n’est pas un parcours métier réussi. La correction des sockets ne valide pas cette préparation. |
| Lecture xwininfo concurrente à une fermeture ou calibration périmée | Conserver les vrais diagnostics du banc ; une lecture BadWindow se réessaie sans modifier le focus. Une calibration invalide est refusée avant saisie, sans tuer l’application ni consommer la première touche. |
| Suite Core exécutée depuis une copie déplacée ou via sa DLL | Le banc utilise son apphost dans le répertoire bin canonique : recherches de sources relatives et scénarios de relancement en dépendent. Conserver les tentatives rejetées ; ne pas modifier les assertions. |
| Publish RID modifie les locks d’une suite en cours | Publier dans un checkout isolé ; la restauration RID peut ajouter des cibles aux locks. Conserver les différences, vérifier les identités des dépendances et restaurer seulement les octets générés. Les binaires de test restent figés. |

Logs et essais rejetés : `artifacts/linux/comparison-file-menus-20261017/`, paquet et preuves de livraison dans son snapshot. Les outils Xvfb et D-Bus du banc restent privés ; leur absence du PATH ne constitue pas un défaut produit.

## Tri et priorités

| Piège | Règle retenue |
| --- | --- |
| Lanceur natif long interrompu après les gestes UI | Application et Openbox ont fini à 0, mais le lanceur a renvoyé 143 avec finalisation incomplète sur 0ac : ne pas reconstruire les fichiers manquants ni valider le redémarrage non joué. Garder ce banc partiel, puis refaire un parcours court sur le paquet courant avec vrais retours et drainage. |
| Premier clic de champ Settings sans focus | L’aide de l’éditeur recouvrait le champ voisin sur 0ac. Garder les aides sur leurs trois icônes comme Windows ; deux premières saisies et aide positive vérifiées sur 5a52. Le second clic initial reste un geste réparé, conservé dans les preuves. |
| Décocher un tri impose encore `--date-order` | GitDefault Windows n’ajoute aucun flag global ; garder les options explicites du filtre et les nombres JSON 0/1/2. |
| Le premier `--all` est pris pour HEAD ou pour la première ligne | Une opération se vérifie sur HEAD explicite ; la grille passe par le graphe Windows, qui peut repousser un parent reçu avant un enfant tardif. Vérifier l’ordre brut, l’ensemble des commits et l’ordre visible séparément. |
| Le panneau réalphabétise la sortie native ou forme trop tôt ses dossiers | Trier avant les limites, appliquer les priorités aux branches complètes avant regroupement et garder la stabilité de chaque niveau. |
| OK Settings sauvegarde toute la vue de session | Relire les défauts du disque et ne remplacer que Sort ; les autres choix View attendent Save as default. |
| Un motif invalide reste caché derrière une première correspondance | Valider tous les motifs avant toute sauvegarde ; erreur lisible et saisie conservée, matching borné et captures nommées ExplicitCapture gardées. |

Preuves et limites : [TODO](todo.md), ADR [0082](adr/preferences/sorting/0082-native-reference-sorting.md). Full01 et ciblés rejetés sont conservés ; aucun audit natif ancien n’est transféré.


## CommitInfo — diagnostics et fermeture

| Piège | Règle retenue |
| --- | --- |
| Une lecture passive efface le diagnostic d’une autre commande | Attribuer l’erreur à sa lecture ; CommitInfo ne peut effacer que l’instance qu’il a lui-même publiée. Les échecs de filtre, Fetch, références et Undo du full01 EA6 restent conservés ; correctif couvert par le ciblé Root puis le Full02 b356 vert ; ce résultat ne vaut pas validation native. |
| La fixture restaure XDG ou supprime le dépôt avant la fermeture réelle | Demander Close puis attendre, avec pump/layout/render bornés, fenêtre effectivement fermée et lectures CommitInfo drainées. Une lecture Git bloquée vérifie cette barrière ; le timeout fait échouer la fixture au lieu de valider la fermeture. |
| `fonts:SystemFonts` absent dans les lots communs 02/03 | Conserver ces rouges sans dispense. Le lot commun 04 vert n’établit pas leur cause exacte ; ne pas généraliser une exécution isolée à la suite complète. |

Le [Full01 EA6 refusé](../../artifacts/linux/commit-information-20261017/full-01/desktop-result.json) conserve ses sept échecs Desktop ; le [Full02 b356](../../artifacts/linux/commit-information-20261017/full-02/parent-verification.json) est vert, avec assertions métier conservées.

Les refus des paquets [01](../../artifacts/linux/snapshots/b356d5f67/proofs/setup-source-reconciliation.json) (modes) et [02](../../artifacts/linux/snapshots/b356d5f67-package02/proofs/package-post-build-source-reconciliation.json) (locks), du premier checker de log et du checker04 restent conservés. Le [checker05](../../artifacts/linux/snapshots/b356d5f67-package03/proofs/log-finalized-parent-verification-05/package-parent-verification.json) détaille la réconciliation sans réécriture des logs et la portée privée du contrôle : les quatre processus protégés antérieurs ont une identité connue, mais leur environnement et leurs FD n’ont pas été audités ; aucun audit global de l’UID.

Le [natif accepté](../../artifacts/linux/snapshots/b356d5f67/proofs/native-commit-information-parent-review.json) couvre Browse anglais/clair/X11 seulement. Le refus003 a précédé toute entrée : il ne compte pas comme un clic réel supplémentaire. AT-SPI est indisponible sur les trois relevés ; aucune certification d’accessibilité, des autres hôtes ou variantes. ADR [0083](adr/ui/history/0083-shared-commit-information.md).

## Historique fichier Browse — présélection et publication

| Piège | Invariant |
| --- | --- |
| OID d’ouverture devient borne et cache les descendants | Séparer présélection et requête ; découverte des noms depuis HEAD puis graphe normal. Les chemins courant/ancien ne suivent pas les mêmes futurs renames. |
| Ancien commit renommé montre le premier autre fichier | Conserver la liste Diff complète puis choisir le nom historique dans le premier groupe ; cas avec bruit alphabétiquement premier. |
| Refresh ou grep d’Index/WT lit HEAD | Garder le vrai endpoint typé ; Current sans HEAD ne doit pas effacer les références de All. Refuser Reset depuis EmptyTree avant confirmation. |
| Nouveau Blame expose une identité mélangée ou une ligne périmée | Ancien document et identité restent ensemble jusqu’au succès complet ; navigation des anciennes lignes bloquée pendant la nouvelle demande, erreur courante vide l’aperçu. |
| Close retourne avant les lecteurs | Sceller puis annuler ; provider d’infobulle lié au lifetime et tous acteurs drainés avant Dispose/profil/dépôt. |

[Full01 14b89 refusé](../../artifacts/linux/file-history-browse-20261017/full-01/parent-failure-review.json) : 1460/1466 Desktop, six échecs conservés avec leurs entrées ; candidat non livrable. La validation de la correction `1e6cc163a` reste séparée : [Full02 vérifié](../../artifacts/linux/file-history-browse-20261017/full-02/parent-verification.json), [revue native parent](../../artifacts/linux/snapshots/1e6cc163a/proofs/native-file-history-parent-review.json). Les réglages partagés et la correction F5 de la paire retenue relèvent de l’ADR0086 ; les essais et la validation ADR0085 restent attribués à leur source.

## Réglages d’historique fichier — ADR0086

Conserver Exact/Simplify lorsqu’ils sont inactifs ; dans Browse en deux passes, réserver Full/Simplify au graphe et sous leurs conditions imbriquées. Après F5, repeupler CommitInfo depuis la sélection réelle de la paire retenue, pas seulement depuis le hint startup. [suites complètes vérifiées](../../artifacts/linux/file-history-options-20261017/full-01/parent-verification.json) et [paquet exact vérifié](../../artifacts/linux/snapshots/f4e99bec0/proofs/package-parent-verification.json) ; Git/headless seulement, audit GUI natif ouvert.

La [régression sur l’ancienne source](../../artifacts/linux/file-history-options-20261017/baseline-initial-pair-red/host-verification.json) et les [essais ciblés](../../artifacts/linux/file-history-options-20261017/targeted-parent-review.json) sont conservés séparément du full final. Un Reset vers First artificiel n’est pas une action du menu pour une sélection réelle ; une tâche de métadonnées annulée à la fermeture peut lever OperationCanceledException après drainage.


## Recherche incrémentale — ADR0087

| Piège | Intention conservée |
| --- | --- |
| `%s` + `%b` n’est pas le message complet | Garder `%B` séparément ; un collage peut traverser le sujet multiligne et le corps. |
| Un préfixe sélectionne un commit, puis le terme échoue | Garder le dernier résultat, comme Windows ; un négatif atomique se teste par collage. |
| Presse-papiers tardif ou Espace sur Index/WT | Une action plus récente invalide le collage ; Espace reste du texte sans Click de bouton. |
| Paire initiale artificielle ou retour au commit déjà sélectionné | Distinguer sélection logique et ListBox ; réduire la paire et restaurer le panneau adapté sans relancer l’historique. |

Les essais ciblés 01/02 ont corrigé des erreurs de compilation (extension clipboard/import et enum de touche) ; 03/04 des erreurs de scénario (préfixes et focus après clic d’onglet). Ils restent archivés séparément du ciblé 05 et des suites complètes. Les résultats et limites sont dans le TODO.


## Navigation parent/enfant — ADR0088

| Piège | Intention conservée |
| --- | --- |
| Revenir au premier parent après visite depuis le second | Deux piles Windows distinctes du retour Alt ; voisins du graphe chargé, pas découverte du dépôt. |
| Index devance les vrais enfants d’un ancien HEAD | Respecter le point d’insertion Windows dans l’ordre du lecteur, avant le réordonnancement du dessin. |
| Dernière sélection retirée ou F5 perdent l’ancre | Distinguer dernier clic Navigate et récence QuickSearch ; préserver le clavier sans modifier la multisélection. |
| Assigner SelectedItem égal à sa valeur dans une mise à jour groupée vide la sélection | Publier explicitement la sélection finale dans le modèle, sans état vide intermédiaire. |
| Focus liste conservé mais première flèche inactive | La liste stable porte le focus pendant la lecture, puis la nouvelle feuille le reçoit à publication seulement si ce focus appartient toujours à l’opération. |
| Première Échap ignorée par la cible invisible | Réutiliser l’activation modale commune et son bouton initial. |
| WT/Index ordinaire perd sa sélection typée à F5 depuis le diff Changes | Conserver la destination typée et le panneau de retour pendant le rebind de la ligne réelle retenue ; ciblé06 rouge puis les deux cas ciblé07 verts, pas une preuve native de F5. |

Les ciblés01–04 conservent les régressions et le scénario négatif invalide à critère blanc ; ciblé06 conserve les deux pertes WT/Index à F5 avant correction. Ciblé07 accepte 121/121 Desktop, Core8/8 et endpoints16/16 ; les six rendus frais ciblé07 sont relus par Root, avec leur rapport distinct de l’exécution et du natif. Le full final et le natif N01 sont des preuves distinctes ; aucune couverture native F5 transférée.

## Raccourcis — corrections et limites du périmètre ADR0089

| Piège | Cause / règle retenue |
| --- | --- |
| Apply change la touche active mais garde l’ancien texte dans la liste | Le template lisait le brouillon une seule fois et recevait la même collection. Recréer ses lignes, vérifier la touche réellement affichée après Apply/Clear. |
| Premier clic puis Next/Previous Commit perd le clavier | La lecture désactive l’arbre avant son focus natif. Conserver l’intention du clic et restaurer la feuille courante seulement si sélection, activité et intention utilisateur restent inchangées. |
| Remap Stash sur R/Échap absorbé par une action fixe | Résoudre le viewer focalisé et le contexte avant le fallback natif, comme ProcessCmdKey Windows. |
| Un test « retirement » lance un autre workflow | Effacer une affectation laisse les autres IDs disponibles. F8 est QuickPullOrFetch DANS Browse ; un enfant capable peut traiter son propre raccourci en priorité. Choisir une touche réellement libre ou configurer explicitement la collision. Ctrl+R est BASE dans Windows. |
| Reset filters perd le reflog et l’expression mémorisée | Désactiver le filtre de branches sans effacer son brouillon accepté ; conserver le mode reflog. Ses racines restent visibles avec une expression de branche, comme Git. |
| Gestes figés ou absents dans Branches/Worktrees, Fetch et aides Console/Filter | Raccorder chaque contrôle existant à son identité partagée, y compris overflow et racine ; Settings de la barre reste la configuration Git distincte. Save puis None ne recharge aucune donnée Git. |
| Comparaison Index utilise HEAD | Garder la destination typée et une BASE typée ; ne pas convertir une sélection artificielle vide en CurrentRevision. |
| Le full03 lit un PID askpass vide | Le worker de test publiait son marqueur avant la fin de son écriture. Écrire entièrement dans un fichier frère puis renommer sans écrasement ; garder les assertions de vraie annulation et de terminaison. Le rouge Core 674/675 est conservé et les suites finales doivent être fraîches. |
| Le teardown masque une assertion UI | Ne pas capturer ni envoyer KeyRelease au TopLevel détruit par Close/Échap. Les parseurs JSON peuvent émettre une sous-classe de JsonException ; vérifier le contrat d’erreur et les octets préservés. |
| F2 ou Delete de Grid déborde dans un éditeur de texte | Refuser RenameRef/DeleteRef lorsque le focus est un TextBox ou TextArea ; préserver l’identité saisie et ses gestes d’édition, sans commande Git implicite. |
| Une commande Browse avale le raccourci de son panneau de fichiers | Résoudre le child FilePane avant Browse/Grid, depuis son arbre, viewer ou popup ; fermer seulement son popup exact après acceptation d’une commande capable, pas avant le contrôle de disponibilité. |
| Next/Previous est refusé pour un RangeDiff reconnu en mode Auto | Reprendre la capacité de navigation partielle AutoNextPrevious et celle du vrai bouton ; un RangeDiff Auto reconnu reste navigable entre résumés, sans traiter ses hunks internes comme un patch ordinaire. |
| Les anciens oracles de tooltip attendent seulement la légende | Les hints affichent la touche actuellement affectée, y compris Ctrl+F, X et Ctrl+E ; vérifier légende traduite ET hint réel, sans retirer ce dernier pour faire passer l’ancien test. |
| Le F5 de fixture part d’un contrôle devenu invisible après changement programmatique d’onglet | Ouvrir Changes avec un vrai clic sur son TabItem puis envoyer F5 depuis son focus réel ; ne pas réparer le focus par appel direct pour masquer une erreur. |
| Le test du seuil SHA trouve déjà un message au premier caractère | Envoyer le terme de deux caractères en une seule entrée pour isoler le seuil SHA, puis saisir le troisième. Garder la recherche normale après chaque caractère dans les autres scénarios ; aucun rollback de sélection inventé en cas de non-match. |
| Sous-menu ParentChild : cible détachée dans full01 puis ciblé07 | Le chemin hover puis un seul clic strict sur la commande passe ciblé08 ; la cause exacte de la flake antérieure n’est pas prouvée. Conserver les rouges et ne pas affirmer qu’un clic sur hover ferme le menu dans la source Windows. |

## Langue — bootstrap et préférences récentes

| Piège | Règle retenue |
| --- | --- |
| Profil invalide lu avant Avalonia | Laisser le consommateur présenter son diagnostic ; ni sortie fatale précoce ni chooser qui réécrit le profil. |
| Ancien Browse/Settings écrase une langue récemment enregistrée | Relire latest pour les sauvegardes annexes ; distinguer le brouillon Language de sa valeur à l'ouverture. Pas de CAS interprocessus promis. |
| Traduction retirée transformée silencieusement en English par un autre changement | Conserver son identité, montrer un placeholder de repli sans fausse sélection ; remplacer seulement après un vrai choix. |
| Home+Enter depuis une combo sans sélection ne choisit rien | Le scénario doit activer un vrai item ; un clic unique strict remplace ce geste de test, sans setter ni refocus. |

Les [huit régressions initiales](../../artifacts/linux/language-preferences-20261017/worktree-preintegration/regressions-red/language.trx) donnent sept échecs et un succès avant correction. Le [premier broad corrigé](../../artifacts/linux/language-preferences-20261017/worktree-preintegration/corrected-broad/language.trx), 240/241, garde le geste de test invalide ; le [broad final](../../artifacts/linux/language-preferences-20261017/worktree-preintegration/final-candidate-verification.json) passe 241/241 avec les sept régressions corrigées. Le full1711 antérieur aux corrections reste historique ; il ne remplace pas le full final de la source intégrée.

## Polices — densité et flux natif

| Piège | Règle retenue |
| --- | --- |
| Police générale plus grande, métriques ou style ignorés | Utiliser la face réellement rendue et adapter ses hôtes ; conserver les minima compacts 18/22/20 lorsque la ligne tient, sans changer les oracles pour obtenir un vert. |
| Repli d’une famille absente enregistré comme nouveau choix | Garder l’identité demandée et le diagnostic ; seul un choix explicite remplace le rôle. Une sauvegarde sans rapport préserve le profil. |
| Sauvegarde partielle change certaines vues déjà ouvertes | Snapshot Fonts au début de la session ; aperçus et sauvegarde ne changent pas les consommateurs existants. |
| Arrêt natif pendant une lecture ou un style synthétique | Le backend exact conserve et libère le `SKStreamAsset` après la lecture ; ne pas supprimer des fontes/styles du catalogue. ADR0092. |
| Runner rejeté assimilé à un succès complet | Conserver le retour brut 1 du ciblé13-v2 ; la revue Root classe séparément les sorties générées, le VSTest 0 et les 482 succès. |

Les preuves brutes et leurs index [copy02](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-02/index.json), [copy03](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-03/index.json) et [copy04](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-04/index.json) conservent les essais de développement, leurs gates et sorties réelles ; aucune étape ne devient une suite complète par addition.

| Lot | Résultat et limite |
| --- | --- |
| 01/02/04 | Compilation avant tests refusée ; aucune validation UI. |
| 03 | **463/480**, 17 échecs conservés : densité, oracles JSON et infrastructure SDK. |
| 05/06/07 | **05 : crash après 70/481** ; isolé 06 7/7 sans expliquer 05 ; découverte 07 refusée avant tests. |
| 08 | **Crash après 283/481** ; pile `TryGetStream` au clic Bold/Italic réel, sans full accepté. |
| 09/10 | Compilation 09 refusée ; build 10 à 0 et examen des métadonnées, sans validation applicative. |
| 11/14 | Fragment de copie 11 refusé ; build 14 à 0 et copie complète contrôlée, sans nouveau test exécuté. |
| 12 | [Calibration](../../artifacts/linux/fonts-preferences-20261017/root-calibration12-review.json) : officiel **2×SIGSEGV** ; corrigé **2×256 exports**, sorties 0 ; Regular bornée seulement. |
| 13-v2 | [Ciblé](../../artifacts/linux/fonts-preferences-20261017/root-targeted13-review.json) : **VSTest 0, 482/482**, runner brut **1** conservé ; **47 sorties PNG** reclassifiées indépendamment par Root. |
| 15 | Diagnostic instrumenté **276/276**, runner brut **1** conservé ; 403 événements, aucune registration FontManager parent observée, 57 PNG et 1 JSON attribués aux Passed ; aucune reproduction du full01. |
| 16 | Setup inconcluant avant Close ; la présentation avait déjà été traitée. Aucun défaut post-Closing prouvé par cet essai. |
| 17/18 | Même fichier de test figé :17 atteint ses préconditions puis voit un repaint après Closing ;18 passe avec la garde centrale. La causalité SystemFonts n’est pas démontrée. |

Les [12 rendus revus](../../artifacts/linux/fonts-preferences-20261017/root-targeted13-render-review.json) restent headless ; les lineboxes fractionnaires des badges ne prouvent ni encre coupée ni confinement universel. [Syntaxe primaire b74bb4522](../../artifacts/linux/fonts-preferences-20261017/syntax-b74bb4522.result.json) : 2075 fichiers, 0 diagnostic. Full primaire02, paquet exact et natif borné acceptés séparément sur b74 ; limites dans le TODO.

Le [full primaire01](../../artifacts/linux/fonts-preferences-20261017/full-01/run-summary.json) de `95386c18d663430323752c650634f822a40edd4b` est clos en échec : **452/1777 Desktop**, 1325 échecs (1319 `fonts:SystemFonts`, 6 erreurs de thread), sortie réelle 1 ; Core **675/675** et Plugins **35/35**, sorties 0. Ce rouge reste conservé.

La frontière Cleanup seule (**32/32**) et le diagnostic instrumenté du préfixe (**276/276**, 403 événements) ne reproduisent pas ce défaut : aucune registration de FontManager parent observée. Le runner du diagnostic276 conserve son retour brut **1** ; 57 PNG et 1 JSON sont attribués séparément aux cas Passed. Ces essais bornés ne déterminent pas la cause du full01 et ne remplacent pas une suite complète.

Le négatif16 reste inconcluant : le helper avait traité la présentation avant Close. Le négatif17 atteint toutes ses préconditions puis observe **1 repaint de cellule après Closing** (build 0, test 1, 0/1 Passed). Le même fichier de test `CommitInformationIntegrationTests.cs`, SHA256 `dd7f43d3f7384081b966e460e58a2048d559f61ad0b39f7234e2fb64748d612e`, passe en18 après la garde centrale (build 0, test 0, **1/1**). Le commit `b74bb452223a125fdcc1ee052e18e827fcacd20f` intègre cette garde et renforce également le teardown de la fixture Cleanup ; le cas18 ne valide pas à lui seul ce teardown. La cause des échecs `fonts:SystemFonts` du full01 n’est pas définitivement attribuée.

Le [full primaire02 accepté](../../artifacts/linux/fonts-preferences-20261017/full-02/parent-verification.json) de `b74bb452223a125fdcc1ee052e18e827fcacd20f` est terminé : **1778/1778 Desktop, 176 classes**, **675/675 Core**, **35/35 Plugins**, build et trois suites avec sorties réelles **0**. Les identités comparées à la référence Language ajoutent **59 cas Desktop sans retrait** ; Core et Plugins n’en ajoutent ni n’en retirent. Sources et inputs runtime sont restés inchangés, aucun acteur marqué restant. La [référence de validation du snapshot](../../artifacts/linux/snapshots/b74bb4522/proofs/source-validation.json) atteste le full accepté ; le paquet exact et le natif borné sont désormais vérifiés séparément. La syntaxe b74 reste **2075 fichiers, 0 diagnostic**.

Le [paquet exact b74 vérifié](../../artifacts/linux/snapshots/b74bb4522/proofs/package-parent-verification.json) compte **35 403 640 octets, 442 entrées dont 386 fichiers** ; les cinq phases produit ont été réellement attendues à 0 et l’extraction complète concorde avec l’archive (chemins, types, modes, cibles et octets). Le [contrôle du backend livré](../../artifacts/linux/snapshots/b74bb4522/proofs/package-backend-verification.json) confirme la base du lock net8, onze bibliothèques de compilation officielles, l’identité/surface publique, les 35 IVT, les ressources, les bibliothèques natives et les notices/licences. Ces vérifications ne valent pas acceptation native.

Le natif attempt01 est conservé et **non accepté** : commande059 Ctrl+Alt+W refusée par l’allowlist du banc **avant injection**, sans échec applicatif démontré. Le cleanup explicite g02 par SIGTERM et le wait externe1 restent bruts ; ils ne deviennent pas une fermeture normale. Le [mémo d’incident](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-05/preparation/readers/gitextensions-fonts-native-attempt01-incident-20261017.md) décrit ce périmètre. [Attempt02](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-05/natif-attempt02/) est accepté séparément via la [revue Root](../../artifacts/linux/snapshots/b74bb4522/proofs/native-fonts-review.json), sans transférer les observations de01. Les corps et reçus de [attempt01](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-05/natif-attempt01/) restent distincts.

Copy03 ajoute Q15, ses préparations/rapports, une closure calibrée14 corrigée complète et la DLL officielle distincte ; copy04 conserve le complément de preuves Root. L’index [copy05](../../artifacts/linux/fonts-preferences-20261017/development-evidence/copy-05/index.json) lie les deux bancs natifs, le mémo et les préparations. Les copies ne constituent aucune nouvelle validation et leurs omissions/résolutions restent explicites dans chaque index.

Le natif attempt02 complète **77 reçus**, deux GUI à wait0, WM0 et wait externe0 ; Root revoit personnellement sept frames dont Worktrees. Douze checkpoints Git et un contrôle final avec Git stock concordent ; quatre intervalles Settings dédiés ne lancent aucun Git. Le fichier Fonts final est 490 octets, mode0600 ; les acteurs connus sont retirés et le payload reste exact à l’archive. L’opérateur délégué `/root/fonts_native_operator` est explicitement chargé des commandes019–077 ; la revue indépendante Root demeure distincte de cette conduite. Ce résultat borné anglais/clair/X11 ne détermine pas la cause SystemFonts antérieure et ne couvre ni tous les acteurs jamais observés ni toutes les variantes de rendu.

Le défaut post-Closing observé17 et corrigé18 ne doit pas être présenté comme une attribution définitive des 1319 erreurs SystemFonts du full01. Aucun reset global du FontManager ni fallback silencieux n’est revendiqué.


## Config outils — intentions à conserver

| Piège | Règle / limite |
| --- | --- |
| Confondre AutoComplete et ComboBox Windows | Garder flèche et catalogue d’une vraie ComboBox éditable ; le premier ciblé global rouge reste conservé. |
| Exit0 pris pour un changement de l’outil effectif | Une include peut encore gagner ; conserver le résultat natif et le readback disponible, sans fermer OK comme si le choix était garanti. |
| Veto de l’enfant occupé perd la fermeture du propriétaire | Retenir OwnerWindowClosing, annuler/drainer les acteurs connus puis relayer une seule fois, sans second Close réparateur. |
| Cancel pris pour un rollback après Apply ou erreur partielle | Abandonner les seuls brouillons non publiés. Conserver les reçus ; readback inconnu possible après annulation, aucune transaction multi-portées ni CAS interprocessus. |

[Full01 vérifié](../../artifacts/linux/tools-config-20261018/full-01/parent-verification.json) sur d975 ; paquet exact et natif T01 ciblé vérifiés. Le [ciblé01](../../artifacts/linux/tools-config-20261018/targeted-01-root-ownwait.json) conserve son résultat global rouge ; les rendus headless ne deviennent pas une preuve native. [Portée et limites](todo.md#configuration-des-outils-diffmerge--validation-ciblée).


## Config complète et encodages — intentions à conserver

| Piège | Règle |
| --- | --- |
| « Sans valeur » confondu avec Unset | Revenir au choix publié sans valeur restaure le brouillon ; seul Not set demande une suppression. |
| Exit128 pris pour un dépôt absent | Garder le stderr et, si leur lecture réussit, les valeurs raw locales ; aucune destination déduite ni édition si la découverte échoue. |
| Catalogue supposé exhaustif via GetEncodings | .NET 10 omet des codecs qu’il résout, dont GB18030. Complément borné, alias canoniques et codec strict conservé. |
| Favoris implicites dans un test de codec | Initialiser explicitement le profil isolé attendu avant les vrais viewers ; ne pas changer les codecs ni affaiblir les contrôles d’octets pour satisfaire le test. |
| Global pris pour la valeur effective | Local ou une include peuvent gagner après publication ; vérifier et conserver l’avertissement. |

Les ciblés UI01 **59/65** et UI02 **63/66** restent rouges et conservés dans `artifacts/linux/git-config-20261018`. UI03 **32/32** couvre les deux classes touchées, sans réexécuter artificiellement les autres cas déjà verts. La compilation initiale des nouveaux scénarios Core a aussi échoué avant correction ; ses logs restent conservés. Le [full01 refusé](../../artifacts/linux/git-config-20261018/full-01/parent-verification.json) conserve **1881/1889** réussites UI : huit anciens scénarios supposaient l’ancien menu fixe. Leurs profils explicites gardent toutes les assertions/interactions historiques ; les cinq classes corrigées passent **25/25** dans `legacy-encodings-01`. Le [full03](../../artifacts/linux/git-config-20261018/full-03/parent-verification.json) réexécute ensuite la suite entière, sans retrait de cas ni transfert des ciblés. Entre les deux, [full02](../../artifacts/linux/git-config-20261018/full-02/environment-rejection.json) est refusé : sockets interdites par le bac à sable, 0 test UI exécuté, Core 62/714 et Plugins 0/35 ; aucun résultat accepté ni modification produit. [Parcours natif source](../../artifacts/linux/git-config-20261018/native-source-01/root-review.json) limité à son scénario, sans certification du paquet interactif.

## Environnement Git et Checklist — intentions à conserver

| Piège | Règle / limite |
| --- | --- |
| Save/Rebase lit un ancien chemin après frappe | TextChanged Avalonia est différé ; observer TextProperty immédiatement. Le [ciblé UI01](../../artifacts/linux/git-environment-20261018/targeted-ui-01/parent-failure-review.json) reste rouge **128/130** ; corriger la production, conserver les oracles. |
| Un nouvel exécutable partage une séquence entre deux Git | Conserver le contexte aux frontières BeginOperation raccordées ; ne pas étendre cette garantie à toutes les lectures UI successives de ReadBrowserAsync, dont l’audit reste ouvert. |
| Corriger HOME modifie la session ou annule GIT_CONFIG_GLOBAL | Override limité aux enfants, choix hérité explicite et priorité native de GIT_CONFIG_GLOBAL ; aucun USERPROFILE Linux inventé ni `.gitconfig` exigé dans un HOME neuf. |
| Global hors dépôt découvre le cwd du lanceur | Employer un contexte privé hors dépôt, retirer ses redirections et résoudre les emplacements hérités relatifs depuis leur origine avant de changer de cwd. |
| Un clic sur la case publie les chemins encore saisis | Enregistrer uniquement CheckAtStartup et rebaser les autres champs sans perdre les brouillons. Save and rescan attend SaveAll réussi ; Cancel ne rollback pas les publications déjà acquises, y compris après échec partiel. |
| Diagnostic vert pris pour un test réseau ou d’outil externe | SSH ne fait qu’une sonde locale ; Diff/Merge attestent leur configuration, sans exécuter les commandes. Erreur et indisponibilité restent visibles. |
| Dépendance .deb satisfaite prise pour un Git recommandé | Le minimum Linux **2.38** reste distinct des seuils Checklist Windows **2.43/2.53** ; ne pas inventer un alignement. |

[UI02 **152/152**](../../artifacts/linux/git-environment-20261018/targeted-ui-02/desktop.trx), [Core environnement **6/6**](../../artifacts/linux/git-environment-20261018/targeted-core-01/git-environment.log) et [compatibilité ciblée **93/93**](../../artifacts/linux/git-environment-20261018/targeted-core-01/compatibility.log) restent des ciblés ; le full01 est désormais vérifié séparément. Parcours source natifs [02](../../artifacts/linux/git-environment-20261018/native-source-02/closure.json)/[04](../../artifacts/linux/git-environment-20261018/native-source-04/closure.json)/[05](../../artifacts/linux/git-environment-20261018/native-source-05/closure.json) bornés anglais/clair/X11 vérifiés ; aucune preuve de paquet interactif ni d’isomorphisme de l’arbre Settings transférée. [ADR0095](adr/preferences/environment/0095-git-environment-and-checklist.md).

Le [premier contrôle du full01](../../artifacts/linux/git-environment-20261018/full-01/parent-verification.json) a rejeté seulement l’égalité checkout/blob de neuf .targets/.cmd. La [revue02](../../artifacts/linux/git-environment-20261018/full-01/parent-verification-02.json) conserve ce rejet et ses hashes, puis vérifie les attributs du candidat et du checkout et la conversion exacte LF→CRLF de ces neuf chemins uniquement. Les octets réellement testés restent identiques avant/après ; aucune suite relancée et aucune normalisation générale.

Harnais X11 : tentative01 terminée par fermeture de stdin, nettoyage forcé sans acceptation ; tentative03 échouée par NameError avant tout lancement natif. Les parcours02/04/05 et leurs vraies fermetures restent séparés. Le redémarrage02 génération2 a été fermé pendant le chargement ; le dépôt/diff réellement chargés au redémarrage sont vérifiés par04.


## Settings tree — intentions à conserver

- L’arbre ne doit pas recréer les pages ni perdre brouillons ou focus clavier ; Entrée dans la recherche ne publie rien.
- Avec 200 px de navigation, les anciennes pages de largeur minimale 670 peuvent être coupées : conserver le défilement horizontal sur fenêtre étroite.
- Une erreur ancienne doit disparaître après Save réussi ; Fonts AcceptSaved reprend le résultat fusionné pour éviter son écrasement lors d’un Apply répété.
- Cancel après Apply/SaveAll réussi rend true et ne rollback pas les publications ; fermer pendant une opération exige annulation et drainage des acteurs connus.
- Suites complètes, paquet exact et natif source ciblé vérifiés ; pages absentes, Config enfant et portées incomplètes restent des écarts ; aucune parité globale transférée. [ADR0096](adr/preferences/navigation/0096-settings-tree-host.md).

[New01 18/19](../../artifacts/linux/settings-navigation-20261018/targeted-new-01/parent-failure-review.json) : l’oracle neuf exigeait une notice vide malgré la police fusionnée inexistante ; conserver son avertissement de fallback et vérifier la disparition de l’erreur de sauvegarde. [Ciblé02 219/222](../../artifacts/linux/settings-navigation-20261018/targeted-02/parent-failure-review.json) : un accès Config oubliait le nouveau nœud ; deux vrais boutons Scripts étaient hors viewport après mesure horizontale infinie. Borner le contenu au viewport/minimum déclaré, sans scroll réparateur dans les anciens tests ; [ciblé03 223/223](../../artifacts/linux/settings-navigation-20261018/targeted-03/desktop.trx), distinct du full et du natif.

[Full01 2006/720/35](../../artifacts/linux/settings-navigation-20261018/full-01/parent-verification.json) : aucun retrait de cas, sans skip, build et suites à 0. La [sonde complémentaire 2/2](../../artifacts/linux/settings-navigation-20261018/owner-close-probe-01/root-review.json) n’a pas reproduit la perte supposée du résultat Settings après Apply puis fermeture pendant Config occupé. Elle reste hors des 2006 cas : son garde externe à 3 avait inclus quatre PNG modifiés par le full concurrent ; rejet brut et delta exact conservés, sources et binaires inchangés.

[Natif source01](../../artifacts/linux/settings-navigation-20261018/native-source-01/root-review.json) : deux sorties normales à 0 et vrai redémarrage prêt. Un mauvais clic Hotkeys et un clic sous Git replié n’ont rien publié ; un close rejeté par une ancienne identité de focus n’a injecté aucune touche, d’où l’attente suivante expirée. Fermeture ensuite sur le focus réellement observé ; reçus conservés. Aucun défaut produit déduit de ces erreurs opérateur. Le contexte /proc doit correspondre à celui des acteurs : le bac à sable peut masquer les processus escaladés.

## General — intentions à conserver

- Une case sans consommateur réel ne porte pas une fonction. Le libellé historique du statut ahead/behind vise surtout Push ; privilégier sa destination, puis upstream, et reprendre le formateur Windows.
- Ne pas confondre taille de lot et plafond global, destination Clone récente et défaut, état checkout inconnu et propre, autostash et stash manuel.
- Les profils demeurent indépendants : Apply peut avoir publié avant un échec ultérieur. Préserver les champs concurrents et les effets déjà acquis au retry ; ne pas remplacer un profil illisible par un fallback silencieux.

[Build produit01](../../artifacts/linux/general-settings-20261018/build-product-01/result.json) : nameof(Button.IsVisible) référençait la méthode MainWindow.Button. Builds02/03 : syntaxe, type cible ambigu et annotations Linux des nouvelles fixtures ; build05 : mauvais nom de lecteur dans un test neuf. Échecs bruts conservés ; [build07](../../artifacts/linux/general-settings-20261018/build-07/result.json) compile sans avertissement/erreur, sources stables.

[Ciblé01 124/136](../../artifacts/linux/general-settings-20261018/targeted-01/root-red-review.json) : vrai rechargement inutile des références après thème seul, champ numérique rogné et clavier sans incrément ; conserver les oracles et corriger le produit. Les autres échecs venaient des nouvelles fixtures : lecteur vide, Apply pendant scan, titre court et sous-type JsonReaderException. [Ciblé02 142/145](../../artifacts/linux/general-settings-20261018/targeted-02/root-red-review.json) confirme les deux accès au mauvais type du champ chemin et le défaut Haut/Bas restant. Le contrôle numérique compact partagé conserve les brouillons invalides et le focus ; une préférence inchangée ne relance pas les lecteurs ni l’arbre.

[Ciblé03 145/145](../../artifacts/linux/general-settings-20261018/targeted-03/result.json) : sortie0 sans skip, sources stables ; 105 cas General ajoutés, neuf anciens cas des réglages récents couvrent le contrôle partagé. Cette preuve ciblée ne remplace ni full, ni paquet, ni audit natif.

[Full01 interrompu et rejeté](../../artifacts/linux/general-settings-20261018/full-01/root-failure-review.json), source36d11d084 : l’ancien parcours View a exposé InitialsAvatar.Render avec une dimension nulle et donc emSize0. SIGINT limité au runner Desktop possédé ; waits Desktop/full=1, Core720 et Plugins35 terminés0, acteurs contrôlés retirés. Aucun full accepté ni paquet déduit de ce passage partiel. [Reproduction déterministe](../../artifacts/linux/general-settings-20261018/avatar-red-01/root-review.json) : trois cas rouges, tests inchangés à travers la garde de rendu ; le même avatar doit ensuite se réafficher en16×16. Build08 a d’abord corrigé une mauvaise API Bounds de la nouvelle fixture ; son brut est conservé.

[Ciblé04 155/155](../../artifacts/linux/general-settings-20261018/targeted-04/result.json) après garde : trois reproductions vertes, sept anciens cas HistoryMenuParity inchangés et 145 cas du ciblé précédent ; build10 sans avertissement/erreur, entrées stables. Nouveau full02 requis sur le commit corrigé ; aucun retrait ni assouplissement de l’oracle View.

[Full02 rejeté](../../artifacts/linux/general-settings-20261018/full-02/root-failure-review.json) : 2113/2114 Desktop, Core720 et Plugins35 réussis ; sorties Desktop/full1, acteurs retirés. Le premier hit Checkout après Reset another branch touche le fond malgré une géométrie finale correcte. L’ancien cas inchangé passe isolément ; la cause de cet échec transitoire reste non démontrée. Ne pas le reclasser en réussite ni déduire de paquet.

[Montage Checkout reproduit](../../artifacts/linux/general-settings-20261018/checkout-layout-red-02/root-review.json) : trois cas gated montrent les choix Local changes avant disponibilité des données. Séparer disponibilité et état dirty nullable ; le mode sans vérification conserve ses choix une fois prêt. L’assertion propriétaire du premier essai neuf utilisait à tort IsEffectivelyEnabled pour la modalité native ; correction de fixture conservée, anciens oracles intacts. Cette correction métier ne prouve pas à elle seule la cause du hit transitoire full02.

[Ciblé05 170/170](../../artifacts/linux/general-settings-20261018/targeted-05/result.json) : build13 sans avertissement/erreur, sources stables, trois cas de montage verts et douze anciens Reset/Revert inchangés, dont le clic en échec du full02. Les 155 cas du ciblé04 restent verts ; full03 requis avant paquet/natif.

[Full03 accepté](../../artifacts/linux/general-settings-20261018/full-03/parent-verification.json), source `2ceb059bc` : 2117/2117 Desktop, 720/720 Core, 35/35 Plugins ; build et suites à 0, sources/runtime stables. Les 2006 anciens cas restent présents, avec 111 ajouts et aucun skip. Les parcours historiques View et Reset/Checkout passent sans modification de leurs oracles ; cela ne démontre pas la cause du hit transitoire du full02.

[Paquet exact](../../artifacts/linux/snapshots/2ceb059bc/proofs/package-parent-verification.json) : cinq phases à 0, extraction complète et backend vérifiés isolément. Les seuls deltas tardifs des logs runtime/install sont les avertissements de bus privés explicitement reconnus, hashes avant/après conservés. Aucun résultat interactif du paquet ni installation hôte déduit.

[Natif source General](../../artifacts/linux/general-settings-20261018/native-source-01/root-review.json) : Apply/Cancel, Entrée, dépendances, histogram réel, défaut Clone, premier clic/Haut/Bas et vrai redémarrage ; deux sorties normales à 0 et intégrité conservée. Les premières images de lancement, de saisie longue et de Clone précédaient la fin du rendu ; observation suivante avant geste, sans réparation ni publication supplémentaire. Périmètre anglais/clair/X11 privé uniquement.

## Appearance — brouillons et consommateurs

[Ciblé01 55/62](../../artifacts/linux/appearance-settings-20261018/review/targeted-new-01-diagnosis.json) : nouveaux helpers de thème/combo incorrects, oracles historiques inchangés. [Ciblé02 62/63](../../artifacts/linux/appearance-settings-20261018/review/cancellation-red.json) : Settings attendait une branche facultative d’un autre récent ; le token consommateur arrête cette attente sans tuer le worker partagé. Ciblé03 conserve ce cas vert mais révèle un clic Apply avant la vraie fin du scan Checklist ; attendre OperationCompletion sans forcer l’activation. [Ciblé04 63/63](../../artifacts/linux/appearance-settings-20261018/targeted-new-04/result.json), zéro skip.

[Full01 interrompu](../../artifacts/linux/appearance-settings-20261018/review/full-01-rejection.json) : deux premiers clics Checkout échouent après resize ; SIGINT du Desktop possédé, waits1 et aucun TRX complet. L’isolé3/3 ne prouve pas la cause. [Barrière de rendu](../../artifacts/linux/appearance-settings-20261018/review/checkout-render-adaptation.json) : attendre le batch réellement rendu, puis vérifier dimensions/layout avant l’unique hit/clic, sans polling ni changement produit ; [ciblé05 85/85](../../artifacts/linux/appearance-settings-20261018/targeted-05/result.json).

[Full02 rejeté 2179/2180](../../artifacts/linux/appearance-settings-20261018/review/full-02-rejection.json) : seul ancien oracle Dashboard attendait un libellé sans branche malgré le défaut Windows/Linux actif. Attendre la vraie lecture, vérifier `repository (main)` et conserver les cibles littérales, Caption, largeurs et Cancel/OK. [Ciblé06 64/64](../../artifacts/linux/appearance-settings-20261018/targeted-06/result.json), filtre excluant explicitement les 27 cas RepositoryBranchVisibility. Aucun paquet déclenché sur ce full rejeté.

[Full03 rejeté 2179/2180](../../artifacts/linux/appearance-settings-20261018/review/full-03-rejection.json) : deux sondes de branche au lieu d’une lors de la réouverture. Le premier Dashboard invalidait la prélecture Main ; réutiliser celle-ci, garder F5/revisites explicites. Oracle inchangé, [ciblé07 91/91](../../artifacts/linux/appearance-settings-20261018/review/branch-initial-refresh-green.json). Le brut ne permet pas d’identifier laquelle des deux sondes était annulée. Core720/720 et Plugins35/35 passent dans les full02/03 ; entrées stables, acteurs retirés, rejets conservés.

[Full04 accepté](../../artifacts/linux/appearance-settings-20261018/full-04/parent-verification.json), source `81a7aca67` : **2180 Desktop / 720 Core / 35 Plugins**, zéro skip/échec/retrait, build sans avertissement/erreur. [Paquet isolé exact](../../artifacts/linux/snapshots/81a7aca67/proofs/package-parent-verification.json) et [revue native source ciblée](../../artifacts/linux/appearance-settings-20261018/native-source-01/root-operator-review.json) acceptés séparément. Premier lancement du banc refusé avant création d’acteur car son reçu Root était rangé dans le payload scellé ; reçu déplacé hors payload, outils et manifeste inchangés. Les captures de transitions précèdent parfois le rendu asynchrone : nouvelle observation avant le geste suivant, aucune réparation du focus. Natif anglais/clair/X11 uniquement ; limites et reste à porter dans TODO.

## Avatars partagés — activation

[Ciblé01 rejeté](../../artifacts/linux/appearance-settings-20261018/review/avatar-targeted-01-rejection.json) : 101/102, fermeture Blame historique retardée par un drain sans runtime lié. Réserver ce drain aux fenêtres liées ; même ancien oracle, ciblé02 102/102 puis ciblé03 105/105. Ces preuves précèdent l’activation Settings/App.

[Lot04 rejeté](../../artifacts/linux/appearance-settings-20261018/review/avatar-targeted-04-rejection.json) : filtre `~Avatar` élargi par la SetUpFixture de namespace, **2271/2280**, neuf échecs. Quatre nouveaux helpers observaient Owner avant son affectation (attendre le vrai Opened), deux goldens confondaient fond de fenêtre et toolbar ; trois anciennes preuves new-process manquaient du SDK dans le checkout séparé. Aucun ancien oracle assoupli ; réparer le banc. [Ciblé05 rejeté 33/35](../../artifacts/linux/appearance-settings-20261018/review/avatar-targeted-05-rejection.json) : les deux nouveaux goldens d’ordre enum étaient faux ; Windows donne Default/Custom/None. La troisième preuve new-process exclue du filtre05 reste explicitement reprise au suivant.

[Ciblé06 accepté 252/252](../../artifacts/linux/appearance-settings-20261018/review/avatar-targeted-06-green.json) : classes pleinement qualifiées, 103 nouveaux et 149 anciens, dont les trois preuves new-process ; zéro skip, entrées stables, sorties0 et acteurs retirés. Trois nouveaux oracles ont été relus avant compilation : le vrai renderer doit s’annuler sans pompage UI, retirement/annulation du waiter sont une course légitime, et le pixel CommitInfo distingue le PNG provider du placeholder. Ces cas ne remplacent ni le vrai App/classic ni le prochain full/paquet/natif.

[Full01 2283/720/35](../../artifacts/linux/shared-avatars-20261018/full-01/parent-verification.json) et paquet3954 vérifiés. Le [parcours natif](../../artifacts/linux/shared-avatars-20261018/native-source-01/root-operator-review.json) confirme le fonctionnement local, mais découvre un défaut visuel : une URL Custom longue agrandit la colonne éditeur Auto partagée entre groupes et pousse les aides hors du viewport (première frame13, encore présent après redémarrage). Défaut historique 3954 préservé, corrigé et revérifié sur c831 ci-dessous ; aucune acceptation visuelle globale. La première synthèse de preuves a aussi refusé un mauvais comptage interne (cinq phases produit contre huit résultats incluant préparation/extraction) ; correction du vérificateur seulement, preuves produit inchangées.

[Régression de disposition](../../artifacts/linux/appearance-settings-20261018/appearance-layout-patch01/verification.json) : premier build refusé, import `Avalonia.Input` manquant dans le nouveau test seulement ; conservé. Même nouveau test ensuite **0/2 sur3954**, débordement réel après URL86 ; correction de colonnes puis **40/40 ciblés verts**, dont deux nouveaux et38 historiques, zéro skip. Aucun ancien oracle changé. [Clôture c831](../../artifacts/linux/appearance-layout-20261018/review/appearance-layout-acceptance-01.json) : full 2285/720/35, paquet isolé et nouveau natif source acceptés ; draft de 858 caractères et aides visibles sans clic externe. Défaut de l’ancienne preuve conservé.

[Préparation des paquets 01/02](../../artifacts/linux/appearance-layout-20261018/review/root-package-retention-and-link-01.json) refusée avant toute phase produit : umask 0077 altérait les bits exécutables ; 0022 changeait directory gitlink 775→755. Gardes inchangées, bruts et outer initial à 1 conservés ; retry 0002 et propriétaires réels du paquet final vérifiés séparément. [Finaliseur 01](../../artifacts/linux/appearance-layout-20261018/review/finalizer-attempt-01-rejection.json) refusé avant écriture : avant `clean:true` et après `status:""` sont deux schémas propres distincts ; v2 vérifie chacun exactement, mêmes preuves et [vrai retour à 0](../../artifacts/linux/appearance-layout-20261018/review/finalizer-execution-02.json). Infrastructure/bookkeeping, sans nouveau défaut produit.

## Troncature des chemins

[Ciblé01](../../artifacts/linux/truncate-path-20261018/validation-01/targeted.trx) : 275/278 ; un ancien oracle attendait le Grid exact devenu composant commun FilePathHeader, et deux nouveaux cas exigeaient JsonException exacte au lieu de sa sous-classe du parseur. Adaptations précises, aucune suppression de cas ni modification des assertions métier. [Ciblé02](../../artifacts/linux/truncate-path-20261018/validation-02/verification.json) : vrai test à 0, 278/278 ; owner à 1 pour deux PNG historiques supplémentaires atteints après correction de l’oracle. Brut false conservé ; ces deux sorties ont des writers explicites, les 711 autres fichiers runtime sont identiques. Aucun troisième run pour ce diagnostic de banc.

[Full01 interrompu avant tests](../../artifacts/linux/truncate-path-20261018/review/full-01-pretest-interrupt.json) : revalider le catalogue de sorties par fichier rendait l’inventaire prohibitif ; valider une fois par inventaire, puis tester l’appartenance. [Full02](../../artifacts/linux/truncate-path-20261018/full-02/inputs-comparison.json) garde build/tests à 0 mais runner et pipeline à 1 : onze preuves de tests ajoutées, aucun des 1098 anciens inputs Desktop modifié ou supprimé. La [qualification complémentaire distincte](../../artifacts/linux/truncate-path-20261018/review/full-02-postprocessed-root-review-01.json), réellement terminée à 0, conserve le brut runtime false et les deux retours 1 ; aucune nouvelle exécution des suites.

## Dictionary — catalogue et fermeture

[Trois rouges ciblé02](../../artifacts/linux/dictionary-20261018/review/targeted-02-red-root-review.json) conservés : catalogue `InvalidDataException` désormais affiché, vraie composition IME et attente Closed corrigées dans les fixtures sans changer les assertions ; la course d’autofermeture Commit réactivant les contrôles pendant le drain a son état dédié et sa régression. [Ciblé05 accepté](../../artifacts/linux/dictionary-20261018/review/targeted-05-root-review.json) : 297/297 Desktop et 8/8 Core.

[Native01 rejeté sur ca568406f](../../artifacts/linux/dictionary-20261018/review/native-source-01-root-visual-rejection.json) : None persistait mais les anciens pixels restaient ; effacer les plages lors du remplacement du service corrige ce rendu, [même test pixels rouge puis vert](../../artifacts/linux/dictionary-20261018/review/none-render-red-green-01-root-review.json). Les anciennes sorties normales n’annulent pas ce rejet ; [native03 ciblé accepté](../../artifacts/linux/dictionary-20261018/review/native-source-03-root-review.json) confirme le retrait immédiat des marques et la persistance après redémarrage, anglais/clair/X11 privé.

[Full02 rejeté sur8212](../../artifacts/linux/dictionary-20261018/review/full-02-root-failure-review.json) : 2475/2476 Desktop, ancienne fixture cliquant OK avant la fin du scan Git, bouton correctement désactivé ; attendre OperationCompletion et SaveEnabled avant Theme/LogClear conserve toutes les assertions. Aucun lien causal avec le catalogue Dictionary n’est établi ; [full03 accepté sur014](../../artifacts/linux/dictionary-20261018/review/full-03-root-review.json), 2476/728/35, sans requalifier les bruts précédents.

## Complétion Commit — ciblés et isolation

[Core01](../../artifacts/linux/commit-completion-20261018/core-targeted-01/owner-result.json) et [Core02](../../artifacts/linux/commit-completion-20261018/core-targeted-02/owner-result.json) restent refusés à la compilation ; Core03 exécute réellement 11/11 à 0. [Desktop01 brut](../../artifacts/linux/commit-completion-20261018/desktop-targeted-01/owner-result.json) : 333/338 à 1, trois nouveaux cas confondaient null/texte vide et deux ouvraient le menu programmatiquement sans Opening ; seuls deux nouveaux fichiers de fixtures sont corrigés, avec null-or-empty et vrai clic droit. La [revue ciblée distincte](../../artifacts/linux/commit-completion-20261018/review/targeted-root-review-01.json) retient 305 cas passés des 21 classes inchangées et Desktop02 33/33 à 0, soit 338 identités couvertes, sans inventer un run vert 338/338.

[Full01 rejeté pour isolation](../../artifacts/linux/commit-completion-20261018/full-01/root-rejection-01.json) : lecture du LanguageEnglish du profil hôte, oracle Askpass de culture invalide perturbé ; interruption volontaire Root, vrai wait130 et quatre identités possédées retirées, sans modification du profil hôte ni signal à une application hôte observés. Aucun changement produit/tests pour ce défaut de banc ; [full02](../../artifacts/linux/commit-completion-20261018/full-02/parent-verification.json) est accepté sur la même source avec XDG config privé par suite et cache/data isolés communs, puis [paquet](../../artifacts/linux/commit-completion-20261018/review/package-01-root-review.json) et [natif borné](../../artifacts/linux/commit-completion-20261018/review/native-source-01-root-review.json) acceptés séparément ; les bruts Core01/02, Desktop01 et full01 restent inchangés.

## Complétion — survol et sélection

Le popup pouvait afficher deux lignes bleues : le présentateur Fluent peignait le survol en plus de la sélection. `ClassicCompletionList` applique les ressources historiques au conteneur et au présentateur pour les deux états sélectionné/non sélectionné, communs à Commit/Clone/champs dépôt. [Build/ciblé106](../../artifacts/linux/completion-list-20261018/review/targeted-01-root-review.json) et [natif Commit borné](../../artifacts/linux/completion-list-20261018/review/native-source-01-root-review.json) sur8d9 vérifiés ; le full/paquet069 reste une référence distincte.

## Messages Commit — formatage et chiffres

L’[archive des essais](../../artifacts/linux/commit-templates-20261018/worktree-evidence-01/index.json) conserve les bruts W01–08 : formatage/caret, Undo différé, chiffres cachés par Fluent, puis correctifs et oracles inchangés. Contrôles Classic et gardes de formatage précèdent le [ciblé primaire fe1 214/214](../../artifacts/linux/commit-templates-20261018/review/targeted-primary-02-root-review.json) ; cette validation bornée ne devient pas un nouveau full.

## Messages Commit — deux écarts natifs

[Native01 refusé](../../artifacts/linux/commit-templates-20261018/review/native-source-01-root-review.json) : la première Échap du déroulant fermait Settings ; Ctrl+T montrait feat sélectionné mais Entrée appliquait le parent survolé. La sortie normale à 0 ne suffisait pas. Le [natif source02 fe1 accepté](../../artifacts/linux/commit-templates-20261018/review/native-source-02-root-review.json) conserve Settings/brouillon après Échap et applique feat/feat() par les premiers Enter, sous souris immobile, avec Undo et fermetures réelles à 0.

W06 conserve 19 verts/deux rouges : sélectionner les seuls ancêtres ne protégeait pas le mappage sous la souris. Le build W07 sur une API inexistante reste archivé ; W08 puis primaire02 qualifient le garde libéré au mouvement réel/appui/Échap/fermeture, sans faux Enter/Click. La reprise réelle du pointeur et les autres variantes natives restent ouvertes ; le [paquet03 isolé](../../artifacts/linux/commit-templates-20261018/review/package-03-root-review.json) ne vaut pas audit interactif du paquet.

## Commit branche et superproject — essais et gardes

Les bruts [Core01–04](../../artifacts/linux/commit-branch-superproject-20261018/worktree-validation/core-04/owner-result.json) restent séparés : ambiguïtés de compilation puis fixtures de chemin à tiret initial/linked child, Core03 20/20 et oracle d’ancêtre invalide renforcé avant Core04 20/20 ; un ancêtre invalide sans association à l’enfant est ignoré ; une association déclarée vers un parent corrompu conserve l’erreur Git native. La création Worktrees gérée depuis un sous-module absorbé garde son défaut distinct ; la fixture [linked child native Core03](../../artifacts/linux/commit-branch-superproject-20261018/worktree-validation/core-03/core.log) vérifie réellement racines, common et index. [Desktop02](../../artifacts/linux/commit-branch-superproject-20261018/worktree-validation/targeted-02/desktop.log) bloquait une fixture en initialisant une seconde fenêtre pendant un push suspendu : initialisation déplacée avant la barrière, puis acteurs de cette fixture libérés par son marqueur privé. Desktop03 et05 échouent au build faute de namespaces ; [Desktop04](../../artifacts/linux/commit-branch-superproject-20261018/worktree-validation/targeted-04/desktop.log) garde **411/415**, deux rendus Cancel dépassant d’un pixel et deux hypothèses historiques de disposition/accès direct à Templates. Géométrie et pilote de toolbar canonique corrigés sans retrait d’identités ; [W06 415/415](../../artifacts/linux/commit-branch-superproject-20261018/review/worktree-targeted-06-root-review.json) n’est ni un full ni une preuve primaire/native/paquet.

L’indexation parent suit le commit et le push réussis, jamais un push seul, échec ou annulation. Une lecture JSON tardive défaillante ne doit pas réactiver le défaut Stage ; changement de l’association/entrée ciblée, verrou, erreur native ou readback incomplet laisse le commit enfant, HasCommitted et les effets réels visibles, sans auto-close. Le HEAD enfant est relu après récupération/rebase ; aucun commit/push du parent ni transaction globale de son index n’est promis. [Core primaire01](../../artifacts/linux/commit-branch-superproject-20261018/review/core-primary-01-root-review.json) qualifie ces 20 scénarios sur `1cbebda758` ; [Desktop primaire415](../../artifacts/linux/commit-branch-superproject-20261018/review/targeted-primary-01-root-review.json) qualifie séparément la source UI8872 ; [natif source01 français/sombre/X11 borné](../../artifacts/linux/commit-branch-superproject-20261018/review/native-source-01-root-review.json) accepté séparément, sans commit/push/staging ou geste detached natif ; [paquet exact isolé](../../artifacts/linux/commit-branch-superproject-20261018/review/package-01-root-review.json) vérifié séparément. L’avertissement tardif du bus privé après smoke garde les hashes de log avant/après ; aucune interface empaquetée ou installation hôte n’est déduite. État courant dans le [TODO](todo.md#branche-et-superproject-depuis-commit--qualification-ciblée).

## Detailed — premiers ciblés Core

[Core01](../../artifacts/linux/detailed-settings-20261019/core-01/result.json) échoue au build sur six diagnostics des nouveaux tests : cinq spans conservés à travers await et un mode Unix sans garde de plateforme. [Core02](../../artifacts/linux/detailed-settings-20261019/core-02/result.json) garde une fixture de routage qui confondait clé absente et item nil sérialisé. La table utilise maintenant l’absence réelle ; le test de suppression explicite d’un nil reste présent. [Core03](../../artifacts/linux/detailed-settings-20261019/core-03/result.json) exécute réellement 51/51 à 0 ; bruts antérieurs inchangés, sans preuve Desktop/native/paquet déduite.

Une valeur XML vide/invalide masque les couches inférieures : parser avant résolution fabriquerait un repli. Les sauvegardes acquittées et le marqueur de migration empêchent le retry ou l’ancien JSON de ressusciter une suppression ; CAS et verrous ne promettent pas une transaction globale. Une erreur ls-remote ne devient pas un succès local et n’autorise aucun fetch. Une valeur Merge hors0–100 doit rester visible sans clamp/write implicite ; count0 n’envoie pas --log=0. Pour les worktrees absorbés, le common directory seul ne prouve pas un checkout : vérifier son rattachement réciproque et conserver les effets natifs partiels plutôt que simuler un rollback.

Desktop build01 conservé : déclaration de propriétés de fixture corrigée ; aucun test exécuté dans cet essai. Le ciblé01 garde 229/236 : 85 nouveaux cas verts, trois régressions de rechargement Push sans l’option serveur et quatre attendus d’arbre à étendre à Detailed. Les attentes existantes sont maintenues ; le chemin local ne recharge plus à chaque onglet et le nouveau nœud garde son icône Windows.

Core full01 est un échec de banc (766/787), lancé via dotnet/DLL alors que les cas éditeur/askpass/redémarrage doivent se relancer par l’apphost. Full02 relancé correctement passe 787/787. Le premier Desktop full est interrompu après trois échecs de redémarrage : ces tests exigent checkout/.dotnet. Une copie physique du SDK privé est fournie, sans lien au SDK primaire ; le préflight3/3 passe avant le nouvel ensemble complet. Ces bruts et le drainage réel de la suite arrêtée restent conservés.

Le nouveau full produit une trace owner-close-trace dans le dossier du test : les manifests bruts restent false lorsqu’ils la détectent. La revue Root n’exclut que ce chemin précisément épinglé, son writer et sa copie attachée au TRX réussi. La trace Pull est contrôlée de même ; les trois JSON de géométrie préexistants restent byte-identiques pendant les fulls et ne sont écartés que de la comparaison build→full. Aucun changement de binaire n’est accepté. Les snapshots sources et les comptes complets du nouvel ensemble restent indépendants des essais précédents. La création Worktrees depuis un absorbé avait aussi subi un build sur des sources concurrentes non figées ; il ne fournit aucun verdict de test.

## Rapport CI Windows — pièges à conserver

| Piège | Contrat retenu pour ADR0136 |
| --- | --- |
| false interprété comme onglet masqué | ShowInBuildReportTab=false choisit Open report centré ; la présence dépend de la préférence de page et de l’URL du résultat sélectionné. |
| Statut agrégé et lien d’un autre run | Partager un représentant Windows StartDate pour texte/statut/tooltip/cellule/menu/PR/rapport ; les runs frères restent disponibles comme données. |
| Poll identique change le gagnant à date égale | >= s’applique aux observations nouvelles/modifiées ; ne pas rejouer les anciennes données à chaque publication ni trier par UpdatedAt. GitLab conserve la date Windows null, Jenkins ses admissions propres. |
| Ancien lien réapparaît après sélection/configuration | Invalider le contexte ; le nouveau représentant sans URL retire la page et ne reprend pas l’URL d’un run précédent. Diagnostic Azure visible dans le statut même sans page. |
| Ctrl+7 présent seulement au catalogue | Raccorder l’identité configurable FocusBuildServerStatus au contrôle disponible ; pas de page créée ni focus volé par une arrivée CI. |

Contrat relu dans Windows BuildServerWatcher et BuildReportTabPageExtension ; repères et limites dans [ADR0136](adr/extensions/ci/0136-windows-build-report.md). Source 545f : build-01 conserve CS8604 dans une assertion Azure nullable, remplacée par le littéral fixe attendu. Ciblé01 91/94 : trois montages de test corrigés (culture au constructeur ; inspection du contenu conservé des onglets non sélectionnés), sans modification produit ni retrait d’oracle. [Ciblé02/Core](../../artifacts/linux/build-report-parity-20261020/review/root-targeted-review.json) : 94/94 et 1062/1062 ; ac59 reste la référence complète Desktop/Plugins.

[Natif01](../../artifacts/linux/build-report-parity-20261020/native-01/root-retirement-review.json) est rejeté : KeyError generation dans le banc, dont le journal finish omettait ce champ ; application fermée normalement à 0, contrôleur interrompu (130), infrastructure retirée. La copie corrigée garde la génération réelle et la vérifie ; [Natif02](../../artifacts/linux/build-report-parity-20261020/review/root-native-review.json) termine à 0, deux apps normales à 0, douze checkpoints EN/FR. [Paquet](../../artifacts/linux/build-report-parity-20261020/review/root-package-review.json) exact/DPKG isolé distinct. [Inventaire contrôlé](../../artifacts/linux/build-report-parity-20261020/review/root-inventory-review.json) : 2469 C#, zéro diagnostic, niveaux inchangés. HTML true et live authentifié restent ouverts.

## Contributeurs, liens et CI — qualification du lot

Les premiers builds avaient détecté quatre spans traversant await dans les nouveaux tests Core et une variable locale de caption en doublon : corrigés avant qualification. Le premier ciblage Desktop (184/185) a trouvé la modification directe d’un TabControl à ItemsSource : le rapport CI utilise désormais la collection de détails commune. Le premier full a été interrompu pour les dernières corrections de présentation ; il reste une exécution incomplète, sans verdict transféré.

[Full02 conservé](../../artifacts/linux/remaining-windows-20261019/desktop-full-02/root-tool-wait-receipt.json) : 2807/2808, le seul échec historique exigeait les mots « terminal emulator ». Le diagnostic les conserve désormais, sans changer ce test. Le natif01 avait aussi trouvé un preview Dark blanc sous hover puis revenu blanc/noir après navigation : les anciens DynamicResource bindings et le fond du Border Fluent étaient en cause.

La [reproduction dédiée](../../artifacts/linux/remaining-windows-20261019/desktop-console-repro-02/results/desktop.trx) échoue réellement 2/2 avant correction (au survol, avant la réattache). Le ciblage à zéro sélection reste une non-preuve. Le premier nouveau ciblage (240/242) avait un setup de sélection erroné : placer CaretIndex après SelectionStart/End annulait normalement la sélection ; ordre corrigé, assertions conservées. [Ciblage corrigé](../../artifacts/linux/remaining-windows-20261019/desktop-console-fixed-01/results/desktop.trx) : 2/2 ; [full final](../../artifacts/linux/remaining-windows-20261019/review/root-full-review.json) : 2810/2810, avec le test historique inchangé. [Natif02](../../artifacts/linux/remaining-windows-20261019/review/root-native-review.json) : palette après navigation/survol/redémarrage et Cancel sans delta, deux vraies sorties à 0. Les bruts, y compris les diagnostics modifiant les manifests runtime, restent conservés ; seules les traces précisément rattachées à des tests réussis sont exclues par la revue.

Le premier petit banc live, placé sous R, héritait les propriétés WinForms : build refusé, aucune requête. Le banc indépendant temporaire vérifie ensuite le client Core réellement compilé : lecture anonyme du dépôt public, cache sans requête et ETag304, sans identifiant machine. Cette observation ne qualifie pas l’interface live authentifiée/Enterprise. Le rapport CI de ce lot historique restait une liste et une ouverture externe explicite ; le complément lien centré false et représentant commun relève d’ADR0136, sans transfert des preuves antérieures.

## Pull requests — essais conservés

Les premiers builds conservent l’API Avalonia inexistante et les diagnostics nullable des nouveaux tests ; corrigés avant build06. Le premier ciblé a été interrompu sur Guest+Enter : le RadioButton consommait Enter au lieu du bouton par défaut. Le dialogue traite désormais ce geste sur ses trois radios ; les attentes sont bornées, sans remplacer le test par un clic. Le ciblé02 conserve 91/92 : le test du veto Checkout devait acquitter le vrai Process en échec avant d’attendre la suite ; comportement produit conservé.

Le natif02 a trouvé l’activation tardive des modales PR ; activation partagée après mappage ajoutée. Le natif03 a trouvé la coloration de validation Commit appliquée au corps d’une PR ; éditeurs remplacés par des TextBox multilignes ordinaires et deux régressions ajoutées. Desktop full01 a été arrêté pour cette correction. Full02 a été arrêté pour raccorder le lecteur de template borné déjà présent dans Core ; deux cas UI oversized/lien sortant vérifient ce chemin. Ces deux essais restent incomplets, sans verdict de réussite. [Natif05](../../artifacts/linux/pull-requests-20261019/native-05/root-native-review.json) et [full final](../../artifacts/linux/pull-requests-20261019/review/root-full-review.json) qualifient séparément la source corrigée ; les essais précédents restent intacts. Le client local HTTPS ne certifie pas un service GitHub réel/Enterprise. Le smoke du paquet n’est pas un audit interactif de ses PR. Le composite paquet01 conserve sa sortie1 après cinq phases réelles à0 : son contrôle de fonte attendait `.avalonia.resources` au lieu de `!AvaloniaResources`. La [revérification statique corrigée](../../artifacts/linux/pull-requests-20261019/package-static-recheck-01/result.json) passe sur le même paquet inchangé ; phases et résultat initial conservés.

## TeamCity — focus, palette et changement d’instance

Les builds01/02/05 conservent les erreurs d’import/type du produit et des nouveaux tests, corrigées avant build07. Ciblé01 : 188/191, défaut de focus initial du TreeView et scénario de portée explicite mal configuré. L’arbre est maintenant focalisable ; les champs Local demandent le fournisseur Local. La vérification des captures a trouvé les labels de projets noirs en thème sombre : palette Windows commune réappliquée à l’attachement/changement de thème/sélection, avec assertion du contraste des projets. Ciblé02 : 189/191, Tab parcourait plusieurs nœuds ; le treeview est un unique arrêt Tab comme Windows. Ciblé03 : 191/191 Desktop et 25/25 Core. Full01 passe 2968/873/35 sur d09ea80bb, conservé comme résultat historique. Une seconde relecture a trouvé l’ancien statut conservé lorsque le constructeur de remplacement rejetait un nouveau token vide : ciblé04 reproduit 191/192, puis invalidation avant construction et ciblé05 passe 192/192. Full02 qualifie la correction ; aucune réussite précédente transférée à sa source.

Relecture Windows : LogAsGuest concerne le lien du rapport, jamais le mode REST ; ces choix sont séparés et un test garde les champs inactifs. Relecture du raccord : changer d’instance sans credentials laissait les anciens liens/statuts ; ils sont retirés avant publication de l’erreur, testés sur le vrai hôte MainWindow. Les fichiers utilisateurs et XML ne sont pas une transaction ; une publication acquise n’est pas annulée par Cancel. Suites complètes et paquet rattachés dans le TODO ; aucun audit interactif étendu TeamCity ni serveur réel déduit des transports de test.

## GitLab — essais et présentation

Build01/02 ont révélé un callback avant initialisation et une capture de paramètre dupliquée dans la fixture ; corrigés. Ciblé01 échouait sur un oracle français reprenant la culture anglaise après `await`, corrigé par culture explicite. Ciblé04 passe 224/224 Desktop et 30/30 Core. Un token hérité sans URL stockée est désormais refusé par Save comme par Core, jusqu’à ressaisie.

Native01 montrait l’accent orange Ubuntu et deux notices Effective. La palette Windows HotTrack et la notice partagée corrigent ces écarts ; [Native02](../../artifacts/linux/gitlab-20261019/native-02/root-native-review.json) qualifie seulement son parcours source X11/local.

Garder les temporaires des suites **courts et hors dépôt** : Core01 finit à 899/903 car ses fixtures découvrent le dépôt parent ; Desktop02 dépasse le viewport Clone intentionnel de 80px avec un préfixe long. Desktop01/02 sont interrompus et ne qualifient rien, même avec une phase wait0. Le [diagnostic Clone](../../artifacts/linux/gitlab-20261019/review/clone-fixture-diagnostic-review.json) passe 1/1 clair/sombre avec un préfixe court, sans modifier produit ni oracle. Les [suites finales](../../artifacts/linux/gitlab-20261019/review/root-full-review.json) restent distinctes ; aucun serveur GitLab réel ni geste du paquet installé n’en est déduit.

## AppVeyor — HEAD détachée et oracle de langue

Le premier ciblé conserve 256/258 Desktop : la lecture de `symbolic-ref` devait accepter le code1 attendu en HEAD détachée, puis utiliser le premier remote. Le test français chargeait son oracle depuis la culture du contexte appelant après `await` ; le catalogue attendu doit être choisi explicitement. Les corrections passent le ciblé frais et les suites complètes ; les résultats bruts du ciblé01 restent conservés.

## AppVeyor — limites résiduelles

L’adaptateur Windows signale lui-même un écart entre le préfixe v2 annoncé par la documentation et ses routes effectives : le portable conserve ce contrat, sans déduire sa validité sur un serveur réel d’un transport de test.

Le présentateur CI commun fournit une liste et des liens externes, pas le rapport HTML Windows embarqué. Le serveur AppVeyor réel/authentifié, les interactions installées et les variantes natives au-delà du parcours ciblé restent non qualifiés tant que leurs preuves propres ne sont pas rattachées ; ni une page écrite ni une suite voisine ne les certifie.

## CI Azure DevOps et Jenkins

La revue statique Azure a corrigé une autorisation PAT fondée sur des substituts syntaxiques de template : seules les variables réelles du dépôt créent le contexte autorisé. Le launcher de Token management et son erreur sont possédés avec Settings. Les essais Azure01/02 ont aussi distingué le vrai défaut de fermeture propriétaire, les gestes de badge et le double adorner Avalonia ; les essais échoués restent dans `artifacts/linux/azuredevops-20261019/`.

La revue statique Jenkins a séparé historique en cache et nouvelles émissions : une ancienne fin ne doit pas bloquer un running neuf, les diagnostics d'une limite disparaissent à la récupération réelle, et le watcher ne reçoit pas un build éliminé par le plafond global. Les identifiants dépendent de l'endpoint complet, y compris son chemin. Ces constats statiques ne sont pas présentés comme des exécutions échouées. Repères : ADR0114/0116 et `artifacts/linux/jenkins-20261020/review/`.

## Portable workflows — limites et corrections

FormEditor garde dirty après échec et signale une écriture commencée non vérifiée : contrôles optimistes, restauration des permissions et drainage ne forment pas une transaction contre les éditeurs externes. Les règles conservent leur writer/newline propre.

Deux écarts de revue Fork/Clone ont été corrigés avant qualification : conserver le fork accepté avant le GET secondaire et vérifier l’identité des détails avant publication. Ils ne sont pas présentés comme des tests exécutés en échec. Le clone acquis et son upstream éventuel restent des effets séparés ; aucune répétition automatique du POST.

Confirmations acquitte chaque profil séparément ; Colors distingue intentions View temporaires et défauts édités. Cancel ne revient pas sur Apply acquis. Le rapport récupérable ne prétend pas couvrir le circuit fatal NBug. Le picker FilePrompt sans token reste un arrêt natif non garanti, même si ses retours tardifs sont écartés.

Advanced utilise également des reçus partiels. La conservation des clés étrangères par PreferencesStore.Save est limitée au premier niveau valide ; les objets typés et les autres champs gardent leur contrat antérieur.

Les essais rouges restent conservés : compilation et ciblés initiaux, puis targeted04 (324/327) et desktop-full01 interrompu après le défaut de contrat IO connu. Corrections : focus du vrai champ invalide, navigation clavier du symbole et IOException explicite pour une destination de préférences qui est un dossier. L’assertion IOException historique reste stricte ; les suites fraîches sur b745 passent 3441/1057/35.

Natif01 interrompu (sortie 130) n’est pas accepté ; natif02 a terminé normalement et garde ses propres preuves. Le premier générateur d’inventaire a refusé l’ancienne carte sans BugReportDialog après un scan Roslyn réussi ; après mise à jour de la carte, génération et contrôle exact ont réussi (2444 fichiers, zéro diagnostic).

## SSH — autorité et observations à conserver

GIT_SSH est un exécutable littéral, pas une commande shell. Ne pas effacer GIT_SSH_COMMAND/core.sshCommand ni forcer ssh.variant pour rendre le choix applicatif dominant. Ne pas conclure qu’un -V du binaire choisi valide une commande prioritaire ou l’authentification. Plink/Pageant/PuTTYgen existent sous Unix ; registre, détection .exe et WaitForInputIdle sont les frontières Windows à remplacer.

Une opération de dépôt fige son runner ; un nouveau GitRunner indépendant ne partage pas ce scope. Le premier scénario SSH confondait ces deux autorités : il utilise désormais les vrais remote heads du même dépôt. Une observation d’A doit disparaître après édition/Apply de B ; la publication instantanée du flag startup ne doit pas effacer le diagnostic du scan qui vient de réussir. Régressions dédiées dans les tests SSH/environnement. Les builds initiaux refusés (import TextWrapping puis garde Unix des fixtures) sont conservés dans le lot ; aucun n’est compté comme réussite. [Validation courante](todo.md#ssh--perimetre-verifie).

## FilePrompt — fermeture du picker et acceptation

Écarter un résultat tardif et attendre BrowseCompletion ne ferme pas un picker natif sans token. Le picker géré possédé ADR0131 remplace ce chemin pour les scripts. Son modèle Avalonia 11.3.22 ignore la saisie FileName lors d’Open : accepter directement ce modèle peut retourner l’ancienne sélection. L’adaptateur distingue sélection inchangée et saisie, vérifie les fichiers et conserve les noms littéraux.

Les ciblés01/02 du lot `file-picker-20261020` ont révélé un renommage interdit d’un contrôle déjà stylé dans TemplateApplied ; le test signale maintenant l’exception réelle plutôt qu’un simple délai d’ouverture. Le premier run a été arrêté après échec avéré, puis ses acteurs retirés. Après correction, ciblé04 passe 58 Desktop et 38 Core Scripts ; ni les essais rouges ni les captures headless ne qualifient un parcours natif.

## Cache des commandes — essais conservés et corrections C2

Le [Desktop C1](../../artifacts/linux/command-cache-20261020/desktop-full-01/desktop.log) a été interrompu par SIGTERM avant TRX : 3542 lignes Passed/11 Failed ne forment pas un verdict complet. La [revue de phase indépendante](../../artifacts/linux/command-cache-20261020/desktop-full-01/root-interrupted-phase-review.json) conserve le retrait malgré result.results vide.

Dix échecs venaient des scripts Git non fermés : [témoin rouge 49/50](../../artifacts/linux/command-cache-20261020/review/root-script-regression-red-review.json), puis [50/50 sur C2](../../artifacts/linux/command-cache-20261020/review/root-targeted-review-02.json). Recovery affichait encore un objet supprimé ; son API fraîche conserve maintenant l’erreur portable, sans assimiler ce choix à Windows.

L’ancien test ClassicSettings levait Click sur OK désactivé par le scan automatique GitEnvironmentSession. Le [diagnostic ciblé08](../../artifacts/linux/command-cache-20261020/targeted-08/desktop.log) enregistre enabled=False/busy=True ; le test attend désormais la disponibilité avant son clic.

[Native02](../../artifacts/linux/command-cache-20261020/native-02/root-retirement-review.json) a fermé normalement l’application puis retiré le contrôleur à 130 : mousebind Titlebar absent du fixture Openbox privé, aucun défaut produit prouvé. Seul [native03](../../artifacts/linux/command-cache-20261020/review/root-native-review.json) porte l’acceptation Root. Les [fulls C2](../../artifacts/linux/command-cache-20261020/review/root-full-review.json) et le [paquet exact](../../artifacts/linux/command-cache-20261020/review/root-package-review.json) sont acquis ; l’[inventaire02](../../artifacts/linux/command-cache-20261020/review/root-inventory-review-02.json) a été régénéré et contrôlé.

## Géométrie du journal — cas reproduits

[Build01](../../artifacts/linux/command-log-geometry-20261020/build-01/result.json) conserve trois erreurs du banc (callback Avalonia interne) ; la réflexion du test lit désormais le vrai délégué déjà câblé. [Build03](../../artifacts/linux/command-log-geometry-20261020/build-03/result.json) conserve l’accès produit refusé à IWindowImpl.WindowState : utiliser l’état public publié synchroniquement, sans réflexion produit. Ces erreurs de compilation sont distinctes du [rouge exécuté 109/114](../../artifacts/linux/command-log-geometry-20261020/review/root-red-review-01.json) : quatre dernières bornes normales perdues avant Max/Min et Open périmé après rebootstrap. Snapshot immédiat et profil partagé versionné corrigent les cinq cas, mêmes tests ; [ciblé03 114/114](../../artifacts/linux/command-log-geometry-20261020/review/root-targeted-review.json). Les suites complètes, gestes natifs et paquet conservent leurs preuves distinctes.

[Desktop-full01 interrompu volontairement](../../artifacts/linux/command-log-geometry-20261020/review/full01-stop-request.json) : délai de 1 800 s trop court face aux 33 min observées auparavant. Retrait indépendant acquis, retour nonzero et résultat incomplet conservés ; aucune ligne Passed ne devient verdict complet. La [suite complète fraîche](../../artifacts/linux/command-log-geometry-20261020/review/root-full-review.json), le [natif source](../../artifacts/linux/command-log-geometry-20261020/review/root-native-review.json) et le [paquet isolé](../../artifacts/linux/command-log-geometry-20261020/review/root-package-review.json) ont leurs preuves distinctes. Le natif ne certifie ni premier mapping/no-flash, hot-DPI/Wayland ni interactions installées.

## Journal des processus externes — fixtures

[Ciblé01](../../artifacts/linux/command-log-external-process-20261020/targeted-01/results/desktop.trx) : 185 passés, huit échecs puis interruption après 120 s d’inactivité. Le banc devait ouvrir son second SafeHandle avant la libération du processus et attendre réellement la continuation Avalonia ; tests03 corrige ces préconditions, sans changer les oracles de code, PID, confidentialité ou drain.

[Ciblé02](../../artifacts/linux/command-log-external-process-20261020/targeted-02/desktop.log) : un Git absent produisait une sortie shell 127 avec attente Enter, pas l’erreur helper attendue. Le répertoire absent introduit ensuite par tests04 contenait le secret de fixture dans Directory : [ciblé03](../../artifacts/linux/command-log-external-process-20261020/targeted-03/results/desktop.trx) conserve 328/329 passés et un échec. Root05 utilise un répertoire absent neutre et conserve les arguments secrets dans la vraie requête. Les assertions restent inchangées ; [ciblé final](../../artifacts/linux/command-log-external-process-20261020/review/root-targeted-review.json) et [suites complètes](../../artifacts/linux/command-log-external-process-20261020/review/root-full-review.json) ont leurs propres résultats.

[Natif01](../../artifacts/linux/command-log-external-process-20261020/native-01/root-failed-retirement-review.json) : le parcours UI avait fini, mais le nettoyage a reçu EPERM sans enregistrer le PID refusé. Aucun verdict natif ni inventaire après n’est déduit de cet essai. Le banc02 utilise un bus privé sans activation de services et consigne chaque signal ; cela ne prouve pas la cause du refus01. Le [natif02](../../artifacts/linux/command-log-external-process-20261020/review/root-native-review.json) a fini normalement, avec 18 constats visuels, deux fins Browse à 0 et deux waitpid Gitk à 0, sans erreur de nettoyage.

[Inventaire01](../../artifacts/linux/command-log-external-process-20261020/inventory-01/root-failed-retirement-review.json) : génération refusée, ADR0142 référencé avant sa création. Enregistrer l’ADR métier avant de générer la matrice ; [inventaire02](../../artifacts/linux/command-log-external-process-20261020/review/root-inventory-review.json) : 2497 C#, zéro diagnostic, niveaux inchangés.

## Compléments portables — lectures et fermeture

Les essais L25 restent conservés dans `artifacts/linux/portable-dialog-completions-20261020/`. Ciblé01 : 103/138, puis ciblé02 : 201/207. Corrections : publication readonly de l’aperçu patch, vrai ItemsPresenter dans le template sans onglets, ouverture réelle du Diff avant recherche des feuilles, fermeture idempotente et baseline du lecteur. Le nouveau test GPG corrige son hypothèse : les champs étoilés for-each-ref pèlent aussi les tags imbriqués, comme Windows.

Ciblé03 a été interrompu par l’inactivité après 123 résultats (121 passés, deux échecs) : Assert.ThrowsAsync bloquait la continuation Avalonia du nouveau test de stash. Await explicite conserve l’oracle exact InvalidOperationException. Le groupe Combined se déplie par l’en-tête et Droite : la configuration Windows ShowRootLines=false ne fournit pas de chevron racine utilisable ; aucun gutter produit modifié pour le pilote. Les anciens tests Blame sont raccordés au nouveau modal en gardant les mêmes OID, contenus et assertions Git.

Ciblé04 : 398/405 ; ciblé05 : 71/79. Les diagnostics d’ordinal montrent que le premier clic passe, mais désactive l’arbre pendant Refresh avant le second : ce défaut produit est distinct des hypothèses initiales de publication du hit-test/centre du header. Ciblé05 révèle aussi AuthorMargin.Render lisant le placeholder Bitmap libéré pendant le drain de fermeture de Blame. Les tests réels de double clic et de fermeture restent stricts.

Les builds refusés 01/02/05/07 restent distincts : propriété ListBox inexistante, surcharge NUnit, appel à une API Modal étrangère au lot, puis identifiant field réservé par C# 14. Build04 a recompilé l’ancien candidat après refus d’import sur une modification documentaire indépendante ; il ne qualifie pas le candidat suivant. Les fichiers étrangers du primaire restent hors source testée, leurs dérives README/feature-map étant observées séparément.

Le correctif du panneau gauche rejoint GoToRef Windows avec une lecture annulable, sans délai arbitraire ; deux anciens cas qui exigeaient le filtrage Linux sont réalignés en conservant leurs identités et leurs oracles Git/sélection/critères. Ciblé06 : 100/102 ; les huit cas de lecture concurrente et les deux rendus après Dispose passent. Ses deux échecs restants visent le centre transparent du libellé après checkout : le dernier clic du pilote vise désormais son vrai texte. Build11 conserve le refus CA1416 du nouveau processus privé ; son constructeur porte maintenant le garde Linux explicite.

Ciblé12 : 158/172 succès, quatorze échecs. Douze nouveaux tests Ctrl+K interdisaient aussi le merge-base légitime du lecteur de comparaison ; le ciblé13 diagnostique garde le prédicat initial et donne 6/18 succès, douze échecs ne contenant que ce verbe sensible. Les piles du cache asynchrone ne permettent pas un whitelist fiable par appelant. Le nouvel oracle comportemental conserve les dix-huit cas, interdit toujours log/status/fetch et vérifie désormais la référence du graphe, l’identité et l’ordre de chaque ligne ; les no-op gardent leur absence totale de nouvelle commande. Aucun contournement produit pour rendre cet oracle vert.

Les deux autres échecs du ciblé12 distinguent un pilote cherchant WorkingFileStatusList dans un onglet inactif et un vrai veto de fermeture imbriquée non repris après drainage. Le pilote lit le contrôleur existant ; le correctif produit partage DeferredOwnerCloseReplay entre les deux bases, sans deuxième Close dans l’oracle ni polling. L’audit trouve déjà le replay dans Main, Commit, Settings et GitProcess ; Init et le flush tracking hors IsBusy reçoivent des scénarios Git privés. Les deux cas rapides Ctrl+double clic et cinq scénarios Log passent déjà dans ce ciblé, sans être transférés comme qualification du correctif ultérieur.

Revue des candidats G2/G3/G4 : raccord Select in left panel manquant aux marques ; ancien Settings écrasant le choix spelling d’un autre Commit ; drapeau regex ignoré par l’adaptateur et reçu d’effets possibles manquant après native nonzero. Les candidats corrigent ces chemins avec scénarios de gestes réels, sans changer les anciens oracles Plugins de lease rejeté. Build22 compile le produit mais échoue sur une annotation nullable du nouveau test de caption ; l’assertion NotNull est conservée et l’annotation corrigée. Build23 réussit les trois projets ; qualification finale du lot liée aux fulls frais ci-dessus.

Ciblé14 : 345/375 succès, 30 échecs réels et 12 acteurs retirés. Vingt-sept cas de branches obsolètes révèlent TextChanged Avalonia différé : les notifications des valeurs initiales effacent une preview plus récente. Les trois champs observent désormais TextProperty synchroniquement ; aucun délai produit ajouté. Les fixtures natives reconnaissent le verbe après les options globales Git sans modifier argv. Le pilote Remotes clique son véritable onglet avant édition. Un cas de curseur Ctrl+clic et un cas de fermeture imbriquée Log restent séparés, sans suppression d’oracles.

Ciblé15 : 37/37 UI (branches obsolètes et replay propriétaire) et 42/42 Plugins passent après ces corrections, sans skip ni contournement. Les nouvelles preview restent stables après rendu et le premier clic conserve le focus. Les captures EN/clair et FR/sombre sont relues en headless ; aucun verdict natif déduit. Les deux défauts G2/Log du ciblé14 restent à traiter séparément.

Ciblé14, curseur G2 : la trace observe un autre TreeViewItem après deux pressions rapprochées sur la même branche ; le checkout lui-même n’est pas journalisé. Les sources Windows et Linux routent le vrai double clic vers Checkout même avec Ctrl. Le scénario de clics indépendants attend donc le vrai délai de plateforme et exige DoubleTapped=0, sans retirer les assertions de curseur/marques/focus ni changer le produit.

Ciblé14, fermeture Log : le descendant occupé reçoit OwnerWindowClosing, puis ferme Log après drainage ; Browse ne voit jamais Closing et reste ouvert. Le Browse possédant ce modal emprunte temporairement OwnerWindowOnly pour sceller son propre token avant le veto descendant. La policy précédente est restaurée ; un nouveau cas vérifie que fermer seulement Log garde Browse ouvert et publie les effets acquis. Aucun replay aveugle vers le grand-parent.

Ciblé16 : 376/377 succès, un échec et aucun skip. Ctrl+K, références, page Commit, branches obsolètes et fermeture Log seul passent. Le Close du Browse scelle maintenant son lifetime, mais son Post(log.Close) laisse la continuation libérée gagner avant l’annulation de l’enfant ; CancellationWasRequested reste faux. Les traces conservent cet ordre précis. La correction vise uniquement le callback UI, sans délai de test ni assertion retirée.

Ciblé17 : 104/104 UI et 42/42 Plugins passent après transmission synchrone sur le thread UI ; les deux gestes de fermeture annulent le vrai token avant libération de la tail et drainent ensuite leurs effets. Treize acteurs retirés, mêmes sources/binaires. Les 376 autres succès du ciblé16 gardent leur portée ; la suite complète fraîche reste nécessaire sur le commit de produit 1c6068f4017eae8344eee25ad3f7bd280cbaa847.

Validation complète fraîche de 1c6068f4017eae8344eee25ad3f7bd280cbaa847 : Desktop03 4051/4051 (333 classes, 5539/0, 217 acteurs retirés), Core01 1144/1144 (12115/0, 207 acteurs) et Plugins01 42/42 (95768/0, cinq acteurs). Les 3763 identités Desktop historiques et les scénarios Core/Plugins antérieurs sont conservés ; aucun skip ni ancien échec requalifié. Build27 exact : trois projets, zéro avertissement/erreur, 19597/0 et 18 acteurs retirés. Les preuves natives et paquets historiques gardent leurs propres sources ; le paquet de ce lot reste à vérifier séparément.

Ciblé07 : 27/27 confirmations et Core comparaisons 9/9/15, mais Plugins 41/42. Le nouveau test HTML exigeait à tort les accents littéraux ; Windows et Linux emploient WebUtility.HtmlEncode. Le nouvel oracle conserve balises/ampersand échappés et texte Unicode après décodage, sans changer l’export produit. Ciblé08 passe 2/2 UI et 42/42 Plugins ; les suites complètes fraîches du lot gardent leur propre résultat. Full01 est interrompu après 37 succès : un ancien test de checkout attendait la nouvelle question du stash sans y répondre. État /proc et piles dotnet-stack conservés avant SIGINT ; attente réelle 82771/1 et sept identités retirées. Le pilote répond désormais physiquement No dans la vraie fenêtre possédée, avec tous anciens cas et assertions conservés. Ciblé09 passe 7/7 UI et 42/42 Plugins ; aucun verdict complet tiré de Full01.

Full02 exécute 3946 cas : 3940 succès et six échecs, sans skip/timeout. Deux oracles attendaient l’ancien filtrage automatique du panneau gauche ; quatre seconds clics étaient des doubles clics réels (ClickCount=2), ouvrant Log/CommitDiff. Le diagnostic ciblé10 conserve 58/62 succès et les quatre échecs ; aucun double clic produit neutralisé. Le pilote distingue désormais les clics indépendants par le délai réel de la plateforme et ajoute deux doubles clics rapides explicites. Ciblé11 : 104/106 succès ; ces deux nouveaux cas révèlent le caret désélectionné non restauré après Log. Le workflow readonly réutilise la garde F5, conserve les oracles et évite le refresh global inutile. Les reçus et les validations ultérieures gardent leur périmètre propre.

Prévol du producteur d’inventaire02 : 6983/1 avant toute phase SDK/analyse. Le hash de __file__ recevait une str au lieu de Path ; le producteur03 normalise Path(p), sans modification du générateur produit. Les fichiers avant-échec restent byte-identiques dans inventory-preflight-01, déplacement tracé séparément. Aucun résultat d’inventaire n’est déduit de cet arrêt ; compilation produit, suites et paquet exact restent inchangés.

Inventaire01, producteur03 : build et recensement réussissent (2547 C#, zéro diagnostic), puis generate échoue sur CommitDialogSettingsValues. Le recenseur historique exporte les classes, pas les records ; le support est donc référencé par son vrai fichier source file:, sans inventer un type recensé ni modifier niveau/aspect. Les cinq acteurs sont retirés après 48573/1 ; preuves de cet échec préservées. La relance utilise inventory-02 et ne remplace aucun reçu.


## Compléments L26/L27 — oracles et chemins partagés

L26 a exposé l’ordre des handlers : capturer Ctrl après la sélection perdait le geste initiateur ; la capture se fait maintenant dans le même handler Tunnel avant modification/focus. Alt+Gauche était consommé par TreeView avant Back ; le handler partagé traite l’historique après la garde du propriétaire et avant l’arbre. Les échecs bruts restent conservés ; le [ciblé navigation C9](../../artifacts/linux/reference-selection-completion-20261021/targeted-05/root-retirement-review.json) passe 249/249 sans retirer les cas historiques.

L27 : ciblé01 161/164, puis ciblé02 471/472. Le fixture script configurait un hotkey avant Scripts.Save : ordre du fixture corrigé, route réelle et assertions conservées. La mesure française compte un espace de fin de ligne invisible ; captures et HasCollapsed confirment les neuf champs visibles. L’oracle utilise TextLayout.Width, même tolérance/containment, sans changer les dimensions produit. Build03 avait deux références nullable dans le nouveau test : NotNull conservé, annotations seules.

Le refus du vrai éditeur Git modifie l’extension TREE de l’index (118→137 octets), après écriture du cache et avant l’éditeur. [Témoin Git seul](../../artifacts/linux/portable-settings-completion-20261021/review/root-native-index-control-review.json) puis [octets UI capturés](../../artifacts/linux/portable-settings-completion-20261021/review/root-editor-failure-oracle-review.json) prouvent mêmes en-tête/entrées et seules métadonnées TREE recalculées. Le nouveau test compare désormais la sortie binaire complète ls-files --stage -z, avec HEAD, contenu préparé, absence de faux commit/push et drainage inchangés ; les oracles byte-exacts index.lock restent stricts. Aucun rollback produit pour satisfaire le cache du test.

Lors du natif L26, les courtes références déplaçaient horizontalement tout l’arbre. Borner la révélation à l’entête mesurée corrige ce défaut, mais le ciblé06 (9/10) révélait encore une accumulation avant layout : trois demandes donnaient X=-144. Le diagnostic brut conserve ces mesures. Le composant partagé compense uniquement le déplacement X en attente sur son hôte natif compatible ; Y et hauteur restent Avalonia. Les dix cas initiaux sont conservés, quatre cas ajoutés ; [ciblé08](../../artifacts/linux/reference-selection-completion-20261021/targeted-08/root-retirement-review.json) 14/14 et [ciblé final](../../artifacts/linux/reference-selection-completion-20261021/targeted-07/root-retirement-review.json) couvrent aussi les consommateurs. Le premier natif reste explicitement refusé pour ce défaut horizontal ; seul le nouvel essai est qualifié.

Le premier contrôle du paquet s’est arrêté avant le GUI faute de xvfb-run dans PATH. Le même .deb, sans reconstruction, est repris avec le Xvfb privé épinglé ; cinq contrôles réels complètent publish/build. La tentative échouée reste archivée.

[Final L27](../../artifacts/linux/portable-settings-completion-20261021/review/root-targeted-review.json) : 472/472 Desktop, 42/42 Plugins ; [Core complet](../../artifacts/linux/portable-settings-completion-20261021/review/root-core-full-review.json) 1144/1144. Preuves [natives](../../artifacts/linux/reference-selection-completion-20261021/review/root-native-review.json) et [paquet](../../artifacts/linux/portable-settings-completion-20261021/review/root-package-review.json) séparées. La référence complète Desktop L25 reste historique ; aucun échec antérieur requalifié, niveau N4 ou interaction installée déduit.

## Suite Plugins native — préparation de l’index

Le premier passage du lot ADR0162 conserve 41/42 scénarios : les blobs insérés directement dans l’index ne sont pas présents dans le worktree et le contrôle d’état propre de Find large files échoue. Les fichiers représentables sont désormais matérialisés ; les noms NTFS impossibles restent de vraies entrées Git avec CE_VALID. Le contrôle de propreté, les chemins opaques et les instantanés complets du dépôt restent exigés. Dernier passage local : 42/42, mêmes identités, avec arrêt vérifié par référence noyau. Journaux initiaux et corrigés : `artifacts/cross-platform/20261103/plugin-portability-local`. Les exécutions natives Windows/Mac de ce lot restent à qualifier.

## Qualification Plugins Windows et attentes de sortie

ADR0162 : les deux Mac passent leurs 42 cas ; Windows expose 37/42. Deux scénarios Statistics écrivaient des noms impossibles sur NTFS, une solution contenait `*`, un préfixe magique devenait littéral dans GitRepository et le PID de `cmd/git.exe` différait du vrai client Trace2. Les arbres Git opaques sont conservés ; LOC emploie un vrai fichier valide et vérifie aussi l’entrée HEAD d’origine. Renommages/copies sont construits dans le vrai index. Le vrai Git est sélectionné depuis son exec-path et le pipe vérifie PID et octets sans les lire. La qualification Windows du correctif reste requise. Preuve initiale : `runs/41728467879/win-x64/job.log`.

ADR0163 : les deux sondages à 10 ms allongeaient Core CI de 326 à 1 552 s. SIGCHLD privé et attente WNOWAIT bloquante retirent cette latence sans libérer les PID plus tôt. Le premier benchmark mélangeait DLL ancienne et helpers récents à cause du cache de référence MSBuild ; échec conservé, mesure reprise dans un projet isolé avec quatre SHA appariés. Médiane sur 200 vrais appels : 21,24 → 4,67 ms ; Core local 1 210/1 210 en 481 s, cinq contrats noyau, 42 Plugins, 151 Desktop et cinq contrôles Debian réussis. Les logs bruts et la première erreur restent dans `artifacts/cross-platform/20261103/process-exit-events`.

Le passage Windows `ed1161b8c` remonte à 41/42 ; l’unique erreur restante est un pipe déjà fermé avant Peek (109), avec le véritable PID Git désormais correct. La fixture ne considère plus une connexion initiale comme une preuve Trace2 : plusieurs instances restent prêtes, les connexions transitoires fermées sont remplacées, et seuls PID authentifié plus octets réellement en attente permettent la suite du test. Aucune lecture ne débloque Git avant annulation. Log initial : `runs/41732665414/win-x64/job.log`. Qualification Windows du correctif toujours requise.
