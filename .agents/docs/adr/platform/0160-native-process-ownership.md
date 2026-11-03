# ADR 0160 · Propriété native des processus Git

Statut : en cours. [Objectif trois plateformes](0159-avalonia-three-platforms.md). Une suite verte ne prouve pas que tous les descendants sont drainés.

Windows démarre le processus suspendu, l’affecte à un Job Object privé avec `KILL_ON_JOB_CLOSE`, puis reprend son thread. Les seuls handles hérités sont stdin/stdout/stderr. Les descendants rejoignent le job ; son arrêt reste possible après la sortie du parent. Le retour normal attend aussi un job vide, même après EOF. Les arguments suivent les règles argv Windows, l’environnement reste capturé et les flux binaires conservent la même boucle de copie.

Le projet `GitExtensions.Native.Tests` exerce cinq contrats noyau sur le vrai runner Windows : parent vivant, parent sorti avec pipes hérités, parent sorti avec pipes indépendants, puis échanges binaires volumineux et arguments littéraux avec sorties 0/73. Les marqueurs stdout/stderr prouvent l’héritage ; un autre processus du même exécutable doit survivre à l’annulation. Cette étape bloque le job avant les contrats portables et la livraison. Les autres systèmes ne simulent pas les appels Win32.

Le contrôle portable de rebase ouvre le vrai lock depuis un autre apphost pendant un hook natif, vérifie son refus, puis rouvre le même chemin après le rebase et contrôle HEAD/branche/fichiers. Il rejoint les contrats CrossPlatform, désormais 151 cas. Les cinq contrats Windows sont distincts de ce sous-ensemble.

Preuves locales : compilation sans avertissement/erreur ; 1 210 Core, 151 portables, puis quatre cas de banc compatibles Linux (pipes hérités et duplex) réussis. Les appels noyau Windows restent à qualifier sur le runner réel. Journaux dans `artifacts/cross-platform/20261103/`.

Le premier job Windows `41694781899` passe trois cas, dont le drainage sans pipes et les échanges binaires. Les deux cas hérités ont révélé un défaut du banc : le second `Process.Start` sans redirection n’alimentait pas les handles de sortie avec `CreateNoWindow`. La redirection de stdin force leur transmission explicite ; les assertions de marqueurs restent inchangées. La nouvelle source reste à requalifier nativement.

Le [run `41695937711`](https://github.com/Azreal42/gitextensions/actions/runs/41695937711), source `e8ef4816d`, passe les cinq contrats noyau Windows et les 151 contrats portables sur Windows/Mac Intel ; leurs archives extraites et vrais backends passent également. Les TRX et empreintes téléchargés sont conservés. ARM reste annulé avant allocation, et cette preuve ne clôt pas les suites métier natives complètes.

macOS reçoit maintenant un superviseur C privé. `posix_spawn` mappe explicitement cinq descripteurs avec `CLOEXEC_DEFAULT` : trois flux, commandes et événements. ACK précède le fork/exec ; `waitid(WNOWAIT)` réserve le leader du groupe jusqu’au drainage. L’arrêt passe par le canal privé ; aucun PID recyclé n’est signalé depuis le code géré. Le petit pont C évite l’ABI variadique `fcntl` d’Apple Silicon. Le bundle exige les deux Mach-O à la bonne architecture, avant sa signature récursive.

Les cinq contrats de processus sont mutualisés sur les runners Windows et Mac, en plus des 151 portables. Le test Mac distingue un zombie arrêté d’un acteur vivant ; son nettoyage est coopératif et ne signale pas un PID Unix recyclé. Deux arbres réels, avec/sans pipes, valident le protocole du superviseur depuis Linux (pidfd, parent sorti avant annulation, enfant arrêté avant retour). La compilation gérée, quatre cas de banc Linux et les 151 portables passent ; ces preuves ne valent pas encore exécution macOS du nouveau backend.

Le premier passage du nouveau backend Mac (`41701323689`, source `dfb1b84ea`) passe les trois contrats de drainage, y compris parent sorti et pipes fermés. Les deux contrats duplex échouent uniquement sur l’alias du répertoire temporaire : `getcwd` retourne `/private/var` pour un lancement sous `/var`. Le banc conserve le chemin de lancement et compare désormais le résultat à sa cible résolue indépendamment par `realpath`, sans relâcher les assertions argv, flux, environnement et code de sortie. Compilation locale et quatre contrats comparables Linux réussis ; nouvelle qualification native requise.

Le cas sans pipe a été exécuté séparément sur le backend Linux existant : le runner est revenu sans annulation alors qu’un enfant était encore vivant. Échec conservé dans `posix-closed-pipes-baseline`, nettoyage des seuls acteurs du test par références conservées. Cette limite POSIX reste ouverte ; la nouvelle étape Windows ne remplace aucun contrôle Linux ni la future supervision macOS. La console interactive/PTY, les descendants sortis de leur groupe et les suites natives complètes restent également à traiter.

Repères : [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), [création suspendue et handles](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), [CI native](../../../../.github/workflows/avalonia-platforms.yml).

API : [ABI Apple Silicon](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms), [spawn Apple](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/spawn/spawn.h), [wait Apple](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/wait.h).
