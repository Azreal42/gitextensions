# 0050 — Nettoyer le répertoire de travail

Statut : code vérifié localement ; audit natif et paquet requis avant livraison.

## Intention

Retrouver les neuf parcours de `FormCleanupRepository` pour retirer les fichiers non suivis du worktree courant, indépendamment du commit sélectionné, en gardant les choix et la disposition Windows.

## Décision

Conserver les trois modes, leurs défauts, les filtres multiligne et les sélecteurs de chemins ; aucune persistance ni preview automatique. Entrée dans un champ multiligne ajoute une ligne. Cleanup demande Yes/No, reste ouvert et relit les options après Yes.

Git décide des suppressions actuelles : le preview est readonly, sans force ; Cleanup utilise un unique `-f`. Les inclusions restent des pathspecs Git et les exclusions suivent la conversion historique, appliquée une fois. Aucune liste issue d'un ancien preview ne devient autorité de suppression.

Le parent puis `submodule foreach --recursive` conservent l'ordre et les erreurs natifs ; les enfants reçoivent les inclusions et options, pas les exclusions. Les chemins utilisateur restent des données. Après annulation ou fermeture, aucun nouvel enfant ne démarre ; signaler les effets partiels, sans rollback fictif.

Les sélecteurs contrôlent les chemins physiques du worktree et les métadonnées Git. Les tests avec objets StorageProvider ne valent pas audit du vrai sélecteur natif. Le dispatcher CLI historique `cleanup` reste à porter.

Repères : `CleanupRepositoryDialog`, `MainWindow.Cleanup/Browser`, `GitRepository.Clean*`, `CleanMode` original partagé. Preuves et validations restantes dans [TODO](../../../todo.md).
