# ADR 0147 — Ouvrir la comparaison capturée du sous-module

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Intention : depuis Diff/File tree, voir dans le Browse enfant les deux états gitlink réellement choisis dans le superproject.

Entrées : Windows FileStatusList.OpenSubmodule ; Linux menus partagés Browse/Compare → FileMenuAdditionalOperations → transport BrowseRequest.

Décision : capturer et transporter les deux côtés dans leur sens ; Gitlink Index devient l’OID enfant, WorkingTree reste live.

Les renommages conservent l’ancien chemin du premier côté ; dépôt enfant, objets et état index sont revalidés avant lancement.

Une cible supprimée, conflictuelle, périmée ou absente produit son erreur ; aucun remplacement silencieux par HEAD, checkout ou autre module.

Le lancement partage le transport CLI typé et les garde-fous de sélection/annulation ; ouvrir ne modifie ni checkout enfant ni index parent.

Repères/tests source : [opérations Linux](../../../../../src/linux/GitExtensions.Desktop/FileMenuAdditionalOperations.cs) ; [SubmoduleBrowseComparisonTests](../../../../../tests/linux/GitExtensions.Desktop.Tests/SubmoduleBrowseComparisonTests.cs).

Validation Git/headless liée au TODO ; ouverture native des variantes/renommages/courses et interaction installée restent à auditer.
