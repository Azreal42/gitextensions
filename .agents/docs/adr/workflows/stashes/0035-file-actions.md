# ADR 0035 · Actions sur les fichiers d’un stash

Statut : sous-périmètre livré et vérifié ; état du paquet et limites dans le [TODO](../../../todo.md).

Besoin : reprendre les commandes du FileStatusList de FormStash sans confondre application du stash entier et patch du fichier affiché.

Cherry pick changes applique le patch entier du fichier visible avec le three-way/index historique ; une multisélection ne signifie pas appliquer tous ses fichiers. Le stash reste présent, y compris si Git laisse des conflits à résoudre. Conserver le stash affiché et sa sélection de fichiers après une action contextuelle ; relire le status sans revenir implicitement aux changements courants. Le chargement du diff garde la liste active pour préserver le focus du clic et le raccourci R ; les commandes restent protégées pendant l’opération. Lire les octets du patch canonique ; le codec du viewer et les options d’affichage ne doivent pas modifier la mutation.

Reset file(s) to agit sur la sélection et propose le snapshot du stash ou son premier parent. La confirmation originale garde Annuler par défaut et une option distincte pour supprimer les nouveaux fichiers. Désindexer d’abord vers HEAD puis restaurer les fichiers présents dans le snapshot ; une suppression optionnelle ne stage pas implicitement une deletion. Rename touche les deux noms, copie seulement sa destination. Valider toute la sélection et son stash avant mutation ; les protections contre collision/untracked et sortie du worktree ne sont pas des fallbacks vers d’autres fichiers.

Repères : FormStash, FileStatusList.ContextMenu, FormResetChanges, FileViewer.ApplySelectedLines ; GitRepository.Stashes, StashDialog, ResetChangesDialog, StashScenarios, StashMenuTests. Les autres commandes de fichiers restent dans l’audit général ; aucune commande branche/reword inventée pour ce formulaire.
