# 0070 · Gestion des sous-modules

Statut : périmètre ciblé testé ; audit natif exhaustif ouvert.

Le développeur retrouve FormSubmodules : grille Name/Status à gauche, Add sous la grille, six détails copiables à droite, Pull près de Branch et Synchronize/Update/Remove en bas. Le séparateur conserve une largeur absolue, pas un ratio. Une seule fenêtre reprend le type historique SubmoduleRemovalDialog ; Add/Pull/Remove réutilisent leurs workflows partagés.

Chaque commande possède la sélection capturée puis revalidée. Update exécute `submodule update --init --recursive -- chemin`, Sync `submodule sync -- chemin` sans récursion. Les API globales restent réservées à leurs consommateurs explicites Checkout/Pull/plugins. Pull ouvre la vraie racine du module, avec le même runner Git ; refuser la découverte ascendante du superprojet et une inscription/identité remplacée.

Les détails reflètent le vrai `submodule status` pour le chemin littéral. Branch est son annotation descriptive exacte, sans retirer tags/ d’un vrai nom de tag ; Commit vient de HEAD quand le module est actif, de l’index sinon. Un fichier sale seul ne signifie pas Modified. Le conflit de gitlink reste visible, sans SHA zéro inventé, contrairement au défaut Windows. Le retrait reprend les gardes existantes et conserve fichiers/dépôt interne.

Après toute fenêtre/action, relire le module et conserver sa sélection par nom/chemin ; ne pas choisir silencieusement un autre module disparu. Les erreurs et effets partiels natifs restent visibles. Une fermeture du propriétaire annule puis draine le processus ou la lecture ; rejouer sa fermeture après le drainage. Une tentative Remove autorisée demande conservativement la relecture de Browse, même si une restauration IO échoue.

La copie de grille garde les valeurs complètes : TAB interne remplacé par espace, CR/LF conservés, en-têtes traduits si toutes les lignes sont sélectionnées (une seule ligne ici). Reprendre les largeurs communes et le défilement des entêtes/cellules comme WorktreeTable.

Repères : SubmoduleRemovalDialog.*, SubmoduleDialogPreferencesStore, MainWindow.SubmoduleRemoval/Workflows, GitRepository.Submodules.*, GitSubmoduleSnapshot. Références Windows : FormSubmodules, GitModule.GetSubmodulesInfo, Commands.SubmoduleUpdate/Sync ; règles de copie DataGridView WinForms 10.

Validation : SubmodulesDialogTests, SubmoduleRemovalDialogTests et SubmoduleManagementScenarios ; suites complètes et paquet liés au SHA dans le TODO. Trois tailles dans les deux thèmes couvertes en headless ; échantillons inspectés. Aucun N4 ; assistant de conflit de gitlink distinct : ADR 0071.
