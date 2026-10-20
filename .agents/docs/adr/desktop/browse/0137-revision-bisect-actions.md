# 0137 — Classer la révision choisie pendant un bisect

Statut : périmètre du commit `bab820090` qualifié ; [preuves et limites](../../../todo.md#bisect-contextuel).

Le menu Windows de la grille propose Bad, Good, Skip puis Stop au premier niveau pendant un bisect. Ces actions restent en tête dans les menus complet et ciblé ; elles ne sont pas des sous-commandes d’Other actions. Le portage conserve libellés, traductions et icônes Windows.

Le classement porte sur la dernière ligne ciblée, qui peut différer du checkout et de l’ancre SelectedItem en multisélection. Capturer ensemble dépôt et SHA de la ligne focalisée avant le premier await ; un changement ultérieur de sélection ou de dépôt ne doit pas remplacer cette cible. Bad/Good/Skip appellent MarkBisectAsync avec ce SHA explicite ; Stop appelle ResetBisectAsync sans SHA. La session réelle est vérifiée dans BISECT_START du répertoire Git du worktree. Le refus des dépôts bare est une garde Linux explicite, distincte du menu Windows lu.

Comme RevisionGrid Windows, les trois classements utilisent FormProcess/useDialogSettings=false et gardent l’acquittement ; Stop du menu utilise true et respecte la préférence de fermeture. Le Stop de la fenêtre FormBisect utilise false : ne pas fusionner ces politiques. Rafraîchir après une commande lancée, y compris après erreur ou annulation à effets partiels ; conserver sortie/conclusion Git et rendre la navigation à la grille dès la première flèche, sans rollback artificiel.

La fenêtre à cinq boutons et sa question de bornes restent le parcours [ADR0051](0051-undo-and-bisect.md), classant le checkout courant. Aucun contrôle de script ou workflow bisect run n’a été trouvé dans les UI Windows/Linux comparées ; ne pas présenter cette fonction comme migrée.

Repères : Windows RevisionGridControl (groupe et ContinueBisect/StopBisect), FormBisect ; Linux MainWindow.RevisionMenu/RevisionBisect, RepositoryDialogs.BisectDialog, GitProcessDialog, GitRepository.Bisect/Bisect.Process ; BisectRevisionContextTests. Les variantes non exercées et les treize familles restent ouvertes ; niveaux inchangés, aucun N4.
