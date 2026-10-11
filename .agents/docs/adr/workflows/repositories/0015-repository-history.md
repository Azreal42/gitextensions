# ADR 0015 · Dépôts récents et favoris

Statut : accepté.

Besoin : retrouver le dashboard original, ses catégories, recherches et dépôts épinglés ; garder les favoris lorsque l'historique récent est nettoyé.

Décision : historique XDG dédié avec migration des anciens récents natifs. Les captions raccourcies sont uniquement visuelles ; Git reçoit toujours le chemin littéral. Les ancres Top/Recent suivent la capacité historique originale et survivent à l'éviction. Les modifications du dialogue de réglages sont enregistrées uniquement avec OK. Une ouverture lit les métadonnées avant de changer le dépôt actif ; annuler ce chargement conserve le dépôt et la sélection affichés.

Repères : `RepositoryHistoryStore`, `RepositoryHistoryPresentation`, `DashboardControl`, `DashboardCategoryDialog`, `RecentRepositoriesSettingsDialog`, `MainWindow.Repositories`.
