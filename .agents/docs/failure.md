# Failure

Ne garder ici que les causes qui évitent de refaire une impasse. Logs, candidats refusés et mesures historiques restent dans `artifacts/linux/`, avec leur source ; état du dernier paquet dans [TODO](todo.md).

## Limites ouvertes

| Observation | Cause / suite |
| --- | --- |
| Solution originale WinForms/Win32 | Portage UI et frontières OS encore incomplet ; le packaging ne résout pas cette dépendance. |
| Chemins Linux à octets non UTF-8 | API .NET basée sur string UTF-8 ; Unicode valide couvert, roundtrip des octets invalides à traiter. |
| APT sur OS vierge non exécuté | Installation/lancement DPKG isolés utilisent les dépendances hôte ; la simulation APT ne clôt pas ce gate. |
| Notes reflog personnalisées ambiguës | `%gs` n'est pas recodé par Git ; la relecture du sujet du commit couvre le cas standard, pas toutes les notes libres. |
| Avertissements IBus lors du lancement hôte | `Destroy` absent de `org.freedesktop.IBus.Service` dans le journal Avalonia. Effet sur la saisie et lien avec une fermeture non établis ; ne pas déduire un crash de ces seuls avertissements. [Journal](../../artifacts/linux/snapshots/aae27bff4/proofs/host-desktop-launch-20261015-01.log). |
| Lecture de branches Clone laisse des descendants actifs derrière un relais | Interrompre le relais avant de capturer ses enfants peut perdre l’ancre de descendants aux pipes intermédiaires. Capture avant signaux vérifiée par trois régressions avant/après et un Abort Clone natif clair sur `aae27bff4` ; reprise de la requête de branches et autres variantes encore requises. SID seul reste insuffisant ; nettoyage de l’ancien essai distinct de la réussite produit. |

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

## Lecteurs et UI — résolus

| Piège | Cause / règle retenue |
| --- | --- |
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
| Tests Avalonia bloquent ou teardown masque l'exception | Attente async normale sur dispatcher, BindingPriority.Template pour TemplateBinding ; ne pas modifier l'isolation pour masquer le défaut. |
| Test d’infobulle exige PopupRoot en headless | Le host réel est OverlayPopupHost ; tester son ouverture, son rattachement, le présentateur et ses pixels sans changer la configuration des popups. Le natif reste une validation distincte. |
| AvaloniaTheory non découvert | Constructeur NUnit TheoryAttribute incompatible : entrées AvaloniaTest et parcours partagé. |
| Culture/Alt/HEAD du test ne correspondent pas à l'écran | Fixer culture avant construction, poser focus initial, sélectionner le vrai commit attendu ; réouverture conserve volontairement la sélection. |
| Pixels sélectionnés absents ou rejetés | Attendre une frame, exiger la vraie sélection, comparer le mélange anticrénelé plutôt qu'un pixel opaque. |
| Geste XTest envoyé avant rendu, focus logique sans focus X11 | IsViewable peut précéder une frame encore noire ; mesurer une vraie frame rendue et le titre/PID avant input. Calibration sans WM explicite, sans preuve Wayland. |
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
