# ADR 0038 · Push rapide

Statut : sous-périmètre implémenté, vérifié et livré ; compléments dans le [TODO](../../../todo.md).

Besoin : Ctrl+Maj+↑ et Maj-clic du bouton Push lancent la même configuration que FormPush, sans demander un clic supplémentaire dans le formulaire. Une réussite termine le workflow ; un échec ou un refus de confirmation ouvre les options déjà initialisées, avec l'erreur et les choix conservés.

Réutiliser les confirmations et la reprise du Push normal ; un refus de hook ne propose pas de forcer. La branche poussée vient du checkout relu, indépendamment du commit du graphe sélectionné. L'annulation du processus et la fermeture du dépôt restent liées au cycle de vie de Browse.

Repères : GitUICommands.StartPushDialog, FormPush.PushAndShowDialogWhenFailed ; PushDialog.PushAndShowWhenFailedAsync, MainWindow.Workflows, MainWindow.Shortcuts, MainWindow.Toolbar, PushDialogTests.
