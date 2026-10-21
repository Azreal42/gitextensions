# ADR 0158 · Browse à largeur réduite

Statut : implémenté ; validation et limites dans le [TODO](../../../todo.md#h37-compact-browse-layout).

Garder les positions explicites gauche/droite/dessous et la largeur initiale de 490 lorsque l’espace le permet. Rétrécir la fenêtre ne constitue pas un choix utilisateur : limiter temporairement la largeur latérale sans perdre le réglage du séparateur, y compris après un passage dessous. La liste, ses objets, sa sélection et son focus restent en place ; un resize ne relit pas Git.

Le budget appartient à Browse : colonnes fixes communes, allocations réelles des lignes visibles (dont CI), graphe et chrome de liste, puis place pour le sujet. Conserver la largeur demandée de la colonne, distincte de sa largeur effectivement limitée. Ne pas modifier la position sauvegardée ni imposer une largeur minimale globale au composant partagé.

Les valeurs de l’en-tête CommitInfo gardent leurs lignes explicites, sans retour automatique ni ellipse, comme l’en-tête Windows. Un viewport horizontal limité à cet en-tête donne accès à la valeur entière à faible largeur ; message et références conservent leur retour à la ligne. C’est une adaptation de la disposition Avalonia : Windows peut élargir la table entière à la largeur naturelle de son en-tête. Historique fichier et Blame réutilisent le même composant, sans nouveau lecteur ni transfert de focus.

Repères : [budget et largeur choisie](../../../../../src/linux/GitExtensions.Desktop/MainWindow.CommitInfoLayout.cs), [placement des panneaux](../../../../../src/linux/GitExtensions.Desktop/MainWindow.Browser.cs), [en-tête partagé](../../../../../src/linux/GitExtensions.Desktop/CommitInformationView.cs). Autorité Windows : [FormBrowse.LayoutRevisionInfo](../../../../../src/app/GitUI/CommandsDialogs/FormBrowse.cs), [en-tête sans WordWrap](../../../../../src/app/GitUI/CommitInfo/CommitInfoHeader.Designer.cs), [largeur naturelle et disposition](../../../../../src/app/GitUI/CommitInfo/CommitInfo.cs).

Les [régressions de taille et gestes réels](../../../../../tests/linux/GitExtensions.Desktop.Tests/BrowseMinimumLayoutDiagnosticTests.cs) attendent le nouveau rendu après resize ; elles vérifient aussi le défilement jusqu’au dernier caractère du hash, le choix du séparateur et la première flèche après clic. Les colonnes Notes/CI cumulées et grandes polices peuvent encore dépasser le budget physique ; aucun déplacement automatique dessous n’est introduit.
