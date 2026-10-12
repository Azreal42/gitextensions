# ADR 0018 · Messages et session de commit

Statut : accepté.

Besoin : reprendre un message inachevé dans son worktree et retrouver les derniers messages sans mélanger brouillons et préférences globales.

Décision : réemployer les fichiers Git historiques `COMMITMESSAGE`, `MERGE_MSG` et `GitExtensions.amend`. Le message de merge prime et ne restaure pas Amend. Les options durables et le dernier message global sont enregistrés dans XDG ; auteur, signature et No verify restent des choix de session. Sans index, proposer annulation, indexation des fichiers visibles ou commit vide ; une erreur d'indexation ne devient jamais un commit vide.

L’entrée dans le message peut afficher le fichier staged, sans déplacer le point de saisie. Ctrl+Entrée valide le message tel qu’écrit ; une action de commit en cours interdit une nouvelle soumission.

Repères : `CommitDialog`, `CommitDraftStore`, `CommitPreferencesStore`, `CommitEmptyDialog`, `GitRepository.CommitMessages`. Templates avancés, validation personnalisée, scripts et commit du superprojet restent ouverts.
