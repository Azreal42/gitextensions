# ADR 0043 · Merge et conflits

Statut : sous-périmètre vérifié et livré sur `d144dcf69` ; limites dans le [TODO](../../../todo.md).

Besoin : proposer les choix de FormMergeBranch avec les images d’aide originales, plutôt qu’une simple confirmation. Le choix fast-forward doit primer sur merge.ff. Sans commit n’empêche pas un fast-forward natif ; squash n’ajoute pas de parent de merge. Les messages automatiques gardent le nom sélectionné et les messages personnalisés respectent le codec Git.

Un rejet de hook reste une erreur visible même si MERGE_HEAD existe. Seuls les conflits et le marqueur de reprise rerere historique justifient une reprise. Résoudre des fichiers ne crée pas implicitement de commit ; la fenêtre Commit est proposée seulement si le workflow le demande.

Le resolver lit les trois stages réels de l’index. Une version absente ne vaut pas autorisation de supprimer. Git mergetool reçoit des paths littéraux ; refuser avant mutation les noms que son helper transforme en sortie quotée, tout en conservant les choix de stage et l’édition manuelle.

NoCommit est global ; NoFF/log/count sont partagés par CommonDirectory entre worktrees. La chaîne de réglages distribués et les scripts restent ouverts.

Repères : FormMergeBranch, MergeConflictHandler, Commands.MergeBranch ; MergeBranchDialog, ResolveConflictsDialog, GitRepository.Merge/Conflicts, OriginalCommandHelp.
