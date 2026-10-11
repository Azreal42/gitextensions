# ADR 0018 · Messages et session de commit

Statut : accepté.

Besoin : reprendre un message inachevé dans son worktree et retrouver les derniers messages sans mélanger brouillons et préférences globales.

Décision : réemployer les fichiers Git historiques `COMMITMESSAGE`, `MERGE_MSG` et `GitExtensions.amend`. Le message de merge prime et ne restaure pas Amend. Les options durables et le dernier message global sont enregistrés dans XDG ; auteur, signature et No verify restent des choix de session. Sans index, proposer annulation, indexation des fichiers visibles ou commit vide ; une erreur d'indexation ne devient jamais un commit vide.

Repères : `CommitDialog`, `CommitDraftStore`, `CommitPreferencesStore`, `CommitEmptyDialog`, `GitRepository.CommitMessages`. Templates avancés, validation personnalisée, scripts et commit du superprojet restent ouverts.
