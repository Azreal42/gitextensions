# ADR 0064 · Menus View et Navigate de l’historique

## Besoin

Les menus principaux View et Navigate étaient des listes abrégées, sans les groupes et icônes du code Windows. La référence est `RevisionGridMenuCommands.cs`, réutilisée par `FormBrowseMenus.cs` pour les menus principaux et le clic droit.

## Décision

`MainWindow.HistoryMenus.cs` définit les commandes une seule fois : ordre, séparateurs, titres, images originales, gestes clavier, disponibilité et coches. Les deux hôtes utilisent les mêmes fonctions et états ; la traduction prend la catégorie XLF `RevisionGrid` et les identifiants Windows. Le style commun conserve les lignes de 22 px, séparateurs de 6 px et images de 16 px, en clair et sombre. Le View principal ajoute Toolbars comme FormBrowseMenus.

Navigate reprend les révisions artificielles/HEAD, commit courant, saisie de révision, enfant, parent, premier/dernier parent, ancêtre commun, précédent/suivant et recherche précédente/suivante. L’historique inclut la première sélection chargée. Les raccourcis de grille et des popups appellent les mêmes commandes ; les touches BrowserBack/Forward sont aussi raccordées. La recherche rapide utilise le champ de filtre existant ; elle ne porte pas encore le fournisseur de recherche incrémentale Windows. En présence de plusieurs enfants/parents, la navigation ordinaire conserve le choix déterministe existant ; le sélecteur Windows reste à porter. Premier/dernier parent et merge-base entre sélections ou avec HEAD utilisent les vrais objets Git.

View reprend les sections Branches, Commits, Grid labels, Grid info, Columns, Sorting et Settings persistence. Les changements de labels/colonnes/corps/dates relatives/graphe mettent à jour les contrôles existants sans rebinder la grille ; les relations sont calculées une fois par sélection/historique. Les lectures de dates de committer et de notes sont groupées par 200 révisions. Les filtres de stashes/checkpoints et tris utilisent les options Git natives. Les entrées Toolbars commandent les groupes et éléments de la barre compacte, y compris leur présence dans le débordement. Ctrl+E révèle un champ masqué avant de le focaliser.

`HistoryViewPreferencesStore` sauvegarde atomiquement les défauts dans le profil XDG avec Save current view settings as default. Les modifications restent en session avant cet enregistrement explicite. Les fonctions de lecture annulable par panneau de l’ADR 0063 restent inchangées.

## Limites et validation

Les trois options de labels superproject, les deux options de statut CI et Scripts toolbar sont visibles mais désactivées avec une explication : leurs fournisseurs ne sont pas portés. La parité de ces moteurs, la recherche incrémentale et le sélecteur de parents/enfants Windows restent ouverts. Aucun faux statut CI ou faux label n’est produit.

`HistoryMenuParityTests` contrôle les vrais menus/rendus clair-sombre, commandes communes, images/gestes, navigation sur merge Git, changements en place, sauvegarde/rechargement du profil, notes/corps/date committer, options de log et masquage du débordement. Les régressions de focus, de lecture et de combo Branches sont rejouées. Les résultats finaux et le paquet sont rattachés au commit source dans le TODO et les preuves du snapshot ; les captures headless ne valent pas audit natif étendu.
