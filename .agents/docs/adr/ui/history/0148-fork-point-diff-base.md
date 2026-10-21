# ADR 0148 · Prochain point de branchement comme base de diff

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Ctrl+K emploie les premiers parents déjà chargés et avance au moins une fois. Il s’arrête sur une branche locale/distante ou un nombre d’enfants différent de un, comme RevisionGridControl Windows. Une étiquette distante masquée reste une vraie référence ; un tag seul ne constitue pas cet arrêt. Aucune recherche Git supplémentaire ne remplace une ligne filtrée ou absente.

La sélection finale est publiée en un lot ; les mêmes lecteurs de diff, lignes et gardes de focus restent propriétaires de la comparaison. Index et working tree gardent leur type, sans faux commit. Le raccourci reste configurable. Le périmètre un/deux côtés est porté ; l’ordre natif et les collisions de trois sélections ou plus restent ouverts et la commande conserve alors l’état intact. Les traces incidentes d’historique WinForms ne sont pas déclarées identiques.
