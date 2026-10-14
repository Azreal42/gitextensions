# ADR 0057 · Préparation des scripts historiques

Statut : préparation pure et stockage vérifiés localement ; exécution, UI et événements ouverts.

Besoin : reprendre la configuration et les variables Windows sans perdre les scripts de l’utilisateur ni changer les gardes des workflows.

Décision : import explicite XML/legacy, profil global XDG et conservation du texte illisible avant toute décision de remplacement. Figer le contexte des révisions/fichiers avant les demandes typées ; conserver les différences entre annulation, refus et choix vide. La préparation retourne du texte, pas un argv ni une preuve d’exécution.

Suite : valider le transport des arguments avec de vrais exécutables avant le runner, les menus et les 22 raccords. Les points Before/After et leurs effets partiels suivent l’original ; ne pas imposer une garde After-success commune. Aucun événement Clone.

Repères : `Core/Scripts/`, `ScriptsPreferencesStore`, `ScriptScenarios`, `ScriptsPreferencesStoreTests`, `src/app/GitUI/ScriptsEngine/`. Preuves et périmètre restant : [TODO](../../../todo.md).
