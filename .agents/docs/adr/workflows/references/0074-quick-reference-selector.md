# ADR 0074 · Sélecteur rapide de références

Statut : en cours ; niveau N1 conservé jusqu’aux validations du candidat.

Besoin : F2 et Suppr dans la grille doivent agir sur la référence voulue du dernier commit sélectionné, sans confondre une branche et un tag homonymes ni déclencher une mutation au simple choix.

Décision : reprendre FormQuickGitRefSelector/FormQuickItemSelector : liste sans bordure ni filtre/icônes, groupes local/remote/tag triés selon la culture courante, première référence présélectionnée, huit lignes visibles au maximum et mesure bornée aux 100 premiers caractères. Bouton, Entrée et double-clic valident ; Échap annule. Les en-têtes restent sélectionnables et donnent un choix nul, comme Windows. Placement au coin graphe/ligne, ajusté à la zone de travail du bureau.

Zéro candidate ne fait rien ; une ouvre directement le dialogue métier ; plusieurs ouvrent le sélecteur. F2 inclut les branches locales courantes ; Suppr exclut la locale courante et route chaque type vers son dialogue existant. Le commit focalisé est conservé dans une sélection multiple. Le sélecteur n’utilise pas l’état Busy global, ne remplace pas l’historique et restaure son focus à la fermeture. Le vrai contrôle natif a révélé aussi un premier Échap consommé par l’activation du dialogue métier. Partager l’activation après mapping entre RepositoryDialog, GoToCommitDialog et ce sélecteur, sans réassigner le champ actif ni activer un propriétaire ayant une sous-modale visible.

Une lecture native sans verrou de mutation applique l’expression de rattachement Windows, avec les règles natives du Git installé pour les tags ; le hash brut de la référence reste distinct du commit rattaché. La règle Windows de HEAD distant redondant est conservée. Après le choix, nom/hash/contexte sont relus ; sélection, réouverture même chemin et fermeture annulent les lectures avant tout routage.

Une référence distante sans mapping supprimable reste affichée dans le dialogue métier avec une raison explicite et l’action désactivée. Ne pas vider silencieusement sa présélection ni deviner une branche serveur ; conserver les gardes existantes des refspecs et références symboliques.

Le mode Select est disponible pour le futur raccord des scripts. Ce raccord et FormQuickStringSelector restent ouverts. Le caller Windows des scripts ignore DialogResult et peut conserver une sélection après Échap : l’intégration Linux devra vérifier l’acceptation explicite, sans reproduire implicitement ce défaut. Le commentaire du moteur Linux est corrigé ; son comportement ne change pas.

Repères : QuickGitRefSelectorDialog, MainWindow.QuickReferences/Shortcuts/BrowserReads, GitRepository.QuickReferences ; dialogues RenameBranch/DeleteBranch/DeleteRemoteBranch/DeleteTag partagés. Première activation et focus natifs, suite complète, traductions/thèmes, Wayland et DPI/accessibilité restent à valider selon le périmètre indiqué dans le TODO.

Validation connue : 138 tests Desktop ciblés réussis sur le code de `954a1412d`. Le candidat `10d889d9b` reste refusé pour le premier Échap du dialogue métier ; le contrôle natif corrigé et la suite Desktop complète ne sont pas exécutés. Core 554/554 et Plugins 35/35 ont été exécutés sur `10d889d9b`, composants inchangés dans `954a1412d` ; ce résultat ne certifie pas l’activation OS.
