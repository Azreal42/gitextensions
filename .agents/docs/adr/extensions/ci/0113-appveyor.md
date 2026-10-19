# ADR 0113 · AppVeyor dans le suivi CI partagé

Reprendre la page Windows compte/token/projets et son option nullable de résultats de tests. Les projets acceptent plusieurs identifiants ; sans projet explicite, la découverte du compte reste distincte de la suggestion du dossier affichée par la page. Une lecture, un no-op ou Cancel ne sauvegarde pas cette suggestion.

Les brouillons utilisent le moteur XML et les portées partagés : Effective en lecture seule, Apply/Cancel et CAS optimiste communs. Le token masqué reste dans le fichier choisi, sans coffre ni import d’identité système. L’adaptateur garde le contrat de routes Windows, notamment la distinction des tokens v2 pour la liste des projets.

`AppVeyorApiClient` et `AppVeyorBuildSource` alimentent les statuts par SHA, durées, résumé des tests et liens PR/build du présentateur commun. Les lectures restent bornées, annulables et drainées ; aucune mutation distante n’est ajoutée.

Le rapport HTML embarqué Windows reste absent ; la liste et les ouvertures externes constituent un périmètre partiel. Page N2 sur le périmètre vérifié, aucun N4. [Preuves et limites](../../../todo.md#appveyor--périmètre-vérifié) ; le code reste la source de vérité.
