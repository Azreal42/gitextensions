# ADR 0041 · Retrait d'un sous-module

Statut : implémenté et contrôlé localement ; audit natif et paquet à renouveler dans le [TODO](../../../todo.md).

Besoin : reprendre Remove de FormSubmodules, après confirmation Yes/No, sans effacer les fichiers ni le dépôt interne. Windows retire le gitlink de l'index, la section de .gitmodules et la section locale submodule.<name> ; aucun deinit ni nettoyage de .git/modules.

S'il reste des sections, sauvegarder et indexer .gitmodules ; sinon retirer seulement son entrée de l'index et conserver le fichier de travail. Valider sélection et chemins avant toute mutation. Préparer les modifications sur des copies, puis publier sous verrous Git ; une erreur de préparation conserve les fichiers vivants. Un échec IO de publication tente de restaurer ces seuls fichiers, sans modifier le contenu des sous-modules.

Repères : FormSubmodules.RemoveSubmoduleClick, GitModule.UnstageFile ; GitRepository.SubmoduleRemoval, SubmoduleRemovalDialog, MainWindow.SubmoduleRemoval.
