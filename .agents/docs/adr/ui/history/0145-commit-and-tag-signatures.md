# ADR 0145 — Lire les signatures du commit et de ses tags

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.
Intention : retrouver dans le panneau GPG existant les diagnostics du commit et des tags annotés qui le désignent.

Entrées : Windows panneau GPG / GitGpgController ; Linux GpgTab → GitRepository.ReadGpgVerificationAsync → GitGpgPresentation.

Décision : partager le parseur des statuts ; résoudre le commit puis capturer les OID des tags avant verify-tag raw/message.

Le peeling des refs Git Windows inclut les tags annotés imbriqués ; les tags légers sont exclus. Plusieurs tags gardent leurs diagnostics distincts.

Les signatures non valides/non disponibles restent des états rendus ; une erreur de transport n’est pas un succès inventé.

La publication vérifie dépôt et commit encore sélectionnés ; annulation/fermeture suivent les lecteurs Main.

Repères : [Windows](../../../../../src/app/GitCommands/Git/Gpg/GitGpgController.cs) ; [lecteur Linux](../../../../../src/linux/GitExtensions.Core/GitRepository.GpgVerification.cs).

Tests source : [GpgVerificationTests](../../../../../tests/linux/GitExtensions.Desktop.Tests/GpgVerificationTests.cs).

Validation Git/headless liée au TODO ; tests de transport contrôlé et tags réels ne certifient pas tous les signers, agents et règles de confiance.
