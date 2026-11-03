# ADR 0160 · Propriété native des processus Git

Statut : en cours. [Objectif trois plateformes](0159-avalonia-three-platforms.md). Une suite verte ne prouve pas que tous les descendants sont drainés.

Windows démarre le processus suspendu, l’affecte à un Job Object privé avec `KILL_ON_JOB_CLOSE`, puis reprend son thread. Les seuls handles hérités sont stdin/stdout/stderr. Les descendants rejoignent le job ; son arrêt reste possible après la sortie du parent. Le retour normal attend aussi un job vide, même après EOF. Les arguments suivent les règles argv Windows, l’environnement reste capturé et les flux binaires conservent la même boucle de copie.

Le projet `GitExtensions.Native.Tests` exerce cinq contrats noyau sur le vrai runner Windows : parent vivant, parent sorti avec pipes hérités, parent sorti avec pipes indépendants, puis échanges binaires volumineux et arguments littéraux avec sorties 0/73. Les marqueurs stdout/stderr prouvent l’héritage ; un autre processus du même exécutable doit survivre à l’annulation. Cette étape bloque le job avant les contrats portables et la livraison. Les autres systèmes ne simulent pas les appels Win32.

Le contrôle portable de rebase ouvre le vrai lock depuis un autre apphost pendant un hook natif, vérifie son refus, puis rouvre le même chemin après le rebase et contrôle HEAD/branche/fichiers. Il rejoint les contrats CrossPlatform, désormais 151 cas. Les cinq contrats Windows sont distincts de ce sous-ensemble.

Preuves locales : compilation sans avertissement/erreur ; 1 210 Core, 151 portables, puis quatre cas de banc compatibles Linux (pipes hérités et duplex) réussis. Les appels noyau Windows restent à qualifier sur le runner réel. Journaux dans `artifacts/cross-platform/20261103/`.

Le cas sans pipe a été exécuté séparément sur le backend Linux existant : le runner est revenu sans annulation alors qu’un enfant était encore vivant. Échec conservé dans `posix-closed-pipes-baseline`, nettoyage des seuls acteurs du test par références conservées. Cette limite POSIX reste ouverte ; la nouvelle étape Windows ne remplace aucun contrôle Linux ni la future supervision macOS. La console interactive/PTY, les descendants sortis de leur groupe et les suites natives complètes restent également à traiter.

Repères : [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), [création suspendue et handles](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), [CI native](../../../../.github/workflows/avalonia-platforms.yml).
