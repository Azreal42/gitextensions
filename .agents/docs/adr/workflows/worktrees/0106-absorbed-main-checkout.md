# ADR 0106 · Checkout principal d’un sous-module absorbé

Décision : compléter l’[ADR0068](0068-manage-and-create-worktrees.md) ; socle Git validé ; nouveau parcours natif absorbé encore ouvert.

Git peut lister le répertoire commun du sous-module comme worktree principal. Le snapshot le rattache au vrai checkout uniquement si son GitDirectory et son CommonDirectory pointent réciproquement vers ces métadonnées. Le principal et le courant restent protégés contre Remove ; une registration remplacée ou un checkout étranger est refusé.

La création gérée fonctionne depuis l’absorbé principal et ses linked worktrees, avec la politique native relative/absolue. Conserver les index/HEAD initiaux et les effets Git partiels après erreur de hook ou annulation, sans rollback fabriqué. Repères : `GitRepository.Worktrees.Snapshots`, `WorktreeScenarios`. [Core ciblé et limites](../../../todo.md#detailed-et-worktrees-absorbés--qualification-partielle) ; aucun nouveau niveau acquis.
