# ADR 0165 · Console interactive Desktop macOS

Statut : implémenté, qualification Linux locale réussie ; exécution native Mac encore requise. [PTY commun](0164-native-posix-terminal.md), [propriété des processus](0160-native-process-ownership.md).

Le lanceur et le helper Desktop partagent le protocole Linux/macOS : identité noyau du pair, nonce avant ACK, environnement capturé, résultat Git puis attente de la vraie sortie du helper. Le propriétaire conserve son verrou jusque-là. EOF annule l’opération ; il ne prouve pas sa fin. Le répertoire privé reste mode0700, les requêtes et sockets mode0600 ; la borne UTF-8 passe à 103 octets sur Darwin, avec le même repli court pour TMPDIR long.

Linux transfère son pidfd par SCM_RIGHTS avant le hello : le parent ne rouvre plus le PID numérique. Darwin interdit le transfert d’une kqueue ; le récepteur crée sa propre observation pendant que le helper attend ACK, puis revérifie le pair encore connecté. Le pont C utilise les vrais en-têtes pour UID/PID et kevent ; l’attente dédiée ne bloque pas le ThreadPool. Aucun terminal serveur n’est signalé. [Noyau Apple : descripteurs transférables](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/kern_descrip.c), [options du pair Unix](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/un.h).

LaunchServices ouvre Terminal.app avec un fichier .command privé créé exclusivement. Un exec avec env -i transmet l’environnement capturé, les arguments littéraux et le répertoire ; chaque valeur est citée séparément. Le fichier et le répertoire restent vivants jusqu’à la sortie du helper. Les choix de terminaux Linux ne sont pas utilisés pour ce lancement Mac.

Validation locale : zéro avertissement/erreur ; 14 contrats noyau/PTY/IPC, 192 Desktop combinés, 1 210 Core, 42 Plugins et cinq contrôles Debian, aucun ignoré. Les identités Core/Plugins antérieures restent présentes. Trois parcours utilisent le vrai helper sous un terminal externe : résultat Git et acquittement, staging d’un hunk, annulation avec index conservé. Trois autres exécutent le transport .command avec arguments et environnement hostiles, mode privé et refus de remplacement/lien. La CI Mac encode ces six cas et les quatre nouveaux contrats IPC ; Linux garde ses suites complètes.

Limites : ouverture réelle de Terminal.app et ces parcours sur Mac non qualifiés ; GitHub refuse actuellement la création des runs (HTTP422, Actions désactivé effectivement malgré enabled=true dans les permissions du dépôt). Console/IPC Windows, descendants Mac sortis de leur groupe, suites Core/Desktop natives complètes et audits GUI restent ouverts. Aucune clôture du portage global. Preuves : artifacts/cross-platform/20261104/mac-console.

Repères : PosixSocketPeerProcess, gitextensions-posix-bridge.c, MacInteractiveConsoleTerminal, InteractiveFileConsoleLauncher/Application ; PosixSocketPeerProcessTests, MacInteractiveConsoleTerminalTests, PosixInteractiveConsoleIntegrationTests.
