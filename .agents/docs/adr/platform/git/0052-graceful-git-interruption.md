# ADR 0052 · Interruption des processus Git possédés

Statut : premier C01 GUI `fbd82d6bb` refusé ; correction progressive `91a47a6b6` vérifiée localement et sous trace, nouveau candidat en construction, audit GUI à reprendre.

Besoin : libérer l’interface après annulation ou erreur d’observateur, conserver les effets Git partiels et protéger les autres invocations Git.

Décision : découvrir les acteurs avec progression conservée et parcours finis. Accorder à chaque acteur vivant prouvé 500 ms après son SIGINT, puis forcer son arrêt ; un parent déjà mort ne consomme pas cette grâce. Attendre leur fin avec des références stables ; l’arrêt reste indépendant du handshake et d’exec. Conserver l’erreur initiale et les octets déjà reçus.

Limite : conserver les verrous étrangers ou sans preuve. Un enfant jamais observé, devenu orphelin sans pipes, reste hors preuve ; reprise forcée des verrous, sous-modules/worktrees et croissance continue à valider.

Repères : `GitProcessGroup`, `GitProcessLaunch`, `GitRunner`, `GitProcessStreamingScenarios.Interruption`, `GitProcessStreamingScenarios.OrphanProgress`. Preuves et périmètre restant : [TODO](../../../todo.md).
