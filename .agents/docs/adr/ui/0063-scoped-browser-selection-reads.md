# ADR 0063 · Lectures de sélection et focus de la grille

## Problème et référence Windows

Un clic de commit ou de fichier passait par MainWindow.RunAsync, qui désactivait la grille, les onglets, les arbres et la barre avant toute lecture. Le focus était perdu pendant PointerPressed ; la sélection suivante était ignorée tant que _busy était vrai. Le File tree était aussi reconstruit alors que son onglet était masqué.

Sources relues : FormBrowse.RefreshSelection, FillFileTree, FillDiff et UpdateTargets ; RevisionDiffControl.SetDiffsAsync, ShowSelectedFileDiffAsync et ses séquences _setDiffSequence / _viewChangesSequence. Windows dissocie les lectures des panneaux, charge les onglets à leur activation et annule les anciennes sélections sans désactiver tout le navigateur.

## Décision

MainWindow.BrowserReads porte des séquences annulables indépendantes : liste de différences de révision, diff sélectionné, arbre du commit, blob sélectionné, diff du worktree et signature. Les tokens sont passés explicitement aux lectures et vérifiés avant publication sur le thread UI. Commencer une sélection annule sa précédente lecture ; changer de commit invalide les seuls panneaux de révision. Une commande exclusive, un changement de dépôt ou la fermeture annulent les lectures concernées. Les sources annulées restent vivantes jusqu’à la fin de leur tâche.

Les lectures ne passent plus par SetActionsEnabled et ne repositionnent pas la barre. Le focus appartient au contrôle réellement cliqué et reste utilisable pendant Git : aucun Focus différé ne peut le reprendre après un autre clic. IsBusy interne agrège les tâches en attente pour l’attente des tests ; _busy continue de protéger les workflows exclusifs.

Le diff et le File tree sont chargés à l’activation selon la sélection courante, y compris quand déplacer les informations Commit rend le Diff visible. Un changement de fichier ne remplace ni la grille ni la liste des fichiers. Le texte/image précédents restent visibles pendant la lecture ; le résultat complet remplace le document en une publication sans texte vide intermédiaire. La configuration du viewer est collectée avant publication et n’est pas partiellement modifiée par une lecture annulée. Filtrer/vider la sélection annule aussi le diff qui aurait pu réapparaître après coup.

Le patch applicable, l’état prêt du viewer et les boutons de staging partiel sont invalidés dès le début du chargement du diff du worktree, et restent indisponibles après un échec (notamment un encodage de dépôt invalide). L’identité du diff modifiable indique alors le fichier demandé sans autoriser le document précédent. Une commande de staging ne peut pas appliquer le patch de l’ancien fichier à la nouvelle sélection. Les dialogues et modifications Git conservent leur garde exclusive. Une lecture interrompue par un workflow est reprise pour l’onglet visible après celui-ci.

## Validation et limites

BrowserSelectionTests utilise de vrais clics et gestes haut/bas Avalonia avec un exécutable Git enveloppé dans une barrière contrôlée par fichier. Il vérifie le focus pendant/après lecture, la dernière sélection, l’annulation de processus, les sources de contrôles stables, le chargement différé, le filtrage d’une lecture en vol, la fermeture du dépôt et l’absence de staging d’un ancien patch. Le TODO référence les résultats effectifs.

Les tests restent headless ; aucun nouveau parcours natif prolongé n’est revendiqué. Les autres familles de migration restent ouvertes.
