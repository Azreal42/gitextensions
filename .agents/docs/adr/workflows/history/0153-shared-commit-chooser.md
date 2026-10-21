# ADR 0153 · Choix commun d’une révision

Statut : décision retenue ; qualification et preuves dans le [TODO](../../../todo.md).

Cherry-pick, Revert, Rebase et Export partagent le même dialogue au-dessus du presenter existant. Leurs classes, identifiants de contrôles et retours `GitCommit?` restent compatibles. Compare reçoit un résultat `GitCompareRevision` : commit réel, Index ou WorkingTree ; les artificiels ne deviennent pas des commits Git.

La préselection résout expressions et tags. Rebase From montre la branche actuelle avec le merge-base comme borne native `...SHA`, excluant cette borne et ses ancêtres. Le dialogue ne change aucune préférence de filtrage globale.

GoTo réutilise le [dialogue commun](0073-go-to-commit.md). Les deux premiers parents restent dans les lignes chargées ; une cible filtrée garde le filtre et produit le message possédé. Une sélection typée unique relie WorkingTree→Index→HEAD capturé ; Index unborn n’a aucun parent. Les flèches traversent les deux surfaces, avec focus de liste activable localement au sélecteur. La préférence artificielle est capturée à l’ouverture ; un dépôt bare n’offre pas ces endpoints, un dépôt propre peut les offrir.

Un seul noyau modal possède lectures, GoTo, annulation et fermeture ; la publication attend leur fin réelle et refuse un propriétaire fermé ou un token obsolète. Compare partage ce même noyau entre ses sélecteurs de branches et de commits : un seul picker rejoint sa file de drainage, puis la quitte avant de relire la comparaison. La demande de fermeture du propriétaire est conservée pendant l’annulation de l’enfant, avec la politique de fermeture temporaire commune au Revision Log. Cancel conserve ses endpoints et sa comparaison. Ouvrir ou naviguer ne modifie ni références, index, worktree ni profil. Les paires métier restent décrites dans [ADR0016](../references/0016-revision-comparisons.md) et la navigation Browse dans [ADR0088](../../ui/history/0088-parent-child-navigation.md).

Limites : 500 commits et filtre message existants ; grille/filtres Windows complets encore ouverts. Les deux lignes artificielles sont épinglées en tête sous l’en-tête, tandis que Windows les insère avant HEAD dans le flux. Variantes natives, plateformes et interaction installée restent à qualifier dans le TODO.

Sources Windows : [FormChooseCommit](../../../../../src/app/GitUI/HelperDialogs/FormChooseCommit.cs), [FilterInfo](../../../../../src/app/GitUI/UserControls/RevisionGrid/FilterInfo.cs), [FormRebase](../../../../../src/app/GitUI/CommandsDialogs/FormRebase.cs), [FormDiff](../../../../../src/app/GitUI/CommandsDialogs/FormDiff.cs). Repères Linux : `ChooseCommitDialog`, `ChooseCommitSelectionPresenter`, `ArtificialRevisionRows`, `ExportDialogControls`, `CompareRevisionsDialog.ChooseRevisionAsync`, `CompareBranchPicker`. Scénarios source : `ChooseCommitDialogTests`, `ComparisonCommitPickerTests` ; aucune exécution déduite de leur présence.
