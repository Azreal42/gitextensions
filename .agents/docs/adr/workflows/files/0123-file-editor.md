# ADR 0123 — Éditer un fichier existant

Retrouver FormEditor depuis les menus Edit de Browse/Compare et l’édition du vrai config local. FileEditorDialog réemploie le lecteur, la recherche, les codecs et la position courante ; FormEdit reste un visualiseur en mémoire distinct. Le lancement CLI editor reste hors lot.

Save compare le texte à son baseline/undo, conserve le BOM présent ou absent et n’ajoute aucune fin de ligne. EditableFileStore réutilise les primitives IO des règles sans leur normalisation de newline. Identité, octets, permissions et destination des liens sont revérifiés ; aucun fichier manquant n’est créé. Cette sauvegarde optimiste n’est pas une transaction interprocessus.

Une erreur garde le document dirty et ouvert ; une écriture commencée mais non vérifiée impose un rechargement. Échap respecte Find, menu et sélection avant la fermeture avec choix Save/Discard/Cancel. Les effets de Save restent visibles après Cancel ultérieur.

N2 partiel : suites complètes, paquet exact et natif Browse ciblé vérifiés ; [périmètre et preuves](../../../todo.md#portable-workflows--perimetre-verifie).
