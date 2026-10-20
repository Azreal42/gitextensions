# ADR 0126 — Compléter les prompts réellement appelés

Les trois fenêtres dédiées existent déjà dans le runtime Scripts (ADR0078) ; ce lot ne recrée pas de classes équivalentes. QuickStringSelector utilise la clé XLF commune Windows. Son ancre graphe est capturée avec la sélection Script avant tout await ; le fitting partagé s’applique une fois après mesure, sous garde d’annulation/propriétaire, sans suivre un déplacement utilisateur. Hors Browse, le repli existant demeure.

Rename/Move réutilise SimplePrompt : brouillon littéral conservé, nouvelle Window par essai, revalidation avant écriture et retry après l’erreur native. Échap garde la priorité de désélection du champ. Le writer et la création de dossiers ne changent pas.

FilePrompt est inchangé : le picker ne reçoit aucun CancellationToken. Écarter une réponse tardive et attendre BrowseCompletion ne prouve ni son arrêt ni sa fermeture native.

QuickStringSelector et SimplePrompt sont N2 sur les parcours testés ; FilePrompt conserve N1. [Périmètre vérifié](../../../todo.md#portable-workflows--perimetre-verifie).

Le picker FilePrompt est désormais possédé et annulable sur le périmètre de l’[ADR0131](0131-owned-file-picker.md) ; les limites N1 ci-dessus décrivent la référence historique.
