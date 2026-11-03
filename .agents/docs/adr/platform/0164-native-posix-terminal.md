# ADR 0164 · Terminal interactif POSIX natif

Statut : implémenté et testé sur Linux ; qualification macOS requise. [Cycle de vie](0161-posix-process-lifecycle.md), [notifications](0163-process-exit-notifications.md).

Le Core partage désormais son vrai PTY entre Linux et macOS : `openpty`, puis `login_tty` avant exec dans le superviseur réservé. `GitInteractiveTerminalProcess` utilise ce moteur sans `script` util-linux ni commande shell. Les arguments restent littéraux, PATH et environnement sont capturés ; l’éditeur utilisateur est conservé. Le protocole ACK précède toujours le fork Git, les erreurs exec sont rapportées et le propriétaire attend le drainage des acteurs.

Deux files de 64 Kio bornent le relais non bloquant. Le mode console hérite des descripteurs du terminal en les dupliquant explicitement ; le mode flux garde les pipes privés. Le terminal appelant passe temporairement en mode brut, retrouve ses attributs avant retour et transmet les changements de taille. L’arrêt reste réactif lorsque la sortie est saturée ; EOF du PTY ne remplace jamais la preuve de sortie des descendants.

Cinq contrats réels complètent les cinq contrats noyau : session/terminal de contrôle, stdin/out/err TTY et argv/environnement, sorties 0/73, véritable `git add --patch` par l’API publique avec restauration contrôlée par `stty -g`, enfant survivant sans descripteur TTY, et écrivain de 32 Mio bloqué puis arrêté. Les dix cas passent localement, sans ignoré. Les 186 tests Desktop combinant contrats portables et consoles passent également, ainsi que les 1 210 Core, 42 Plugins et cinq contrôles Debian. Compilation sans avertissement/erreur, trois ELF compatibles glibc 2.34. La CI Linux garde ses suites complètes et sélectionne les cinq nouveaux contrats ; les deux jobs macOS les ajoutent à leurs six contrats, Windows conserve ses cinq contrats.

Limites : lanceur Desktop et IPC encore Linux, console Windows à porter, descendants Darwin sortis de leur groupe et suites métier natives complètes encore ouverts. Le Core commun ne vaut pas qualification des fenêtres de console macOS. Journaux : `artifacts/cross-platform/20261104/posix-terminal`.

Repères : `src/linux/GitExtensions.Core/Native/gitextensions-terminal.h`, `GitInteractiveTerminalProcess.cs`, `PosixGitProcess.cs` ; `tests/linux/GitExtensions.Native.Tests/PosixTerminalTests.cs`.
