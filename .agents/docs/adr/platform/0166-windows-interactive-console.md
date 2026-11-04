# ADR 0166 · Console interactive Windows — prototype Core

Statut : Core et raccord Desktop compilés ; aucun contrat de console exécuté sur Windows. [Propriété native](0160-native-process-ownership.md), [terminal POSIX](0164-native-posix-terminal.md).

WindowsGitProcess conserve son chemin sans fenêtre et ses pipes par défaut. Deux modes explicites s’ajoutent : héritage des vrais handles console du caller, ou nouveau host console sans héritage de handles. La création reste suspendue jusqu’à l’assignation au job privé. Le mode interactif retire CREATE_NO_WINDOW et transmet seulement les trois copies héritables attendues. [Héritage des handles](https://learn.microsoft.com/en-us/windows/win32/sysinfo/handle-inheritance), [handles console](https://learn.microsoft.com/en-us/windows/console/console-handles).

GitInteractiveTerminalProcess possède désormais un chemin Windows : statut naturel après sortie du primaire et job vide ; annulation par arrêt du job puis drainage. Les modes et pages de code du caller sont sauvegardés avec des références privées et restaurés après sortie, y compris si Git est terminé de force. WindowsConsoleHost prépare le job extérieur du futur helper Desktop ; sa libération asynchrone attend la sortie des acteurs.

Trois contrats natifs sont encodés : vrai nouveau host et console, argv/environnement/répertoire littéraux, saisie Unicode via WriteConsoleInput, statuts 0/73, annulation, observation indépendante du vrai enfant et restauration des modes/pages de code. Le child change ECHO avant son signal de disponibilité et les encodages avant sortie. La CI Windows les sélectionne avec ses cinq contrats noyau existants.

Validation actuelle : compilation des trois projets sans avertissement/erreur ; dix contrats POSIX passent après extraction du chemin commun. Cette vérification Linux ne prouve aucune API console Windows. Les trois nouveaux cas restent à exécuter sur une vraie machine Windows ; GitHub refuse actuellement les nouveaux runs (HTTP422 malgré enabled=true).

Les éléments Desktop initialement manquants sont implémentés dans le lot ci-dessous. Leur exécution Windows et les suites métier natives complètes restent requises ; aucun état de console Windows qualifiée n’est déclaré.

Repères : WindowsGitProcess, WindowsConsoleModes, WindowsConsoleHost, GitInteractiveTerminalProcess.Windows ; WindowsConsoleProbe et WindowsTerminalTests. Preuves : artifacts/cross-platform/20261104/windows-console.

Le raccord Desktop est désormais écrit et compilé avec les quatre contrats Windows explicitement inclus : helper WinExe dans un nouveau job/console, pipe CurrentUserOnly, PID client égal au processus lancé, PID serveur égal au parent et nonce avant ACK. Le parent attend le vrai helper et le job vide ; les lectures admises sont jointes après arrêt. Le helper reçoit annulation/EOF, attend Enter sans verrou Console.In et traite la fermeture native sans dépendre d’une dernière écriture console. Le parent garde son job extérieur comme repli si Windows termine le helper.

Quatre parcours Windows sont encodés : staging d’un vrai hunk, annulation, fermeture de la console, fermeture de l’owner avec mutation concurrente retenue. Un observateur séparé s’attache seulement à la console dédiée et utilise ses vrais événements clavier et son buffer visible. Ces parcours sont compilés sur leur OS natif ; CompileWindowsConsoleTests=true permet aussi leur vérification syntaxique hors Windows. Aucun test Linux existant n’est retiré.

Validation du source combiné : zéro avertissement/erreur ; 14 contrats POSIX, 192 Desktop sélectionnés, 1 210 Core, 42 Plugins et cinq contrôles Debian, zéro ignoré. Les trois nouveaux cas console Core et quatre Desktop Windows sont seulement compilés. La source POSIX précédente 1bdf47e27 a en plus passé ses 4 631 Desktop complets et son paquet exact ; ce résultat n’est pas transféré au présent source sans nouveau passage. Preuves : artifacts/cross-platform/20261104/windows-console.

Restent : exécution Windows des sept nouveaux cas, qualification réelle Mac du nouveau terminal/IPC, suites métier complètes Windows/Mac, lancement Tools Ctrl+G et choix de terminaux par plateforme, descendants Darwin hors groupe et audits natifs étendus. GitHub refuse encore les nouveaux runs malgré les permissions du dépôt activées.
