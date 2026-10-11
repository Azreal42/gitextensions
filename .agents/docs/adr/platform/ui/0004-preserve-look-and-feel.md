# ADR 0004 · Préserver l'interface existante

Statut : accepté, validation visuelle en cours.

Besoin explicite : garder le look & feel de Git Extensions pendant le portage Linux. Les utilisateurs doivent retrouver leurs repères et leurs gestes.

Décision : conserver menus, barres d'outils compactes, icônes existantes, navigation des refs à gauche, table/graphe et informations du commit en haut, diff/arbre/console en bas. Réutiliser les couleurs des thèmes historiques ; porter les dialogues et raccourcis plutôt que redessiner les workflows.

Conséquence : aucune validation finale sur le seul démarrage. Comparer les écrans portés au `FormBrowse.Designer.cs` et aux écrans historiques, puis vérifier les interactions réelles.

Repères : `src/app/GitUI/CommandsDialogs/FormBrowse.Designer.cs`, `src/app/GitUI/Themes/`, `src/app/GitUI/Resources/Icons/`, `src/linux/GitExtensions.Desktop/MainWindow.Browser.cs`.
