# ADR 0012 · Branches et tags

Statut : accepté.

Besoin : retrouver les actions Commands, choix et gestes de l'original, sans passer par une commande libre.

Décision : reprendre les cinq dialogues, icônes et raccourcis ; réemployer les enums historiques. Valider l'ensemble des branches avant une suppression batch, ne retirer que les worktrees choisis, confirmer abandon de changements/reset/force. Une mutation partiellement réussie impose un refresh. Une référence de navigation disparue remet le scope à All en conservant les critères ; une expression saisie par l'utilisateur reste explicite.

Repères : `FormCreateBranch`, `FormCheckoutBranch`, `FormDeleteBranch`, `FormTag`, `FormDeleteTag`, `GitRepository.References`, `Reference*Tests`. Rename/Compare : voir [ADR 0016](0016-revision-comparisons.md).
