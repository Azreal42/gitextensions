# ADR 0149 · Références soulignées et filtre explicite

Statut : accepté. Validation L25 conservée ; complément L26 vérifié en Git/headless, preuves et limites dans le TODO.

Séparer le curseur du treeview et l’ensemble souligné Windows. Ctrl bascule un nœud, Ctrl+Shift inclut ses descendants logiques, les flèches déplacent le curseur sans modifier les marques. La commande depuis la grille « Select in left panel » remplace cet ensemble par la référence révélée. Un simple GoToRef ne modifie pas le filtre d’historique.

Le menu « Filter for selected » capture les noms complets, le dépôt et la version de sélection ; il réutilise le lecteur de filtre partagé. Les homonymes branche/tag/remote restent distincts. Recherche, tri et rebuild préservent les marques par identité, une ref supprimée est retirée, un autre dépôt invalide le callback. Linux masque des nœuds lors de la recherche ; Windows les colore.

Ctrl GoToRef ajoute/retire la vraie ligne visée sans vider la dernière sélection ; un Ctrl-reclic du même curseur change seulement sa marque. L’ordre récent d’abord, le caret séparé et le focus arbre sont conservés, sans filtre ni reconstruction ; les lecteurs annulables gardent la sélection plus récente prioritaire. Une paire initiale réelle suit [Selected, First], caret First : GetToBeSelectedRevisions puis SelectRowsIfReady Windows sélectionnent FirstId avant SelectedId. L’extension Ctrl des endpoints artificiels reste ouverte ; WT/Index et les paires artificielles sont préservés intégralement.
