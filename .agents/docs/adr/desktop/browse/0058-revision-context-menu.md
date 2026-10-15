# 0058 — Menu contextuel des commits

## Intention

Retrouver le menu de la capture Windows : ordre, groupes, icônes originales, raccourcis et densité. Une action de branche vise les références du commit pointé, indépendamment du panneau gauche.

## Décision

Conserver les menus Avalonia natifs ; appliquer localement la palette historique, des lignes de 22 px et la gouttière des icônes. Les libellés contextualisés réutilisent les XLF. Le clic droit conserve une multisélection existante et diffère le chargement du diff pour permettre l’ouverture du menu. Une demande différée reste en attente pendant un dialogue et reprend au retour à l’état libre, y compris après Cancel ; elle ne doit pas interrompre le clic sur une action.

Réutiliser les dialogues de branche, tag, reset, cherry-pick, revert, archive et comparaison. Lire les références au peuplement du menu ; exclure les références symboliques et la branche courante du checkout/suppression. Garder les protections bare et les confirmations.

Advanced cible la sélection : Edit/Reword modifient une seule instruction du vrai todo Git et conservent les merges. Fixup/Squash/Amend ouvrent Commit avec cette cible ; un amend! ajoute un commit après HEAD et ne signifie jamais amend HEAD. Abort reste celui de Git.

Les bascules View non portées restent ouvertes ; une entrée visible doit avoir une action réelle. La navigation ne modifie pas silencieusement les filtres pour rendre un commit visible.

Repères : `MainWindow.RevisionMenu/RevisionNavigation/RevisionAdvanced`, `RevisionContextMenuAppearance`, `CommitDialog`, `GitRepository.Rebase.TargetedCommit`, `GitRebaseEditor.TargetedCommit`. Référence : `RevisionGridControl`, `CopyContextMenuItem`, `RevisionGridMenuCommands`. État et preuves dans [TODO](../../../todo.md).
