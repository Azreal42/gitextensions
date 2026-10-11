# ADR 0016 · Comparer les révisions

Statut : accepté.

Besoin : conserver les choix BASE/Compare, les comparaisons de branche et les outils externes sans remplacer l'aperçu déjà affiché dans Browse.

Décision : dialogue natif dédié ; endpoints commit/index/worktree typés. La merge-base est figée à l'ouverture comme dans l'original. Les chemins renommés et fichiers non suivis restent littéraux. Un outil externe peut éditer les fichiers : relire les données même après une erreur ou annulation. Les lectures de démarrage commencent après l'établissement du propriétaire, pour fermer et annuler ensemble.

Repères : `CompareRevisionsDialog`, `ComparisonRevisionSelector`, `CompareBranchPicker`, `GitRepository.Comparisons`, `MainWindow.Comparisons`. Create/Checkout/Rename réemploient le normaliseur original et sa garde de saisie, avec les préférences partagées. Rename conserve les filtres de navigation après renommage.
