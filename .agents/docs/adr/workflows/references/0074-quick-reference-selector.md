# ADR 0074 · Sélecteur rapide de références

Statut : accepté sur le périmètre ciblé N3 ; scripts et audit de parité complète restent ouverts.

Besoin : F2 et Suppr dans la grille doivent agir sur la référence voulue du dernier commit sélectionné, sans confondre une branche et un tag homonymes ni déclencher une mutation au simple choix.

Décision : reprendre FormQuickGitRefSelector/FormQuickItemSelector : liste sans bordure ni filtre/icônes, groupes local/remote/tag triés selon la culture courante, première référence présélectionnée, huit lignes visibles au maximum et mesure bornée aux 100 premiers caractères. Bouton, Entrée et double-clic valident ; Échap annule. Les en-têtes restent sélectionnables et donnent un choix nul, comme Windows. Placement au coin graphe/ligne, ajusté à la zone de travail du bureau.

Zéro candidate ne fait rien ; une ouvre directement le dialogue métier ; plusieurs ouvrent le sélecteur. F2 inclut les branches locales courantes ; Suppr exclut la locale courante et route chaque type vers son dialogue existant. Le commit focalisé est conservé dans une sélection multiple. Le sélecteur n’utilise pas l’état Busy global, ne remplace pas l’historique et restaure son focus à la fermeture. Le vrai contrôle natif a révélé aussi un premier Échap consommé par l’activation du dialogue métier. Partager l’activation après mapping entre RepositoryDialog, GoToCommitDialog et ce sélecteur, sans réassigner le champ actif ni activer un propriétaire ayant une sous-modale visible.

Une lecture native sans verrou de mutation applique l’expression de rattachement Windows, avec les règles natives du Git installé pour les tags ; le hash brut de la référence reste distinct du commit rattaché. La règle Windows de HEAD distant redondant est conservée. Après le choix, nom/hash/contexte sont relus ; sélection, réouverture même chemin et fermeture annulent les lectures avant tout routage.

Une référence distante sans mapping supprimable reste affichée dans le dialogue métier avec une raison explicite et l’action désactivée. Ne pas vider silencieusement sa présélection ni deviner une branche serveur ; conserver les gardes existantes des refspecs et références symboliques.

Le mode Select est disponible pour le futur raccord des scripts. Ce raccord et FormQuickStringSelector restent ouverts. Le caller Windows des scripts ignore DialogResult et peut conserver une sélection après Échap : l’intégration Linux devra vérifier l’acceptation explicite, sans reproduire implicitement ce défaut. Le commentaire du moteur Linux est corrigé ; son comportement ne change pas.

Repères : QuickGitRefSelectorDialog, MainWindow.QuickReferences/Shortcuts/BrowserReads, GitRepository.QuickReferences ; dialogues RenameBranch/DeleteBranch/DeleteRemoteBranch/DeleteTag partagés. Première activation et retour du focus validés sous X11/Openbox ; clair/sombre et anglais/français vérifiés en headless. En-tête sélectionné non exercé en natif, Select/scripts, Wayland et DPI/accessibilité restent ouverts.

Validation : [source et périmètres](../../../../../artifacts/linux/snapshots/f2c42762f/proofs/source-validation.json), [15 groupes natifs sur le paquet exact](../../../../../artifacts/linux/snapshots/f2c42762f/proofs/native-quick-reference/results.json). Le candidat antérieur 10d889d9b refusé pour sa première touche perdue reste conservé ; aucune parité N4 annoncée.
