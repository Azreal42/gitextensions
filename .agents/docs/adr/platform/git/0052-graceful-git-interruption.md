# ADR 0052 · Interruption des processus Git possédés

Statut : source et candidat Debian `fbd82d6bb` vérifiés ; audit GUI ouvert, candidat non livré.

Besoin : libérer l’interface après annulation ou erreur d’observateur, conserver les effets Git partiels et protéger les autres invocations Git.

Décision : laisser Git traiter l’interruption et nettoyer naturellement ses verrous pendant 500 ms, puis forcer l’arrêt des acteurs dont la propriété est prouvée. Attendre leur fin avec des références stables ; l’arrêt reste indépendant du handshake et d’exec. Conserver l’erreur initiale et les octets déjà reçus.

Limite : conserver les verrous étrangers ou sans preuve. Un enfant jamais observé, devenu orphelin sans pipes, reste hors preuve ; reprise forcée des verrous, sous-modules/worktrees et croissance continue à valider.

Repères : `GitProcessGroup`, `GitProcessLaunch`, `GitRunner`, `GitProcessStreamingScenarios.Interruption`. Preuves et périmètre restant : [TODO](../../../todo.md).
