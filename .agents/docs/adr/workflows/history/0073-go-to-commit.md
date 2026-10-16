# ADR 0073 · Aller à un commit

Statut : accepté sur le périmètre ciblé testé ; aucune parité complète déclarée.

Besoin : retrouver un commit depuis une expression Git, un tag ou une branche locale, sans modifier les filtres ni perdre le focus de la grille. Référence : FormGoToCommit et ses tests Windows de parcours du focus.

Décision : reprendre les trois champs, le bouton Go, le groupe Help, le lien git-rev-parse et les ancrages Windows. Le champ actif fournit la cible ; choix explicite ou Entrée valide, Tab et autocomplétion seuls ne naviguent pas. Aucun MRU ajouté.

Tags et branches locales sont lus séparément, avec la limite Windows de 1 000 par liste, sans prendre le verrou des mutations. Ces lectures laissent l’expression utilisable et sont annulées/drainées à la fermeture ou au changement de contexte, y compris une réouverture du même chemin.

Le presse-papiers propose uniquement une révision résoluble et ne remplace aucune saisie récente ni validation en cours. Go fige les trois champs et résout un vrai commit Git, y compris les tags annotés ; Escape/fermeture reste une annulation. Un SHA complet inconnu reste refusé plutôt que d’emprunter le raccourci Windows sans vérification.

Une cible visible est sélectionnée, révélée et focalisée, avec navigation précédente/suivante. Après Échap ou fermeture par la croix, restaurer aussi le focus de la grille, seulement si le contexte reste valide : le retour automatique d’Avalonia pouvait focaliser Start sous un vrai gestionnaire de fenêtres, malgré des tests headless verts. Une révision invalide et un commit existant mais filtré donnent des messages distincts. Comme Windows, aucun filtre n’est retiré et aucune extension d’historique n’est déclenchée. Back/Forward affiche aussi l’avertissement de commit filtré en conservant l’avancement de son index.

À l’ouverture, activer explicitement la modale après rendu et avant les entrées, sans réassigner le champ actif. Sous X11, une fenêtre seulement mappée peut laisser l’owner désactivé actif ; sa première touche sert alors à activer la modale et disparaît. Une fermeture ou une autre modale visible interdit cette activation différée.

La complétion partagée n’émet un choix validé que pour un geste explicite. Les anciens consommateurs conservent leur comportement et la comparaison littérale des chemins Linux.

Repères : `GoToCommitDialog`, `MainWindow.RevisionNavigation`, `MainWindow.BrowserReads`, `RepositorySourceControls`, `GitRepository.RevisionNavigation` ; résultats et paquet liés au SHA dans le TODO. Les préférences Windows de tri configurable des références ne sont pas encore portées. Audit GUI natif étendu, Wayland et DPI/accessibilité restent ouverts ; les tests de traductions longues et de thèmes ne valent pas audit de toutes les variantes.
