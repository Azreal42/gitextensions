# ADR 0162 · Suite Plugins sur les hôtes natifs

Statut : implémenté, qualification CI native à compléter. [Objectif trois plateformes](0159-avalonia-three-platforms.md), [supervision POSIX](0161-posix-process-lifecycle.md). Aucun changement produit ou de thème.

Les 42 scénarios Plugins gardent leurs noms et assertions métier. Un vrai apphost .NET, copié sous le nom de l’exécutable configuré, remplace les scripts Python Linux : argv littéraux, répertoire, PID journalisé, sorties, code 7, attente et annulation restent observés à la frontière du processus. Le profil Git est un fichier privé et les hooks un répertoire vide ; aucun `/dev/null` ni Python système n’est nécessaire.

Les blobs aux noms non représentables sur NTFS sont écrits par `hash-object`/`update-index` avec leurs octets d’origine. Git pose CE_VALID à l’insertion ; aucun nom opaque ne repasse par la normalisation Windows. Les autres fichiers sont matérialisés et le contrôle de propreté demeure. Le pathspec Windows `:(top,literal)` conserve le véritable antislash dans le nom stocké ; le cas POSIX conserve son argument antérieur.

L’annulation des notes de version vise toujours un véritable `git log`, pas un faux lecteur : ouverture FIFO bloquée sur POSIX ; écritures Trace2 bloquées dans un named pipe Windows de 4 Kio sans lecture du serveur. Le client Windows est vérifié par le noyau. L’image Git et sa référence stable (handle, pidfd ou kqueue) doivent être vivantes avant annulation et arrêtées avant son retour ; l’instantané intégral du dépôt reste identique. Les journaux et buffers du test restent privés.

Validation locale : compilation sans avertissement/erreur et 42/42 scénarios, identités antérieures conservées. Le premier passage 41/42 détectait des fichiers absents du worktree après préparation directe de l’index ; la matérialisation corrige la fixture sans retirer le contrôle d’état propre. La CI native ajoute la suite entière, sans filtre, et publie son journal ; le workflow Linux complet reste en place. Résultats : `artifacts/cross-platform/20261103/plugin-portability-*`.

Limites : les 151 contrats Desktop natifs restent un sous-ensemble ; les suites Core/Desktop complètes Windows/macOS et la console interactive/PTY/IPC restent ouvertes. La source `44ccea0c4` a déjà qualifié les trois backends graphiques et leurs archives, dont Apple Silicon, au périmètre précédent. Les nouveaux contrats Plugins doivent encore passer sur chaque hôte natif.

Repères : [Trace2 Git](https://github.com/git/git/blob/master/trace2/tr2_dst.c), [named pipes Git for Windows](https://github.com/git-for-windows/git/blob/main/compat/mingw.c), [pathspec racine](https://github.com/git/git/blob/master/pathspec.c), [index-info](https://github.com/git/git/blob/master/builtin/update-index.c).
