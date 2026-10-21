# ADR 0156 · Flux fichier avec contexte de renommage

Statut : préparation Core H36 implémentée et testée ; raccord Browse encore ouvert dans le [TODO](../../../todo.md#h36-progressive-history).

Le premier commit sélectionnable doit déjà connaître les anciens noms du fichier. Préparer une seule fois le suivi depuis HEAD, sans plafond de graphe, puis parcourir les chemins littéraux avec parents réécrits. Un dossier ne suit pas un fichier ; aucune correspondance n’est inventée pour une branche divergente absente du préparcours.

`StreamFileHistoryGraphAsync` partage le builder, parser et drainage de `StreamRevisionGraphAsync`. Chaque lot et le résultat vide/final portent le même contexte readonly. Le flux ne conserve pas de liste complète des commits ; les lots restent provisoires jusqu’au succès natif. Son environnement Git couvre préparcours et graphe jusqu’au vrai drainage des processus et callbacks.

Le plafond explicite (0 illimité) reste distinct de Count, ignoré côté flux ; les options de branche gardent leur priorité native. Le lecteur snapshot garde ses validations, dont Count positif, et son transport capturé. Cette API ne rend pas encore Browse progressif.

Sources : [GitRepository.FileHistoryGraph](../../../../../src/linux/GitExtensions.Core/GitRepository.FileHistoryGraph.cs), [GitLogQuery](../../../../../src/linux/GitExtensions.Core/GitLogQuery.cs), [scénarios Git réels](../../../../../tests/linux/GitExtensions.Core.Tests/FileHistoryStreamingScenarios.cs). Preuves exécutées et limites dans le TODO ; le code reste la source de vérité.
