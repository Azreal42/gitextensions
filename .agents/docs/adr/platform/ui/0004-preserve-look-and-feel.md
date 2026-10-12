# ADR 0004 · Préserver l'interface existante

Statut : accepté ; Browse/Commit et dialogues portés comparés en clair/sombre, audit final en cours.

Besoin explicite : garder le look & feel de Git Extensions pendant le portage Linux. Les utilisateurs doivent retrouver leurs repères et leurs gestes.

Décision : conserver menus, barres d'outils compactes, icônes existantes, navigation des refs à gauche, table/graphe et informations du commit en haut, diff/arbre/console en bas. Réutiliser les couleurs des thèmes historiques ; porter les dialogues et raccourcis plutôt que redessiner les workflows. Garder aussi les choix natifs des confirmations, dont les liens de commande du commit sans index. Conserver les adaptations de contraste des icônes prévues par chaque thème.

Les couleurs de sélection des contrôles restent celles des thèmes historiques, même si le bureau Linux fournit un autre accent. Cette règle vaut aussi pour les états coché, survolé et pressé dans tous les dialogues.

Les commandes portées gardent leur emplacement historique : pas de nouvelles rangées ou de cases contextuelles lorsque l’original les place dans ses réglages.

Conséquence : aucune validation finale sur le seul démarrage. Comparer les écrans portés au `FormBrowse.Designer.cs` et aux écrans historiques, puis vérifier les interactions réelles.

Repères : `src/app/GitUI/CommandsDialogs/FormBrowse.Designer.cs`, `src/app/GitUI/Themes/`, `src/app/GitUI/Resources/Icons/`, `src/linux/GitExtensions.Desktop/MainWindow.Browser.cs`.
