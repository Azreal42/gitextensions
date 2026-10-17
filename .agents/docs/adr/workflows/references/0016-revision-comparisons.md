# ADR 0016 · Comparer les révisions

Statut : accepté sur le périmètre décrit ; FormDiff reste N2.

Besoin : comparer BASE/A/B sans remplacer l’aperçu de Browse, avec les choix et gestes de l’original.

Décision : dialogue dédié et endpoints commit/index/worktree typés ; merge-base figée à l’ouverture, chemins renommés et non suivis littéraux. Relire après un outil externe, même en échec/annulation. Démarrer les lectures après établissement du propriétaire.

Browse, Commit et Compare partagent FileStatusList : six modes, regex/MRU, renommages, densité 18 px et préférences. Dans Compare, seule une feuille sélectionnée alimente le lecteur ; parents/multisélections le vident. La racine unique sans enfant est automatiquement sélectionnée comme sous Windows, sans descendre dans un parent. Dans Browse, le groupe ordinaire filtré vide efface contenu/identité ; résumés de dossiers, groupes non vides et RangeDiff gardent leur comportement propre.

Les lectures ciblées laissent filtre/liste utilisables : réutiliser la même sélection, annuler la précédente et refuser les publications/erreurs périmées. Une opération globale ou la fermeture annule et draine ces lectures. Aucun retour du focus lors d’une reconstruction. Après une action de mode, reprendre seulement son origine encore valide ; abandonner l’ancienne origine pointeur quand le clavier prend la suite. Le décor de ligne ne prend pas le focus, et les palettes restent locales au contrôle.

Create/Checkout/Rename partagent normalisation, gardes et préférences ; Rename conserve les filtres de navigation.

Restent ouverts : menus métier, groupes AllParents/range/base des révisions divergentes, métadonnées staged/tracked/sous-module, variantes de rendu/outils et gestes de plage au clavier. Pas de parité globale certifiée.

Repères : `CompareRevisionsDialog/Selection`, `ComparisonRevisionSelector`, `CompareBranchPicker`, `GitRepository.Comparisons`, `GitBranchNaming`, `RenameBranchDialog`, `MainWindow.Comparisons/Diffs`, `FileStatusListView/Builder`, `FileStatusListPreferencesStore`. Source, tests et preuves : [TODO](../../../todo.md).
