# ADR 0163 · Notifications de sortie des processus

Statut : implémenté, suites locales réussies ; qualification CI finale en cours. [Cycle de vie POSIX](0161-posix-process-lifecycle.md), [suite Plugins native](0162-native-plugin-suite.md).

Les attentes périodiques du superviseur et du parent managé ajoutaient environ 20 ms à chaque commande courte. La suite Core CI passait de 326 à 1 552 secondes entre les sources `226d0b29a` et `44ccea0c4`, tout en restant fonctionnelle. Deux cents vrais appels `git --version`, après échauffement, mesurent une médiane de 21,24 ms avant et 4,67 ms après, sur la même machine ; ce gain local ne vaut pas mesure native Mac ou Windows.

Le superviseur attend SIGCHLD via un pipe privé non bloquant et CLOEXEC. Le handler écrit un octet en préservant errno ; il ne récollecte rien. L’événement reste lisible si la sortie précède poll. Tant que le primaire vit, seules les notifications et commandes réveillent le superviseur. La cadence de 10 ms demeure pendant la grâce d’arrêt et le drainage des descendants, notamment après récollection d’un pont adopté. Propriété pidfd/groupe, signaux, ACK avant fork, terminal report et vérifications des vrais acteurs restent inchangés.

Le parent attend `waitid(WEXITED | WNOWAIT)` sur un thread dédié, sans timer ni occupation bloquante du ThreadPool. Le superviseur reste non récollecté : son PID demeure réservé jusqu’au même Dispose propriétaire. Annulation avant HELLO, erreurs et flux admis doivent toujours terminer avant libération de l’opération.

Vérification locale : compilation sans avertissement/erreur ; cinq contrats noyau, 1 210 Core, 42 Plugins, 151 Desktop et cinq contrôles Debian réussis. Les identités Core/Plugins antérieures restent présentes. Core prend 481 secondes sur ce passage local, contre environ 27 minutes avant. Les trois ELF respectent le minimum glibc 2.34. Les suites et livraisons natives du commit final restent à qualifier ; aucun filtre, cas ignoré ou contrôle remplacé.

Le premier benchmark après modification avait réutilisé un cache MSBuild de référence DLL avec des helpers récents ; il échoue et reste archivé. Le projet de mesure est isolé, et les SHA de la DLL et des trois helpers doivent correspondre au produit avant exécution. Journaux, échantillons et contrôles : `artifacts/cross-platform/20261103/process-exit-events`.

Repères : [waitid et WNOWAIT](https://man7.org/linux/man-pages/man2/waitid.2.html), [fonctions sûres dans un handler](https://man7.org/linux/man-pages/man7/signal-safety.7.html).
