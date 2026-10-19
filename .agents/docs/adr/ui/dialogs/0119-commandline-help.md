# ADR 0119 — Aide de démarrage en fenêtre

Lire les commandes disponibles sans ouvrir de dépôt. Le seul argument littéral `help` ouvre `CommandlineHelpDialog` dans une application autonome ; `--help` garde stdout et `--version` sa priorité.

La fenêtre reprend le format Windows 394×662, le titre traduit et l'icône originale. Son texte sélectionnable provient de l'usage public du dispatcher Linux, sans annoncer les commandes Windows absentes. Le court texte introductif conserve un fallback anglais.

Langue, fontes, palettes et clipboard sont partagés ; découverte de dépôt, historique récent, Checklist et avatars ne démarrent pas. Fermer termine cette application d'aide. Repères : `Program`, `CommandlineHelpDialog/Application`, `CommandlineHelpDialogTests`.
