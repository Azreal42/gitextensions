# 0026 — Lecture et encodage des fichiers

Décision : reprendre les gestes du FileViewer original, son sélecteur d'encodage compact et ses traductions. « Aller à la ligne » vise le fichier, même lorsqu'un patch ne montre qu'un extrait. Le même fichier garde sa position lors d'un changement de thème ou de contexte ; changer de fichier ou de révision repart au début.

Les copies ancienne/nouvelle version portent sur la sélection ou le patch affiché, avec les règles historiques de préfixes et de fins de ligne. Elles ne reconstruisent pas les lignes absentes.

Le choix d'encodage reste dans la session et ne réécrit pas Git. Le défaut vient de `i18n.filesencoding` ; le BOM prime pour un fichier complet. Les octets du patch restent disponibles et le staging partiel exige une conversion réversible, avec les chemins UTF-8 et le contenu dans son encodage. Refuser une conversion ambiguë plutôt que modifier le fichier silencieusement.

La sélection n'inclut pas la ligne qui commence à sa borne finale. Un chargement échoué rend l'aperçu précédent inapplicable ; il ne doit jamais agir sur l'ancien fichier depuis la nouvelle sélection.

Un aperçu limité en octets peut omettre le dernier caractère incomplet ; il conserve les octets chargés et reste strict sur les erreurs intérieures. La même fin incomplète dans un fichier complet reste invalide.

Repères : `Editor/FileViewer`, `FileViewerInternal`, `DiffViewerReadingMenu`, `DiffView`, `GitDiffClipboard`, `GitFileEncodingCodec`, `GitEncodedPatch`.
