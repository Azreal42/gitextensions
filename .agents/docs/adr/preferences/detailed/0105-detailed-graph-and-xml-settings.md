# ADR 0105 · Detailed, graphe et réglages XML

Décision : reprendre les six champs Windows ; portage N2 partiel sur la source `344a910d1`, preuves et limites dans le [TODO](../../../todo.md#detailed-et-worktrees-absorbés--qualification-partielle).

| Champ Windows | Défaut | Autorité Linux |
| --- | --- | --- |
| `MergeGraphLanesHavingCommonParent` | true | Global Desktop |
| `RenderGraphWithDiagonals` | true | Global Desktop |
| `StraightenGraphDiagonals` | true | Global Desktop |
| `Detailed.GetRemoteBranchesDirectlyFromRemote` | false | XML |
| `Detailed.AddMergeLogMessages` | false | XML |
| `Detailed.MergeLogMessagesCount` | 20 | XML, tout Int32 sans clamp |

La page présente Effective en lecture seule ; Local=`CommonDirectory/GitExtensions.settings`, Distributed=`workdir/GitExtensions.settings`, Global=`XDG/gitextensions/GitExtensions.settings`. Bare exclut Distributed ; hors dépôt seul Global existe. Résoudre le premier brut non-null Local→Distributed→Global avant parsing : vide/invalide masque les couches basses. Null supprime ; clés inconnues conservées, no-op sans réécriture des octets. Les consommateurs Effective écrivent Local si un brut Local/Distributed existe, sinon Global ; brut identique sans effet, jamais Distributed implicitement.

`GitXmlSettingsStore` est commun avec Dictionary : snapshots, verrous partagés et CAS optimiste octets/identité/liens/permissions, écriture engagée drainée puis readback. Sauvegardes séquentielles acquittées ; aucune transaction entre fichiers ou contre tout éditeur externe.

Apply publie les intentions ; Cancel abandonne les brouillons restants. Graph est éditable seulement en Global : algorithme/rendu Windows, options immuables dans Browse, historique fichier et picker de révision ; publication par profil et attachement, générations retirées ignorées. Aucune mutation de topologie Git.

Push Multiple seul consomme le vrai `ls-remote --heads` dans son Process possédé ; noms/hashes du remote sélectionné, sans fetch ni repli local après erreur. Merge publie AddLog/count dès le changement explicite, même suivi de Cancel ; count0 omet le flag. Hors0–100 reste visible et Merge désactivé jusqu’au choix explicite, sans écrasement du réglage. L’ancien JSON sert seulement à une migration explicite des clés absentes vers Local, puis marqueur empêchant leur résurrection ; ouvrir/annuler seul ne migre rien.

Repères : `DetailedSettingsPage/Session`, `DetailedGraphSettings`, `GitDistributedSettingsSnapshot`, `GitXmlSettingsStore`, `PushDialog`, `MergeBranchDialog/PreferencesStore`. Code et tests restent la source de vérité.
