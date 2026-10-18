# ADR 0094 · Page Config et encodages partagés

Statut : suites et paquet vérifiés ; parcours source natif X11 borné, audit étendu ouvert.

Retrouver les treize champs de la page Windows dans une grille commune, avec les mêmes libellés XLF, ordre et actions. Réutiliser le lecteur et la publication Diff/Merge pour les sept champs supplémentaires ; garder les brouillons Local/Global et Apply/OK explicites. Une modification de portée ne publie rien.

Distinguer absence, chaîne vide et entrée sans valeur. Le helper vide réinitialise la chaîne héritée ; Unset retire seulement l’entrée native. Revenir à un encodage publié sans valeur restaure le brouillon initial. Valeurs inconnues, origines et multivaleurs restent lisibles sans normalisation. Une erreur native garde son diagnostic, sans destination devinée ni édition possible ; les valeurs raw restent visibles lorsque leur lecture réussit malgré l’échec de rev-parse. Une configuration illisible reste indisponible, sans valeurs inventées.

Le sélecteur d’encodages reprend les deux listes Windows. Sa préférence XDG est commune à Config et aux lecteurs, indépendante de la configuration Git : OK enfant reste acquis après Cancel parent. Retirer un favori ne change pas l’encodage du fichier courant. Un profil illisible est signalé et ne peut être écrasé par défaut. Les codecs stricts et les octets des patches restent inchangés.

Repères : `GitToolsSettingsPage/Dialog/DraftSession`, `GitRepository.ToolConfiguration`, `AvailableEncodingsDialog/Catalog/PreferencesStore`, `DiffViewerReadingMenu`. Git 2.26 reste requis par ce lecteur à origines ; édition brute disponible séparément. Global hors dépôt dans l’UI, découverte exhaustive et audit natif des nouveaux champs/pickers restent ouverts. Écart connu : la liste préférée enregistrée vide reste vide sur Linux, tandis que Windows reprend les défauts ; seules les bases déjà incluses sont protégées du retrait.

[Suites complètes](../../../../../artifacts/linux/git-config-20261018/full-03/parent-verification.json). La matrice distingue ce lot des validations natives précédentes de l’ADR 0093.

[Parcours source natif](../../../../../artifacts/linux/git-config-20261018/native-source-01/root-review.json) : source `964b9a927`, nom Local, annulations, préférence enfant et redémarrage ; anglais/clair/X11 seulement. Le commit `a79bc9c08` ne change que les fixtures de tests (preuve source-equivalence), sans transfert au paquet interactif.
