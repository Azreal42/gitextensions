# ADR 0061 · Arbre de navigation compact

## Besoin

Reproduire la densité de la capture Windows : lignes rapprochées, petits chevrons, retrait régulier et pointillés de hiérarchie. Le code RepoObjectsTree.InitImageList réserve 16 px pour l’icône et 1 px de chaque côté vertical, soit 18 px. Le contrôle Windows est sans bordure.

## Décision

ClassicTreeViewAppearance adapte uniquement le TreeView natif de navigation : entêtes 18 px, retrait 19 px, zone de chevron 19 px avec glyph 7 px, icônes originales 16 px et texte 12. La hauteur fixe est appliquée à PART_LayoutRoot, pas au TreeViewItem entier qui contient aussi les descendants. Les templates, le clavier, la sélection et les automation peers restent natifs. Les entêtes ont une marge de départ de 6 px à l’intérieur du contenu ; le ScrollViewer garde son padding nul.

ClassicTreeConnectors ajoute les pointillés à partir des positions réelles des entêtes. Ce décor ne reçoit pas les événements pointeur ; il est limité au viewport, suit le défilement et détache ses événements avec la vue. Les limites des tirets sont alignées sur des pixels entiers pour garder de vrais intervalles, contrôlés sur les captures. Il ne remplace aucun nœud ou contrôle interactif. Les petits chevrons gardent leur contrôle ToggleButton natif et leur binding IsExpanded.

Les racines Branches/Remotes utilisent LocalBranchRoot/RemoteBranchRoot ; les dossiers utilisent BranchFolder et les dépôts distants Remote. Les entêtes réutilisent OriginalMenuItems.Icon via le helper existant. La branche courante est en gras, comme LocalBranchNode.GetFontStyle. Les noms de références restent littéraux et les états d’ouverture sont conservés au rafraîchissement.

## Pièges vérifiés

Un padding de 6 px sur le TreeView faisait défiler horizontalement le viewport lors du focus du nœud. Le chevron se déplaçait alors de 6 px entre PointerPressed et PointerReleased, annulant le clic. Le retrait est placé dans chaque entête ; un vrai clic à coordonnées identiques et la navigation au clavier restent vérifiés.

Le thème natif utilise un entête indenté à l’intérieur d’un Border de ligne pleine largeur : mesurer le libellé, pas le Border, pour vérifier le retrait. Initialiser le nom du décor avant son attachement à l’arbre visuel.

Références : RepoObjectsTree.cs/Designer, NativeTreeView et LocalBranchNode du code Windows ; [template natif Avalonia 11.3.22](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Themes.Fluent/Controls/TreeViewItem.xaml). Les mesures/rendus et l’état du paquet figurent dans le [TODO](../../todo.md). Aucun audit natif étendu ni aucune famille de migration n’est clôturé par cette tranche.
