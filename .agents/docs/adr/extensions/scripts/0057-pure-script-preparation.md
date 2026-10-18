# ADR 0057 · Catalogue et exécution des scripts historiques

Statut : runtime intégré, suites complètes de la source `19bbfed70` acceptées ; paquet exact vérifié ; natif S01 ciblé vérifié. Aucune acceptation ancienne transférée.

Reprendre les scripts de l’utilisateur, leurs emplacements et leurs événements sans perdre le contexte ni modifier silencieusement le programme exécuté.

Le catalogue conserve champs, IDs et ordre Windows, ainsi que huit exemples désactivés. Settings édite un brouillon ; Cancel l’abandonne, OK sauvegarde le XML OwnScripts dans scripts.json du profil XDG. Un changement concurrent observable ou un profil illisible donne un diagnostic, sans remplacement silencieux ni CAS interprocessus. Scripts et Hotkeys ne forment pas une transaction globale ; des écritures déjà réussies restent acquises après une erreur ultérieure. Les touches Scripts suivent les IDs stables, même après renommage ou déplacement ; menus, toolbar et raccourcis utilisent le même exécuteur. L’import XML/legacy reste explicite ; aucun import global Windows implicite.

ScriptHostContext matérialise révisions, références et fichiers avant les demandes typées. ScriptTemplate prépare une seule fois l’aperçu historique et un plan d’exécution. L’aperçu n’est pas réversible : ne jamais reconstruire argv depuis Quote. Les doubles {{x}} gardent les valeurs exactes ; les simples {x} restent des fragments RAW, tokenisés suivant la grammaire Linux sans shell implicite. Quote ou escape incomplet donne un refus diagnostiqué.

Pour un shell POSIX explicitement choisi avec -c, le programme groupé par l’auteur conserve un seul argument ; ses doubles marqueurs nus deviennent des littéraux POSIX par valeur. Les quotes internes autour d’un marqueur sont refusées. Ce traitement lexical n’est pas une AST shell générale ; le RAW du modèle VS Code garde notamment sa limite avec les apostrophes. Les huit définitions ne sont pas réécrites. PowerShell utilise explicitement pwsh -Command ; la console Windows NoExit n’est pas reproduite.

ScriptProcessRunner réutilise le cycle natif de GitRunner avec environnement hérité, argv séparés et sans politique Git/askpass ajoutée. Started signifie exec observé ; résultat signifie wait et drainage réels. Foreground utilise le Process possédé ; background rend Started tôt mais reste supervisé jusqu’à fermeture/drainage. Un échec peut avoir modifié le dépôt.

ScriptWorkflowScope arrête les continuations après fermeture. Les Before/After restent aux frontières Windows, avec nouveau contexte par script ; pas de règle After-success uniforme ni événement Clone. Les mutex sont relâchés pendant l’UI ; les phases distantes et sous-module préparées revalident leurs gardes ensuite.

Repères : Core/Scripts, GitRunner, ScriptsSettingsPage, ScriptsPreferencesStore, DesktopScriptExecutor, MainWindow.Scripts et ScriptWorkflowScope. Limites : icônes associées Windows, outils/OS indisponibles, variantes natives et import global. Preuves et périmètres : [suites complètes](../../../../../artifacts/linux/scripts-runtime-20261018/full-02/parent-verification.json) et [TODO](../../../todo.md#validation-courante--scripts-runtime) ; paquet exact vérifié ; natif S01 ciblé vérifié. Le natif vérifié reste limité à S01 ; les événements ne reçoivent aucune preuve native de ce parcours.
