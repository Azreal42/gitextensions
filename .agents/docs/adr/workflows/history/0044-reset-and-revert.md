# ADR 0044 · Reset et Revert

Statut : sous-périmètre vérifié et livré sur `d144dcf69` ; limites dans le [TODO](../../../todo.md).

Besoin : retrouver les fenêtres dédiées Windows. Reset courant propose Soft/Mixed/Keep/Merge/Hard, Soft par défaut ; seul Hard demande l’avertissement de perte des changements. Reset autre branche exige Force pour un déplacement non fast-forward, protège les autres worktrees et mémorise le choix de checkout.

Revert ne crée pas de commit par défaut. Un merge exige un parent numéroté ; les drafts existants sont conservés avec leur codec. Les anciens appelants Core automatiques gardent leur contrat. La sélection est traitée du plus ancien au plus récent ; annuler une fenêtre passe à la suivante, mais des conflits non résolus empêchent toute nouvelle mutation. Chaque fenêtre repart avec AutoCommit désactivé.

Fermer le propriétaire arrête les dialogues et le post-traitement. Les changements déjà appliqués restent visibles au refresh ; les sous-modules sont proposés selon le choix historique.

Repères : FormResetCurrentBranch, FormResetAnotherBranch, FormRevertCommit, RevisionGridControl ; ResetCurrentBranchDialog, ResetAnotherBranchDialog, RevertDialog/Sequence, GitRepository.ResetBranches/Reverts.
