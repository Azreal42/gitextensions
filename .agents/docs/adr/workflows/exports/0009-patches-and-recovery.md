# 0009 · Exports et reprise de patches

L’archive contient les blobs de la révision choisie, avec filtre de chemins ou comparaison, sans écraser un fichier existant. Format-patch conserve la sélection historique : un commit, deux bornes inclusives, ou plusieurs commits individuels.

Distinguer un diff de travail et une série mailbox : préserver auteurs/messages, montrer l’état Git réel et permettre continue/skip/abort après résolution. Un skip peut avancer au conflit suivant malgré un code retour non nul ; ne pas déduire l’état de ce seul code.

La maintenance montre les diagnostics fsck et tailles réels ; GC utilise la rétention normale de Git après confirmation.

Repères : `GitRepository.Export*`, `GitRepository.Maintenance*`, `ArchiveDialog`, `FormatPatchDialog`, `ApplyPatchDialog`, `MaintenanceDialog`, `ExportDialogTests`.
