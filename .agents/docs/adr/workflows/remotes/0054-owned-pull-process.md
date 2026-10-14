# ADR 0054 · Sortie continue de Pull/Fetch

Statut : vérifié localement sur `7fc8756da` ; audit natif du nouveau payload encore requis. Preuves et compléments dans le [TODO](../../../todo.md).

Besoin : voir Git pendant l’opération, retrouver Keep/OK/Abort et conserver les changements sauvegardés après interruption.

Décision : réutiliser le Process partagé, possédé par la fenêtre visible qui déclenche l’action. Garder stdout/stderr reçus et le vrai dernier résultat Git ; la console peut contenir le stash puis Pull sans inventer un résultat composite. Acquitter Process avant la reprise, les sous-modules ou la proposition de réapplication.

Conserver le hash du stash déjà créé après erreur ou annulation. Une relecture seule bornée peut retrouver ce checkpoint sans remplacer l’erreur initiale ni relancer une mutation. Un nouvel essai possède son propre checkpoint ; l’ancien reste disponible dans Stash.

Fermer le propriétaire après la fin de Git arrête la suite, sans réécrire l’exit obtenu. Abort attend l’arrêt des acteurs Git possédés connus et conserve leurs effets partiels.

Repères : `GitRepository.Network`, `PullDialog`, `MainWindow.Pull`, `GitProcessDialog`, `PullProcessDialogTests` ; originaux `FormPull`/`FormProcess`.
