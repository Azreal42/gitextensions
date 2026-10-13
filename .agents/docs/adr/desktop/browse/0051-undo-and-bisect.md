# 0051 — Undo et Bisect depuis Browse

Statut : code et candidat Debian `05ee73cff` vérifiés ; nouveau lot FormProcess non validé, audit natif ouvert.

## Intention

Annuler le dernier commit de HEAD en retrouvant ses changements dans le staging, indépendamment de la sélection du graphe. Retrouver la fenêtre Bisect Windows à cinq actions et son ordre de sélection.

## Décision

Undo conserve la confirmation Warning/Yes et sa suppression mémorisable, y compris après No/Échap/fermeture. Exécuter le reset soft sur le littéral `HEAD~1` : résoudre d'abord son SHA changerait le reflog et l'erreur native sans parent. Index et worktree restent ceux de Git.

Bisect démarre sans bornes avant de proposer la plage : première sélection good, dernière bad, sans tri chronologique. Refuser la proposition conserve la session démarrée. Accepter exécute Good puis Bad, seulement si Good réussit ; classement courant et Stop referment la fenêtre historique.

Conserver les effets Git partiels après erreur/annulation ; ne pas simuler de rollback. Les erreurs et conclusions natives restent lisibles dans des dialogues owned avant fermeture. La progression de chaque phase et la préférence historique CloseProcessDialog font l’objet d’un nouveau lot en cours, non validé ; les sorties terminales du candidat `05ee73cff` ne suffisent pas à clore la parité. La reprise des verrous après interruption et les autres fonctions du FormProcess historique restent ouvertes.

Repères : `MainWindow.Browser/Workflows`, `UndoLastCommitConfirmationDialog`, `BrowseCommandPreferencesStore`, `BisectDialog`, `GitRepository.Advanced/Bisect`. Référence : `FormBrowse`, `FormBisect`, `MessageBoxes.ConfirmSuppressible`, `FormProcess`. Preuves et validations restantes dans [TODO](../../../todo.md).
