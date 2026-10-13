# 0051 — Undo et Bisect depuis Browse

Statut : code et candidats Debian `05ee73cff` / `e94c5d0cf` vérifiés ; parcours natif clair Bisect à une sélection validé, autres variantes et Undo ouverts.

## Intention

Annuler le dernier commit de HEAD en retrouvant ses changements dans le staging, indépendamment de la sélection du graphe. Retrouver la fenêtre Bisect Windows à cinq actions et son ordre de sélection.

## Décision

Undo conserve la confirmation Warning/Yes et sa suppression mémorisable, y compris après No/Échap/fermeture. Exécuter le reset soft sur le littéral `HEAD~1` : résoudre d'abord son SHA changerait le reflog et l'erreur native sans parent. Index et worktree restent ceux de Git.

Bisect démarre sans bornes avant de proposer la plage : première sélection good, dernière bad, sans tri chronologique. Refuser la proposition conserve la session démarrée. Accepter exécute Good puis Bad, seulement si Good réussit ; classement courant et Stop referment la fenêtre historique.

Conserver les effets Git partiels après erreur/annulation ; ne pas simuler de rollback. Les erreurs et conclusions natives restent lisibles dans des dialogues owned avant fermeture. Chaque phase rend sa sortie pendant l’exécution. Start/Good/Bad de la plage respectent CloseProcessDialog ; classement courant et Stop gardent leur acquittement historique. Annuler attend la fin des processus possédés sans effacer les effets Git déjà produits. Le code et le candidat Debian `e94c5d0cf` sont vérifiés ; la parité native reste ouverte. L’arrêt gracieux est vérifié localement ([ADR 0052](../../platform/git/0052-graceful-git-interruption.md)) ; son paquet/audit GUI, les verrous après arrêt forcé et les autres fonctions du FormProcess restent ouverts. Un verrou étranger ou sans preuve de propriété doit être conservé.

Repères : `MainWindow.Browser/Workflows`, `UndoLastCommitConfirmationDialog`, `BrowseCommandPreferencesStore`, `BisectDialog`, `GitProcessDialog`, `ProcessDialogPreferencesStore`, `GitRepository.Advanced/Bisect.Process`, `GitRunner`. Référence : `FormBrowse`, `FormBisect`, `MessageBoxes.ConfirmSuppressible`, `FormProcess`. Preuves et validations restantes dans [TODO](../../../todo.md).
