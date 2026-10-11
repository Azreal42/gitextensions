# ADR 0001 · Bureau Linux natif

Statut : accepté, portage en cours.

Besoin : rendre les workflows existants utilisables sur Linux, sans Wine et avec installation classique.

Décision : Avalonia/.NET 10 pour l'interface et un noyau sans dépendance WinForms. Isoler le build portable pendant le remplacement progressif des écrans ; réemployer les modèles/parsers qui restent indépendants de l'OS.

Conséquence : remplacer les contrôles WinForms, GDI, ConEmu, CredentialManager et les interfaces graphiques de plugins. Un écran d'historique et un commit ne suffisent pas à déclarer la migration terminée.

Repères : `src/linux/`, `src/app/GitUI/`, [plateformes Avalonia](https://docs.avaloniaui.net/docs/supported-platforms).
