# ADR 0149 · Références soulignées et filtre explicite

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Séparer le curseur du treeview et l’ensemble souligné Windows. Ctrl bascule un nœud, Ctrl+Shift inclut ses descendants logiques, les flèches déplacent seulement le curseur. La commande depuis la grille « Select in left panel » remplace cet ensemble par la référence révélée. Un simple GoToRef ne modifie pas le filtre d’historique.

Le menu « Filter for selected » capture les noms complets, le dépôt et la version de sélection ; il réutilise le lecteur de filtre partagé. Les homonymes branche/tag/remote restent distincts. Recherche, tri et rebuild préservent les marques par identité, une ref supprimée est retirée, un autre dépôt invalide le callback. Linux masque des nœuds lors de la recherche ; Windows les colore. Le Ctrl de GoToRef vers la multisélection de la grille reste un complément distinct.
