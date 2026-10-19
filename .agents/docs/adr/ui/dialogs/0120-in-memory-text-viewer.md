# ADR 0120 — Visualiseur de texte en mémoire

Afficher un texte dans le lecteur partagé, modifiable en mémoire par défaut ou readonly au choix de l'appelant. Le nom de fichier fournit le contexte de syntaxe, jamais une destination de sauvegarde.

`TextEditDialog` reprend le FileViewer plein cadre 733×571 de FormEdit et son titre View. `FileHistoryContentView`/`DiffView` gardent sélection, copie, recherche et undo/redo. Échap retire d'abord la sélection réelle du texte, puis ferme ; Find et le menu contexte gardent leur priorité. Aucun résultat de saisie, validation ou bouton Save n'est ajouté.

Recovery Quick view ouvre ce composant readonly, comme FormVerify Windows. Le propriétaire possède chargement/fermeture et récupère le focus de la liste. FormEditor, prompts et notes restent distincts.
