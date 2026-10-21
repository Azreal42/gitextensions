# ADR 0143 — Lire un fichier patch

Statut : accepté. Validation Git/headless et limites dans le TODO, lot Compléments portables.

Intention : examiner un patch choisi sur disque et ses fichiers sans lancer Apply ni modifier le dépôt.

Entrées : Windows FormBrowse → FormViewPatch ; Linux Browse → View patch file → ViewPatchDialog.

Décision : reprendre chemin/Browse, grille Filename/Change/Type et lecteur partagé readonly en patch fixe ; le parseur Windows lié conserve les chemins et types.

Annulation/erreur gardent le dernier aperçu accepté ; fermer attend les lectures et réponses du picker possédés.

Le mode fixe désactive les options qui régénéreraient un autre diff Git ; les gestes de lecture restent disponibles.

Repères : [Windows](../../../../../src/app/GitUI/CommandsDialogs/FormViewPatch.cs) ; [Linux](../../../../../src/linux/GitExtensions.Desktop/ViewPatchDialog.cs).

Tests source : [ViewPatchDialogTests](../../../../../tests/linux/GitExtensions.Desktop.Tests/ViewPatchDialogTests.cs).

Validation Git/headless liée au TODO ; picker natif, variantes et interaction installée restent ouverts.
