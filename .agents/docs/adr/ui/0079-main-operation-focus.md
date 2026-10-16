# ADR 0079 · Retour clavier après une opération Browse

Statut : intégré ; Desktop 1241/1241 puis matrice native ciblée sur `2e3bba726`. Filtre actif, première Échap et premières flèches vérifiés ; aucune parité globale certifiée.

Besoin : après F5 ou la fermeture d’un dialogue lancé depuis un commit, les premières flèches naviguent depuis ce commit. Un autre champ ou sous-dialogue choisi pendant l’attente garde sa saisie.

Décision : préserver l’origine de l’opération dans MainWindow.RunAsync, distinctement du focus interne RepositoryDialog (ADR 0077). Une action contextuelle porte son dépôt et son hash, car son popup possède le focus au lancement. Après remplacement des lignes, retrouver le conteneur actuel par hash sans modifier la sélection. Restaurer seulement si l’origine reste valide, sans enfant visible, fermeture ou autre contrôle focalisé. Le bookmark d’action est consommé à l’entrée ; il ne doit pas contaminer une opération ultérieure pendant la reprise des panneaux.

Repères Windows : RevisionGridControl.PerformRefreshRevisions conserve SelectedObjectIds puis réactive/focalise la grille ; FormBrowse restaure ActiveControl sauf menu ouvert. Adaptation Avalonia : garde du dépôt et résolution du nouveau conteneur, sans Activate ni rappel générique ContextMenu.Closed.

Repères Linux : MainWindow.FocusRestoration, RunAsync, RevisionAction et entrées dynamiques des références ; MainWindowBusyFocusTests. Première frappe dans le dialogue, retour modal, vrai F5 et restauration après désactivation sont des contrats distincts. Le natif exige les premières touches sans clic de réparation.

Les raccourcis ferment le popup avant une commande reconnue. HistoryFilterDialog focalise son premier contrôle interactif à Opened, comme l’ordre Tab Windows ; sans cela, Échap restait routé au propriétaire. La fermeture préalable du menu ne suffisait pas à elle seule : diagnostics conservés. Le natif7a révèle un second niveau : le contrôle est focalisé logiquement mais Filter revisions n’est pas actif auprès du gestionnaire de fenêtres. La première Échap n’a pas été envoyée après cette attente expirée ; le filtre réutilise désormais ModalWindowActivation.AfterMapping avec garde Closed/visibilité/enfant ; validation native sur `2e3bba726` réussie sur ce parcours, sans clic de réparation.

Limite : les lignes de commits remplacées sont retrouvées par hash ; un autre contrôle détaché n’est pas recréé par nom. Cette restauration n’atteste pas le focus de toutes les panes.
