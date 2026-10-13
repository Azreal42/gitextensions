# ADR 0042 · Combo du filtre Branches

Statut : implémenté ; 235 scénarios Core et 37 tests Desktop ciblés passent. Replays natifs de développement clair/sombre passés ; suite complète, audit natif du payload et paquet de ce lot encore attendus.

Référence : Windows master afc5123953fef0d128a9dd2ee3b3a682a234ac6e, `FilterToolBar` et `RevisionGrid.FilterInfo`. Ce filtre ne remplace pas le menu de checkout de la branche courante.

Le développeur compose une expression de révisions. La combo suggère les refs ; Local est coché initialement, Remote/Tag sont indépendants, aucun type coché montre toutes les refs. La sélection et la saisie restent un brouillon : Entrée seule applique. Les refresh ne perdent pas ce brouillon et ne modifient pas implicitement le filtre accepté.

Conserver les expressions séparées par espaces, exclusions, plages, patterns et options de révision Git ; Entrée dans la barre avertit pour chaque ref simple inexistante puis applique les tokens valides. Le dialogue avancé garde ses expressions sans cette validation, comme Windows. Garder séparés All/Current/Filtered et Reflog ; désactiver le filtre custom ne détruit pas sa valeur. Comme Windows, Current/custom incluent le dernier stash si son affichage est actif.

Limite du portage : les options qui écrivent, lancent des outils externes ou changent le format de sortie du reader sont incompatibles. La tokenisation historique ne prend pas en charge les phrases entre guillemets contenant des espaces ; ne pas ajouter un interpréteur shell.

Repères : `BranchHistoryFilterComboBox`, `MainWindow.BranchFilter/Toolbar/History`, `GitBranchFilter`, `GitLogQuery`, tests Core/Desktop. Le [TODO](../../../todo.md) garde les preuves et le statut du paquet livré.
