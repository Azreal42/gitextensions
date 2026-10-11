# 0007 · Contenu du fichier sélectionné

Conserver la liste de fichiers à gauche du visualiseur et le choix du parent pour les merges. Une suppression montre le contenu du parent ; Commit distingue index et dossier de travail.

Reprendre FileViewer : première image, taille réelle si elle tient, sinon ajustement proportionnel ; image illisible en hexadécimal. Les chemins et les blobs restent ceux de Git, sans traitement par un shell.

Repères : `GitRepository.Diffs`, `MainWindow.Diffs`, `FileContentView`, `FileContentTests`.
