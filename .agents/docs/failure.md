# Failure

Ne garder ici que les causes qui évitent de refaire une impasse. Logs, candidats refusés et mesures historiques restent dans `artifacts/linux/`, avec leur source ; état du dernier paquet dans [TODO](todo.md).

## Limites ouvertes

| Observation | Cause / suite |
| --- | --- |
| Solution originale WinForms/Win32 | Portage UI et frontières OS encore incomplet ; le packaging ne résout pas cette dépendance. |
| Chemins Linux à octets non UTF-8 | API .NET basée sur string UTF-8 ; Unicode valide couvert, roundtrip des octets invalides à traiter. |
| APT sur OS vierge non exécuté | Installation/lancement DPKG isolés utilisent les dépendances hôte ; la simulation APT ne clôt pas ce gate. |
| Notes reflog personnalisées ambiguës | `%gs` n'est pas recodé par Git ; la relecture du sujet du commit couvre le cas standard, pas toutes les notes libres. |

## Git et état des workflows — résolus

| Piège | Cause / règle retenue |
| --- | --- |
| Stash non suivi laissé malgré exit 0 | `--literal-pathspecs` global casse le nettoyage de `stash push -u` ; réserver les pathspecs littéraux aux arguments concernés. |
| Bare bloque à `stash list` | Lire le reflog de `refs/stash`, sans exiger un worktree. |
| Action de fichier Stash revient aux changements courants | Le refresh générique perd la sélection ; comme FormStash, conserver le snapshot et ses fichiers après Cherry pick/Reset, puis relire le status pour les conflits. |
| R ne répond plus après un clic de fichier Stash | Le chargement du diff désactivait le TreeView pendant PointerPressed et effaçait son focus. Garder la liste immuable active ; IsBusy protège les commandes et la sélection suivante queue une relecture. |
| Stage all touche des fichiers masqués | Agir sur les feuilles du filtre d'affichage ; Selection Filter ne change que la sélection. Une liste vide ne devient pas mutation globale. |
| Indexer un renommage vise l'ancien nom absent | Indexer le nom courant ; désindexer les deux noms. |
| Skip am avance au conflit suivant mais affiche « appliqué » | Relire l'état am après exit non nul ; le code retour seul ne dit pas où la série s'est arrêtée. |
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
| Valeur initiale d’une combo disparaît à l’ouverture | AutoCompleteBox peut reporter le texte vide du gabarit après montage ; initialiser la sélection présente dans ItemsSource et tester la vraie activation modale. |
| Message déclaré Latin1 mais écrit UTF-8 | Codec strict partagé, stdin binaire et draft atomique ; valider avant toute mutation. |
| CP1252 du hunk appliqué aux chemins range-diff UTF-8 | Séparer sections contenu/metadata/fichier. Git peut tronquer un en-tête contenant newline ; ne pas reconstruire des octets absents. |
| Merge ignore `pull.ff=only` | `--no-rebase` prime aussi sur la config ff-only ; réappliquer `--ff-only`. Rebase garde la priorité de l'oracle Git natif. |
| Précontrôles Pull divergent de FormPull | Token Main dès l'initialisation ; [ All ] force Fetch, confirmation rebase garde No sans commande de repli. |
| Abandon du cherry-pick laisse le conflit sur une cible renommée | Le diff source seul ne décrit pas le checkout ; sauvegarder les cibles de renommage pertinentes et refuser les redirections hors couverture avant toute écriture. |
| Sous-module retiré revient après refresh | La dernière .gitmodules reste volontairement sur disque ; la liste suit les gitlinks de l'index, comme git submodule status. |
| Retrait incohérent avec .gitmodules modifiée | rm --cached refuse parfois avant Save+Stage ; Windows ignorait ce résultat. Préparer le futur contenu sur copies si des sections restent, garder les protections du gitlink et publier les fichiers sous verrous Git. |

## Lecteurs et UI — résolus

| Piège | Cause / règle retenue |
| --- | --- |
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

## Build et validation — repères

| Piège | Cause / sortie |
| --- | --- |
| SDK Snap inutilisable dans le sandbox | Utiliser le SDK officiel local `.dotnet` et une version complète dans global.json. |
| Version Debian contient HOME | Expansion Bash de `~` ; citer le caractère et vérifier le paquet reproductible. |
| `dotnet test` annonce succès sans scénarios Core/Plugins | Projets console IsTestProject=false : `dotnet run --no-build --no-restore` et compteurs des logs. Apphost et DOTNET_ROOT local requis pour helpers enfants. |
| VSTest arrêté avant tout scénario dans le sandbox | Socket TCP local refusé (`SocketException 13`) ; exécuter les tests isolés avec cet accès, garder l’essai arrêté hors validation. |
| Assert.ThrowsAsync bloque le dispatcher Avalonia | Attendre réellement la tâche et capturer l’exception ; l’assertion NUnit synchrone ne laisse pas avancer le modal. |
| Bouton visible mais clic central traverse au Panel | Un Background null local efface le style et son fond ; ClearValue rend aussi survol/pression au thème. Conserver un seul clic au centre dans le test. |
| Compteur Click reste nul malgré fermeture | Button.ClickEvent route en Bubble ; écouter la route réelle, sans rejouer le geste. |
| Tests Avalonia bloquent ou teardown masque l'exception | Attente async normale sur dispatcher, BindingPriority.Template pour TemplateBinding ; ne pas modifier l'isolation pour masquer le défaut. |
| AvaloniaTheory non découvert | Constructeur NUnit TheoryAttribute incompatible : entrées AvaloniaTest et parcours partagé. |
| Culture/Alt/HEAD du test ne correspondent pas à l'écran | Fixer culture avant construction, poser focus initial, sélectionner le vrai commit attendu ; réouverture conserve volontairement la sélection. |
| Pixels sélectionnés absents ou rejetés | Attendre une frame, exiger la vraie sélection, comparer le mélange anticrénelé plutôt qu'un pixel opaque. |
| Geste XTest envoyé avant mapping, focus logique sans focus X11 | Attendre IsViewable, matcher le préfixe du titre ; calibration du vrai modal sans WM explicitée. Ni preuve de premier clic ni preuve Wayland. |
| WM_DELETE_WINDOW absent dans Xvfb sans WM | Avalonia met en cache les atoms absents ; le harnais pose les trois noms standard avant GUI et garde la connexion jusqu’à la fin du driver. Aucun comportement du WM hôte revendiqué. |
| Plan natif vert puis launcher exit 2 | Figer/copier le launcher avant lancement, vérifier séparément exécution et fermeture. |
| Harnais ne trouve pas un titre contenant des guillemets | Lire le vrai titre via XFetchName et vérifier le PID par XRes ; ne pas découper la sortie de xwininfo sur le premier guillemet. |
| Coordinateur disparu malgré app/keeper exit 0 | Conserver l’exit extérieur et le rapport manquant ; ne pas inventer coordinator0. Session/groupe isolés et nouveau cas avant acceptation. |
| Manifeste du harnais contient son propre SHA vide | Écrire le manifeste après le gel des fichiers puis le hasher à l’extérieur ; conserver l’ancienne anomalie et vérifier chaque fichier réel. |
| Hash DPKG hôte différent après validation | Comparer avant/après dans l'intervalle du test ; changement hôte ultérieur consigné séparément. |

Les correctifs locaux du lecteur/Blame sont inclus dans les paquets livrés ; leurs audits natifs restent attribués aux lots d'origine. Les anciennes mentions « paquet suivant à valider » ne remplacent pas les limites actuelles du TODO.
