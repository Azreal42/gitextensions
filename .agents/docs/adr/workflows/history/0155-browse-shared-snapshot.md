# ADR 0155 · Historique Browse sur snapshot typé commun

Statut : H35 implémenté et testé ; paquet isolé vérifié. Source `3dd4eb926`, preuves et limites dans le [TODO](../../../todo.md#h35-browse-shared-snapshot). La parité complète reste ouverte.

Browse partage les lignes et le graphe du chooser. WorkingTree et Index sont des endpoints typés du même ListBox ; leurs clés de vue ne sont jamais des OID Git. `HistoryRevisionItems` compare le payload complet ; `IncrementalRevisionRows.PublishSnapshot` conserve les objets inchangés et publie sans Reset. Ordre d’admission WinForms, ancre, focus et viewport restent les repères. Le clic droit garde les membres sur la ligne latest déjà sélectionnée ; sinon il cible une sélection unique.

Browse publie à l’EOF de sa requête bornée ; il ne reçoit pas le streaming du chooser ([ADR0153](0153-shared-commit-chooser.md)). B est la sélection la plus récente (`sélection[0]`). Avec All parents, quatre sélections prennent A à l’index 2 : `B,baseB,A,baseA` ; trois suivent `B,BASE,A`. All parents désactivé, ou au-delà de quatre, seule la comparaison **dernière sélection→B** demeure. Les bases et séries Range admissibles restent réelles ; HEAD capturé ne transforme aucun endpoint artificiel en commit. Chaque feuille garde sa paire et le vrai parent artificiel ([ADR0016](../references/0016-revision-comparisons.md)).

Un rafraîchissement générique suit le nouveau HEAD ; s’il est inchangé, il restaure la sélection survivante. Le retour explicite d’une action contextuelle conserve l’intention de sélection de l’utilisateur : c’est un raffinement Linux, pas une règle générale des mutations Windows.

ShowArtificial republie le snapshot en place sans relire l’historique. Si HEAD est masqué, le rafraîchissement mémorise jusqu’à 50 révisions natives, HEAD compris, même lorsque les artificiels sont cachés. La republication réutilise ce cache et conserve le parent réel de l’Index ; un changement de sélection peut relire le panneau actif et CommitInfo visible, jamais le diff caché. Si le menu View retire la dernière ligne, son origine MenuItem devient la grille vide ; le retour différé à Loaded garde les protections de dépôt, fermeture et focus plus récent.

Le rétablissement WT exécute `checkout -- .`, celui de l’Index `reset --hard --quiet --`. `clean -d -f --` reste explicite, après succès sous la même lease ; effets partiels et annulation imposent drainage puis rafraîchissement.

Suites : [ADR0157](0157-progressive-browse-host.md) raccorde Browse progressif ; [ADR0158](../../ui/layout/0158-compact-browse-layout.md) traite la largeur minimale, avec leurs périmètres et limites dans le TODO. Niveaux inchangés, treize familles ouvertes, aucun N4. Le code reste la source de vérité.

Sources : [MainWindow.InlineHistory](../../../../../src/linux/GitExtensions.Desktop/MainWindow.InlineHistory.cs), [HistoryView](../../../../../src/linux/GitExtensions.Desktop/MainWindow.HistoryView.cs), [RevisionGridControl Windows](../../../../../src/app/GitUI/UserControls/RevisionGrid/RevisionGridControl.cs). Autres repères : `FocusRestoration`, `GitRepository.ComparisonGroups.Multiple/RevisionAncestry/ArtificialReset` ; scénarios `SourceSnapshotTests`, `InlineBrowseHistoryTests`, `ArtificialResetScenarios`, sans résultat déduit de leur présence.
