# ADR 0016 · Comparer les révisions

Statut : accepté.

Besoin : conserver les choix BASE/Compare, les comparaisons de branche et les outils externes sans remplacer l'aperçu déjà affiché dans Browse.

Décision : dialogue natif dédié ; endpoints commit/index/worktree typés. La merge-base est figée à l'ouverture comme dans l'original. Les chemins renommés et fichiers non suivis restent littéraux. Un outil externe peut éditer les fichiers : relire les données même après une erreur ou annulation. Les lectures de démarrage commencent après l'établissement du propriétaire, pour fermer et annuler ensemble.

Repères : `CompareRevisionsDialog`, `ComparisonRevisionSelector`, `CompareBranchPicker`, `GitRepository.Comparisons`, `MainWindow.Comparisons`. Create/Checkout/Rename réemploient le normaliseur original et sa garde de saisie, avec les préférences partagées. Rename conserve les filtres de navigation après renommage.

Le filtre et la liste restent utilisables pendant une lecture de fichier, comme les séquences indépendantes de FormDiff Windows. CompareRevisionsDialog.Selection porte une demande par comparaison/fichier/onglet : réutiliser la sélection identique, annuler la précédente et refuser toute publication ou erreur périmée. Filtrer ne publie pas les sélections intermédiaires. Les opérations globales annulent/drainent ces lectures avant de démarrer ; Close attend le drainage, y compris quand Avalonia ferme les enfants avant de notifier leur propriétaire. Aucun Focus différé n’est ajouté.

Validation ciblée :53/53, dont11 nouveaux cas de saisie, concurrence et fermeture ; la régression échoue sur l’original7a avec seulement c au lieu de context-focus-compare. Inclus dans Desktop1241/1241 sur2e3bba726 ; frappe native complète et retour aux commits vérifiés. FormDiff reste N2 : liste/modes/regex/menus historiques encore incomplets.
