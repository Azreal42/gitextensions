# ADR 0015 · Dépôts récents et favoris

Statut : accepté.

Besoin : retrouver le dashboard original, ses catégories, recherches et dépôts épinglés ; garder les favoris lorsque l'historique récent est nettoyé.

Décision : historique XDG dédié avec migration des anciens récents natifs. Les captions raccourcies sont uniquement visuelles ; Git reçoit toujours le chemin littéral. Les ancres Top/Recent suivent la capacité historique originale et survivent à l'éviction. Les modifications du dialogue de réglages sont enregistrées uniquement avec OK. Une ouverture lit les métadonnées avant de changer le dépôt actif ; annuler ce chargement conserve le dépôt et la sélection affichés.

Repères : `RepositoryHistoryStore`, `RepositoryHistoryPresentation`, `DashboardControl`, `DashboardCategoryDialog`, `RecentRepositoriesSettingsDialog`, `MainWindow.Repositories`.

Ouverture locale (H29) : reprendre la fenêtre compacte Windows, son combo éditable et ses boutons Parent/Browse/Open, avec les icônes et traductions existantes.
Les suggestions gardent l’ordre Windows : destination clone configurée, parent du dépôt courant, récents ; dernier dépôt et HOME seulement en repli, chemins Linux comparés ordinalement.
La découverte capture l’environnement Git une fois et retourne le même candidat au noyau d’ouverture Browse ; le dialogue ne publie ni historique ni service.
Une erreur possédée conserve le texte pour réessayer ; Parent rejoint aussi `/` sans doubler le séparateur, annuler garde le dépôt et la sélection courants.
Browse utilise le picker partagé : annulation logique, attente de sa vraie Task et rejet du résultat tardif ; l’API native ne promet pas d’annulation. Complétion et lectures sont drainées avant retour.
Repères H29 : `OpenRepositoryDialog`, `StartupRepositoryDialog`, `RepositorySourceControls`, `CloneDirectoryCompletion.Dismiss`, `GitRepository.Discovery`, `MainWindow.ChooseRepositoryAsync/OpenRepositoryAsync` ; preuves et limites dans le [TODO](../../../todo.md#h29--ouverture-locale-de-dépôt).
