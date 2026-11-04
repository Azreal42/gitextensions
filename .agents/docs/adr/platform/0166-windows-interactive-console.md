# ADR 0166 · Console interactive Windows — prototype Core

Statut : prototype Core compilé ; aucun contrat de console exécuté sur Windows. [Propriété native](0160-native-process-ownership.md), [terminal POSIX](0164-native-posix-terminal.md).

WindowsGitProcess conserve son chemin sans fenêtre et ses pipes par défaut. Deux modes explicites s’ajoutent : héritage des vrais handles console du caller, ou nouveau host console sans héritage de handles. La création reste suspendue jusqu’à l’assignation au job privé. Le mode interactif retire CREATE_NO_WINDOW et transmet seulement les trois copies héritables attendues. [Héritage des handles](https://learn.microsoft.com/en-us/windows/win32/sysinfo/handle-inheritance), [handles console](https://learn.microsoft.com/en-us/windows/console/console-handles).

GitInteractiveTerminalProcess possède désormais un chemin Windows : statut naturel après sortie du primaire et job vide ; annulation par arrêt du job puis drainage. Les modes et pages de code du caller sont sauvegardés avec des références privées et restaurés après sortie, y compris si Git est terminé de force. WindowsConsoleHost prépare le job extérieur du futur helper Desktop ; sa libération asynchrone attend la sortie des acteurs.

Trois contrats natifs sont encodés : vrai nouveau host et console, argv/environnement/répertoire littéraux, saisie Unicode via WriteConsoleInput, statuts 0/73, annulation, observation indépendante du vrai enfant et restauration des modes/pages de code. Le child change ECHO avant son signal de disponibilité et les encodages avant sortie. La CI Windows les sélectionne avec ses cinq contrats noyau existants.

Validation actuelle : compilation des trois projets sans avertissement/erreur ; dix contrats POSIX passent après extraction du chemin commun. Cette vérification Linux ne prouve aucune API console Windows. Les trois nouveaux cas restent à exécuter sur une vraie machine Windows ; GitHub refuse actuellement les nouveaux runs (HTTP422 malgré enabled=true).

Restent : helper Desktop Windows, allocation/ouverture des handles de console, pipe CurrentUserOnly et authentification du PID réellement lancé avant ACK, fermeture/annulation et journal avec maintien du verrou jusqu’au job vide, vrai hunk Git, intégration avec le raccord Desktop Mac, suites métier natives complètes. Ne pas promouvoir ce prototype comme console Windows qualifiée.

Repères : WindowsGitProcess, WindowsConsoleModes, WindowsConsoleHost, GitInteractiveTerminalProcess.Windows ; WindowsConsoleProbe et WindowsTerminalTests. Preuves : artifacts/cross-platform/20261104/windows-console.
