# ADR 0055 · Push observable et reprise dans le même Process

Statut : implémenté et vérifié localement sur `6ee608a9e` ; paquet et audit natif dans le [TODO](../../../todo.md).

Besoin : retrouver le transfert et Keep/OK/Abort dans Push, Quick Push et Commit & Push, avec les confirmations et possibilités de récupération originales.

Réutiliser le Process partagé. Le rejet et son Pull éventuel précèdent Done ; une nouvelle tentative garde la même fenêtre et remet sa console courante à zéro. Conserver séparément les sorties natives des tentatives précédentes.

Une clôture ou annulation du workflow arrête ses continuations sans effacer un résultat Git déjà terminé. Attendre les processus possédés avant de fermer les propriétaires imbriqués. Un commit réussi reste conservé si son Push échoue ou est annulé (ADR 0046).

Repères : `GitRepository.Push`, `PushDialog`, `GitProcessDialog`, `GitProcessAttempts`, `MainWindow.Workflows/Shortcuts/Toolbar`, `CommitDialog` ; original `FormPush`/`FormProcess`. Confirmations et récupération : ADR 0036/0038/0046. Scripts Before/After, PR hébergeurs et grille ls-remote restent ouverts.
